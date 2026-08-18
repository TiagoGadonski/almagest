# Almagest

**Live: [https://almagest.fly.dev](https://almagest.fly.dev)** — API on
Fly.io (region `gru`, scales to zero machines when idle), database on
Neon's free tier with `pgvector`. Neon over Fly's own Managed Postgres for
cost: Fly MPG starts at USD 38/month, not justified for a single-user
portfolio project. CI (`.github/workflows/ci.yml`) passed on GitHub's own
runners on its first execution.

`GET /` serves interactive Swagger UI (via Swashbuckle), not a 404 —
deliberate, and deliberately on in every environment including Production,
not just `Development`. There's no other landing page for this API, and a
bare `curl`-only demo felt like a worse first impression for anyone
clicking the link above than a browsable, documented API surface. The real
cost of that choice: `/documents`, `/agent`, and `/agent/approve` have
"Try it out" enabled by default, which makes it one click, not a crafted
`curl` command, to ingest a real file (real Voyage/Anthropic cost) or
approve a side-effecting agent action (writes to the production database).
Each of those endpoints says so directly in its Swagger description —
named there, not just here.

Ingests your PDFs and Markdown files, answers questions about them with
per-claim citations and a similarity score, and answers questions against
your own structured data (contacts, tasks, calendar) by generating SQL
under five layers of validation. Solo project, .NET 8, Clean Architecture.

---

## Demo

Asking a real question against the live deployment (question, translated:
*"What is a smart city, and what fields of knowledge contribute to it?"*):

```bash
curl -X POST https://almagest.fly.dev/ask -H "Content-Type: application/json" \
  -d '{"question":"O que e uma cidade inteligente e quais areas do conhecimento contribuem para isso?"}'
```

Real response, captured from the live deployment while writing this README:

```json
{
  "route": "rag",
  "found": true,
  "answer": "# O que é uma Cidade Inteligente e Áreas do Conhecimento Contribuintes\n\n## Definição de Cidade Inteligente\n\nSegundo os excertos, uma cidade inteligente vai além de apenas tecnologia. [chunk:2a9a8e42...] define uma cidade inteligente como \"aquela capaz de combinar tecnologia, planejamento urbano, sustentabilidade, governança e participação social para resolver problemas concretos.\" [...] ## Áreas do Conhecimento Contribuintes\n\n- Engenharia\n- Arquitetura\n- Ciência de dados\n- Administração pública\n- Economia\n- Psicologia ambiental\n- Políticas públicas",
  "citations": [
    { "chunkId": "2a9a8e42-e251-4436-8088-bbb636ea4821", "sectionTitle": "Cidades Inteligentes e Sustentáveis: Tecnologia, Mobilidade, Energia e Qualidade de Vida", "similarity": 0.6459229203508555 },
    { "chunkId": "0ca4c259-a888-4dcf-bc15-871bd74a1bfb", "sectionTitle": "Privacidade e proteção de dados", "similarity": 0.6204343068340845 },
    { "chunkId": "a29f155d-b5fc-4f55-b885-e8d7c1eddd35", "sectionTitle": "Considerações finais", "similarity": 0.6071799216514874 }
  ]
}
```

(Two more citations at similarity 0.60 and 0.59 omitted for length.) The
scores are identical to the ones this same question produced against a
separately-ingested local copy of the same source document — embeddings
are deterministic, so the same text under the same model lands on the same
similarity regardless of which database stores it. Chunk and document IDs
differ because it's a different (Neon, not local) database.

A question with nothing in the corpus, against the same live deployment:

```bash
curl -X POST https://almagest.fly.dev/ask -H "Content-Type: application/json" \
  -d '{"question":"Qual a receita de feijoada?"}'
```

```json
{"route":"rag","answer":"I couldn't find anything in your documents that answers this question.","found":false,"citations":[]}
```

Ingesting a new document — this part only makes sense run locally, not
against the shared public deployment:

```bash
curl -X POST localhost:8080/documents -F "file=@cidades_inteligentes_rag.md"
```

Real state after ingestion, queried from the database: this file produced
5 chunks, one of 7 documents / 17 chunks in the local corpus (the live
deployment's Neon database was seeded with the same documents separately).

## Run it in three commands

```bash
docker compose up -d --build
curl -X POST localhost:8080/documents -F "file=@yourfile.pdf"
curl -X POST localhost:8080/ask -H "Content-Type: application/json" -d '{"question":"..."}'
```

One prerequisite before these three: `cp .env.example .env` and fill in
`ANTHROPIC_API_KEY` and `VOYAGE_API_KEY`. There's no mocked "demo mode" —
`/documents` and `/ask` call real Anthropic and Voyage endpoints, so real
credentials are required from the first request.

## Architecture

```
Api             minimal API, composition root
                POST /documents · POST /ask · POST /chat
                POST /agent · POST /agent/approve · GET /health
                     │
                     ▼
Application     use cases + ports only — no I/O, no AI SDK, no database driver
                     │
                     ▼
Infrastructure  one adapter per port — every external dependency lives here:
                  Voyage AI (embeddings) · ONNX runtime (offline fallback)
                  Anthropic Claude, via IChatClient (chat/SQL-gen/routing)
                  Microsoft Agent Framework (the tool-calling agent loop)
                  PostgreSQL + pgvector (all persistence)
                     │
                     ▼
Domain          entities only — Document, DocumentChunk, Contact, Task, ...
                no AI dependency, no database dependency
```

Dependencies point inward only. `Application` references `Domain` plus
`Microsoft.Extensions.AI`'s abstractions — never a concrete Anthropic,
Npgsql, or agent-framework type. That boundary is not aspirational: when
the Anthropic client package was swapped mid-project (see below), the
change touched zero files in `Application`.

## Technical decisions

**Chunking: a complete paragraph and a fragment of one are not the same
thing.** The first version of the chunker never merged text across
paragraph breaks — correct for keeping topics separate, except it turned
any document made of short paragraphs into one chunk per paragraph, each
carrying almost no context. Removing that rule and merging everything
freely fixed that, and broke the opposite thing: unrelated paragraphs from
different sections started sharing a chunk. The fix was recognizing these
are different failure modes needing different rules — a whole paragraph
merges freely with its neighbors, but a fragment produced by splitting an
oversized paragraph only merges with another fragment of that same
paragraph. Separately, the token budget reserves the overlap *before*
packing chunks (`budget = target - target*overlapRatio`), not after —
computing overlap on top of a full-size chunk would silently exceed the
configured target size. Rejected: fixed-size splitting (cuts mid-sentence
regardless of structure) and semantic splitting (an extra model call per
document, not justified at this scale). Details: [`docs/phases/01-rag.md` §3.3](docs/phases/01-rag.md).

**Embeddings are asymmetric, and treating them as symmetric broke
retrieval silently.** Voyage trains separate encoding modes for the text
being indexed versus the text used to search it. The first implementation
sent every embedding request — indexing and querying alike — with
`input_type: "document"`. It didn't error. It just returned wrong answers:
every real question scored below the similarity floor, and `/ask` reported
"not found" regardless of whether the document existed. Confirmed with a
temporary diagnostic log before touching any code: real question, real
corpus, top candidate at 0.50 similarity against a 0.70 floor. Fixed by
adding `EmbeddingPurpose` (`Document` or `Query`) to the `IEmbeddingService`
port — an enum, not a raw string, so the port stays provider-agnostic;
`VoyageEmbeddingService` translates it to `input_type`, `OnnxEmbeddingService`
ignores it because MiniLM has no asymmetric mode and says so in a comment.
Rejected: a raw string parameter mirroring Voyage's own vocabulary directly
in the port — would leak a provider-specific concept across the
Infrastructure boundary for no benefit. No dedicated ADR yet; see the
`EmbeddingPurpose` doc comment in
[`IEmbeddingService.cs`](src/Almagest.Application/Ports/IEmbeddingService.cs)
and the README history below.

**PostgreSQL + pgvector, not a dedicated vector database.** The
text-to-SQL phase needs relational Postgres regardless — contacts,
projects, tasks, calendar events are ordinary rows, not vectors. Running
both in one engine means one transaction boundary (a document row and its
chunk embeddings commit together) and one container to operate, instead of
two datastores with two failure modes and a consistency problem between
them. Rejected: a dedicated vector database (Pinecone, Qdrant, Weaviate) —
faster ANN tuning at very large scale, a cost this project doesn't pay at
personal-document-corpus size and wouldn't recoup given the second engine
it would add. [ADR 2](docs/adr/0002-pgvector-over-dedicated-vector-database.md).

**Text-to-SQL: five layers, one design intent, one honest gap.** Generated
SQL passes through: forced-tool-call structured output (reliability, not
security), a table/column allowlist enforced twice (in the introspection
prompt and independently again during validation), AST-based validation
against the real PostgreSQL grammar (not regex — a well-documented losing
game against quoting and encoding tricks), a dedicated Postgres role with
`SELECT`-only grants applied via `SET LOCAL ROLE`, and bounded execution
(`statement_timeout`, mandatory `LIMIT`, always `ROLLBACK`). Each layer is
*designed* to hold if every other layer already failed. That is a design
intent, not a verified property — the integration suite exercises all five
layers working together against a real Postgres role, which is real
evidence the stack works end to end, but nothing yet disables layers one at
a time to prove each one holds alone. Rejected: trusting constrained
generation by itself (a forced tool call makes well-formed SQL *likely*,
not safe), and regex/pattern validation instead of parsing a real syntax
tree. [ADR 3](docs/adr/0003-five-layer-text-to-sql-security.md).

**Clean Architecture: `Application` cannot name an AI SDK, and that's the
whole point.** Every use case depends on ports (`IChatService`,
`IEmbeddingService`, `ISqlExecutor`, ...) that `Application` itself
declares; every concrete client — the official `Anthropic` package,
`Npgsql`, `Microsoft.Agents.AI`, ONNX Runtime — lives in `Infrastructure`
and nowhere else. This is what makes 94 unit tests run against hand-written
fakes with zero network calls and zero database. It also survived a real
test: this project's Anthropic client started on the community `Anthropic.SDK`
package, hit a `MissingMethodException` against a real account, and moved
to Anthropic's own official package once one existed — a change confined
entirely to `Infrastructure`. Rejected: passing a concrete SDK client
straight into a use case to avoid writing an interface — cheaper on day
one, and it would have made that same SDK swap a change to every use case
that touched chat, not a change to one adapter. No dedicated ADR; the rule
itself is `Claude.md`'s standing constraint, exercised across every phase
doc in `docs/phases/`.

## How the similarity floor was calibrated

`SimilarityFloor` is 0.45, not the 0.70 it launched with. 0.70 was a guess,
written before this system had ever embedded a real document. After fixing
the asymmetric-embedding bug above, real questions against the real,
ingested 17-chunk corpus scored 0.59–0.67 when the answer was genuinely
present, and at most 0.40 when it wasn't. 0.45 sits in that gap.

That sample was small — a handful of manually-run questions, not a
measured recall curve — which is the reason `tests/Almagest.Eval` exists:
an eval harness that reports recall@5 and keyword-match accuracy
mechanically, no LLM judge. It has since completed a real run — see
[Evaluation](#evaluation) below for the numbers.

## Evaluation

```
recall@5: 100% (14/14)
accuracy: 57% (8/14)
```

Real numbers from a real run of `dotnet run --project tests/Almagest.Eval`,
against the 14 real questions in
[`tests/eval/questions.md`](tests/eval/questions.md), on the local
17-chunk corpus. Required context, not softened:

- **Recall is optimistic by construction.** The corpus is 17 chunks, so
  top-5 retrieval covers roughly a third of everything indexed. This
  validates that the pipeline works end to end; it is not a retrieval
  benchmark.
- **Of the 6 accuracy misses, 4 are correct answers expressed in different
  words than the expected fact stems** — a limit of substring matching,
  not a retrieval failure. **The other 2** are chunks that made it into the
  top-5 but scored below the 0.45 confidence floor before generation, so
  `AskQuestionUseCase` returned "not found" instead of an answer.
- **The expected facts in `tests/eval/questions.md` were not adjusted
  after seeing the generated answers.** These are the numbers from the
  first completed run, not a curve-fit result.

## Known limitations

**Found and fixed by actually running the system** — real ingestion and
real `/ask` calls against 7 documents/17 chunks surfaced two bugs no test
had caught, both fixed as of this writing:

- Query embeddings were sent with `input_type: "document"` instead of
  `"query"` (see the embeddings decision above) — every `/ask` call
  returned "not found" regardless of whether the answer existed.
- `VoyageEmbeddingService` sent every chunk in a single HTTP request,
  regardless of size — fine at 17-chunk scale, would exceed Voyage's
  per-request token ceiling on a larger document. Fixed with batching
  under a configurable token/text-count ceiling, verified against Voyage's
  published API limits, not guessed.

**Open:**

| Gap | Why it matters | Next step |
|---|---|---|
| Swagger UI's "Try it out" is enabled by default on every endpoint, including `/documents`, `/agent`, and `/agent/approve` | One click ingests a real file (real Voyage/Anthropic cost) or approves a side-effecting agent action (writes to the production database) — no `curl` command, no confirmation beyond the agent's own approval step, needed | Disable "Try it out" for those three specifically, or gate them behind a warning interstitial |
| Retrieval parameters (`SimilarityFloor` etc.) are duplicated across composition roots | `Almagest.Api/Program.cs` and `Almagest.Eval/Program.cs` each hardcode their own `RetrievalOptions`, with no shared source of truth. The eval harness silently measured a different configuration than the one the API served (floor 0.70 vs. 0.45) until this was discovered by reading its own diagnostic output | Later iteration |
| `Almagest.Api` and `Almagest.Eval` read different environment variables for the database connection string | `Almagest.Api` accepts both `ALMAGEST_CONNECTION_STRING` and `DATABASE_URL` (translating the `postgres://` URI form automatically). `Almagest.Eval` only reads `ALMAGEST_CONNECTION_STRING` — pointing it at a Fly/Neon-style `postgres://` URL requires reformatting it by hand first | Later iteration |
| Rate limiting is sized for a single burst, not an account-wide quota | Retry-with-backoff smooths over one request's transient 429s; nothing tracks the account's overall RPM/TPM budget across concurrent requests | Later iteration |
| No validation that extracted text is legible before embedding | A corrupted PDF extraction (garbled OCR, wrong encoding) is chunked and embedded as-is | Later iteration |
| No PII or secrets detection at ingestion | Documents are embedded and stored as given, no scan first | Later iteration |
| No document deletion path; no `ON DELETE CASCADE` on the chunk foreign key | Removing a document today means deleting rows by hand in the right order | Later iteration |
| Token counts are approximated by whitespace-delimited words, not a real tokenizer | Chunk sizing, context budgeting, and cost estimates all sit on this approximation | Later iteration |
| No per-layer fault-injection test for the text-to-SQL security design | See the defense-in-depth decision above — the gap is named there, not hidden here | Later iteration |
| No automated drift check between the SQL allowlist constant and migration `GRANT`s | Integration tests exercise the grants that happen to be tested, not a full diff | Later iteration |
| No reranking, hybrid search, or query rewriting | Deliberately deferred since the first phase | Later iteration |
| Eval accuracy grading is substring/keyword matching, no LLM judge | A coarse proxy for correctness; an LLM-judge upgrade needs its own individually-approved prompt | Later iteration |
| OpenTelemetry's token-cost table is a hardcoded $/million-token constant | Not fed by a live pricing source, will drift out of date | Later iteration |
| `Almagest.Infrastructure`'s test coverage is not measured | The CI coverage gate is scoped to `Almagest.Application` only; Infrastructure — where the SQL security logic and Postgres role-switching live — has no measured number | Later iteration |
| The Fly machine hibernates when idle (`min_machines_running = 0`) | The first request after a period of inactivity takes several seconds while Fly starts the machine back up | Accepted tradeoff — cost savings for a single-user portfolio project |
| No load or performance testing | Nothing in scope needs it yet at personal-data scale | Later iteration |
| No multi-statement SQL sessions ("narrow that down") | Chat memory isn't wired into the SQL path | Later iteration |
| Only note/reminder creation, no edit/delete | Update/delete is a larger blast radius per side-effecting action | Later iteration |
| Single-user throughout | Every phase scoped this way by design | Later iteration, if ever |

## Next steps

- Extract `RetrievalOptions` (and the connection-string resolution) into
  configuration shared between `Almagest.Api` and `Almagest.Eval`, so the
  two composition roots can't silently drift apart the way `SimilarityFloor`
  just did — see [Known limitations](#known-limitations).
- Investigate the 2 genuine "not found" misses from the [Evaluation](#evaluation)
  run (top-5 chunks that scored below the 0.45 floor) — is the floor still
  slightly high, or is that specific content under-represented in the
  corpus?
- Write the per-layer fault-injection test for the text-to-SQL security
  stack (disable each layer in turn, confirm the others still catch the
  attack) — the gap named twice above.
- `docs/deployment/first-deploy.md` still describes the deploy as
  hypothetical ("not executed from this environment") — now stale, since
  the deploy above is real. Needs a pass to reconcile it with what actually
  happened.

---

Further reading: phase-by-phase scope and rejected alternatives in
[`docs/phases/`](docs/phases/), five short ADRs in
[`docs/adr/`](docs/adr/), deployment commands and CI secrets in
[`docs/deployment/`](docs/deployment/).
