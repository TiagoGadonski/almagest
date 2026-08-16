# Phase 5 — Production

> Status: **in progress** · Target: the operational half of a portfolio
> project -- tests that catch real infrastructure bugs (not just unit-level
> logic), a CI pipeline that runs without a paid API key, visibility into
> what every request actually costs and where its time goes, a measurable
> answer to "is retrieval any good," and the documentation a reviewer
> actually reads.

---

## 1. Goal

Nothing in this phase adds a new way to ask a question. It makes the
existing four phases *trustworthy at a glance*: provably tested against a
real database, buildable by someone who doesn't have an Anthropic key,
observable in production, measured rather than assumed to work, and
explained in five short documents instead of four long ones.

**This phase has a different shape than 1-4, worth stating up front.**
`Claude.md`'s hands-off list -- chunking strategy, context assembly, prompt
construction, retrieval logic, Application port interfaces -- barely applies
here. Testcontainers tests exercise existing SQL, not new retrieval
strategy. The ONNX embedding service implements the same `IEmbeddingService`
port Phase 1 already approved -- no new port. CI, tracing, deployment, and
docs are not "AI" decisions at all. The one place the hands-off list
plausibly reaches is the eval script's grading method, addressed in 3.5. The
rest of this document proceeds without the per-artifact gating ceremony of
Phases 1-4, because there is close to nothing here that ceremony applies to.

