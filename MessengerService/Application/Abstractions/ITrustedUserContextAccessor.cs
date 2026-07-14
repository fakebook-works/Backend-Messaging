namespace MessengerService.Application.Abstractions;

/// <summary>
/// Provides the authenticated user established by the trusted gateway middleware.
/// Implementations must never read an untrusted client header directly.
/// </summary>
public interface ITrustedUserContextAccessor
{
    long RequireUserId();
}
