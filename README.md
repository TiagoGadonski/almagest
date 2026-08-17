# Almagest

Ingests your PDFs and Markdown files, answers questions about them with
per-claim citations and a similarity score, and answers questions against
your own structured data (contacts, tasks, calendar) by generating SQL
under five layers of validation. Solo project, .NET 8, Clean Architecture.

---

## Demo

Ingesting a document from the current test corpus:

```bash
curl -X POST localhost:8080/documents -F "file=@cidades_inteligentes_rag.md"
```

Real state after ingestion, queried from the database: this file produced
5 chunks (document id `e256b150-eb91-48f9-b892-f5d274a9b76f`), one of 7
documents / 17 chunks currently indexed.

Asking a question against it (question, translated: *"What is a smart
city, and what fields of knowledge contribute to it?"*):

```bash
curl -X POST localhost:8080/ask -H "Content-Type: application/json" \
  -d '{"question":"O que e uma cidade inteligente e quais areas do conhecimento contribuem para isso?"}'
```

Real response, captured while writing this README:

```json
{
  "route": "rag",
  "found": true,
  "answer": "# O que é uma Cidade Inteligente e Suas Áreas de Conhecimento\n\n## Definição de Cidade Inteligente\n\nUma cidade inteligente é aquela que combina **tecnologia, planejamento urbano, sustentabilidade, governança e participação social** para resolver problemas concretos [chunk:57697a2b-dbce-4f78-9007-816dcbd3e881]. [...] ## Áreas do Conhecimento que Contribuem\n\n- Engenharia\n- Arquitetura\n- Ciência de dados\n- Administração pública\n- Economia\n- Psicologia ambiental\n- Políticas públicas [chunk:57697a2b-dbce-4f78-9007-816dcbd3e881]",
  "citations": [
    { "chunkId": "57697a2b-dbce-4f78-9007-816dcbd3e881", "sectionTitle": "Cidades Inteligentes e Sustentáveis: Tecnologia, Mobilidade, Energia e Qualidade de Vida", "similarity": 0.6459229203508555 },
    { "chunkId": "622c9fd9-93f5-48a4-942d-8fc4ed901b2d", "sectionTitle": "Privacidade e proteção de dados", "similarity": 0.6204343068340845 },
    { "chunkId": "a006e99e-4ee3-409c-a268-ea767de036f3", "sectionTitle": "Considerações finais", "similarity": 0.6071799216514874 }
  ]
}
```

(Two more citations at similarity 0.60 and 0.59 omitted here for length.)
A question with no support in the corpus returns `"found": false` and an
explicit "I couldn't find anything in your documents that answers this
question" instead of an improvised answer.

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
and nowhere else. This is what makes 88 unit tests run against hand-written
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

That sample is small — a handful of manually-run questions, not a measured
recall curve — and it's the reason `tests/Almagest.Eval` exists: an eval
harness that reports recall@5 and keyword-match accuracy mechanically, no
LLM judge. It has not completed a run yet — the free-tier API plan's rate
limit was hit before a full pass finished. No recall or accuracy number is
reported anywhere in this repo. The number this section actually gives you
is the raw similarity range above; treat it as a starting point pending a
real eval run, not a benchmark result.

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
| No live GitHub Actions run observed | No push access from this environment | First push |
| No live Fly.io deployment | No cloud credentials in this environment; deploying is a real-cost action needing the project owner present | The project owner |
| No load or performance testing | Nothing in scope needs it yet at personal-data scale | Later iteration |
| No multi-statement SQL sessions ("narrow that down") | Chat memory isn't wired into the SQL path | Later iteration |
| Only note/reminder creation, no edit/delete | Update/delete is a larger blast radius per side-effecting action | Later iteration |
| Single-user throughout | Every phase scoped this way by design | Later iteration, if ever |

## Next steps

- Rewrite `tests/eval/questions.md` against the real ingested corpus and
  run `Almagest.Eval` to completion once past the rate limit — first real
  recall@5/accuracy numbers.
- Run the first real Fly.io deploy per
  [`docs/deployment/first-deploy.md`](docs/deployment/first-deploy.md);
  that document names exactly what's unverified about it.
- Push to GitHub and observe the first real CI run
  (`.github/workflows/ci.yml`) — reviewed and passes `actionlint` and a
  local `docker build`, never executed by GitHub itself.
- Write the per-layer fault-injection test for the text-to-SQL security
  stack (disable each layer in turn, confirm the others still catch the
  attack) — the gap named twice above.

---

Further reading: phase-by-phase scope and rejected alternatives in
[`docs/phases/`](docs/phases/), five short ADRs in
[`docs/adr/`](docs/adr/), deployment commands and CI secrets in
[`docs/deployment/`](docs/deployment/).
