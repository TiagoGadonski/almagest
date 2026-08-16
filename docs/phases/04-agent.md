# Phase 4 — Agent

> Status: **in progress** · Target: a single entry point where the model
> decides, per turn, which of a small set of tools to use -- read-only ones
> answered immediately, side-effecting ones only after the user explicitly
> approves the exact call about to run.

---

## 1. Goal

`message -> agent loop -> [pick a tool | answer directly] -> (if side-
effecting: pause for approval) -> execute -> (repeat, bounded) -> final
answer`

Everything this phase adds is orchestration, not new capability: Phase 1's
RAG pipeline and Phase 3's text-to-SQL pipeline become *tools* a model
chooses between, rather than a hard-coded router deciding for it (Phase 3's
`IQueryRouter` picked RAG-vs-SQL before either ran; this phase lets the model
pick per turn, potentially using more than one, and adds two tools that
change the user's data instead of only reading it).

The two side-effecting tools (create note, set reminder) are the first
write path in the entire project. Every other phase was read-only by
explicit design (Phase 3, decision on write access: "a fundamentally
different risk profile"). This phase is where that risk profile gets taken
on -- deliberately, narrowly, and gated behind human approval on every call,
not just logged after the fact.

---

## 2. Scope

### In scope

- Tools for: RAG search (wraps `AskQuestionUseCase`), structured data
  questions (wraps `AskDataQuestionUseCase`), creating a note, and setting a
  reminder
- Explicit human approval required before either side-effecting tool
  actually runs -- the model can *propose* the call, nothing executes until
  approved
- A bounded tool-calling loop: a hard iteration cap, and a structural
  argument for why the agent can never re-enter itself
- Retry with backoff for transient tool/model failures, distinct from and
  not compounding with the iteration cap
- Structured logging of which tool was chosen, with what arguments, for
  every call -- approved, rejected, or read-only
- Unit tests for the decision loop against faked tools (tool selection,
  approval gating, iteration cap, retry)

### Out of scope -- deliberately

| Gap | Why it matters | Deferred to |
|---|---|---|
| Editing or deleting existing personal data (tasks, contacts, projects, notes, reminders) | Only *creation* is in scope; update/delete is a larger blast radius per action | Later iteration |
| Multi-agent orchestration (agents calling other agents) | Nothing in scope needs more than one agent; see decision 3.1 | Later iteration, if a real need shows up |
| Actually sending/delivering reminders (notifications, scheduled jobs) | "Set a reminder" persists intent; a delivery mechanism is a separate, unrelated system (scheduling, notification channels) | Later iteration |
| Standing/remembered approvals ("always approve reminders") | Every side-effecting call is approved individually, no policy layer yet | Later iteration |
| Streaming the agent's intermediate reasoning to the client | Phase 2 added streaming for plain chat; this phase's tool-call round trips are not streamed | Later iteration |

---

## 3. Decisions

### 3.1 Orchestration framework -- Microsoft Agent Framework, not Semantic Kernel's `KernelFunction`/plugins

This reverses the framework named in the request's own requirements list, so
it gets the most detailed justification in this document, and is called out
explicitly for sign-off before implementation, not buried in prose.

**What changed since Phase 1's Semantic Kernel decision:** Microsoft Agent
Framework 1.0 reached general availability on April 3, 2026 -- the
production-ready unification of Semantic Kernel and AutoGen, built by the
same teams, with an explicit migration guide from Semantic Kernel. The core
`Microsoft.Agents.AI` package is stable and under active release (1.17.0 as
of this writing). None of that existed when Phase 1 picked
Anthropic.SDK → `IChatClient` → Semantic Kernel -- at the time, Semantic
Kernel's bridge was the only path, and it still required `[Experimental]`
APIs (`SKEXP0001`, suppressed in this project's `.csproj` files today) to
connect `IChatClient` to Semantic Kernel's chat completion surface.

**Why Agent Framework fits this project specifically, not just generically:**

- `ChatClientAgent` wraps *any* `Microsoft.Extensions.AI.IChatClient` --
  including the exact `IChatClient` this project has built in `Program.cs`
  since Phase 1 (`AnthropicClient` → `AsIChatClient()` → `.AsBuilder()...`).
  No new Anthropic-specific package is required for the agent itself; the
  Anthropic-specific convenience package (`Microsoft.Agents.AI.Anthropic`)
  remains in preview, so this design deliberately doesn't depend on it --
  only the stable, GA `Microsoft.Agents.AI` core.
- Tools are `Microsoft.Extensions.AI.AIFunction` -- the exact abstraction
  `ClaudeMetadataExtractor`, `ClaudeSqlGenerator`, and `ClaudeQueryRouter`
  already use (Phases 2-3). Using Semantic Kernel's `KernelFunction`/
  `KernelPlugin` instead would introduce a *second*, parallel tool-definition
  paradigm alongside the one already used three times in this codebase, for
  no functional gain.
- `ApprovalRequiredAIFunction` is a built-in wrapper that makes a tool call
  come back as `ToolApprovalRequestContent` instead of executing --
  first-party human-in-the-loop support for exactly the "explicit
  confirmation" requirement, not something to hand-roll.
- `FunctionInvokingChatClient.MaximumIterationsPerRequest` is a documented,
  built-in stop condition for the tool-calling loop -- directly answers the
  "limite de iterações" requirement without custom loop-counting code.

**Cost of switching, stated plainly:** this adds a package family
(`Microsoft.Agents.AI`) not used anywhere else in the project yet. It is
scoped additively -- `ClaudeChatService` (the Semantic-Kernel-bridged,
non-agentic chat/summarization/grounding path from Phases 1-2) is untouched.
Nothing about this phase requires ripping Semantic Kernel out of the project;
it requires not extending it into a role (agentic tool orchestration) it was
never exercised in here, when a newer, GA, purpose-built framework already
does that role natively on top of the same underlying abstractions.

Rejected: Semantic Kernel `KernelFunction`/plugins + Semantic Kernel's own
agent/planner abstractions, as the request originally specified. Rejected
because (a) Semantic Kernel's Anthropic path is still the indirect,
`[Experimental]`-flagged bridge, a shakier foundation for a *new* capability
than a GA framework built for exactly this; (b) it fragments tool
definition into two paradigms in one codebase; (c) iteration limits and
approval gating would have to be hand-built on a part of Semantic Kernel
this project has never actually exercised, duplicating what Agent Framework
ships and tests as a first-class feature.

### 3.2 RAG and SQL become tools, not a pre-decided route

Phase 3's `IQueryRouter` classified a question as RAG-or-SQL *before either
ran*. This phase replaces that hard dispatch with two `AIFunction`s wrapping
`AskQuestionUseCase.ExecuteAsync` and `AskDataQuestionUseCase.ExecuteAsync`
unchanged -- neither use case's internals change, only how they're reached.
The model can now, within one turn, call one, the other, both, or neither
(answering directly) -- something a pre-classification router structurally
couldn't do.

Rejected: keeping `IQueryRouter` and layering tool-calling only for the two
new side-effecting tools. Rejected because it means two different dispatch
mechanisms live side by side answering the same kind of question, which is
harder to reason about than one.

### 3.3 Side-effecting tools -- narrow, additive-only, and their own small schema

Two tools: create a note (free-text content), set a reminder (message +
a future timestamp). Both map to new tables (`notes`, `reminders`) rather
than overloading `tasks` (Phase 3) -- a note isn't a task with a status, and
a reminder is a point in time, not a due date on existing work. Both tools
only ever `INSERT` -- no update, no delete, matching the "creation only" scope
line above.

Rejected: modeling a reminder as a `tasks` row with a `due_date` and a
special status. Rejected because it conflates two different domain concepts
(work to complete vs. a point in time to be notified) for the sake of one
fewer table, at the cost of a schema that lies about what it represents.

### 3.4 Approval -- every side-effecting call, no exceptions, no standing rules

Both write tools are wrapped in `ApprovalRequiredAIFunction`. There is no
"approve once, trust for the rest of the session" mode in this phase (see
Scope) -- every single proposed write returns to the caller as a pending
`ToolApprovalRequestContent`, showing the exact tool name and arguments the
model wants to execute, and nothing runs until that specific call is
approved. Read-only tools (RAG, SQL) are not wrapped -- they already have
their own safety story (Phase 1's grounding, Phase 3's five validation
layers) and approval on every read would make the agent unusable.

Rejected: approving at the start of a conversation ("this session may create
notes") rather than per call. Rejected because it's exactly the standing-
permission pattern the Scope section defers -- the argument for it is
convenience, and convenience is not the priority for the first write path in
the project.

### 3.5 Loop termination and recursion -- answered directly, as requested

**How the loop terminates:** `FunctionInvokingChatClient` (which
`ChatClientAgent` is built on) makes one provider call per tool round trip.
`MaximumIterationsPerRequest` caps how many of those round trips a single
`RunAsync` performs before the framework stops and returns whatever it has
-- a hard ceiling independent of what the model "decides" to do. Configured
low (this phase: 5) because the tool set is small and a real answer should
need very few hops.

**What prevents recursion:** structurally, not just by the counter above.
Every tool this phase defines -- RAG, SQL, create-note, set-reminder -- wraps
a single, non-agentic operation (a use case method or a single `INSERT`).
None of them construct or invoke another agent, another `RunAsync`, or
another tool-calling loop. A tool cannot call the agent that called it,
because no tool in this phase's set is capable of calling *anything* that
loops back to `ChatClientAgent.RunAsync`. Recursion isn't prevented by a
guard that could have a bug -- it's absent because nothing in the tool set
has the shape that could produce it. `MaximumIterationsPerRequest` is the
backstop for the loop simply taking too many turns, not for recursion, which
is a different failure mode with a different (structural, not numeric)
answer.

### 3.6 Errors and retry -- bounded, and independent of the iteration cap

Transient failures (a dropped DB connection, a rate-limited model call)
retry with exponential backoff, same pattern as `VoyageEmbeddingService`
(Phase 1) -- a small, fixed maximum attempt count. This retry budget is
local to one tool call and does not consume `MaximumIterationsPerRequest`
-- a retried call that eventually succeeds still counts as the one loop
iteration it took, not one per attempt. Deliberate: conflating the two
would mean a flaky network could exhaust the iteration budget before the
model gets a real turn.

A tool call that exhausts its retries returns a clear failure to the model
(not an exception that kills the whole run) -- the model can then explain
the failure to the user or try a different tool, the same "explicit
not-found over silent improvisation" stance every prior phase has taken.

### 3.7 Logging -- tool, arguments, and why, on every call

Every tool invocation -- selected, approved/rejected, succeeded/failed --
is logged with the tool name, its arguments, and (when the model's response
included reasoning text alongside the tool call) that text as the "why".
This is standard `ILogger` structured logging, not a new subsystem --
consistent with the project not having introduced bespoke telemetry
anywhere else.

---

## 4. Architecture

```
Domain            Note, Reminder -- ordinary entities, same invariant style
                  as every other Domain type in this project. No AI, no
                  agent-framework knowledge.

Application       Tools defined as AIFunction (Microsoft.Extensions.AI),
                  wrapping: AskQuestionUseCase, AskDataQuestionUseCase
                  (existing, unchanged), and two new use cases --
                  CreateNoteUseCase, SetReminderUseCase.
                  Owns: which tools exist, which require approval, the
                  agent's system instructions -- hand-written per Claude.md,
                  same treatment as every prior phase's prompts.

Infrastructure    NoteStore / ReminderStore (Npgsql, mechanical). The agent
                  itself: a ChatClientAgent wrapping the IChatClient already
                  built in Program.cs, configured with the four tools above
                  (two plain, two ApprovalRequiredAIFunction-wrapped) and
                  MaximumIterationsPerRequest.

Api               New endpoint accepting a message and, when resuming after
                  an approval, the user's approve/reject decision -- exact
                  shape decided during implementation alongside the gated
                  orchestration proposal.

Tests             The decision loop tested against faked tools -- selection,
                  approval gating, iteration cap, retry -- no real model or
                  database calls, same testability argument every phase has
                  made.
```

---

## 5. Definition of done

- [ ] RAG and text-to-SQL are reachable as tools the model chooses between,
      not a pre-decided route
- [ ] Creating a note and setting a reminder are tools; neither executes
      without a separate, explicit approval step naming the exact call
- [ ] The tool-calling loop has a hard iteration cap and cannot recurse by
      construction (no tool re-enters the agent)
- [ ] Transient tool/model failures retry with backoff, bounded, and
      distinct from the iteration cap
- [ ] Every tool call is logged with its name, arguments, and available
      rationale
- [ ] Unit tests cover tool selection, approval gating, the iteration cap,
      and retry behavior against faked tools
- [ ] No secret in `appsettings.json` (carried forward)

---

## 6. Interview questions this phase must answer

1. Why Microsoft Agent Framework over Semantic Kernel's own agent/plugin
   abstractions, given the project already had a working Semantic Kernel
   connection?
2. Walk through exactly what happens from the model proposing a
   side-effecting call to it actually executing. Where could a user say no,
   and what happens then?
3. What specifically prevents the agent from calling itself, as opposed to
   just running too many turns?
4. Why is the retry budget for a single tool call kept separate from the
   overall iteration cap?
5. A tool call fails every retry. What does the model see, and what does
   the user see?
6. Why create-only, no edit or delete, for the first write path in the
   project?
7. What would have to be true before per-session "always approve" became
   safe to add?
