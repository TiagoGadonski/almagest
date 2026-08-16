# Phase 3 — Text-to-SQL

> Status: **in progress** · Target: ask a question in natural language about
> structured personal data (tasks, projects, calendar, contacts, and the
> document metadata Phase 2 extracted) and get back an answer grounded in a
> query that is provably safe before it ever reaches PostgreSQL.

---

## 1. Goal

One additional pipeline, routed to from the same entry point as chat:

`question -> route (RAG or SQL) -> [SQL path] introspect schema -> generate
SQL as structured output -> validate by parsing, not by pattern-matching ->
execute read-only, time-boxed, row-capped -> format the result set back into
natural language`

The load-bearing idea of this phase is not "can an LLM write SQL" -- it
reliably can. It's that **nothing downstream of the LLM trusts it**. Every
security control in this document is written to hold even if every other
control in this document has already failed. That framing drove every
decision below, not just the ones under "Security."

---

## 2. Scope

### In scope

- A relational schema for personal productivity data (contacts, projects,
  tasks, calendar events), cross-referenced to the `documents` table Phase 2
  already introduced (a task can cite the document it came from)
- Schema introspection at runtime, restricted to an explicit allowlist,
  feeding the SQL-generation prompt
- SQL generation as structured output (forced tool call), never free text
  parsed out of prose
- A dedicated, unprivileged PostgreSQL role for executing generated queries
- AST-based validation of every generated query before execution
- Mandatory statement timeout and row limit on every execution
- Routing between the RAG pipeline (Phase 1) and the SQL pipeline (this
  phase) for an incoming question
- Formatting the result set back into a natural-language answer
- Unit tests specifically targeting injection and malicious-query attempts,
  not just the happy path

### Out of scope -- deliberately

| Gap | Why it matters | Deferred to |
|---|---|---|
| Write access (INSERT/UPDATE/DELETE) via natural language | A fundamentally different risk profile -- read-only is the only version of this feature that ships without a human-in-the-loop confirmation step | Later iteration, if ever |
| Multi-statement / multi-question SQL sessions (follow-up "narrow that down") | No SQL-specific conversational state; Phase 2's chat memory is not wired into the SQL path yet | Later iteration |
| Query result caching | Every question re-executes; fine at personal-data scale | Later iteration |
| Per-column data masking (e.g. partially redacting emails) | Allowlisting is table/column granularity, not cell-level | Later iteration |
| Cost-based query complexity limits (e.g. rejecting expensive joins before running them) | `statement_timeout` is the backstop instead -- simpler, still effective | Later iteration |

---

## 3. Decisions

### 3.1 Schema -- personal productivity data, cross-referenced to documents

`contacts`, `projects`, `tasks`, `calendar_events`, plus read access to the
existing `documents` table (title, type, date range, extracted tags/entities
jsonb). `tasks.source_document_id` and `calendar_events.related_contact_id`
give the schema enough real structure to make joins meaningful ("which tasks
came from documents tagged 'legal'?") without inventing a domain disconnected
from what Almagest already stores.

Rejected: a schema unrelated to the existing `documents` table. Rejected
because the whole point of one assistant across RAG and SQL is that the two
pipelines see the same personal corpus -- a disconnected demo schema would
make Phase 3 a tech demo bolted onto the project rather than a second way of
asking questions about the same data.

### 3.2 SQL generation -- structured output, forced tool call, same mechanism as Phase 2 metadata extraction

Exactly the reasoning in `02-memory.md` §3.3: a forced tool call constrains
*generation*, not just the parsing step afterward. The tool's schema requires
a single SQL string. Nothing about "structured output" is treated as a
security control here -- see §3.4. It's a reliability improvement (the model
reliably produces one parseable statement instead of prose-wrapped SQL) that
happens to also be a prerequisite for the real security control, which is
parsing that statement for real.

### 3.3 Schema introspection -- runtime, but pre-filtered to the allowlist before the model ever sees it

