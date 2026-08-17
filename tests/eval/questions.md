# Evaluation questions

Grading contract (see `docs/phases/05-production.md` §3.5): **recall@5** checks
whether a chunk from the *expected document* shows up in the top 5 results of
`IChunkStore.SearchAsync`; **accuracy** checks whether every expected fact
appears as a case-insensitive substring of `AskQuestionUseCase`'s generated
answer. Both are mechanical string checks — no LLM judge.

Expected document is matched against the ingested document's *title*
(substring, case-insensitive). Expected facts are semicolon-separated; all of
them must be present for the question to count as accurate.

## Running this harness

On Voyage's free tier (3 requests/minute), firing one embedding call per
question with no pacing exhausts the quota mid-run and the run aborts with
`EmbeddingProviderException`. `Almagest.Eval` pauses between questions —
default 25 seconds, controlled by `ALMAGEST_EVAL_DELAY_MS` (milliseconds).
Raise it if a run still gets rate-limited; lower it (or set it to `0`) once
running against a paid tier or Anthropic/Voyage keys with more headroom.
Progress is logged to stderr as `question N of M` so a slow run doesn't
look stuck. If an individual question still fails (rate limit or otherwise),
the run does not abort — the failure is recorded and reported separately at
the end, excluded from the recall/accuracy denominator, and the remaining
questions still run.

## How these rows are written

Because accuracy is a plain substring check, expected facts are **short,
distinctive stems** rather than full phrases. `governan` matches both
"governança" and "governantes"; `confian` matches "confiança" and "confiar".
Two facts per question is deliberate: every additional fact is another chance
for a false negative caused by the model's phrasing rather than by a genuine
retrieval failure. The stems are ugly to read on purpose — the metric measures
whether the right information was retrieved, not whether the model conjugated
verbs the way the question author would have.

Questions are a mix of direct lookups (vocabulary close to the source text)
and paraphrases (same answer, different words). The paraphrased ones — the
tech-support impersonation question, and the one about sensors not being
sufficient on their own — are the ones that distinguish semantic retrieval
from keyword matching.

## Known limits of this measurement

- **The corpus is small.** 7 documents / 17 chunks. Top-5 retrieval therefore
  covers roughly a third of everything indexed, so recall@5 is optimistic by
  construction. It validates that the pipeline works end to end; it is not a
  retrieval benchmark. A corpus an order of magnitude larger is needed for
  that.
- **Document coverage is uneven.** Five of the seven documents produced only
  one or two chunks, so they carry a single question each. Only
  `Engenharia_Social` (6 chunks) and `cidades_inteligentes` (5 chunks) support
  real variety.
- **False positives are not measured here.** The scorer treats an empty
  expected-document string as a recall hit and an empty fact list as an
  inaccurate answer, so out-of-corpus questions cannot be encoded in this
  table without corrupting both metrics. They are tested manually instead —
  see below.

## Out-of-corpus questions (tested manually, not by this harness)

These should all return `found: false`. Run them against `POST /ask` by hand
and record the outcome in the README.

- Qual a taxa Selic atual?
- Como configurar um cluster Kubernetes?
- Qual a receita de feijoada?
- Quantos habitantes tem Curitiba?

---

| Question | Expected Facts | Expected Document |
|---|---|---|
| O que a Engenharia Social explora para obter informações importantes de uma organização? | elo mais fraco; confian | Engenharia_Social |
| Por quais meios um ataque de Engenharia Social pode ser realizado? | telefone; e-mail | Engenharia_Social |
| Por que um contato que se apresenta como suporte técnico pode comprometer uma conta mesmo sem explorar uma falha do sistema? | suporte; senha | Engenharia_Social |
| No exemplo de Kevin Mitnick, como um disquete deixado no banheiro poderia ser usado num ataque? | curiosidade; Cavalo de Tr | Engenharia_Social |
| Qual é a principal defesa contra ataques de Engenharia Social? | treinamento; polític | Engenharia_Social |
| Quais elementos compõem uma definição completa de cidade inteligente? | tecnologia; planejamento urbano; governan | cidades_inteligentes |
| Para que servem programas de dados abertos numa cidade inteligente? | transparên; dados abertos | cidades_inteligentes |
| Por que espalhar sensores pela cidade não garante, por si só, uma gestão mais eficiente? | integra; qualidade dos dados | cidades_inteligentes |
| O que é manutenção preditiva e qual seu objetivo em sistemas urbanos? | desgaste; antes da falha | cidades_inteligentes |
| No caso de Nova Aurora, quais conexões de dados gerariam ganhos no transporte e na resposta a enchentes? | bilhetagem; defesa civil | cidades_inteligentes |
| Como priorizar o pagamento de dívidas e qual o tamanho ideal da reserva de emergência? | juros mais altos; reserva de emergência | Educacao-Financeira |
| Que cuidados tomar ao começar a investir com pouco capital? | perfil de risco; diversific | Educacao-Financeira |
| Quais fatores determinam a rentabilidade de um FII obtida por meio dos aluguéis? | vacância; dividendo | Invista-em-FII |
| Que tipos de produtos digitais podem ser vendidos para gerar renda extra na internet? | e-book; curso | Ganhe-Renda-Extra |
