# 4. Structured output via forced tool calls, independently re-validated against JSON Schema

## Status

Accepted (Phase 2, metadata extraction). Reused unchanged for SQL
generation (Phase 3) and the agent's tool-call arguments (Phase 4) — the
same mechanism every time a model must return structured data, not a
one-off.

## Context

Two places in this project need the model to return data conforming to a
known schema — extracted document metadata (Phase 2) and generated SQL
(Phase 3) — where malformed output is not just an inconvenience but,
for SQL, a security surface (ADR 3). Anthropic has no "JSON mode." The
question this decision explicitly answers: how is schema conformance
actually guaranteed, and what happens when generation doesn't conform?

## Decision

Two mechanisms, layered:

1. **Constrain generation** — define the target schema as an
   `Microsoft.Extensions.AI.AIFunction`'s input schema, force
   `ChatOptions.ToolMode` to that specific tool, and read the (already
   JSON-parsed) function-call arguments. The model decodes directly into a
   schema-shaped slot instead of producing prose that has to be located
   and parsed. This makes conformance *highly likely*, not guaranteed —
   tool-call argument generation is schema-*guided*, not a hard grammar
   constraint, so a required field can still come back missing or an enum
   value can still land outside its allowed set.
2. **Validate independently, every time, against the same schema
   document** — the returned arguments are run through a real JSON Schema
   validator (`JsonSchema.Net`), using the identical schema that defined
   the tool, so there is no second hand-maintained copy of "what valid
   looks like" that could drift from the first. `System.Text.Json`
   deserializing without throwing is explicitly *not* trusted as
   equivalent to schema validity — it only checks rough shape
   compatibility, not required-ness, enum membership, or format
   constraints.

On validation failure: the errors are fed back to the model in a follow-up
message and the call is retried once (a cheap, well-established repair
strategy). If the retry also fails, the two call sites degrade
differently by design — metadata extraction proceeds with no metadata for
that document (an enrichment, not a precondition for the document to be
searchable) rather than failing ingestion; SQL generation has no
equivalent safe degradation, so a query that never validates is rejected
outright (ADR 3 layers 2-3 exist precisely to catch this).

## Consequences

- One schema per structured-output call site, used both to constrain
  generation and to validate the result — no drift between "what we asked
  for" and "what we check for."
- Extra latency and cost on the (uncommon) repair path — one additional
  model round trip when the first attempt doesn't validate.
- Failure is always explicit (a flagged missing-metadata document, a
  rejected query) rather than silently persisting output that merely
  *deserialized* without actually being valid.

## Rejected alternatives

- **Prompting for raw JSON in response text, regex/substring-extracted.**
  Strictly worse than tool calling: trades one failure mode (schema
  nonconformance) for two (schema nonconformance *and* unparseable text),
  for no offsetting benefit.
- **`ChatOptions.ResponseFormat.ForJsonSchema(...)`**, the
  provider-agnostic structured-output path in `Microsoft.Extensions.AI`.
  Rejected because whether the Anthropic adapter actually routes this into
  forced tool calling under the hood wasn't verified against the
  adapter's source — the documented-for-Anthropic mechanism is used
  directly instead of trusting an abstraction layer's translation of a
  feature the underlying provider doesn't natively expose.
- **Trusting successful deserialization as proof of schema validity.**
  Rejected: a `jsonb` column enforces no schema of its own, and
  `System.Text.Json` not throwing is a much weaker claim than "this
  conforms to the schema" — the independent validation step is the only
  real checkpoint between what the model said and what's persisted or
  executed.

## Related

[`docs/phases/02-memory.md`](../phases/02-memory.md) §3.3-§3.4;
[`docs/phases/03-text-to-sql.md`](../phases/03-text-to-sql.md) §3.2 (SQL
generation reusing this mechanism); [ADR 3](0003-five-layer-text-to-sql-security.md).
