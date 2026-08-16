# 3. Text-to-SQL: five independent security layers, each assuming the others failed

## Status

Accepted (Phase 3). Automated end-to-end by Phase 5's integration tests
(`tests/Almagest.IntegrationTests/ReadOnlyRoleTests.cs`,
`SqlExecutionPipelineTests.cs`), which run the real layers against a real
Postgres role instead of a manual `psql` check.

## Context

Letting an LLM generate SQL against personal data (contacts, tasks,
calendar events, projects) that then actually executes is a real injection
surface: prompt injection, model hallucination, or a subtly wrong query
could read data outside the intended scope or attempt to modify it. This
was explicitly called out as a first-class requirement, not an
implementation detail, with the instruction to treat each defensive layer
as independent — assume every other layer has already failed.

## Decision

Five layers, each sufficient on its own to stop the class of failure it
targets:

1. **Constrained generation** — SQL is produced as a forced tool call
   (structured output), never free text. Reliability, not safety: reduces
   how often later layers have to reject something, but is not trusted
   for security by itself.
2. **Allowlist, enforced twice, in two different systems** — an explicit
   table/column allowlist filters what the schema-introspection prompt
   even shows the model, and is independently re-checked during AST
   validation, so a hallucinated or injected off-list reference is caught
   even if the model somehow saw it.
3. **AST-based validation** — every generated statement is parsed with the
   real PostgreSQL grammar and the syntax tree is walked, not
   regex/string-matched. Pattern matching against SQL text is a
   well-documented losing game (quoting, whitespace, comments, encoding);
   parsing into a real tree reasons about what the query *is*.
4. **A database role that structurally cannot do anything else** — a
   dedicated Postgres role granted `SELECT` on the allowlisted tables and
   nothing else, applied via `SET LOCAL ROLE` inside the transaction so it
   can never leak across a pooled connection. Holds even if layers 2 and 3
   both have a bug, because Postgres itself enforces it, independent of
   application logic.
5. **Bounded blast radius at execution time** — `SET LOCAL
   statement_timeout` and a mandatory row limit bound the cost of anything
   that slipped past every earlier layer; the transaction always ends in
   `ROLLBACK`, never `COMMIT`, even though every allowed statement is
   read-only.

## Consequences

- No single bug anywhere in the pipeline is sufficient to cause data
  exposure or modification outside the allowlisted, read-only scope.
- The security design costs real implementation and maintenance surface:
  an AST parser dependency (`pgsqlparser`), a second database role to
  provision and keep in sync with the allowlist, and tests that exercise
  actual Postgres grants rather than mocking them.
- Named, not hidden, gap: there's no automated check that the `SqlAllowlist`
  constant in code and the migration's `GRANT` statements haven't drifted
  apart from each other beyond what the integration suite happens to
  exercise (see `docs/phases/05-production.md` §7).

## Rejected alternatives

- **Trusting constrained generation alone.** Rejected: a forced tool call
  makes the model *usually* emit well-formed, in-scope SQL — it does not
  make that a security guarantee, since nothing prevents a sufficiently
  adversarial prompt or model error from producing a schema-valid but
  out-of-scope query.
- **Regex/string-pattern validation instead of AST parsing.** Rejected as
  a well-known losing game against quoting, whitespace, comments, and
  encoding tricks.
- **A single application-level allowlist check with no database-level
  enforcement.** Rejected: if the application-layer check has a bug, there
  is nothing else stopping the query — the database role exists precisely
  so a bug in *this* layer isn't the only thing standing between a bad
  query and real data.

## Related

[`docs/phases/03-text-to-sql.md`](../phases/03-text-to-sql.md) §3.4-§3.7.
