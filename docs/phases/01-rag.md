# Phase 1 — Baseline RAG

> Status: **implemented, verified against a real corpus** · ingest documents,
> ask questions in natural language, get answers grounded in the indexed
> content with source attribution.

---

## 1. Goal

Two independent pipelines sharing one vector store.

**Indexing (offline, once per document)**
`upload → parse → chunk → embed → persist`

**Querying (online, once per question)**
`question → embed → top-K search → assemble context → LLM → cited answer`

The two have different latency budgets, different cost profiles and different
test strategies. They are modelled as two separate use cases, not one service.

---

## 2. Scope

### In scope

- Ingest `.pdf` and `.md`
- Recursive chunking with overlap
- Vector storage in PostgreSQL + pgvector
- Top-K similarity search with a relevance floor
- Grounded answer generation via Claude, with per-claim source references
- Explicit "not found in your documents" path when nothing clears the floor
- Unit tests for the chunker and for the two use cases (ports faked)

### Out of scope — deliberately

These are known gaps, documented in the README as *Known limitations*, not
oversights:

| Gap | Why it matters | Deferred to |
|---|---|---|
| Reranking | Cosine similarity ≠ relevance | Later iteration |
| Hybrid search | Vector search is weak on exact tokens (IDs, rare proper nouns) | Later iteration |
| Query rewriting | Poorly phrased questions retrieve poor context | Later iteration |
| Retrieval metrics (recall@k, MRR) | No way to know if quality regressed | Phase 5 |
| Streaming responses | UX only, no architectural impact | Phase 2 |
| Multi-tenant isolation | Single-user by design for now | — |

---

## 3. Decisions

### 3.1 Retrieval strategy — RAG over fine-tuning or full-context

Fine-tuning teaches style and format far better than it teaches facts, and any
new document would require retraining. Stuffing every document into the prompt
breaks down on cost, latency and attention degradation over long contexts.

RAG converts a model-memory problem into a search problem. Search is tractable
engineering. It also yields provenance, which fine-tuning cannot.

### 3.2 Embeddings — Voyage AI

Anthropic ships no embedding model and points to Voyage AI as its recommended
provider. Chosen model: `voyage-4` family, default output dimension **1024**.

> Confirm the exact model id and default dimension against
> https://docs.voyageai.com/docs/embeddings before writing the migration —
> the lineup moves faster than this document.

No official .NET SDK exists, so Infrastructure holds a hand-written
`HttpClient` adapter implementing `IEmbeddingGenerator<string, Embedding<float>>`
from `Microsoft.Extensions.AI`. This is a feature, not a workaround: the
abstraction is the contract, the provider is a detail.

**Consequence to respect:** the dimension is a typed column (`vector(1024)`).
Changing the embedding model invalidates every stored vector. Treat it as a data
migration with a reindex, never as a config toggle. The model id is persisted
alongside each chunk so a mismatch is detectable at query time.

Rejected alternatives:

- **OpenAI `text-embedding-3-small`** — lower friction, first-class .NET support,
  but breaks the single-vendor narrative and teaches less.
- **Local ONNX model** — no API key, no cost, fully deterministic. Lower quality,
  but planned as a *second implementation* of the same port so integration tests
  can run in CI without secrets (Phase 5).

### 3.3 Chunking — recursive, ~800 tokens, 12% overlap

A whole document as a single vector averages its meaning into noise. Chunk size
is the central trade-off: small chunks give precise vectors but lose surrounding
context; large chunks preserve context but dilute the vector and pad the prompt.

Splitter respects structure in descending order: paragraph → sentence → word.
Overlap exists so a sentence landing on a boundary keeps context from both sides.

Chunking is where most RAG quality is won or lost — more than the choice of LLM.
The numbers above are a starting point to be tuned against a fixed question set,
not a conclusion.

Rejected: fixed-size splitting (cuts mid-sentence), semantic splitting (needs an
extra model call per document, not justified at this stage).

**Token counts are approximated by whitespace-delimited words, deliberately.**
This is not a shortcut waiting to be replaced — it's the same class of
decision as everywhere else this project approximates a token count (context
budgeting in `AskQuestionUseCase`/`ChatUseCase` uses an identical
chars-per-token estimate), made once instead of pulling in a tokenizer
dependency for a number that only needs to be roughly right: chunk sizing
tolerates being off by 10-20%, and a real tokenizer would add a dependency
and a per-document cost for a precision this step doesn't need. It remains a
known limitation in the sense that the number can drift from what the actual
embedding/chat model's tokenizer would report, not in the sense that it was
an oversight.

