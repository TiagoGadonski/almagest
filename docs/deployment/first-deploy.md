# First deploy — exact command sequence

**Not executed from this environment.** No Fly.io credentials exist here,
and deploying is a real-cost action that needs the project owner present.
This document exists so the first real deploy is a sequence of commands to
run, not a set of decisions to make under pressure. Everything in it is
based on Fly's current documentation (checked while writing this, August
2026) — Fly's CLI and dashboard change; if a command below doesn't match
what you see, `--help` and the linked docs are the source of truth, not
this file's memory of them.

**Read this before running anything below:** an earlier version of this
project's `fly.toml` recommended `fly postgres create` (Fly's older,
self-managed "Postgres Flex" product). That product **does not include
`pgvector` by default** — `CREATE EXTENSION vector` fails on it unless you
build a custom Postgres image yourself. Fly's newer **Managed Postgres
(MPG)** product supports `vector` as a toggleable extension and is what
this guide uses instead. If you already created a Postgres Flex instance
for this project, either destroy it and use MPG, or see
[Fly's pgvector-on-Flex community thread](https://community.fly.io/t/adding-pgvector-to-fly-postgres/12202)
for the custom-image path — not covered here, since depending on a
community-maintained base image for the production database is a
provenance tradeoff this project would want to make deliberately, not by
default.

## 0. Prerequisites

```bash
# Install flyctl if you haven't: https://fly.io/docs/flyctl/install/
fly auth login
```

## 1. Create the app (no deploy yet)

```bash
cd /path/to/Almagest
fly launch --no-deploy --copy-config
```

`--copy-config` keeps this repo's `fly.toml` instead of generating a new
one. This registers the app (`almagest`, or whatever you rename it to if
prompted) on your Fly account. It does not deploy or start a machine yet.

## 2. Create a Managed Postgres cluster

```bash
fly mpg create
# follow the prompts (name, region, size), or pass flags directly --
# run `fly mpg create --help` for the current flag names.
```

Note the cluster ID it prints — you need it in the next step.

## 3. Enable the `vector` extension on the cluster

As of this writing, MPG extensions are enabled per-cluster from the
dashboard, not a CLI flag:

1. Open the Fly dashboard → your organization → Managed Postgres → the
   cluster you just created → **Extensions**.
2. Toggle **Vector** on.

(PostGIS has a `--enable-postgis-support` creation flag; Vector currently
doesn't per Fly's docs — if that's changed, a flag at `fly mpg create` time
is simpler and this step can be skipped.)

## 4. Attach the cluster to the app

```bash
fly mpg attach <cluster-id> -a almagest
```

This sets a `DATABASE_URL` secret on the app automatically — Fly's
connection string, in `postgres://user:pass@host:port/db` form.
`Almagest.Api` reads `DATABASE_URL` directly and translates it internally
(`PostgresConnectionStringTranslator`); no manual reformatting step exists
anymore.

## 5. Apply migrations

The app does not apply migrations itself (see README "Known limitations" —
`db/migrations/` is versioned SQL, not wired to a migration runner). Run
each file, in order, against the real database once, before the first
deploy that expects the schema to exist:

```bash
# Get a local tunnel to the cluster (keep this running in a separate shell):
fly proxy 15432:5432 -a almagest   # or: fly mpg proxy <cluster-id>, check --help

# In another shell, against the tunnel:
export PGPASSWORD=<from `fly mpg status <cluster-id>` or the dashboard>
for f in db/migrations/*.sql; do
  echo "Applying $f"
  psql "postgresql://<user>@localhost:15432/<db>" -f "$f" || break
done
```

`db/migrations/0001_init.sql` runs `CREATE EXTENSION IF NOT EXISTS vector;`
as its first statement — this only succeeds if step 3 (enabling the
extension on the cluster) already happened.

Verify:

```bash
psql "postgresql://<user>@localhost:15432/<db>" -c "\dt"
# expect: document_chunks, sessions, messages, documents, contacts,
# projects, tasks, calendar_events, notes, reminders
psql "postgresql://<user>@localhost:15432/<db>" -c "\du almagest_readonly"
# expect: the role from db/migrations/0003_text_to_sql.sql to exist
```

## 6. Set the remaining secrets

```bash
fly secrets set \
  ANTHROPIC_API_KEY="sk-ant-..." \
  VOYAGE_API_KEY="pa-..." \
  -a almagest
```

`DATABASE_URL` is already set (step 4). `ANTHROPIC_MODEL`/`VOYAGE_MODEL`
are plain (non-secret) config already in `fly.toml`'s `[env]` block.

## 7. Deploy

```bash
fly deploy -a almagest
```

Builds from the repo's `Dockerfile` (the same one `docker compose` uses
locally — this exact build is also validated in CI, see
`.github/workflows/ci.yml`'s `docker-build` job).

## 8. Verify it's actually up

```bash
curl https://almagest.fly.dev/health
# expect: "ok" with HTTP 200 -- this endpoint checks real database
# connectivity (SELECT 1), not just that the process started. A 503 here
# means the app is running but can't reach Postgres -- check DATABASE_URL
# and that migrations were actually applied (step 5).

curl -X POST https://almagest.fly.dev/documents \
  -F "file=@/path/to/a/real.pdf"
curl -X POST https://almagest.fly.dev/ask \
  -H "Content-Type: application/json" \
  -d '{"question":"whatever the document you just ingested is about"}'
```

## What this sequence has *not* been verified against

This document was written by working through Fly's current documentation
and this project's own code, not by running these commands against a real
Fly account. Concretely unverified:

- The exact current `fly mpg create` flag names (region, size) — `--help`
  is the source of truth, not the placeholder command above.
- Whether `fly mpg attach`'s `DATABASE_URL` includes an `sslmode` query
  parameter, and if so which value. `PostgresConnectionStringTranslator`
  handles it if present and leaves Npgsql's own default alone if absent —
  reviewed for correctness, not exercised against a real MPG connection
  string.
- Whether the role that `fly mpg attach` creates has sufficient privileges
  to run `db/migrations/0003_text_to_sql.sql`'s `CREATE ROLE
  almagest_readonly` and column-level `GRANT` statements without further
  privilege setup. Locally this runs as the Postgres superuser-equivalent
  `almagest` role from `docker-compose.yml`; Fly's attached-app role may be
  scoped differently.

If any of these turn out wrong on a real attempt, the fix belongs in this
document (and, for the connection-string case, in
`PostgresConnectionStringTranslatorTests`) so the next attempt doesn't
repeat the same surprise.
