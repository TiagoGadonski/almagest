# Phase 2 — Memory

> Status: **in progress** · Target: conversations that remember themselves
> without either forgetting silently (truncation) or growing without bound,
> and documents that carry structured, filterable metadata instead of being
> opaque blobs of chunks.

---

## 1. Goal

Two additions on top of Phase 1's ingest/ask pipelines, plus one change to
each:

**Conversation memory (new)** `message -> persist -> [window exceeded? ->
summarize] -> assemble context -> stream answer -> persist`

**Structured metadata (extends ingestion)** `parse -> chunk -> embed ->
extract metadata (schema-validated) -> persist chunks + metadata`

**Filtered retrieval (extends ask/chat)** `question (+ optional filters) ->
embed -> filtered top-K search -> ...` (unchanged from Phase 1 past this
point)

Conversation memory and structured metadata are unrelated capabilities that
happen to land in the same phase because both are, at heart, the same
problem: bounding what the model sees. One bounds it across turns of a
conversation, the other bounds it at the corpus level so retrieval doesn't
have to be purely semantic.

---

## 2. Scope

### In scope

- Session and message persistence in PostgreSQL, surviving process restarts
- Context-window management by summarization, not truncation, when history
  exceeds a configured size
- A `documents` table (closing the Phase 1 gap where `document_id` had no
  metadata home) holding structured, schema-validated metadata extracted at
  ingest time: title, document type, tags, dates, cited entities
- Vector search filterable by that metadata (document type, tags, date range)
- Streaming responses on the conversational endpoint
- Unit tests for summarization behavior and for metadata extraction's
  validation/retry/degrade path, all against faked ports

### Out of scope — deliberately

| Gap | Why it matters | Deferred to |
|---|---|---|
| Vector-backed long-term memory (embedding old turns for recall) | The summary is lossy by construction; embedding-based recall would let specific old details resurface on demand | Later iteration |
| Summary quality metrics / regression detection | No way to know if a summary silently dropped something load-bearing | Phase 5, with retrieval metrics |
| Cross-document entity resolution | Cited entities are stored per document with no linking across documents (same person/org named differently in two files stays two facts) | Later iteration |
| Editing or deleting sessions/messages via the API | No such endpoints yet -- persistence is write-and-read only | Later iteration |
| Multi-user session isolation | Still single-user by design, same as Phase 1 | -- |

---

## 3. Decisions

### 3.1 Conversation storage — PostgreSQL, not Redis or in-memory

Same reasoning as Phase 1's choice of pgvector over a dedicated vector
database: one engine, one transaction boundary, one container. A session's
messages are relational, ordered, small per-row data -- exactly what
PostgreSQL is for. Redis would add a second store and a second failure mode
to reason about, to buy expiry semantics this project doesn't need yet
(nothing here is a cache; conversation history is the product).

Rejected: in-memory (`ConcurrentDictionary`-backed) session store. Rejected
because it loses every conversation on restart, which defeats the point of
"memory" as a phase name.

### 3.2 Context window management — summarize, never truncate

Truncation is silent data loss with no recovery path: the moment a message
scrolls off the window, whatever it said is gone, and neither the user nor
the model can tell that anything is missing. Summarization is lossy too, but
it is *legibly* lossy -- a summary is a visible, inspectable artifact, and
the raw messages it was built from are never deleted, only excluded from the
active context window.

Mechanism: each session tracks how far its running summary extends
(`summarized_through_position`). When the active window (messages after that
cutoff) exceeds a configured size, the oldest messages in that window are
folded into an updated summary via an LLM call, and the cutoff advances.
Each turn's context is then `summary (if any) + messages after the cutoff`,
never the full history.

The exact trigger threshold (message count vs. token estimate) and the
summarization prompt are context-assembly and prompt-construction concerns
-- per `Claude.md`, drafted here only as an explicit, individually-approved
proposal during implementation, not decided unilaterally in this document.

Rejected: sliding-window truncation. Rejected for the data-loss reason above.
Rejected: summarizing on every turn regardless of size. Rejected as wasted
LLM calls for conversations that never grow past a few messages -- summarize
lazily, only when the window is actually exceeded.

### 3.3 Structured metadata extraction -- forced tool calls, not prompted JSON

Anthropic has no "JSON mode" the way some providers do. The documented,
reliable way to get schema-conforming structured output from Claude is tool
calling: define the target JSON Schema as a tool's input schema, force
`tool_choice` to that specific tool, and read the arguments Claude passes to
it. This constrains *generation itself* -- the model is decoding into a
schema-shaped slot, not free text that has to be located and parsed out of a
prose response (no markdown fences to strip, no "here is the JSON:" preamble
to skip, no truncated output mid-brace).