**Overlap is reserved out of the budget up front, not added after chunks are
built.** `budget = TargetTokens - (TargetTokens * OverlapRatio)`, and pieces
are packed against that reduced budget; the overlap words are prefixed onto
each chunk afterward. Computing the overlap first and then packing to the
full `TargetTokens` would let `budget + overlap` exceed the configured chunk
size — the whole point of a target size — so the reservation has to happen
before packing, not as a post-hoc addition.

**Whole paragraphs merge freely with neighbours; fragments of an oversized
paragraph only merge with fragments of the same paragraph.** The first
version never merged across paragraph breaks, which turned short-paragraph
documents into one chunk per paragraph. Removing the rule entirely then
allowed unrelated paragraphs to share a chunk. The distinction that resolves
both is between a complete unit and a fragment of one — caught by a test
written before the implementation.

**Section-title attribution is a linear scan over document headings, not a
binary search.** Each chunk is tagged with the last heading at or before its
offset. Headings are ordered by offset already (the parsers produce them that
way), so a binary search would be the standard move at scale — rejected here
because a document's heading count is small enough (tens, not thousands) that
the simpler linear scan doesn't cost anything measurable, and a binary search
over a list this short would just be more code proving the same point.

### 3.4 Vector store — PostgreSQL + pgvector, HNSW index

Exact nearest-neighbour search is O(n) and does not scale. pgvector offers two
approximate indexes: HNSW (better recall, faster queries, slower build, more
memory) and IVFFlat (fast build, smaller, needs populated data to tune well).
HNSW is the default here.

pgvector over a dedicated vector database because Phase 3 (text-to-SQL) requires
relational PostgreSQL anyway. One engine, one transaction boundary, one
container. The defensible reason is the operational one, not a feature list.

Docker image must be `pgvector/pgvector:pg16` — plain `postgres` lacks the
extension. `CREATE EXTENSION vector` runs as a migration.

### 3.5 LLM access — originally Anthropic.SDK → IChatClient → Semantic Kernel; both legs since replaced

**As originally decided (Phase 1):** no first-party Anthropic connector
existed for Semantic Kernel in .NET, and Anthropic itself had not yet
published an official .NET package, so the plan was the community
`Anthropic.SDK` NuGet package (unofficial, third-party-maintained) bridged
through `IChatClient` into Semantic Kernel:

```
Anthropic.SDK  →  IChatClient  →  AsChatCompletionService()  →  Kernel
```

**What actually happened, running this against a real account:** the
community `Anthropic.SDK` package threw a `MissingMethodException` at
runtime on the call path this project depends on
(`AsIChatClient()`/`GetResponseAsync`) — a version-surface mismatch between
what that package shipped and what was being called against it. By this
point Anthropic had published its own official .NET package (NuGet package
id `Anthropic`, distinct from the community `Anthropic.SDK`), which
implements `Microsoft.Extensions.AI`'s `IChatClient` adapter directly and
does not have this problem. The project migrated to it —
`Almagest.Infrastructure.csproj` pins `Anthropic`, not `Anthropic.SDK`,
today. The lesson generalizes past this one package: a third-party wrapper
around a fast-moving provider API is a real risk category, the same
diligence this project already applies to picking `pgsqlparser` over
guessing and Microsoft's own ONNX/tokenizer packages over a niche wrapper
(Phase 5 §3.2) — an official package, once one exists, is preferred over a
community one filling the gap.

**Semantic Kernel itself was removed later, separately.** It was never more
than a bridge (`AsChatCompletionService()`) for the non-agentic chat/
summarization/grounding path — Phase 4 built the actual agent loop on
Microsoft Agent Framework instead of Semantic Kernel's plugin/planner
abstractions (see `docs/phases/04-agent.md` §3.1), and once that landed,
nothing in the shipped application still called through Semantic Kernel's
bridge. Phase 5 removed the `Microsoft.SemanticKernel` package reference
from `Almagest.Infrastructure` entirely; `ClaudeChatService` calls
`IChatClient.GetResponseAsync`/`GetStreamingResponseAsync` directly, the
same as every other Claude-calling class in the project. Semantic Kernel
remains only in `Almagest.Lab`, the throwaway console app used to verify
third-party APIs by reflection — not in the shipped application.

Layer responsibilities, as they stand today:

- `Microsoft.Extensions.AI` — low-level abstractions (`IChatClient`,
  `IEmbeddingGenerator`). The `ILogger` of the AI stack.
