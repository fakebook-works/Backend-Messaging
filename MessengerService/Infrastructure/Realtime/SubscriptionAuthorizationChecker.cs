using MessengerService.Application.Abstractions;
using MessengerService.Application.Realtime;
using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MessengerService.Infrastructure.Realtime;

/// <summary>
/// Revalidates durable authorization for every subscription event. A fresh scope
/// prevents a long-lived subscription from observing stale EF tracked entities.
/// </summary>
public sealed class SubscriptionAuthorizationChecker(IServiceScopeFactory scopeFactory)
    : ISubscriptionAuthorizationChecker
{
    public async Task<SubscriptionEventAuthorization> AuthorizeConversationEventAsync(
        long userId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var canReceive = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.UserId == userId)
            .Select(user =>
                user.Status == MessagingUserStatus.Active &&
                dbContext.ConversationParticipants.Any(participant =>
                    participant.ConversationId == conversationId &&
                    participant.UserId == userId &&
                    participant.LeftAt == null))
            .SingleOrDefaultAsync(cancellationToken);

        return canReceive
            ? SubscriptionEventAuthorization.Allow
            : SubscriptionEventAuthorization.Terminate;
    }

    public async Task<SubscriptionEventAuthorization> AuthorizeInboxEventAsync(
        long userId,
        RealtimeEvent message,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var state = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.UserId == userId)
            .Select(user => new InboxAuthorizationState(
                user.Status == MessagingUserStatus.Active,
                message.ConversationId == null ||
                dbContext.ConversationParticipants.Any(participant =>
                    participant.ConversationId == message.ConversationId &&
                    participant.UserId == userId &&
                    participant.LeftAt == null)))
            .SingleOrDefaultAsync(cancellationToken);

        if (state is null || !state.IsActive)
        {
            return SubscriptionEventAuthorization.Terminate;
        }

        // A removed member must learn about their own removal through the inbox,
        // but no other delayed event from that conversation may cross the boundary.
        if (message.Kind == RealtimeEventKinds.MemberRemoved && message.UserId == userId)
        {
            return SubscriptionEventAuthorization.Allow;
        }

        return state.CanAccessConversation
            ? SubscriptionEventAuthorization.Allow
            : SubscriptionEventAuthorization.Skip;
    }

    public async Task<SubscriptionEventAuthorization> AuthorizePresenceEventAsync(
        long viewerUserId,
        long subjectUserId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var state = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.UserId == viewerUserId)
            .Select(user => new PresenceAuthorizationState(
                user.Status == MessagingUserStatus.Active,
                viewerUserId == subjectUserId ||
                dbContext.ConversationParticipants.Any(subject =>
                    subject.UserId == subjectUserId &&
                    subject.LeftAt == null &&
                    dbContext.ConversationParticipants.Any(viewer =>
                        viewer.ConversationId == subject.ConversationId &&
                        viewer.UserId == viewerUserId &&
                        viewer.LeftAt == null))))
            .SingleOrDefaultAsync(cancellationToken);

        if (state is null || !state.IsActive)
        {
            return SubscriptionEventAuthorization.Terminate;
        }

        return state.CanSeeSubject
            ? SubscriptionEventAuthorization.Allow
            : SubscriptionEventAuthorization.Skip;
    }

    private sealed record InboxAuthorizationState(bool IsActive, bool CanAccessConversation);

    private sealed record PresenceAuthorizationState(bool IsActive, bool CanSeeSubject);
}
