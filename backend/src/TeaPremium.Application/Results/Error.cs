using System.Diagnostics.CodeAnalysis;

namespace TeaPremium.Application.Results;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    BusinessRule
}

[SuppressMessage("Naming", "CA1716", Justification = "Error is the exact name required by the approved architecture.")]
public sealed record Error(
    string Code,
    string Message,
    ErrorType Type,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null)
{
    public static Error BusinessRule(string code, string message) => new(code, message, ErrorType.BusinessRule);
}