- The official `Anthropic` package — implements `IChatClient` against the
  real Anthropic API.
- `Microsoft.Agents.AI` (Microsoft Agent Framework) — the agent loop's
  orchestration layer (Phase 4), built on the same `IChatClient`. Not
  Semantic Kernel.

Application depends only on the abstractions. `Anthropic`, `Microsoft.Agents.AI`
and Npgsql are confined to Infrastructure.

`max_tokens` is **required** by the Anthropic API — omitting it returns an
opaque 400. Set as a global default in `ConfigureOptions`.

### 3.6 Retrieval parameters

| Parameter | Value | Rationale |
|---|---|---|
| Top-K | 5 | High K injects noise; low K misses the answer |
| Similarity floor | 0.45 (was 0.70) | Below this, answer "not found" rather than improvise |
| Max context tokens | 4000 | Bounds cost and keeps the prompt inside the useful attention window |

These are configuration, tuned against real output, not constants buried in
code. The similarity floor's original 0.70 was a guess made before this
system had ever embedded a real document — it was never validated against
`voyage-4`'s actual output range. Running real queries against a real,
ingested 7-document/17-chunk corpus (after fixing the `input_type` query/
document mismatch — see §3.5's sibling in `docs/phases/05-production.md`)
showed genuinely relevant matches scoring 0.59-0.67 and irrelevant ones
scoring up to ~0.40; 0.45 was chosen to sit below the relevant cluster (with
margin, since these were deliberately well-phrased test queries, not
natural ones) and above most of the irrelevant one. Based on a small manual
sample (2 relevant/3 irrelevant cases) — re-tune against
`tests/eval/questions.md`'s recall@5 once that harness has real coverage of
this corpus, not by further manual spot-checks.

### 3.7 Grounding

The system prompt states explicitly that answers must derive from the supplied
excerpts and that every claim carries its chunk identifier. Without this the
model blends parametric knowledge with retrieved content and provenance is lost.

Context assembly lives in **Application**, not Infrastructure: how context is
selected and framed is product logic, not a storage concern.

---

## 4. Architecture

```
Domain            Document, DocumentChunk, ChunkPosition, DocumentSource.
                  No AI dependency. A chunk knows it has text, an origin and a
                  position — it does not know what a vector is.

Application       Use cases:  IngestDocumentUseCase, AskQuestionUseCase
                  Ports:      IDocumentParser, ITextChunker, IEmbeddingService,
                              IChunkStore, IChatService
                  Owns retrieval logic and context assembly.

Infrastructure    VoyageEmbeddingService, PgVectorChunkStore, ClaudeChatService,
                  PdfDocumentParser, MarkdownDocumentParser,
                  RecursiveTextChunker.

Api               Minimal API: POST /documents, POST /ask

Lab               Console scratchpad for trying APIs without polluting the solution.

Tests             Application fully testable with faked ports — no network calls.
                  That testability is the argument for the architecture.
```

`IChunkStore` returns chunks **with their similarity score**, so Application can
apply the floor and decide what reaches the prompt.

---

## 5. Definition of done

- [x] `POST /documents` indexes a PDF and a Markdown file end to end —
      verified for real: 7 documents (PDF and Markdown) ingested, 17 chunks
      produced
- [x] `POST /ask` returns an answer citing document and chunk — verified for
      real, with a corrected similarity floor (§3.6)
- [x] A question with no supporting content returns an explicit "not found"
      — verified for real
- [ ] Embedding model id stored per chunk; mismatch detected at query time —
      implemented and unit-tested; not exercised against a real
      model-mismatch scenario
- [x] Unit tests: chunker (boundaries, overlap, empty input, oversized token)
- [x] Unit tests: both use cases with faked ports
- [x] `docker compose up -d` brings up PostgreSQL + pgvector
- [ ] No secret in `appsettings.json` — true by code review, not something a
      test enforces
- [x] README documents the decisions above and the known limitations table

---

## 6. Interview questions this phase must answer

Written in advance on purpose. If any of these is uncomfortable, that part is
not understood yet.

1. Why RAG instead of fine-tuning? What would change your mind?
2. What is an embedding, and why is cosine similarity the right metric?
3. Why 800 tokens and 12% overlap? How would you tune them?
4. What does HNSW do, and what does it trade away?
5. Where would this system fail today, and what would you fix first?
6. Why does the Application layer never reference Semantic Kernel?
7. A user searches for an exact invoice number and gets nothing. Why?