The prompt is built from `information_schema.columns`/`information_schema.tables`,
queried at runtime (so schema drift doesn't require redeploying a hand-written
description) -- but the introspection query itself is scoped to the
allowlisted tables (§3.5), not the full schema. The model never learns that
`document_chunks`, `sessions`, or `messages` exist. This is deliberate:
minimizing what the model knows reduces both attack surface (it can't target
a table it doesn't know is there) and hallucination (it can't invent a
plausible-looking reference to something real but off-limits).

### 3.4 Security -- five independent layers, each written as if the others failed

This is the section the request asked to be treated as a requirement, not a
detail. Each layer below is designed to hold on its own. None of them assumes
an earlier layer worked correctly.

**Layer 1 -- constrained generation (reliability, not security).**
Forced tool call, §3.2. Explicitly *not* trusted for safety -- it only
reduces how often later layers have to reject something.

**Layer 2 -- allowlist, enforced twice, in two different systems.**
An explicit list of tables and columns lives in configuration (not scattered
across code). It's enforced once when building the introspection prompt
(§3.3 -- the model never sees anything else) and independently again during
AST validation (§3.6 -- even if a prompt-injected or hallucinated reference
to an off-list table showed up in generated SQL anyway, validation rejects
it on its own, without relying on the model having only seen the allowlist).

**Layer 3 -- AST-based validation, not pattern matching.**
Every generated statement is parsed with the actual PostgreSQL grammar
(§3.6) before it is treated as SQL at all. Regex/string-matching approaches
to "is this query safe" are a well-known losing game (quoting, whitespace,
comments, and encoding all give an attacker -- or an LLM's own creative
phrasing -- ways to slip past a pattern). Parsing into a real syntax tree and
walking it is the only version of this check that reasons about what the
query *is* rather than what it superficially *looks like*.

**Layer 4 -- a database role that structurally cannot do anything else.**
A dedicated PostgreSQL role, granted `SELECT` on the allowlisted tables and
nothing else -- no `INSERT`/`UPDATE`/`DELETE`/`DDL`, no grants on
`document_chunks`, `sessions`, or `messages` at all. This layer holds even if
Layers 2 and 3 both had a bug: Postgres itself refuses anything the role
isn't granted, independent of anything the application decided. Executed via
`SET LOCAL ROLE` inside a transaction (§3.7) so the restriction can never
leak into or out of a pooled connection.

**Layer 5 -- bounded blast radius at execution time.**
`SET LOCAL statement_timeout` and a mandatory, server-enforced row limit
(§3.8) bound the cost of a query that every earlier layer failed to catch --
runaway joins, expensive functions, anything that's syntactically a valid
`SELECT` against allowlisted tables but still expensive or a denial-of-service
vector. The transaction always ends in `ROLLBACK`, never `COMMIT`, even
though every allowed statement is read-only -- one more independent
guarantee that nothing persists even if a data-modifying statement somehow
reached execution.

### 3.5 The allowlist itself

```
contacts:         id, name, email, phone, created_at
projects:         id, name, status, created_at
tasks:             id, project_id, source_document_id, title, status, due_date, created_at
calendar_events:  id, title, starts_at, ends_at, location, related_contact_id, created_at
documents:        id, title, document_type, document_date_start, document_date_end, extracted_metadata, created_at
```

`document_chunks` (raw chunk text and embeddings), `sessions`, and `messages`
are never allowlisted. Chunk text can contain full excerpts of ingested
documents -- exposing it through SQL would route around the RAG pipeline's
grounding/citation framing entirely, which isn't what this feature is for.
Conversation history is excluded for the same reason plus a smaller attack
surface: one less place a generated query could touch.

### 3.6 AST validation -- what gets checked and why

Library: `pgsqlparser` (NuGet id `pgsqlparser`, exact version pinned),
a .NET wrapper around `libpg_query` -- the actual PostgreSQL server parser,
compiled as a standalone library and exposed as a protobuf-backed AST. This
is not a SQL-flavored regex engine and not a different database's grammar
(T-SQL parsers, for instance, would silently mis-parse Postgres-specific
syntax) -- it is PostgreSQL's own grammar, so "parses" and "is valid
PostgreSQL" mean the same thing.

Checks, all against the parsed tree:

1. **Exactly one statement.** The library's statement-splitting exposes this
   directly. Reject anything else outright -- multi-statement input is
   exactly the shape of a classic SQL injection payload
   (`SELECT ...; DROP TABLE ...`).
