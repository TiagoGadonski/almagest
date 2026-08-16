# 5. Microsoft Agent Framework over Semantic Kernel for the agent loop

## Status

Accepted (Phase 4) — reverses Phase 1's original Semantic Kernel choice for
the *agentic* surface specifically. Superseded in scope by Phase 5: Semantic
Kernel has since been removed from `Almagest.Infrastructure` entirely
(`ClaudeChatService` was rewritten onto `IChatClient` directly), so this
decision is no longer additive alongside Semantic Kernel — Semantic Kernel
is retained only in `Almagest.Lab`, the throwaway API-verification console
app, not in the shipped application.

## Context

Phase 1 chose Anthropic.SDK → `IChatClient` → Semantic Kernel because no
first-party Anthropic connector existed for Semantic Kernel, and that
adapter chain was the only path available at the time (requiring
`[Experimental]`, `SKEXP0001`-flagged APIs). Phase 4 needed a tool-calling
agent loop: tools the model chooses between, an iteration cap, and
mandatory human approval before any side-effecting tool executes. The
original phase requirements named Semantic Kernel's `KernelFunction`/plugin
model as the implementation target.

By Phase 4, Microsoft Agent Framework 1.0 (`Microsoft.Agents.AI`) had
reached general availability (April 2026) — the production unification of
Semantic Kernel and AutoGen, built by the same teams, with a documented
migration path from Semantic Kernel. This didn't exist when Phase 1 made
its original choice.

## Decision

Build the Phase 4 agent loop on Microsoft Agent Framework
(`Microsoft.Agents.AI`, stable core only — not the still-preview
`Microsoft.Agents.AI.Anthropic` convenience package), not Semantic Kernel's
`KernelFunction`/plugin/planner abstractions.

Specifically:
- `ChatClientAgent` wraps the same `Microsoft.Extensions.AI.IChatClient`
  this project has built since Phase 1 — no new Anthropic-specific package.
- Tools are `AIFunction` — the identical abstraction already used for
  structured output in Phases 2-3 (ADR 4), not a second, parallel
  tool-definition paradigm.
- `ApprovalRequiredAIFunction` is a first-party wrapper providing exactly
  the "explicit confirmation before every side-effecting call" requirement,
  rather than a hand-rolled approval gate.
- `FunctionInvokingChatClient.MaximumIterationsPerRequest` is a documented,
  built-in bound on the tool-calling loop, directly answering the
  iteration-limit/recursion-prevention requirement.

## Consequences

- The agent's tool-calling surface shares one abstraction (`AIFunction`)
  with every other structured-output call site in the project, instead of
  introducing a second paradigm alongside it.
- Iteration limits and approval gating are framework features, tested by
  the framework, not custom loop-counting/gating code this project would
  otherwise have to write and verify itself.
- Adds a package family not otherwise used at the time it was introduced —
  since made moot by Phase 5 removing Semantic Kernel from the shipped
  application entirely, leaving `Microsoft.Agents.AI` as the only
  agent/orchestration framework in `Almagest.Infrastructure`.

## Rejected alternatives

- **Semantic Kernel `KernelFunction`/plugins + Semantic Kernel's own
  agent/planner abstractions**, as originally specified. Rejected because:
  (a) Semantic Kernel's Anthropic path was still the indirect,
  `[Experimental]`-flagged bridge — a shakier foundation for a *new*
  capability than a GA framework purpose-built for it; (b) it would
  fragment tool definition into two paradigms in one codebase; (c)
  iteration limits and approval gating would have to be hand-built on a
  part of Semantic Kernel this project had never exercised, duplicating
  what Agent Framework ships and tests as first-class features.
- **`Microsoft.Agents.AI.Anthropic`** (the Anthropic-specific convenience
  package). Rejected for staying on preview status at the time — the
  stable, GA core (`Microsoft.Agents.AI`) plus the project's own existing
  `IChatClient` construction was sufficient and avoided depending on a
  preview package for a production-shaped project.

## Related

[`docs/phases/04-agent.md`](../phases/04-agent.md) §3.1 (full justification,
including the "cost of switching" analysis);
[`docs/phases/05-production.md`](../phases/05-production.md) (Semantic
Kernel's subsequent removal from `Almagest.Infrastructure`); [ADR 4](0004-forced-tool-call-structured-output.md).
