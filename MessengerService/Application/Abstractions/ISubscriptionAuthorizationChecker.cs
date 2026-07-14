using MessengerService.Application.Realtime;

namespace MessengerService.Application.Abstractions;

public interface ISubscriptionAuthorizationChecker
{
    Task<SubscriptionEventAuthorization> AuthorizeConversationEventAsync(
        long userId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task<SubscriptionEventAuthorization> AuthorizeInboxEventAsync(
        long userId,
        RealtimeEvent message,
        CancellationToken cancellationToken = default);

    Task<SubscriptionEventAuthorization> AuthorizePresenceEventAsync(
        long viewerUserId,
        long subjectUserId,
        CancellationToken cancellationToken = default);
}

public enum SubscriptionEventAuthorization
{
    Allow,
    Skip,
    Terminate
}
