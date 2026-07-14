using MessengerService.Application;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Models;

namespace MessengerService.GraphQL;

[GraphQLName("Query")]
public sealed class MessagingQuery
{
    public Task<ConversationPage> MyConversations(
        int first = 20,
        string? after = null,
        [Service] MessagingApplicationService messaging = default!,
        [Service] ITrustedUserContextAccessor userContext = default!,
        CancellationToken cancellationToken = default) =>
        messaging.GetMyConversationsAsync(userContext.RequireUserId(), first, after, cancellationToken);

    public Task<ConversationView> Conversation(
        Guid id,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.GetConversationAsync(userContext.RequireUserId(), id, cancellationToken);

    public Task<MessagePage> ConversationMessages(
        Guid conversationId,
        int last = 30,
        string? before = null,
        [Service] MessagingApplicationService messaging = default!,
        [Service] ITrustedUserContextAccessor userContext = default!,
        CancellationToken cancellationToken = default) =>
        messaging.GetMessagesAsync(userContext.RequireUserId(), conversationId, last, before, cancellationToken);

    public Task<IReadOnlyList<UserPresenceView>> UserPresence(
        IReadOnlyList<long> userIds,
        [Service] MessagingApplicationService messaging,
        [Service] ITrustedUserContextAccessor userContext,
        CancellationToken cancellationToken) =>
        messaging.GetPresenceAsync(userContext.RequireUserId(), userIds, cancellationToken);
}