2. **Top-level statement is `SELECT`.** Any `Insert`/`Update`/`Delete`/DDL/
   `Copy`/`Vacuum` node is rejected before it's ever near a connection.
3. **Every CTE is also `SELECT`-only, recursively.** PostgreSQL allows
   data-modifying statements inside a `WITH` clause
   (`WITH x AS (DELETE FROM ... RETURNING ...) SELECT * FROM x`) -- a
   `SELECT`-shaped query at the top level can still smuggle a write. The
   walk is recursive for exactly this reason, not a check of the outermost
   node only.
4. **No comment tokens.** Checked via the library's tokenizer/scanner, not
   the parsed AST (comments are discarded during parsing, so their absence
   from the AST proves nothing -- they have to be checked before or during
   tokenization). Comments are a standard vector for hiding or truncating
   query content from naive validation.
5. **Every referenced table is on the allowlist** (§3.5).
6. **Every referenced column is on the allowlist for the table it's
   qualified against.** Known limitation: for an *unqualified* column
   reference in a multi-table query, the check falls back to "is this column
   name allowlisted for *any* table referenced in the query" rather than
   resolving which specific table it binds to -- full binding resolution is
   real semantic analysis, not just tree-walking, and wasn't justified for
   this phase. Recorded honestly below, not glossed over.
7. **Every function call is on a small allowlist** of read-only, non-volatile
   functions (aggregates, date/string helpers). Blocks both information-
   disclosure functions and cheap denial-of-service ones (`pg_sleep` and
   similar) as a defense-in-depth measure independent of Layer 4's role
   grants.
8. **A `LIMIT` is present and within the configured maximum**, or one is
   appended server-side before execution if the model omitted it. The
   appended value is a fixed, configuration-sourced integer, never
   string-built from anything the model produced -- so this step can't
   reintroduce the class of bug it exists to prevent.

Any failed check rejects the query outright with a specific reason (surfaced
in logs, not to the end user as raw SQL detail) -- there is no partial or
best-effort execution path.

### 3.7 Execution -- a role switch that cannot outlive its transaction

```sql
BEGIN;
SET LOCAL ROLE almagest_readonly;
SET LOCAL statement_timeout = '5s';
<validated query>
ROLLBACK;
```

`SET LOCAL` (not `SET`) is transaction-scoped: it reverts automatically at
`COMMIT` or `ROLLBACK`, regardless of which. This matters specifically
because the connection comes from a pool (`NpgsqlDataSource`, shared with
every other query in the app) -- a plain `SET ROLE` without `LOCAL` would
risk a later, unrelated request reusing the same pooled connection while
still running as the restricted role (a correctness bug, not a security
hole, but a confusing one to debug). `SET LOCAL` removes that failure mode
by construction rather than by discipline ("remember to reset it").

The role itself: `CREATE ROLE almagest_readonly NOLOGIN;` -- it never
accepts a direct connection or needs a password. The application's own login
role is granted membership (`GRANT almagest_readonly TO ...`) so it can
switch into it for exactly the duration of one transaction.

Rejected: a second, separately-credentialed database connection/user for the
read-only role. Rejected because it doubles the secrets to manage
(connection string, password, rotation) for a guarantee `SET LOCAL ROLE`
already provides without any additional credential.

### 3.8 Retrieval parameters (SQL path)

| Parameter | Value | Rationale |
|---|---|---|
| Max rows | 200 | Personal-data scale; large enough to be useful, small enough that a runaway `SELECT *` can't return an unbounded result |
| Statement timeout | 5s | Generous for indexed lookups over a personal dataset, tight enough to bound a pathological join |
| Introspection cache | none (re-queried per request) | Simplicity over the marginal cost -- personal-data schema size makes this cheap |

### 3.9 Routing -- RAG or SQL, decided per question, not per session