`Microsoft.Extensions.AI` already carries this: an `AIFunction` describes the
schema, `ChatOptions.ToolMode` forces that specific tool, and the response's
function-call content carries the (already-parsed) arguments.

Rejected: prompting for raw JSON in the response text and regex/substring-
extracting it. Strictly worse than tool calling -- trades one failure mode
(schema nonconformance) for two (schema nonconformance *and* unparseable
text), for no offsetting benefit.

Rejected: `ChatOptions.ResponseFormat.ForJsonSchema(...)` (the
provider-agnostic structured-output path in `Microsoft.Extensions.AI`).
Rejected because whether the Anthropic adapter actually routes this into
forced tool calling under the hood isn't something this document is willing
to assert without reading that adapter's source -- the explicit,
documented-for-Anthropic mechanism (forced tool call) is used directly
instead of trusting an abstraction layer's translation of a feature the
underlying provider doesn't natively have.

### 3.4 Guaranteeing schema-valid JSON, and what happens when it isn't

This is the question the request explicitly asked to have answered, so it
gets its own numbered decision rather than being folded into 3.3.

Forced tool calling makes schema-conformant output *highly likely*, not
*guaranteed*. LLM tool-call argument generation is schema-guided, not a hard
grammar constraint enforced by a validator on Anthropic's side -- an enum
value can still land outside its allowed set, a field the schema calls
required can still come back missing, nesting can still be subtly wrong.
Trusting "the SDK deserialized it without throwing" is not the same claim as
"this conforms to the schema": `System.Text.Json` deserialization only
checks that the JSON is well-formed and roughly shape-compatible with the
target C# type -- it does not check required-ness, enum membership, string
formats, or array bounds the way a real JSON Schema validator does.

The mechanism, in order:

1. **Constrain generation** -- forced tool call against the schema (3.3).
   Eliminates the most common failure modes by construction.
2. **Validate independently, every time** -- the tool-call arguments are run
   through an actual JSON Schema validator (`JsonSchema.Net`, exact version
   pinned per the preview-package convention) against the *same* schema
   document used to define the tool. One schema, defined once, used both to
   constrain generation and to check the result -- no second, hand-maintained
   copy of "what a valid payload looks like" that could drift from the first.
