namespace Almagest.Application.Ports;

public interface ISqlValidator
{
    SqlValidationResult Validate(string sql);
}

public sealed record SqlValidationResult(bool IsValid, string? FinalizedSql, string? RejectionReason);
