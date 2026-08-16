namespace Almagest.Eval;

public sealed record EvalQuestion(string Question, IReadOnlyList<string> ExpectedFacts, string ExpectedDocument);
