using HotChocolate.Execution;
using HotChocolate.Subscriptions;
using HotChocolate.Types;
using MessengerService.Application;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Realtime;

namespace MessengerService.GraphQL;

[GraphQLName("Subscription")]
public sealed class MessagingSubscription
{
    [Subscribe(With = nameof(SubscribeToConversationEventsAsync))]
    public RealtimeEvent ConversationEvents([EventMessage] RealtimeEvent message) => message;

    public async ValueTask<ISourceStream<RealtimeEvent>> SubscribeToConversationEventsAsync(
        Guid conversationId,
        [Service] MessagingApplicationService messaging,
        [Service] ISubscriptionAuthorizationChecker authorizationChecker,
        [Service] ITrustedUserContextAccessor userContext,
        [Service] ITopicEventReceiver receiver,
        CancellationToken cancellationToken)
    {
        var userId = userContext.RequireUserId();
        await messaging.AuthorizeConversationSubscriptionAsync(userId, conversationId, cancellationToken);
        var source = await receiver.SubscribeAsync<RealtimeEvent>(
            RealtimeTopics.Conversation(conversationId), cancellationToken);
        return new AuthorizationFilteringSourceStream(
            source,
            (_, eventCancellationToken) => authorizationChecker.AuthorizeConversationEventAsync(
                userId, conversationId, eventCancellationToken));
    }

    [Subscribe(With = nameof(SubscribeToInboxEventsAsync))]
    public RealtimeEvent InboxEvents([EventMessage] RealtimeEvent message) => message;

    public async ValueTask<ISourceStream<RealtimeEvent>> SubscribeToInboxEventsAsync(
        [Service] MessagingApplicationService messaging,
        [Service] ISubscriptionAuthorizationChecker authorizationChecker,
        [Service] ITrustedUserContextAccessor userContext,
        [Service] ITopicEventReceiver receiver,
        CancellationToken cancellationToken)
    {
        var userId = userContext.RequireUserId();
        await messaging.AuthorizeInboxSubscriptionAsync(userId, cancellationToken);
        var source = await receiver.SubscribeAsync<RealtimeEvent>(
            RealtimeTopics.Inbox(userId), cancellationToken);
        return new AuthorizationFilteringSourceStream(
            source,
            (message, eventCancellationToken) => authorizationChecker.AuthorizeInboxEventAsync(
                userId, message, eventCancellationToken));
    }

    [Subscribe(With = nameof(SubscribeToPresenceEventsAsync))]
    public RealtimeEvent PresenceEvents([EventMessage] RealtimeEvent message) => message;

    public async ValueTask<ISourceStream<RealtimeEvent>> SubscribeToPresenceEventsAsync(
        IReadOnlyList<long> userIds,
        [Service] MessagingApplicationService messaging,
        [Service] ISubscriptionAuthorizationChecker authorizationChecker,
        [Service] ITrustedUserContextAccessor userContext,
        [Service] ITopicEventReceiver receiver,
        CancellationToken cancellationToken)
    {
        var allowedIds = await messaging.AuthorizePresenceSubscriptionAsync(
            userContext.RequireUserId(), userIds, cancellationToken);
        var source = await receiver.SubscribeAsync<RealtimeEvent>(RealtimeTopics.Presence, cancellationToken);
        var viewerUserId = userContext.RequireUserId();
        return new AuthorizationFilteringSourceStream(
            source,
            async (message, eventCancellationToken) =>
            {
                if (message.UserId is not { } subjectUserId)
                {
                    return SubscriptionEventAuthorization.Skip;
                }

                var authorization = await authorizationChecker.AuthorizePresenceEventAsync(
                    viewerUserId, subjectUserId, eventCancellationToken);
                return authorization == SubscriptionEventAuthorization.Terminate ||
                       allowedIds.Contains(subjectUserId)
                    ? authorization
                    : SubscriptionEventAuthorization.Skip;
            });
    }
}
