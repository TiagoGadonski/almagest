using System.Diagnostics;

namespace Almagest.Infrastructure.Telemetry;

// The shared ActivitySource every Infrastructure adapter uses for LLM and
// database spans. Just the BCL's tracing API -- the actual OpenTelemetry
// SDK (exporters, processors) is wired once in Program.cs and picks this
// source up by name; nothing here references the OpenTelemetry SDK itself.
public static class AlmagestTelemetry
{
    public const string SourceName = "Almagest";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    // Placeholder configuration, not a live pricing feed -- documented as
    // technical debt (docs/phases/05-production.md 7): will silently drift
    // as provider pricing changes.
    public static class Pricing
    {
        public const double ClaudeInputCostPerMillionTokensUsd = 1.00;
        public const double ClaudeOutputCostPerMillionTokensUsd = 5.00;
        public const double VoyageCostPerMillionTokensUsd = 0.12;
    }

    public static double EstimateCostUsd(long tokens, double costPerMillionTokens) =>
        tokens / 1_000_000.0 * costPerMillionTokens;
}
