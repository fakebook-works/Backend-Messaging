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
    /// <summary>Upper bound on conversations one subscription may watch.</summary>
    /// <remarks>
    /// The dock shows at most three chat windows and the messenger page one more, so this is
    /// generous. It exists so a client cannot ask the server to open an unbounded number of
    /// topic subscriptions behind a single connection.
    /// </remarks>
    private const int MaxSubscribedConversations = 25;

    [Subscribe(With = nameof(SubscribeToConversationEventsAsync))]
    public RealtimeEvent ConversationEvents([EventMessage] RealtimeEvent message) => message;

    /// <remarks>
    /// Takes a list rather than a single conversation so a client watching several chats
    /// needs one connection instead of one per chat. Browsers cap concurrent connections per
    /// origin, and every server-sent events stream holds one open for as long as the chat is
    /// on screen, so the per-conversation form starved the rest of the application of
    /// connections once a few windows were open.
    /// </remarks>
    public async ValueTask<ISourceStream<RealtimeEvent>> SubscribeToConversationEventsAsync(
        IReadOnlyList<Guid> conversationIds,
        [Service] MessagingApplicationService messaging,
        [Service] ISubscriptionAuthorizationChecker authorizationChecker,
        [Service] ITrustedUserContextAccessor userContext,
        [Service] ITopicEventReceiver receiver,
        CancellationToken cancellationToken)
    {
        var userId = userContext.RequireUserId();
        var requested = conversationIds.Distinct().ToArray();
        if (requested.Length == 0)
        {
            throw new MessagingApplicationException(
                "CONVERSATION_IDS_REQUIRED",
                "At least one conversation must be supplied.");
        }

        if (requested.Length > MaxSubscribedConversations)
        {
            throw new MessagingApplicationException(
                "TOO_MANY_CONVERSATIONS",
                $"At most {MaxSubscribedConversations} conversations can be watched by one subscription.");
        }

        // Membership is checked up front for every conversation, exactly as the
        // single-conversation form did, so an unauthorised id fails the whole subscribe
        // rather than being silently dropped.
        var allowed = new HashSet<Guid>(requested.Length);
        var sources = new List<ISourceStream<RealtimeEvent>>(requested.Length);
        foreach (var conversationId in requested)
        {
            await messaging.AuthorizeConversationSubscriptionAsync(userId, conversationId, cancellationToken);
            allowed.Add(conversationId);
            sources.Add(await receiver.SubscribeAsync<RealtimeEvent>(
                RealtimeTopics.Conversation(conversationId), cancellationToken));
        }

        return new AuthorizationFilteringSourceStream(
            new MergedSourceStream(sources),
            async (message, eventCancellationToken) =>
            {
                // Every event now has to say which conversation it belongs to, since one
                // stream carries several. ConversationId is nullable on the contract, so an
                // event without one is dropped rather than trusted.
                if (message.ConversationId is not { } conversationId || !allowed.Contains(conversationId))
                {
                    return SubscriptionEventAuthorization.Skip;
                }

                var decision = await authorizationChecker.AuthorizeConversationEventAsync(
                    userId, conversationId, eventCancellationToken);

                // The checker answers Terminate when the viewer may not see this conversation,
                // which was right when the stream carried one. Here it would tear down every
                // other conversation too, so losing access to one is downgraded to dropping
                // its events. A viewer who loses their session entirely is still disconnected —
                // the gateway re-checks the session while a subscription is open.
                return decision == SubscriptionEventAuthorization.Terminate
                    ? SubscriptionEventAuthorization.Skip
                    : decision;
            });
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
