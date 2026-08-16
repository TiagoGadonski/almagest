# Almagest

<!-- Replace <owner>/almagest once this repo has a real GitHub remote --
     the workflow is written, reviewed, and passes actionlint + a local
     `docker build` (see docs/deployment/), but has never been observed
     running in GitHub's own runners, so the badge is honest about showing
     "no runs yet" until then. -->
[![CI](https://github.com/<owner>/almagest/actions/workflows/ci.yml/badge.svg)](https://github.com/<owner>/almagest/actions/workflows/ci.yml)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![License](https://img.shields.io/badge/license-unlicensed-lightgrey)
<!-- Swap for a green "live" badge linking the real https://<app>.fly.dev
     URL after the first real `fly deploy` (docs/deployment/first-deploy.md).
     Points at the deploy guide instead until then, on purpose -- a badge
     that quietly links nowhere real is worse than one that says so. -->
[![Deploy](https://img.shields.io/badge/deploy-not_yet_live-lightgrey)](docs/deployment/first-deploy.md)

A personal knowledge assistant over your own documents and data: ask a
question in natural language and get an answer grounded in your files
(RAG, with citations), your structured personal data (text-to-SQL, five-layer
defense in depth), or both — routed automatically, with a small
set of side-effecting tools (create a note, set a reminder) gated behind
explicit approval. Built as a portfolio project in .NET 8 Clean
Architecture, where every technical decision has to be defensible, not
just working.

**Deploy:** [`fly.toml`](fly.toml) targets Fly.io with Fly Managed Postgres
(`vector` extension). Exact first-deploy command sequence:
[`docs/deployment/first-deploy.md`](docs/deployment/first-deploy.md). Not
deployed live from this environment — see [Deploy](#deploy) below.

---

## What each phase added

| Phase | Capability | Doc |
|---|---|---|
| 1 — RAG | Ingest PDF/Markdown, chunk, embed, retrieve, answer with per-claim citations, explicit "not found" below a similarity floor | [`docs/phases/01-rag.md`](docs/phases/01-rag.md) |
| 2 — Memory | Persisted conversations, context-window summarization (not truncation), schema-validated structured metadata extraction at ingest, metadata-filtered retrieval, streaming chat | [`docs/phases/02-memory.md`](docs/phases/02-memory.md) |
| 3 — Text-to-SQL | Natural language over personal data (contacts/projects/tasks/calendar), SQL as structured output, five-layer defense in depth, RAG-vs-SQL routing | [`docs/phases/03-text-to-sql.md`](docs/phases/03-text-to-sql.md) |
| 4 — Agent | RAG and text-to-SQL become tools an agent chooses between; side-effecting tools (create note, set reminder) require explicit approval; bounded iteration loop | [`docs/phases/04-agent.md`](docs/phases/04-agent.md) |
| 5 — Production | Real-Postgres integration tests, offline embedding fallback, CI with a coverage gate, OpenTelemetry tracing, an eval harness, deployment manifest | [`docs/phases/05-production.md`](docs/phases/05-production.md) |

## Architecture

```
Api  (Almagest.Api — minimal API, composition root)
  POST /documents   ingest a PDF/Markdown file
  POST /ask         RAG: question -> retrieval -> grounded answer + citations
  POST /chat        multi-turn chat, session memory, streaming
  POST /agent       tool-calling agent turn (may pause for approval)
  POST /agent/approve  resume a paused agent turn
  GET  /health      checks real database connectivity, not just process liveness
       │
       ▼
Application  (Almagest.Application — no I/O, ports only, unit-tested with fakes)
  UseCases: IngestDocumentUseCase · AskQuestionUseCase · ChatUseCase
            AskDataQuestionUseCase · CreateNoteUseCase · SetReminderUseCase
  Ports:    IDocumentParser · ITextChunker · IEmbeddingService · IChunkStore
            IChatService · IConversationStore · IMetadataExtractor
            ISchemaProvider · ISqlGenerator · ISqlValidator · ISqlExecutor
            IQueryRouter · INoteStore · IReminderStore · IAgentService
       │
       ▼
Infrastructure  (Almagest.Infrastructure — one adapter per port, all I/O lives here)
  ├─ Pdf/MarkdownDocumentParser, RecursiveTextChunker
  ├─ VoyageEmbeddingService  ──────────────►  Voyage AI (embeddings)
  ├─ OnnxEmbeddingService    ──────────────►  local ONNX runtime (offline fallback)
  ├─ PgVectorChunkStore, PostgresConversationStore,
  │  PostgresReadOnlySqlExecutor, PostgresNoteStore, PostgresReminderStore
  │                          ──────────────►  PostgreSQL + pgvector
  ├─ PgAstSqlValidator                        AST-based allowlist enforcement
  ├─ ClaudeChatService, ClaudeMetadataExtractor,
  │  ClaudeSqlGenerator, ClaudeQueryRouter
  │                          ──────────────►  Anthropic Claude (via IChatClient)
  └─ AlmagestAgentService     ──────────────►  Microsoft Agent Framework
       │
       ▼
Domain  (Almagest.Domain — no AI or database dependency)
  Document, DocumentChunk, ConversationSession, Message, Contact, Project,
  Task, CalendarEvent, Note, Reminder
```

Dependencies point inward only: `Domain` references nothing; `Application`
references `Domain` plus `Microsoft.Extensions.AI` abstractions only; the
official `Anthropic` package, `Microsoft.Agents.AI`, `Npgsql`, and ONNX
Runtime are confined to `Infrastructure`. No concrete AI or database type
crosses the `Application` boundary.

The agent loop (`AlmagestAgentService`) runs on Microsoft Agent Framework
(`Microsoft.Agents.AI`), not Semantic Kernel. Semantic Kernel was Phase 1's
original choice for LLM access generally, before Microsoft Agent Framework
reached GA; Phase 5 removed it from `Almagest.Infrastructure` entirely —
`ClaudeChatService` calls `IChatClient` directly, like every other
Claude-calling class in the project. Semantic Kernel is retained only in
`Almagest.Lab`, a throwaway console app, not in the shipped application. See
[ADR 5](docs/adr/0005-microsoft-agent-framework-over-semantic-kernel.md).

## Run locally

```bash
cp .env.example .env            # fill in ANTHROPIC_API_KEY and VOYAGE_API_KEY
docker compose up -d --build    # Postgres+pgvector, schema applied, API built and started
curl localhost:8080/health      # -> ok
```

## Testing

```bash
dotnet test tests/Almagest.UnitTests               # fast, no I/O, fakes only
dotnet test tests/Almagest.IntegrationTests         # real Postgres via Testcontainers (needs Docker)
```

- **Unit tests** exercise use cases and Application-layer logic against
  hand-written fakes — no network, no database.
- **Integration tests** ([`tests/Almagest.IntegrationTests`](tests/Almagest.IntegrationTests))
  spin up real `pgvector/pgvector:pg16` via Testcontainers, apply the real
  migrations, and exercise `PgVectorChunkStore`, `PostgresConversationStore`,
  the `almagest_readonly` role's actual grants, and the AST validator against
  a live executor — persistence correctness, not model quality.
- **CI** ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs build,
  unit tests, integration tests, a `docker-build` job that builds the same
  `Dockerfile` Fly deploys from (nothing else in the workflow validates that
  the deployable image actually builds), and fails the build if
  `Almagest.Application` line coverage drops below 80% (measured baseline:
  89.2%). No secret is required for any job — see
  [`docs/deployment/ci-secrets.md`](docs/deployment/ci-secrets.md) for the
  one optional pair that avoids a real Docker Hub rate-limit risk on the
  integration-tests job. Passes `actionlint` and a local `docker build`;
  not yet observed running in GitHub's own runners (no push target from
  this environment).
- **Eval harness** ([`tests/Almagest.Eval`](tests/Almagest.Eval),
  [`tests/eval/questions.md`](tests/eval/questions.md)) reports recall@5 and
  keyword-match accuracy against `AskQuestionUseCase` — mechanical grading,
  no LLM judge. Its scoring logic is unit-tested against hand-built fixtures.
  Ingestion itself has been run for real (7 documents, 17 chunks, real Voyage
  and Claude calls, `/ask` returning grounded answers with citations) — but
  the harness script (`dotnet run --project tests/Almagest.Eval`) has not
  itself been executed end to end, and `tests/eval/questions.md` still holds
  placeholder rows that don't match the documents actually ingested. Real
  recall@5/accuracy numbers require rewriting `questions.md` against a real
  corpus and running the script for real — not yet done.

## Observability

Every LLM call (`ClaudeChatService`, `VoyageEmbeddingService`) and every
database round trip (`PgVectorChunkStore`, `PostgresReadOnlySqlExecutor`)
emits an OpenTelemetry span tagged with token usage, estimated cost (from a
hardcoded $/million-token table), row counts, and latency. A console
exporter is always on; set `OTEL_EXPORTER_OTLP_ENDPOINT` to also export to a
collector. Verified locally: spans and tags observed for real, both via the
console exporter and directly via `ActivityListener` in
[`tests/Almagest.IntegrationTests/TelemetryTests.cs`](tests/Almagest.IntegrationTests/TelemetryTests.cs).

## Deploy

[`fly.toml`](fly.toml) targets the existing multi-stage
[`Dockerfile`](Dockerfile) unchanged, with **Fly Managed Postgres (MPG)** as
the database — the same `api`/`db` split as `docker-compose.yml`, ported to
a host that runs it without operating a VM directly. Specifically MPG, not
Fly's older Postgres Flex product: Flex doesn't include `pgvector` without
a custom image, MPG supports it as a toggleable extension. `DATABASE_URL`
(the connection string `fly mpg attach` sets automatically) is read
directly by the app and translated internally
(`PostgresConnectionStringTranslator`) — no manual reformatting step.

Exact command sequence for the first deploy — creating the app, the
Postgres cluster, enabling `vector`, applying migrations, setting secrets,
deploying, and verifying it's actually up:
[`docs/deployment/first-deploy.md`](docs/deployment/first-deploy.md). Which
CI secrets are (and aren't) required: [`docs/deployment/ci-secrets.md`](docs/deployment/ci-secrets.md).

**Not deployed from this environment** — no Fly.io credentials here;
deploying is an action with real cost that needs the project owner present.
See [`docs/phases/05-production.md`](docs/phases/05-production.md) §3.6.

## Key decisions

- **RAG over fine-tuning or full-context stuffing** — turns a model-memory
  problem into a search problem, and yields provenance fine-tuning can't.
- **PostgreSQL + pgvector, not a dedicated vector database** — Phase 3
  needs relational Postgres anyway; one engine, one transaction boundary.
- **Structured output via forced tool calls, validated independently
  against JSON Schema** — every place a model must return structured data
  (metadata extraction, SQL generation), "it parsed" is never trusted as
  "it's valid."
- **Text-to-SQL: defense in depth across five layers** — constrained
  generation, allowlist, real-grammar AST validation, a dedicated
  unprivileged database role, bounded/rolled-back execution. Each layer is
  *designed* to hold if the others fail; that design intent has not yet been
  verified with a per-layer fault-injection test (each layer deliberately
  disabled in turn to confirm the others still catch the attack) — see
  [ADR 3](docs/adr/0003-five-layer-text-to-sql-security.md).
- **Microsoft Agent Framework, not Semantic Kernel, for the Phase 4 agent
  loop** — `ChatClientAgent` wraps the same `IChatClient` already in use;
  `ApprovalRequiredAIFunction` gives first-party human-in-the-loop gating
  instead of hand-rolling one.

Full rationale and rejected alternatives for every decision: the phase docs
linked above, and the five short ADRs in [`docs/adr/`](docs/adr/):

1. [RAG over fine-tuning](docs/adr/0001-rag-over-fine-tuning.md)
2. [pgvector over a dedicated vector database](docs/adr/0002-pgvector-over-dedicated-vector-database.md)
3. [Five-layer text-to-SQL security](docs/adr/0003-five-layer-text-to-sql-security.md)
4. [Forced-tool-call structured output, independently validated](docs/adr/0004-forced-tool-call-structured-output.md)
5. [Microsoft Agent Framework over Semantic Kernel](docs/adr/0005-microsoft-agent-framework-over-semantic-kernel.md)

## Known limitations and next steps

### Found and fixed by actually running the system

Real ingestion and real `/ask` calls against 7 documents/17 chunks surfaced
two bugs no test had caught. Both are fixed as of this writing — named here
because they were real, not because they're still open:

- **Query embeddings were sent with `input_type: "document"`** — the same
  value used for indexed chunks, instead of `"query"`. Voyage trains
  asymmetric query/document encoders; the mismatch silently degraded every
  similarity score, and every `/ask` call returned "not found" regardless of
  whether the answer was actually in the corpus. Fixed by adding an
  `EmbeddingPurpose` (`Document`/`Query`) parameter to `IEmbeddingService`,
  translated to Voyage's `input_type` by `VoyageEmbeddingService` and ignored
  (with a comment explaining why) by `OnnxEmbeddingService`, whose model has
  no asymmetric mode.
- **`VoyageEmbeddingService` sent every chunk in one HTTP request**,
  regardless of size — fine at the 17-chunk scale tested so far, but would
  exceed Voyage's per-request token ceiling on a larger document. Fixed by
  partitioning into batches under a configurable token/text-count ceiling
  (defaults verified against Voyage's published API reference, not guessed).

### Open

| Gap | Why it matters | Next step |
|---|---|---|
| Rate limiting is sized for a single burst, not an account-wide quota | `VoyageEmbeddingService`'s retry-with-backoff smooths over one request's transient 429s; nothing tracks or throttles against the account's overall RPM/TPM budget across concurrent requests | Later iteration |
| No validation that extracted text is legible before embedding | A corrupted PDF extraction (garbled OCR, wrong encoding) would be chunked and embedded as-is; nothing checks the text is plausible language first | Later iteration |
| No PII or secrets detection at ingestion | Documents are embedded and stored as given; no scan for SSNs, API keys, credentials, etc. before persisting | Later iteration |
| No document deletion path; no `ON DELETE CASCADE` on the chunk foreign key | Removing a document today means deleting rows by hand in the right order; there's no API endpoint or cascade | Later iteration |
| Token counts are approximated by whitespace-delimited words, not a real tokenizer | Chunk sizing, context-window budgeting, and cost estimates are all built on this approximation | Later iteration |
| No per-layer fault-injection test for the text-to-SQL security design | Each of the five layers (see [ADR 3](docs/adr/0003-five-layer-text-to-sql-security.md)) is *designed* to hold if the others fail; that design intent has only been exercised as a working stack in the integration suite, never with a layer deliberately disabled to prove the others still catch the attack | Later iteration |
| No automated drift check between the SQL allowlist constant and migration `GRANT`s | Integration tests exercise the grants that happen to be tested, not a full diff against `SqlAllowlist` | Later iteration |
| No reranking, hybrid search, or query rewriting | Deliberately deferred since Phase 1 | Later iteration |
| Eval accuracy grading is substring/keyword matching, no LLM judge | A coarse proxy for correctness; an LLM-judge upgrade needs its own individually-approved prompt | Later iteration |
| OpenTelemetry's token-cost table is a hardcoded $/million-token constant | Not fed by a live pricing source, will drift out of date | Later iteration |
| `Almagest.Infrastructure`'s test coverage is not measured | The CI coverage gate is scoped to `Almagest.Application` only (Phase 5 §3.3); Infrastructure — where the SQL security logic and Postgres role-switching live — has no measured number | Later iteration |
| No live GitHub Actions run observed | No push access from this environment | First push |
| No live Fly.io deployment | No cloud credentials here; deploying is a real-cost action needing the project owner present | The project owner |
| No load or performance testing | Nothing in scope needs it yet at personal-data scale | Later iteration |
| No multi-statement SQL sessions ("narrow that down") | Phase 2's chat memory isn't wired into the SQL path | Later iteration |
| Only note/reminder creation, no edit/delete | Update/delete is a larger blast radius per side-effecting action | Later iteration |
| Single-user throughout | Every phase scoped this way by design | Later iteration, if ever |

## Project structure

```
src/
  Almagest.Domain          entities, no dependencies
  Almagest.Application     use cases + ports, no I/O
  Almagest.Infrastructure  one adapter per port; all AI/DB/HTTP dependencies live here
  Almagest.Api             minimal API composition root
  Almagest.Lab             throwaway console app used to verify third-party APIs by reflection before depending on them
tests/
  Almagest.UnitTests         fast, fakes only
  Almagest.IntegrationTests  real Postgres via Testcontainers
  Almagest.Eval               eval harness (recall@5, accuracy)
  eval/questions.md            eval question set
docs/
  phases/0N-*.md            full scope, decisions, and rejected alternatives per phase
  adr/000N-*.md             five short architecture decision records
  deployment/               first-deploy.md (exact commands), ci-secrets.md
db/migrations/              versioned SQL, applied at container init
```
