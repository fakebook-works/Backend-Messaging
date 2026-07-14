using Microsoft.AspNetCore.Mvc;

namespace MessengerService.Infrastructure.Security;

internal static class SecurityProblemWriter
{
    public static Task WriteAsync(
        HttpContext context,
        int statusCode,
        string title,
        string code,
        CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = statusCode;

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Instance = context.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = context.TraceIdentifier;

        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(problem, cancellationToken);
    }
}
