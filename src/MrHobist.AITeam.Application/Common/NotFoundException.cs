namespace MrHobist.AITeam.Application.Common;

/// <summary>Sinirda 404 + <c>errorCode</c>.</summary>
public sealed class NotFoundException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
