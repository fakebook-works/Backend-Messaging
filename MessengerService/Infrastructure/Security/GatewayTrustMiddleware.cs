using System.Globalization;
using MessengerService.Configuration;
using MessengerService.Contracts.Internal;
using Microsoft.Extensions.Options;

namespace MessengerService.Infrastructure.Security;

public sealed class GatewayTrustMiddleware(
    RequestDelegate next,
    IOptions<GatewayOptions> options,
    ILogger<GatewayTrustMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IMessagingUserProvisioningService users)
    {
        if (!context.Request.Path.StartsWithSegments("/graphql"))
        {
            await next(context);
            return;
        }

        var configuredSecret = options.Value.InternalSharedSecret;
        if (!FixedTimeSecretComparer.IsStrongEnough(configuredSecret))
        {
            logger.LogCritical("The gateway shared secret is missing or too short.");
            await SecurityProblemWriter.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Messaging trust configuration is unavailable.",
                "SECURITY_MISCONFIGURED",
                context.RequestAborted);
            return;
        }

        if (!TrustedHeaderReader.TryReadSingle(
                context.Request.Headers,
                MessagingHeaders.GatewaySecret,
                out var suppliedSecret) ||
            !FixedTimeSecretComparer.Matches(suppliedSecret, configuredSecret))
        {
            await SecurityProblemWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "A trusted gateway is required.",
                "INVALID_GATEWAY_CREDENTIALS",
                context.RequestAborted);
            return;
        }

        if (!TrustedHeaderReader.TryReadSingle(
                context.Request.Headers,
                MessagingHeaders.UserId,
                out var rawUserId) ||
            !long.TryParse(
                rawUserId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var userId) ||
            userId <= 0)
        {
            await SecurityProblemWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "A valid trusted user is required.",
                "INVALID_TRUSTED_USER",
                context.RequestAborted);
            return;
        }

        bool isActive;
        try
        {
            isActive = await users.IsActiveAsync(userId, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not verify the trusted user's Messaging state.");
            await SecurityProblemWriter.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "Messaging user state is unavailable.",
                "USER_STATE_UNAVAILABLE",
                context.RequestAborted);
            return;
        }

        if (!isActive)
        {
            await SecurityProblemWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "The trusted user is not active in Messaging.",
                "MESSAGING_USER_NOT_ACTIVE",
                context.RequestAborted);
            return;
        }

        context.Items[TrustedUserContextAccessor.HttpContextItemKey] =
            new TrustedUserContext(userId);

        await next(context);
    }
}
