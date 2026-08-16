# 1. Retrieval-augmented generation over fine-tuning or full-context stuffing

## Status

Accepted (Phase 1).

## Context

Almagest answers questions against a personal document corpus that changes
over time (new documents added, old ones edited) and must cite exactly
where an answer came from. The model needs to answer *about* this corpus
without ever having been trained on it.

## Decision

Retrieve relevant chunks by vector similarity at query time and ground the
model's answer in those chunks, rather than fine-tuning a model on the
corpus or stuffing the entire corpus into the context window on every call.

## Consequences

- Adding, editing, or removing a document takes effect immediately (embed
  and store, or delete the rows) — no retraining cycle.
- Every claim in an answer can cite the chunk it came from, because the
  chunks are visible inputs to generation, not baked into weights.
- Retrieval quality (chunking, embedding model, similarity floor) becomes
  the primary lever for answer quality — see Phase 1's chunking and
  retrieval-parameter decisions.
- Retrieval can miss (a relevant chunk scores below the similarity floor):
  the system says "not found" rather than guessing, which is the point,
  but it means recall is a real, measured concern — see the eval harness
  (`tests/eval/`) and Phase 5 §3.5.

## Rejected alternatives

- **Fine-tuning a model on the corpus.** No provenance (a fine-tuned model
  can't cite its source), an expensive retrain on every document change,
  and personal-document corpora are far too small to fine-tune well.
- **Full-context stuffing** (pasting every document into every prompt).
  Works only while the corpus is small; cost and latency scale with corpus
  size instead of with question complexity, and it still doesn't produce
  clean per-claim citations without the same retrieval-and-cite machinery
  this decision already builds.

## Related

[`docs/phases/01-rag.md`](../phases/01-rag.md) §3.1, §3.7 (grounding).
