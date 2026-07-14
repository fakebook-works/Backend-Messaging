using MessengerService.Configuration;
using Microsoft.Extensions.Options;

namespace MessengerService.Infrastructure.Security;

public sealed class InternalApiAuthenticationMiddleware(
    RequestDelegate next,
    IOptions<InternalServicesOptions> options,
    ILogger<InternalApiAuthenticationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/internal"))
        {
            await next(context);
            return;
        }

        var configuredSecret = options.Value.MessengerSharedSecret;
        if (!FixedTimeSecretComparer.IsStrongEnough(configuredSecret))
        {
            logger.LogCritical("The internal service shared secret is missing or too short.");
            await SecurityProblemWriter.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Internal API trust configuration is unavailable.",
                "SECURITY_MISCONFIGURED",
                context.RequestAborted);
            return;
        }

        if (!TrustedHeaderReader.TryReadSingle(
                context.Request.Headers,
                MessagingHeaders.InternalServiceSecret,
                out var suppliedSecret) ||
            !FixedTimeSecretComparer.Matches(suppliedSecret, configuredSecret))
        {
            await SecurityProblemWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "Valid internal service credentials are required.",
                "INVALID_INTERNAL_CREDENTIALS",
                context.RequestAborted);
            return;
        }

        await next(context);
    }
}
