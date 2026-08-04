namespace MessengerService.Application.Abstractions;

public interface ISocialGraphPermissionClient
{
    Task<SocialGraphPermissionCheckResult> CheckAsync(
        long actorUserId,
        IReadOnlyCollection<long> targetUserIds,
        SocialGraphPermissionAction action,
        CancellationToken cancellationToken);
}

public enum SocialGraphPermissionAction
{
    CreateDirect,
    SendDirect,
    AddGroupMembers,
    /// <summary>
    /// Read-only block-state inspection. This deliberately does not grant a
    /// messaging operation; it lets Messenger filter group reads/presence and
    /// expose the two directional states without treating non-friends as
    /// permission failures.
    /// </summary>
    InspectBlock
}

public sealed record SocialGraphPermissionDecision(
    long TargetUserId,
    bool Allowed,
    bool IsFriend,
    bool BlockedEitherDirection,
    string? Reason,
    bool ActorBlockedTarget = false,
    bool TargetBlockedActor = false);

public sealed record SocialGraphPermissionCheckResult(
    IReadOnlyList<SocialGraphPermissionDecision> Decisions);
