# Phase 1 — Baseline RAG

> Status: **in progress** · Target: ingest documents, ask questions in natural
> language, get answers grounded in the indexed content with source attribution.

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

### 3.5 LLM access — Anthropic.SDK → IChatClient → Semantic Kernel

No first-party Anthropic connector exists for Semantic Kernel in .NET; the
Microsoft team pointed at `Microsoft.Extensions.AI` as the path forward. The
chain is:

```
Anthropic.SDK  →  IChatClient  →  AsChatCompletionService()  →  Kernel
```

Layer responsibilities:

- `Microsoft.Extensions.AI` — low-level abstractions (`IChatClient`,
  `IEmbeddingGenerator`). The `ILogger` of the AI stack.
- `Microsoft.Extensions.VectorData` — vector store abstractions.
- `Semantic Kernel` — orchestration: prompt templates, plugins, agents. Earns
  its place in Phase 4, not here.

Application depends only on the abstractions. SK, Anthropic.SDK and Npgsql are
confined to Infrastructure.

`max_tokens` is **required** by the Anthropic API — omitting it returns an
opaque 400. Set as a global default in `ConfigureOptions`.

### 3.6 Retrieval parameters

| Parameter | Value | Rationale |
|---|---|---|
| Top-K | 5 | High K injects noise; low K misses the answer |
| Similarity floor | 0.70 | Below this, answer "not found" rather than improvise |
| Max context tokens | 4000 | Bounds cost and keeps the prompt inside the useful attention window |

Both are configuration, tuned against the question set, not constants buried in
code.

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

- [ ] `POST /documents` indexes a PDF and a Markdown file end to end
- [ ] `POST /ask` returns an answer citing document and chunk
- [ ] A question with no supporting content returns an explicit "not found"
- [ ] Embedding model id stored per chunk; mismatch detected at query time
- [ ] Unit tests: chunker (boundaries, overlap, empty input, oversized token)
- [ ] Unit tests: both use cases with faked ports
- [ ] `docker compose up -d` brings up PostgreSQL + pgvector
- [ ] No secret in `appsettings.json`
- [ ] README documents the decisions above and the known limitations table

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