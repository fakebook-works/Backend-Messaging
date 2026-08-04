using MessengerService.Application.Realtime;

namespace MessengerService.Application.Abstractions;

public interface ISubscriptionAuthorizationChecker
{
    Task<SubscriptionEventAuthorization> AuthorizeConversationEventAsync(
        long userId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authorizes one concrete event. Implementations use the event actor to
    /// apply directional block filtering while retaining the durable
    /// membership check above.
    /// </summary>
    Task<SubscriptionEventAuthorization> AuthorizeConversationEventAsync(
        long userId,
        Guid conversationId,
        RealtimeEvent message,
        CancellationToken cancellationToken = default) =>
        AuthorizeConversationEventAsync(userId, conversationId, cancellationToken);

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
