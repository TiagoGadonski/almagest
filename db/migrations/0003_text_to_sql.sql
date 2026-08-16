-- Phase 3: personal productivity schema + the read-only role text-to-SQL
-- executes generated queries under. See docs/phases/03-text-to-sql.md,
-- especially section 3.4, for the full security rationale.

CREATE TABLE contacts (
    id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name       TEXT NOT NULL,
    email      TEXT,
    phone      TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- status is plain text with a CHECK, matching the role column on messages
-- (0002_memory.sql) -- avoids Npgsql enum-type mapping entirely.
CREATE TABLE projects (
    id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name       TEXT NOT NULL,
    status     TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'completed', 'archived')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE tasks (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    project_id          UUID REFERENCES projects (id),
    source_document_id  UUID REFERENCES documents (id),
    title               TEXT NOT NULL,
    status              TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'in_progress', 'done', 'cancelled')),
    due_date            DATE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX tasks_project_id_idx ON tasks (project_id);
CREATE INDEX tasks_source_document_id_idx ON tasks (source_document_id);

CREATE TABLE calendar_events (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    title               TEXT NOT NULL,
    starts_at           TIMESTAMPTZ NOT NULL,
    ends_at             TIMESTAMPTZ,
    location            TEXT,
    related_contact_id  UUID REFERENCES contacts (id),
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX calendar_events_related_contact_id_idx ON calendar_events (related_contact_id);
CREATE INDEX calendar_events_starts_at_idx ON calendar_events (starts_at);

-- --- Read-only role for executing generated SQL --------------------------
--
-- NOLOGIN: never connects directly. The app's own login role switches into
-- it for exactly the duration of one transaction via SET LOCAL ROLE (see
-- PostgresReadOnlySqlExecutor) -- no separate credential to manage.
-- "TO CURRENT_USER" keeps this portable regardless of the configured
-- POSTGRES_USER.
CREATE ROLE almagest_readonly NOLOGIN;
GRANT almagest_readonly TO CURRENT_USER;

GRANT USAGE ON SCHEMA public TO almagest_readonly;

-- Column-level GRANTs: the allowlist enforced at the database layer,
-- independently of anything the application decides. Deliberately excludes
-- document_chunks, sessions, and messages entirely -- see phase doc 3.5.
GRANT SELECT (id, name, email, phone, created_at)
    ON contacts TO almagest_readonly;

GRANT SELECT (id, name, status, created_at)
    ON projects TO almagest_readonly;

GRANT SELECT (id, project_id, source_document_id, title, status, due_date, created_at)
    ON tasks TO almagest_readonly;

GRANT SELECT (id, title, starts_at, ends_at, location, related_contact_id, created_at)
    ON calendar_events TO almagest_readonly;

GRANT SELECT (id, title, document_type, document_date_start, document_date_end, extracted_metadata, created_at)
    ON documents TO almagest_readonly;
