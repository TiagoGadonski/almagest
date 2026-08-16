# Evaluation questions

Grading contract (see `docs/phases/05-production.md` §3.5): **recall@5** checks
whether a chunk from the *expected document* shows up in the top 5 results of
`IChunkStore.SearchAsync`; **accuracy** checks whether every expected fact
appears as a case-insensitive substring of `AskQuestionUseCase`'s generated
answer. Both are mechanical string checks — no LLM judge.

Expected document is matched against the ingested document's *title*
(substring, case-insensitive). Expected facts are semicolon-separated;
all of them must be present for the question to count as accurate.

> **Placeholder set.** These rows describe the *shape* of a real eval set —
> one plausible question per personal-document category this project is
> meant to answer questions about. Ingestion itself works and has been
> verified against a real corpus (7 documents, 17 chunks — see
> `docs/phases/05-production.md` §7), but these specific rows don't
> correspond to that corpus, and `Almagest.Eval` has not been run against
> them. Replace these rows with questions against your own ingested
> documents, keeping the same three-column shape, then run
> `dotnet run --project tests/Almagest.Eval` for real.

| Question | Expected Facts | Expected Document |
|---|---|---|
| What is the monthly rent on my apartment lease, and when does it renew? | R$ 2.200; renews in March | Apartment Lease Agreement |
| What deductible applies to my car insurance policy? | R$ 1.500 deductible | Auto Insurance Policy |
| What did we decide about the Q3 roadmap in last week's planning meeting? | ship the mobile app in September | Q3 Planning Meeting Notes |
| Which vaccinations does my dog still need this year? | rabies booster; due in June | Vet Visit Summary |
| What's the total balance due on last month's credit card statement? | R$ 3.845,20 | Credit Card Statement — October |
| What warranty period covers my new laptop? | 2-year warranty | Laptop Purchase Receipt |
