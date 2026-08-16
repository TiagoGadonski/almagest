# CI secrets — what's actually required

**Short answer: none.** Every job in `.github/workflows/ci.yml` (`build`,
`unit-tests`, `coverage`, `integration-tests`, `docker-build`) runs without
any repository secret configured. This isn't an aspiration — it's a design
decision (docs/phases/05-production.md §3.3, §3.1): unit tests run against
hand-written fakes, integration tests use Testcontainers with hand-built
`float[]` embeddings, and the Docker build only needs the repo's own files.
No step calls Anthropic, Voyage, or Fly.

That means a fork or a fresh clone of this repo can get a fully green CI
run with zero configuration. If a job ever starts requiring a secret to
pass, that's a regression from this design, not an oversight to fix by
adding the secret.

## Optional: Docker Hub credentials (recommended, not required)

`integration-tests` pulls `pgvector/pgvector:pg16` via Testcontainers.
Pulled anonymously, that shares Docker Hub's **100-pulls-per-6-hours-per-IP**
limit with every other GitHub Actions job currently running on the same
shared runner IP pool — a real, commonly-hit source of CI flakiness for
Testcontainers-based workflows (not specific to this repo). Authenticating
raises the limit to 5,000 pulls/day.

If unset, the job still runs — it just pulls anonymously and inherits
whatever headroom is left on GitHub's shared IPs at that moment.

| Name | Type | Value |
|---|---|---|
| `DOCKERHUB_USERNAME` | **Variable** (not secret — it's not sensitive) | Your Docker Hub username |
| `DOCKERHUB_TOKEN` | **Secret** | A Docker Hub [access token](https://app.docker.com/settings/personal-access-tokens) (Account Settings → Personal Access Tokens → Generate) — not your Docker Hub password |

### Configure via the GitHub UI

1. Repository → **Settings** → **Secrets and variables** → **Actions**.
2. **Variables** tab → **New repository variable** → name `DOCKERHUB_USERNAME`, value your Docker Hub username.
3. **Secrets** tab → **New repository secret** → name `DOCKERHUB_TOKEN`, value the access token from Docker Hub.

### Configure via `gh` CLI

```bash
gh variable set DOCKERHUB_USERNAME --body "your-dockerhub-username"
gh secret set DOCKERHUB_TOKEN   # paste the token when prompted, or:
gh secret set DOCKERHUB_TOKEN --body "dckr_pat_..."
```

Both commands need to run from inside the repo (or with `--repo owner/name`)
and need `gh auth login` already done with a token that has `repo` scope.

## Not currently used, but relevant if this changes later

`fly.toml` and `docs/deployment/first-deploy.md` describe deploying **by
hand**, run by the project owner locally — a deliberate choice
(docs/phases/05-production.md §3.6: deploying without the project owner
present, with no real Fly credentials to test the automation against,
would mean shipping a deploy job nobody had verified does the right thing).
If a CI-triggered deploy job is added later, it would need:

| Name | Type | Value |
|---|---|---|
| `FLY_API_TOKEN` | Secret | `fly tokens create deploy` output |

That's a separate, deliberate decision to make later — not part of this
workflow today.
