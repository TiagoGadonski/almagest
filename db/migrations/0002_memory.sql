-- Phase 2: conversation memory + document metadata.
--
-- Runs automatically alongside 0001_init.sql via Postgres's
-- docker-entrypoint-initdb.d mechanism -- only applies to an empty data
-- volume. See README "Known limitations".

CREATE TABLE sessions (
    id                           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    summary                      TEXT,
    -- -1 means "nothing summarized yet"; every message position > this
    -- value is still part of the active (unsummarized) window.
    summarized_through_position  INTEGER NOT NULL DEFAULT -1,
    created_at                   TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_active_at               TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- role is plain text with a CHECK, not a native Postgres enum -- avoids
-- Npgsql enum-type-mapping entirely (reading/writing a checked text column
-- needs no driver-side registration, a native enum would).
CREATE TABLE messages (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id        UUID NOT NULL REFERENCES sessions (id),
    role              TEXT NOT NULL CHECK (role IN ('user', 'assistant', 'system')),
    content           TEXT NOT NULL,
    message_position  INTEGER NOT NULL,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX messages_session_id_position_idx ON messages (session_id, message_position);

-- Closes Phase 1's gap: document_id was a bare grouping key with no
-- metadata home. Typed columns for the named filter targets (type, date
-- range); a single jsonb catch-all for open-ended lists (tags, cited
-- entities) extracted at ingest time. NULL extracted_metadata means
-- extraction failed/degraded for that document -- not "extraction returned
-- nothing", which is a distinct, valid, non-null empty-ish payload.
CREATE TABLE documents (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    title                 TEXT NOT NULL,
    document_type         TEXT,
    document_date_start   DATE,
    document_date_end     DATE,
    extracted_metadata    JSONB,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX documents_document_type_idx ON documents (document_type);
CREATE INDEX documents_date_range_idx ON documents (document_date_start, document_date_end);
CREATE INDEX documents_extracted_metadata_gin_idx ON documents USING gin (extracted_metadata);

-- Safe to add now: every ingested document gets a documents row going
-- forward (even when metadata extraction degrades), which wasn't true in
-- Phase 1 when this table didn't exist yet.
ALTER TABLE document_chunks
    ADD CONSTRAINT document_chunks_document_id_fkey
    FOREIGN KEY (document_id) REFERENCES documents (id);
