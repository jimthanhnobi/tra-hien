namespace TeaPremium.Domain;

public sealed class DomainException : Exception
{
    public const string PublicMessage = "Không thể thực hiện thao tác do quy tắc nghiệp vụ.";

    public DomainException(string code, string message) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}
