-- Phase 4: the agent's two side-effecting tools. Both are creation-only
-- (no update/delete in scope -- see docs/phases/04-agent.md) and standalone,
-- no FK to anything else.

CREATE TABLE notes (
    id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    content    TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE reminders (
    id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    message    TEXT NOT NULL,
    remind_at  TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX reminders_remind_at_idx ON reminders (remind_at);
