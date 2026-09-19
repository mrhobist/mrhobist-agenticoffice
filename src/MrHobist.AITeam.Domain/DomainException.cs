namespace MrHobist.AITeam.Domain;

/// <summary>
/// Bir degismezin ihlali. <see cref="ErrorCode"/> HTTP sinirinda Problem Details'e
/// <c>errorCode</c> olarak gecer ve UI'da Turkce metne eslenir (docs/error-codes.md).
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
