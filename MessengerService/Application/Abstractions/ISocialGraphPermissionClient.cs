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
    AddGroupMembers
}

public sealed record SocialGraphPermissionDecision(
    long TargetUserId,
    bool Allowed,
    bool IsFriend,
    bool BlockedEitherDirection,
    string? Reason);

public sealed record SocialGraphPermissionCheckResult(
    IReadOnlyList<SocialGraphPermissionDecision> Decisions);
