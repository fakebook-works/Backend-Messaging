using MessengerService.Application.Abstractions;

namespace MessengerService.Infrastructure.Security;

internal sealed record TrustedUserContext(long UserId);

public sealed class TrustedUserContextAccessor(IHttpContextAccessor httpContextAccessor)
    : ITrustedUserContextAccessor
{
    internal static readonly object HttpContextItemKey = new();

    public long RequireUserId()
    {
        var context = httpContextAccessor.HttpContext;
        if (context?.Items.TryGetValue(HttpContextItemKey, out var value) == true &&
            value is TrustedUserContext trustedUser)
        {
            return trustedUser.UserId;
        }

        throw new InvalidOperationException(
            "A trusted user is not available for the current request. " +
            "Ensure GatewayTrustMiddleware runs before the GraphQL endpoint.");
    }
}