3. **On failure, repair once** -- the validation errors are fed back to the
   model in a follow-up message ("the previous output failed validation:
   `<errors>` -- fix and resubmit") and the tool call is retried a single
   time. This is a well-established, cheap repair strategy for structured
   output and resolves the majority of near-misses (a stray extra property,
   a date in the wrong format).
4. **On repeated failure, degrade, don't fail the ingestion** -- metadata is
   an enrichment, not a precondition for a document's chunks to exist and be
   searchable. If the retry also fails validation, ingestion proceeds with no
   metadata for that document (columns left null, no row written to a
   metadata table) rather than throwing and losing the whole ingested
   document. This is logged/flagged so a document silently missing metadata
   is discoverable, not silently wrong -- the same "explicit failure over
   silent improvisation" stance Phase 1 took for "not found in your
   documents."

Rejected: accepting the tool-call arguments unvalidated. Rejected because
step 1 reduces but does not eliminate nonconformance, and a `jsonb` column
enforces no schema of its own -- the boundary check is the only checkpoint
that exists between "what the model said" and "what's on disk."
Rejected: failing the entire ingestion when metadata extraction fails.
Rejected because it would make document ingestion's reliability a function of
metadata-extraction reliability, when the two are independent concerns with
very different risk profiles.

### 3.5 Metadata storage -- a `documents` table, typed columns plus a JSONB catch-all

Phase 1 explicitly deferred a `documents` table (`document_id` was a bare
grouping key with no metadata home -- see its Known limitations). This phase
closes that gap, because metadata now needs somewhere to live that supports
filtering.

`document_type` and a date range are named, explicit filter targets in scope
-- they get first-class typed columns (`document_type text`, plus indexed
date columns) so filtering is a normal `WHERE` clause, not a query into
opaque JSON. `tags` and `cited_entities` are open-ended lists without a
fixed vocabulary; they get a `jsonb` column (`extracted_metadata`) rather
than a join table, since there's no current requirement to query "which
documents share this exact tag" efficiently at scale -- if that need shows
up, it promotes to a proper table then.

Rejected: metadata as pure `jsonb` with no typed columns at all. Rejected
because `document_type` and date-range filtering are explicit, named scope
items here, and filtering inside opaque JSON without extracted columns (or a
per-key index) is exactly the kind of thing that's slow and awkward for no
reason when the fields are already known upfront.

### 3.6 Filtering vector search -- `WHERE` alongside the ANN order-by, not a separate service

pgvector supports combining an ordinary `WHERE` clause with an ANN
`ORDER BY ... <=> ...` query; recent pgvector versions (this project already
runs `0.8.6`, confirmed when Phase 1's schema was verified) support
iterative index scans so a filtered query doesn't have to fall back to a
full sequential scan to satisfy a selective filter. Filtering stays a single
SQL statement in `PgVectorChunkStore`.

Rejected: a separate metadata-filtering pass in the application layer (fetch
top-K unfiltered, then discard non-matching results). Rejected because it
makes `topK` a lie whenever the filter is selective -- a document-type filter
that excludes most of the corpus could return zero usable results from a
top-5 unfiltered search even though matching chunks exist further down the
ranking. Filtering has to happen inside the search, not after it.

### 3.7 Streaming -- extend `IChatService`, don't bypass it

`IChatClient.GetStreamingResponseAsync` already exists in the chain proven
in `Almagest.Lab` (Phase 1 only used the non-streaming path). `IChatService`
gains a streaming member returning `IAsyncEnumerable<string>`; the
conversational endpoint (`POST /chat`) writes those increments to the client
as Server-Sent Events. The full response is still assembled and persisted as
one message once streaming completes -- streaming changes delivery, not what
gets stored.

Rejected: a second port (`IStreamingChatService`) parallel to `IChatService`.
Rejected as an arbitrary split of one capability (talking to Claude) across
two contracts for no consumer that needs only one half.

---

## 4. Architecture

```
Domain            Session, Message, MessageRole, DocumentMetadata.
                  No AI dependency, same as Document/DocumentChunk: a
                  session knows it has messages and a summary cutoff, a
                  message knows its role and text -- neither knows what
                  streaming or tool-calling is.

Application       New ports:    IConversationStore, IMetadataExtractor
                  Extended:     IChatService (+ streaming), IChunkStore
                                (+ metadata filter on SearchAsync)
                  New use case: ChatUseCase (session-aware, streaming,
                                triggers summarization when the window is
                                exceeded)
                  Extended:     IngestDocumentUseCase (+ metadata
                                extraction and persistence step)
                  Owns:         summarization trigger/context assembly,
                                the summarization and extraction prompts,
                                the metadata-filtered retrieval logic --
                                same as Phase 1's AskQuestionUseCase, these
                                are hand-written per Claude.md and only
                                drafted here under explicit per-piece
                                approval.

Infrastructure    PostgresConversationStore, ClaudeMetadataExtractor
                  (forced tool call + JsonSchema.Net validation + one
                  repair retry), extended ClaudeChatService (streaming),
                  extended PgVectorChunkStore (metadata filter).

Api               New: POST /chat (SSE streaming, session-aware).
                  Extended: POST /documents response now includes
                  extracted metadata (or a flag that extraction failed).

Tests             Summarization trigger and metadata extraction's
                  validate/retry/degrade path, fully faked -- no network,
                  no database, same testability argument as Phase 1.
```

---

## 5. Definition of done

- [ ] `POST /chat` streams a session-aware, grounded answer over SSE
- [ ] Session and message history persist in PostgreSQL and survive a
      process restart
- [ ] History beyond the configured window is summarized, not dropped; the
      summary plus the messages after its cutoff feed every subsequent turn
- [ ] `POST /documents` extracts title/type/tags/dates/cited-entities via a
      forced tool call against a JSON Schema
- [ ] Extracted metadata is validated against that schema before being
      persisted; on failure, one repair retry; on repeated failure,
      ingestion proceeds without metadata rather than failing
- [ ] Vector search accepts an optional metadata filter (document type,
      tags, date range) applied inside the SQL query, not after it
- [ ] Unit tests: summarization trigger/behavior with faked ports
- [ ] Unit tests: metadata extraction's valid, invalid-then-repaired, and
      repair-also-fails paths, with a faked chat service
- [ ] No secret in `appsettings.json` (carried forward from Phase 1)

---

## 6. Interview questions this phase must answer

1. Why summarize instead of truncate? What's lost either way, and to whom is
   the difference visible?
2. How do you guarantee extracted JSON conforms to its schema, and what
   happens when it doesn't?
3. Why keep raw messages in the database after they're folded into a
   summary instead of deleting them?
4. Why filter vector search with a `WHERE` clause in the same query instead
   of filtering the results afterward in application code?
5. What happens to a streamed response if the client disconnects mid-stream?
   What, if anything, gets persisted?
6. Why does metadata extraction go through the same `IChatService` port as
   conversation and grounding, instead of a separate structured-output port?
7. A user asks about something mentioned 40 messages ago, now folded into
   the summary rather than present verbatim. What can go wrong, and how
   would you notice?