An incoming question is classified before either pipeline runs -- a forced
tool call again (§3.2's mechanism, reused), not a keyword heuristic, since
"how many tasks are overdue" and "what does my contract say about overdue
payments" are lexically similar but need different pipelines entirely. This
is Application-layer retrieval logic in the same sense Phase 1's similarity
floor was -- hand-written, drafted here only as an explicit,
individually-approved proposal during implementation.

Rejected: a single pipeline that always tries both and merges results.
Rejected as needless cost and latency for the common case where a question
is clearly one or the other; nothing in scope needs a hybrid answer that
draws on both structured data and document excerpts simultaneously.

### 3.10 Result formatting -- natural language, with the query shown, not hidden

The formatting step (structured rows -> prose) is context assembly and
prompt construction in the same sense as Phase 1/2's grounding prompts --
hand-written, gated the same way. The one product decision recorded here
because it's a security-adjacent one, not a phrasing one: the executed SQL
is always returned alongside the natural-language answer, not hidden from
the response. A user (or, in review, this document's audience) should never
have to trust a prose summary of a query they can't see.

---

## 4. Architecture

```
Domain            Contact, Project, Task, CalendarEvent -- ordinary entities,
                  same invariant style as Document/DocumentChunk. No AI, no
                  SQL, no knowledge that a query engine exists.

Application       Ports:      ISchemaProvider, ISqlGenerator, ISqlValidator,
                              ISqlExecutor, IQueryRouter
                  UseCases:   AskDataQuestionUseCase (SQL path), routing
                              extension ahead of AskQuestionUseCase/ChatUseCase
                  Owns:       the routing decision, the SQL-generation prompt,
                              the result-formatting prompt -- hand-written,
                              same as Phase 1/2's equivalent pieces.

Infrastructure    PostgresSchemaProvider (introspection, allowlist-filtered),
                  ClaudeSqlGenerator (forced tool call), PgAstSqlValidator
                  (pgsqlparser-backed, the five-layer checks in §3.6),
                  PostgresReadOnlySqlExecutor (SET LOCAL ROLE + statement_timeout
                  + mandatory LIMIT + always-ROLLBACK, §3.7).

Api               Extends the chat/ask surface with routing; no new public
                  endpoint shape, the SQL path is reached through the same
                  entry point as RAG.

Tests             Injection/malicious-query attempts as first-class test
                  cases, not an afterthought bolted onto the happy path --
                  multi-statement payloads, comment smuggling, data-modifying
                  CTEs, off-allowlist tables/columns/functions, missing and
                  oversized LIMIT.
```

---

## 5. Definition of done

- [ ] A question about structured personal data is routed to SQL, not RAG,
      and vice versa, correctly for a representative set of questions
- [ ] Generated SQL is always produced as a forced structured output, never
      parsed out of free text
- [ ] Every generated query is parsed with the real PostgreSQL grammar
      before execution; unparseable or multi-statement input is rejected
- [ ] Non-`SELECT` statements are rejected, including data-modifying
      statements hidden inside a CTE
- [ ] Table and column references are checked against an explicit allowlist
      independent of what the model was shown
- [ ] Comment tokens are rejected via tokenization, not string matching
- [ ] Execution always runs under a dedicated, `SELECT`-only PostgreSQL role
      switched into via `SET LOCAL ROLE`, inside a transaction that always
      ends in `ROLLBACK`
- [ ] Every execution has a statement timeout and a server-enforced row cap
- [ ] Unit tests cover multi-statement injection, comment smuggling,
      data-modifying CTEs, off-allowlist tables/columns/functions, and
      missing/oversized `LIMIT` -- not just a successful query
- [ ] No secret in `appsettings.json` (carried forward)

---

## 6. Interview questions this phase must answer

1. Why validate by parsing instead of by pattern-matching or keyword
   blocklists?
2. Walk through what happens if the AST validator has a bug that lets a
   `DELETE` through. What stops it?
3. Why `SET LOCAL ROLE` instead of a second database credential?
4. How does a data-modifying CTE bypass a check that only looks at the
   top-level statement type, and how does this design defend against it?
5. Why is the introspected schema pre-filtered to the allowlist instead of
   showing the model everything and relying on the allowlist only at
   validation time?
6. What's the actual gap in the column-allowlist check for unqualified
   references, and what would closing it fully require?
7. Why route per-question instead of letting the user pick RAG or SQL
   explicitly, or trying both every time?
