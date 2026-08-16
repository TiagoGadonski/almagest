# 2. PostgreSQL + pgvector over a dedicated vector database

## Status

Accepted (Phase 1). Reinforced by Phase 3, which independently needs
relational Postgres for personal productivity data.

## Context

RAG needs approximate nearest-neighbor search over embedding vectors.
Purpose-built vector databases (Pinecone, Qdrant, Weaviate, Milvus) exist
specifically for this. Almagest also needs a conventional relational store
for conversation history (Phase 2) and personal data like contacts, tasks,
and calendar events (Phase 3).

## Decision

Store vectors in PostgreSQL using the `pgvector` extension, with an HNSW
index, in the same database as every other relational table in the
project — not a separate vector database.

## Consequences

- One engine, one connection pool, one transaction boundary, one container
  in `docker-compose.yml`. A single `INSERT` can write a document row and
  its chunk rows (with embeddings) in the same transaction.
- Phase 3's text-to-SQL security design (allowlist, AST validation, a
  dedicated read-only role) applies uniformly across relational and vector
  tables — no second security model for a second datastore.
- HNSW recall/latency tuning is less mature and less documented than in a
  dedicated vector engine at very large scale — not a concern at
  personal-document-corpus scale, but a real ceiling if this project ever
  needed to index millions of chunks.
- Embedding-model migrations (e.g., moving from Voyage's 1024-dimension
  output to a different model) require a schema change (`VECTOR(N)` is
  fixed-width) — mitigated by storing an `embedding_model_id` per chunk so
  mixed-model data is detectable rather than silently wrong (see Phase 5
  §3.2 for why this also makes the ONNX fallback's different dimension
  safe).

## Rejected alternatives

- **A dedicated vector database alongside PostgreSQL.** Rejected: two
  datastores means two failure modes, two backup strategies, and
  cross-store consistency to reason about (a chunk row in Postgres
  referencing a vector in another system that might be out of sync) — for
  a project that also needs full relational capability (Phase 3) in the
  same request path.
- **A dedicated vector database instead of PostgreSQL entirely** (moving
  relational data into it too). Rejected: these products are not general
  relational databases; Phase 3's schema, foreign keys, and read-only-role
  security design need real PostgreSQL semantics.

## Related

[`docs/phases/01-rag.md`](../phases/01-rag.md) §3.4;
[`docs/phases/05-production.md`](../phases/05-production.md) §3.2 (the
384-vs-1024 dimension non-issue for local embeddings).
