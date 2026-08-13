# Almagest

Personal knowledge assistant: RAG, text-to-SQL and a tool-calling agent over
your own documents. Built in incremental phases — see `docs/phases/`.

Portfolio project. Every technical decision must be explainable and justified;
"it worked" is not a reason.

---

## How to work in this repository

Correctness and understanding matter more than speed here.

- Use plan mode for anything non-trivial. Present the approach and wait for
  approval before writing files.
- When proposing a solution, state the alternative you rejected and why.
- Do not implement these — they are written by hand:
  - chunking strategy and implementation
  - context assembly and prompt construction
  - retrieval logic in the Application layer
  - the port interfaces in the Application layer

  You may review, question and challenge them. You may not author them.
- If a request contradicts a decision already recorded in `docs/phases/`,
  say so instead of silently following the new instruction.
- If a request would produce code that is hard to justify or maintain, say so.
- Prefer the smallest change that satisfies the requirement.

## Conventions

- English in code, namespaces, commits, documentation and comments.
- Clean Architecture, dependencies pointing inward:
  - `Domain` references nothing.
  - `Application` references `Domain` and the `Microsoft.Extensions.AI`
    abstractions only.
  - `Semantic Kernel`, `Anthropic.SDK` and `Npgsql` are confined to
    `Infrastructure`.
  - No concrete AI type crosses the Application boundary.
- Conventional Commits. Scope by layer where it helps: `feat(application):`.
- Semantic Kernel and vector-data packages ship as preview: pin exact versions
  in the `.csproj`. Never a floating range.
- Secrets via environment variables or `dotnet user-secrets`. Never in
  `appsettings.json`.
- `Nullable` and `TreatWarningsAsErrors` enabled solution-wide.
- Do not prefix types with the project name. The namespace already carries it.

## Commands

```bash
dotnet build
dotnet test
dotnet run --project src/Almagest.Lab
docker compose up -d
```

## Known pitfalls

- The Anthropic API requires `max_tokens`. Omitting it returns an opaque 400.
- There is no first-party Anthropic connector for Semantic Kernel in .NET.
  The path is `Anthropic.SDK` → `IChatClient` → `AsChatCompletionService()`.
- Anthropic ships no embedding model. Embeddings come from a separate provider
  (Voyage AI) through a hand-written adapter implementing
  `IEmbeddingGenerator<string, Embedding<float>>`.
- Changing the embedding model invalidates every stored vector. It is a data
  migration with a full reindex, not a configuration change. The embedding
  model id is persisted per chunk so mismatches are detectable at query time.
- `pgvector` requires the `pgvector/pgvector` image. Plain `postgres` lacks the
  extension.
- Vector-data connector packages have been renamed more than once
  (`Connectors.Postgres` → `Connectors.PgVector`). Verify against the current
  package listing rather than older tutorials.