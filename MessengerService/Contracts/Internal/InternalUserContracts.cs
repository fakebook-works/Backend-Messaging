namespace MessengerService.Contracts.Internal;

public sealed record CreateInternalUserRequest(long UserId);

public sealed record InternalUserResponse(long UserId);

public enum ProvisionUserOutcome
{
    Created,
    AlreadyActive,
    DeletedTombstone
}

public interface IMessagingUserProvisioningService
{
    Task<ProvisionUserOutcome> ProvisionAsync(
        long userId,
        CancellationToken cancellationToken = default);

    Task TombstoneAsync(
        long userId,
        CancellationToken cancellationToken = default);

    Task<bool> IsActiveAsync(
        long userId,
        CancellationToken cancellationToken = default);
}
