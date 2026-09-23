using Microsoft.AspNetCore.Diagnostics;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Common;
using MrHobist.AITeam.Domain;

namespace MrHobist.AITeam.Api.Errors;

/// <summary>
/// Sinir: alan istisnalari Problem Details + <c>errorCode</c> olur (docs/error-codes.md).
/// <see cref="DomainException"/> 400 · <see cref="NotFoundException"/> 404 ·
/// <see cref="RuntimeUnavailableException"/> 503 · <see cref="RuntimeErrorException"/> 502 · kalanlar 500 (ayrinti sizdirilmaz).
/// </summary>
public sealed partial class ProblemMapping(ILogger<ProblemMapping> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var (status, code, detail) = exception switch
        {
            DomainException d => (StatusCode(d.ErrorCode), d.ErrorCode, d.Message),
            NotFoundException n => (StatusCodes.Status404NotFound, n.ErrorCode, n.Message),
            RuntimeUnavailableException r => (StatusCodes.Status503ServiceUnavailable, ErrorCodes.RuntimeUnavailable, r.Message),
            RuntimeErrorException r => (StatusCodes.Status502BadGateway, ErrorCodes.RuntimeError, r.Message),
            BadHttpRequestException b => (StatusCodes.Status400BadRequest, "request.invalid", b.Message),
            _ => (StatusCodes.Status500InternalServerError, "internal", "Beklenmeyen hata."),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            // Sessiz 500 saatler kaybettirir (LESSONS): ayrinti istemciye degil, log'a.
            LogUnexpected(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(Problem(status, code, detail, httpContext), cancellationToken).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "beklenmeyen hata: {Method} {Path}")]
    private static partial void LogUnexpected(ILogger logger, Exception ex, string method, PathString path);

    public static IResult Result(int status, string errorCode, string detail)
        => Results.Problem(detail: detail, statusCode: status, extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode });

    private static int StatusCode(string errorCode) => errorCode switch
    {
        ErrorCodes.ConfigFileInvalid or ErrorCodes.KnowledgeInvalidKey => StatusCodes.Status500InternalServerError,
        ErrorCodes.AuthInvalidCredentials or ErrorCodes.AuthRequired => StatusCodes.Status401Unauthorized,
        ErrorCodes.ConfigFileMissing or ErrorCodes.WorkflowNotFound or ErrorCodes.ProjectNotFound or ErrorCodes.ProjectLaunchMissing or ErrorCodes.KnowledgeNotFound or ErrorCodes.McpNotFound or ErrorCodes.McpCatalogNotFound => StatusCodes.Status404NotFound,
        ErrorCodes.AgentExists or ErrorCodes.AgentInUse or ErrorCodes.WorkflowDefaultProtected
            or ErrorCodes.RunNotAwaitingApproval or ErrorCodes.RunNotRetryable or ErrorCodes.RunNotCancellable or ErrorCodes.RunNotAwaitingInput
            or ErrorCodes.ProjectExists or ErrorCodes.ProjectInUse or ErrorCodes.KnowledgeInUse
            or ErrorCodes.McpExists or ErrorCodes.McpInUse => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

    private static Microsoft.AspNetCore.Mvc.ProblemDetails Problem(int status, string code, string detail, HttpContext ctx) => new()
    {
        Status = status,
        Title = status switch { 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 409 => "Conflict", 502 => "Bad Gateway", 503 => "Service Unavailable", _ => "Error" },
        Detail = detail,
        Extensions = { ["errorCode"] = code, ["traceId"] = ctx.TraceIdentifier },
    };
}
