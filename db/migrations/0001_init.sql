-- Phase 1 baseline schema: pgvector extension + the chunk store.
--
-- Runs automatically on first container start via Postgres's
-- docker-entrypoint-initdb.d mechanism (only against an empty data volume).
-- Not a versioned migration tool -- see README "Known limitations".

CREATE EXTENSION IF NOT EXISTS vector;

-- gen_random_uuid() is built into Postgres core since 13, no extra extension needed.

CREATE TABLE document_chunks (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    document_id         UUID NOT NULL,
    chunk_text          TEXT NOT NULL,
    chunk_position      INTEGER NOT NULL,
    section_title       TEXT,
    embedding           VECTOR(1024) NOT NULL,
    embedding_model_id  TEXT NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Approximate nearest-neighbour index for cosine similarity search.
-- HNSW over IVFFlat: better recall and query latency, at the cost of a
-- slower build and more memory -- the right trade-off at this data scale.
CREATE INDEX document_chunks_embedding_hnsw_idx
    ON document_chunks
    USING hnsw (embedding vector_cosine_ops);

-- Supports grouping/filtering chunks by their source document.
CREATE INDEX document_chunks_document_id_idx ON document_chunks (document_id);