**Stated plainly, so it isn't discovered by surprise later:** several of
this phase's deliverables cannot be *verified end-to-end* in this
environment -- there is no real Anthropic/Voyage key to run the eval script
against, no cloud credentials to actually deploy, and no push access to
watch a GitHub Actions run go green. Each of those is built and checked as
far as it honestly can be (config validity, local execution where a real
model isn't required, structural review), and the gap between "built" and
"verified live" is named explicitly in §7, not glossed over.

---

## 2. Scope

### In scope

- Integration tests against a real, ephemeral PostgreSQL (Testcontainers) --
  scoped to persistence-layer correctness (schema, SQL, pgvector operators,
  the read-only role's actual grants), the same category of bug Phase 2's
  FK-ordering mistake and Phase 3's alias-resolution bug turned out to be
- A second `IEmbeddingService` implementation backed by a local ONNX model,
  so tests and CI don't need a Voyage API key
- GitHub Actions: build, unit tests, integration tests, and a hard failure
  if `Almagest.Application`'s line coverage drops below a threshold
- OpenTelemetry tracing around every LLM and database call, with token
  usage/estimated cost and latency as span attributes
- An evaluation harness reading `tests/eval/questions.md`, reporting
  recall@5 and answer-accuracy against `AskQuestionUseCase`
- A deployment manifest for Fly.io, including the managed Postgres side
- A rewritten root `README.md`: build badge, deploy link, an architecture
  diagram spanning all four prior phases, a technical-decisions section, and
  an explicit limitations/next-steps section
- Five short ADRs in `docs/adr/` for the most consequential decisions across
  the whole project

### Out of scope -- deliberately

| Gap | Why it matters | Deferred to |
|---|---|---|
| Running the eval harness against real models in this session | No Anthropic/Voyage credentials here, and ingestion is still blocked on `RecursiveTextChunker` regardless (see §7) | The project owner, once both are available |
| An actual live Fly.io deployment | No cloud credentials in this environment, and deploying is an action with real cost/consequence that needs the project owner doing it, or explicit confirmation plus credentials neither of which exist here | The project owner |
| Watching a real GitHub Actions run go green | No push access from here | The project owner, on first push |
| Distributed tracing backend (Jaeger/Tempo/anything hosted) | OpenTelemetry is wired with an OTLP exporter pointed at *a* collector; standing one up is infra ops, not app code | Later iteration |
| Load/performance testing | Nothing in scope needs it yet at personal-data scale | Later iteration |

---

## 3. Decisions

### 3.1 Integration tests -- persistence correctness, not full LLM pipelines

Testcontainers (`Testcontainers.PostgreSql`, pinned) spins up
`pgvector/pgvector:pg16` -- the exact image `docker-compose.yml` already
uses -- applies the real migrations from `db/migrations/`, and exercises the
Infrastructure classes that talk to it directly: `PgVectorChunkStore`,
`PostgresConversationStore`, `PostgresReadOnlySqlExecutor`,
`PgAstSqlValidator`'s allowlist against the *actual* `almagest_readonly`
grants. Embeddings used in these tests are hand-constructed `float[]`
arrays, not real model output -- these tests are about whether the SQL and
schema are correct, not about embedding quality (that's §3.5's job, and a
different kind of test).

Rejected: full-stack integration tests that also call a real or ONNX
embedding model and a real Claude endpoint. Rejected because it conflates
two different failure classes -- "is this SQL correct against a real
database" and "is retrieval any good" -- into one slow, flaky test category.
Phase 2 and Phase 3's real bugs were both persistence/logic bugs a
Testcontainers-style test would have caught without any model involved.

### 3.2 Local embedding model -- ONNX Runtime + an official tokenizer, not a third-party wrapper package

Model: `sentence-transformers/all-MiniLM-L6-v2`, exported to ONNX -- 384
dimensions, ~90MB (fp32) or ~23MB (int8 quantized), the standard small
sentence-embedding model, fast enough on CPU to not slow down CI.

Runtime: `Microsoft.ML.OnnxRuntime` (official, pinned) for inference,
`Microsoft.ML.Tokenizers`' `BertTokenizer` (official Microsoft package, also
pinned) for WordPiece tokenization directly from the model's `vocab.txt` --
both Microsoft-maintained, both mature, matching this project's general
preference for well-established dependencies (the `PdfPig`/`pgsqlparser`
diligence earlier in this project exists precisely because the alternative
-- a thin, low-download-count wrapper package -- is a real risk category).
`OnnxEmbeddingService` hand-wires tokenization → inference → mean-pooling →
L2-normalization itself; this is exactly the kind of "hand-written adapter"
this project already does for Voyage AI, just against a local runtime
instead of an HTTP API.

**The dimension mismatch, and why it isn't a problem:** `document_chunks.embedding`
is `VECTOR(1024)`, sized for Voyage's output. A 384-dimension local model
cannot write into that column. This is fine because Testcontainers spins up
a *fresh, disposable* database per test run -- integration tests apply their
own migration with a `VECTOR(384)` column, never touching production's
schema or a shared instance. Phase 1's embedding-model-id-per-chunk design
(recorded specifically to detect this class of mismatch) is the same
mechanism that makes mixing model dimensions across environments safe by
construction, not by discipline.

Rejected: a third-party "run this Hugging Face model in C#" wrapper package.
Rejected for the same reason `pgsqlparser` was chosen over guessing --
provenance and maintenance status matter, and the official Microsoft
packages here are a known quantity in a way a low-download niche package
isn't.

### 3.3 CI -- `coverlet.msbuild`'s own threshold gate, scoped to one assembly

`coverlet.msbuild` (pinned) integrates with `dotnet test` directly and
supports `/p:Threshold=X /p:ThresholdType=line /p:Include="[Almagest.Application]*"`
-- a build failure sourced from the coverage tool itself, scoped to exactly
the assembly the request named, rather than a repo-wide number that would
average away a poorly-covered Application layer against a well-covered
Domain one.

Pipeline shape: `build` → `unit tests` (no external dependencies, always
runs) → `integration tests` (Testcontainers, needs Docker-in-Docker on the
runner, GitHub's hosted runners provide this) → coverage gate on the unit
+ integration combined run. No step requires a real Anthropic/Voyage key --
that's the entire point of §3.2.

### 3.4 Tracing -- spans around LLM and database calls, token/cost and latency as attributes

`OpenTelemetry.Extensions.Hosting` wires ASP.NET Core and outbound HTTP
instrumentation automatically. On top of that, explicit
`ActivitySource`-based spans wrap: every `IChatClient`/`IEmbeddingService`
call (tagging model id, input/output token counts already available from
`ChatResponse.Usage`/embedding usage data, and an estimated cost computed
from a configured $/token rate), and every `ISqlExecutor`/`IChunkStore`
database round trip (tagging row count and duration). Cost-per-token is
configuration, not a constant -- pricing changes, and hardcoding it would
silently go stale (recorded as technical debt in §7 regardless, since there
is no automated way to keep it current without a pricing-feed dependency
this phase doesn't add).

Rejected: relying solely on ASP.NET Core's and Npgsql's automatic
instrumentation without custom spans. Rejected because neither knows about
token usage or dollar cost -- that information only exists at the
application layer, where the LLM response is actually read.

### 3.5 Evaluation harness -- and the one place this phase touches the hands-off list

`tests/eval/questions.md`: a human-readable table of question, expected
answer keywords/facts, and expected source document -- not expected chunk
IDs, since chunk IDs are runtime-generated GUIDs that don't exist until
ingestion runs. Grading two things: **recall@5** (did a chunk from the
expected source document appear in the top-5 retrieved, per
`IChunkStore.SearchAsync`) and **accuracy** (does the generated answer
contain the expected facts). Recall@5 is a mechanical check -- no model
involved, no hands-off concern. Accuracy grading, if done by asking an LLM
"does this answer contain fact X" rather than simple substring matching, is
a prompt -- and prompt construction is exactly the category `Claude.md`
reserves for hand-authoring. Resolved the same way Phases 1-4 resolved it:
the harness starts with simple substring/keyword matching (no prompt, no
gate needed) as the default grading method; an LLM-judge upgrade, if wanted
later, would get its own individually-approved proposal exactly like every
prompt in this project has.

### 3.6 Deployment -- Fly.io, manifest only

`fly.toml` targeting the existing multi-stage `Dockerfile` unchanged, plus
Fly Postgres (with the `pgvector` extension available on Fly's Postgres
image) as the managed database, and secrets (`ANTHROPIC_API_KEY`,
`VOYAGE_API_KEY`, connection string) set via `fly secrets`, never committed.
This is the same shape as `docker-compose.yml`'s `api`/`db` split, ported to
a host that runs it without the project owner operating a VM directly.

Rejected: writing deployment automation that actually runs (a GitHub Actions
deploy job triggered on push to `main`). Rejected for this phase --
deploying without the project owner present to approve it, on a session with
no real Fly.io credentials to test against, would be building automation
nobody has verified does the right thing. The manifest is reviewed and
merged first; wiring it into CI is a one-line follow-up once it's been run
by hand at least once.

---

## 4. Architecture

```
tests/Almagest.IntegrationTests   New test project. Testcontainers-backed,
                                   real Postgres, no external API calls.

src/Almagest.Infrastructure/
  Embeddings/OnnxEmbeddingService.cs   Second IEmbeddingService -- same
                                        port Phase 1 defined, no new port.

src/Almagest.Infrastructure/
  Telemetry/                    ActivitySource wrappers around IChatClient
                                 and database calls; OTel wiring in Program.cs.

tests/eval/
  questions.md                  Human-authored question/expected-fact/
                                 expected-document table.
  EvalRunner (console app or a
  dotnet-run-able script)       Computes recall@5 and accuracy against
                                 AskQuestionUseCase; not runnable to
                                 completion here (blocked on ingestion --
                                 see §7), but structurally complete and
                                 unit-testable on its own scoring logic.

.github/workflows/ci.yml        build -> unit tests -> integration tests ->
                                 coverage gate on Almagest.Application.

fly.toml, fly.*.toml (if split) Deployment manifest, not executed here.

docs/adr/000X-*.md              Five short ADRs.

README.md                       Rewritten: badge, deploy link, full
                                 architecture diagram, decisions, limitations.
```

---

## 5. Definition of done

- [ ] Integration tests run against a real, ephemeral Postgres via
      Testcontainers and pass locally
- [ ] `OnnxEmbeddingService` implements `IEmbeddingService` with no network
      call, verified against real sentence pairs (similar sentences score
      higher than unrelated ones)
- [ ] `.github/workflows/ci.yml` exists, is syntactically valid, and runs
      build/unit/integration/coverage-gate steps without requiring any
      secret to be present
- [ ] LLM and database calls produce OpenTelemetry spans with token/cost and
      latency attributes, verified locally with a console exporter
- [ ] The eval harness's scoring logic (recall@5, accuracy) is unit tested
      against hand-built fixtures, independent of whether it can run
      end-to-end here
- [ ] `fly.toml` is present and matches the existing Docker/compose shape
- [ ] `README.md` has a build badge, deploy link, full architecture diagram,
      decisions section, and limitations/next-steps section
- [ ] Five ADRs exist in `docs/adr/`
- [ ] Technical debt is listed explicitly, not implied

---

## 6. Interview questions this phase must answer

1. Why do integration tests use hand-built embedding vectors instead of a
   real or local model?
2. Walk through why a 384-dimension local model and a 1024-dimension
   production column don't conflict.
3. Why scope the coverage gate to `Almagest.Application` specifically
   instead of the whole solution?
4. What's actually measured in a trace span for an LLM call that ASP.NET
   Core's automatic instrumentation wouldn't already give you?
5. Why does the eval harness's default grading avoid an LLM judge, and what
   would it take to add one properly?
6. Why is the deployment manifest written but not wired into CI yet?

---

## 7. Known technical debt at the end of this phase

This list is the point of the section, not an afterthought:

- **The eval harness cannot run to completion in this environment.**
  `RecursiveTextChunker` has been an intentional stub since Phase 1 --
  ingestion throws before any document produces retrievable chunks. Real
  recall@5/accuracy numbers require the project owner's chunker
  implementation *and* real API credentials, neither available here.
- **No live GitHub Actions run has been observed.** The workflow file is
  reviewed for syntactic and logical correctness, not proven green.
- **No live deployment exists.** `fly.toml` is unexecuted configuration.
- **Token cost is a hardcoded configuration value**, not fed by a live
  pricing source -- it will silently drift out of date as provider pricing
  changes.
- **No tracing backend is stood up.** Spans are emitted (OTLP-ready); there
  is no Jaeger/Tempo/hosted collector receiving them yet.
- **The eval harness's accuracy grading is substring/keyword matching**,
  a coarse proxy for "the answer is actually correct" -- an LLM-judge
  upgrade is deferred per §3.5, gated the same way every prompt in this
  project is.
- ~~Coverage threshold's actual numeric value is a first guess~~ --
  measured for real during implementation: `Almagest.Application` line
  coverage is 89.2% as of this phase; the CI gate is set to 80%, real
  headroom below the measured baseline rather than a round-number guess.
- **No load or performance testing** of any kind exists.
- **The read-only role's grants are checked by the integration suite
  against whatever `db/migrations/` currently says** -- there's no
  automated check that the `SqlAllowlist` constant in code and the
  migration's `GRANT` statements haven't drifted apart from each other
  beyond what the integration test happens to exercise.
