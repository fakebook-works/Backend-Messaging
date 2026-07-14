using MessengerService.Contracts.Internal;
using MessengerService.Application.Realtime;
using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MessengerService.Controllers;

public sealed class MessagingUserProvisioningService(MessagingDbContext dbContext)
    : IMessagingUserProvisioningService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProvisionUserOutcome> ProvisionAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        EnsurePositive(userId);

        if (dbContext.Database.IsNpgsql())
        {
            return await ProvisionPostgresAsync(userId, cancellationToken);
        }

        return await ProvisionWithoutTransactionsAsync(userId, cancellationToken);
    }

    private async Task<ProvisionUserOutcome> ProvisionPostgresAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await AcquireUserLifecycleLockAsync(userId, cancellationToken);

        var existing = await LockUserAsync(userId, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return OutcomeFor(existing);
        }

        var user = NewUser(userId, MessagingUserStatus.Active, DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProvisionUserOutcome.Created;
    }

    private async Task<ProvisionUserOutcome> ProvisionWithoutTransactionsAsync(
        long userId,
        CancellationToken cancellationToken)
    {

        var existing = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.UserId == userId, cancellationToken);

        if (existing is not null)
        {
            return OutcomeFor(existing);
        }

        var user = NewUser(userId, MessagingUserStatus.Active, DateTimeOffset.UtcNow);

        dbContext.Users.Add(user);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return ProvisionUserOutcome.Created;
        }
        catch (DbUpdateException)
        {
            // An at-least-once delivery can race another create/delete request.
            // Detach our failed insert and use the winner's durable state.
            dbContext.Entry(user).State = EntityState.Detached;

            var winner = await dbContext.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);

            if (winner is null)
            {
                throw;
            }

            return OutcomeFor(winner);
        }
    }

    public async Task TombstoneAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        EnsurePositive(userId);

        if (dbContext.Database.IsNpgsql())
        {
            await TombstonePostgresAsync(userId, cancellationToken);
            return;
        }

        await TombstoneWithoutTransactionsAsync(userId, cancellationToken);
    }

    public Task<bool> IsActiveAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return Task.FromResult(false);
        }

        return dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                user => user.UserId == userId && user.Status == MessagingUserStatus.Active,
                cancellationToken);
    }

    private static void MarkDeleted(MessagingUser user, DateTimeOffset deletedAt)
    {
        user.Status = MessagingUserStatus.Deleted;
        user.DeletedAt = deletedAt;
    }

    private async Task TombstonePostgresAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // The advisory lock serializes lifecycle calls even when the user row
        // does not exist yet. The row lock below coordinates deletion with all
        // operations (notably presence heartbeat) that mutate an existing user.
        await AcquireUserLifecycleLockAsync(userId, cancellationToken);
        var existing = await LockUserAsync(userId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var publishRevocation = existing?.Status == MessagingUserStatus.Active;

        if (existing is null)
        {
            dbContext.Users.Add(NewUser(userId, MessagingUserStatus.Deleted, now));
        }
        else if (existing.Status == MessagingUserStatus.Active)
        {
            MarkDeleted(existing, now);
        }

        await RevokeMessagingAccessAsync(
            userId,
            now,
            lockRows: true,
            publishRevocation: publishRevocation,
            cancellationToken: cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task TombstoneWithoutTransactionsAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await dbContext.Users
            .SingleOrDefaultAsync(user => user.UserId == userId, cancellationToken);
        var publishRevocation = existing?.Status == MessagingUserStatus.Active;

        if (existing is null)
        {
            dbContext.Users.Add(NewUser(userId, MessagingUserStatus.Deleted, now));
        }
        else if (existing.Status == MessagingUserStatus.Active)
        {
            MarkDeleted(existing, now);
        }

        // EF InMemory does not support relational transactions or raw SQL. A
        // single SaveChanges still gives its unit tests an atomic state change.
        await RevokeMessagingAccessAsync(
            userId,
            now,
            lockRows: false,
            publishRevocation: publishRevocation,
            cancellationToken: cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RevokeMessagingAccessAsync(
        long userId,
        DateTimeOffset now,
        bool lockRows,
        bool publishRevocation,
        CancellationToken cancellationToken)
    {
        var presence = lockRows
            ? await dbContext.UserPresences
                .FromSqlInterpolated(
                    $"SELECT * FROM messenger.presence WHERE user_id = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.UserPresences
                .SingleOrDefaultAsync(value => value.UserId == userId, cancellationToken);

        // Read IDs in PostgreSQL UUID order, then acquire each conversation
        // lock in exactly that order. Re-query memberships only after all locks
        // are held so concurrent admin removals choose a single successor.
        var conversationIds = await dbContext.ConversationParticipants
            .AsNoTracking()
            .Where(participant => participant.UserId == userId && participant.LeftAt == null)
            .OrderBy(participant => participant.ConversationId)
            .Select(participant => participant.ConversationId)
            .ToListAsync(cancellationToken);

        var conversations = new Dictionary<Guid, Conversation>(conversationIds.Count);
        foreach (var conversationId in conversationIds)
        {
            var conversation = lockRows
                ? await dbContext.Conversations
                    .FromSqlInterpolated(
                        $"SELECT * FROM messenger.conversations WHERE id = {conversationId} FOR UPDATE")
                    .SingleAsync(cancellationToken)
                : await dbContext.Conversations
                    .SingleAsync(value => value.Id == conversationId, cancellationToken);
            conversations.Add(conversationId, conversation);
        }

        var memberships = await dbContext.ConversationParticipants
            .Where(participant => participant.UserId == userId && participant.LeftAt == null)
            .ToListAsync(cancellationToken);

        var presenceChanged = presence?.IsOnline == true;
        if (presence is not null)
        {
            presence.IsOnline = false;
            presence.ExpiresAt = now;
            presence.UpdatedAt = now;
        }

        foreach (var membership in memberships)
        {
            membership.LeftAt = now;
        }

        foreach (var membership in memberships.Where(value => value.Role == ParticipantRole.Admin))
        {
            var conversation = conversations[membership.ConversationId];
            if (conversation.Type != ConversationType.Group)
            {
                continue;
            }

            var anotherAdminExists = await dbContext.ConversationParticipants.AnyAsync(
                participant => participant.ConversationId == membership.ConversationId &&
                               participant.UserId != userId &&
                               participant.LeftAt == null &&
                               participant.Role == ParticipantRole.Admin,
                cancellationToken);
            if (!anotherAdminExists)
            {
                var successor = await dbContext.ConversationParticipants
                    .Where(participant => participant.ConversationId == membership.ConversationId &&
                                          participant.UserId != userId &&
                                          participant.LeftAt == null)
                    .OrderBy(participant => participant.JoinedAt)
                    .ThenBy(participant => participant.UserId)
                    .FirstOrDefaultAsync(cancellationToken);
                if (successor is not null)
                {
                    successor.Role = ParticipantRole.Admin;
                }
            }
        }

        foreach (var conversationId in memberships.Select(value => value.ConversationId).Distinct())
        {
            var conversation = conversations[conversationId];
            conversation.UpdatedAt = now;

            var realtimeEvent = new RealtimeEvent(
                Guid.NewGuid(),
                RealtimeEventKinds.MemberRemoved,
                conversationId,
                null,
                userId,
                null,
                now);
            var recipients = await dbContext.ConversationParticipants
                .Where(participant => participant.ConversationId == conversationId &&
                                      participant.UserId != userId &&
                                      participant.LeftAt == null)
                .Select(participant => participant.UserId)
                .ToListAsync(cancellationToken);

            AddOutboxEvent(realtimeEvent, RealtimeTopics.Conversation(conversationId), now);
            foreach (var recipient in recipients)
            {
                AddOutboxEvent(realtimeEvent, RealtimeTopics.Inbox(recipient), now);
            }
        }

        var revokeStreams = publishRevocation || memberships.Count > 0 || presenceChanged;
        if (revokeStreams)
        {
            var realtimeEvent = new RealtimeEvent(
                Guid.NewGuid(),
                RealtimeEventKinds.AccessRevoked,
                null,
                null,
                userId,
                null,
                now);
            AddOutboxEvent(realtimeEvent, RealtimeTopics.Inbox(userId), now);
        }

        if (revokeStreams)
        {
            var realtimeEvent = new RealtimeEvent(
                Guid.NewGuid(),
                RealtimeEventKinds.PresenceChanged,
                null,
                null,
                userId,
                null,
                now);
            AddOutboxEvent(realtimeEvent, RealtimeTopics.Presence, now);
        }

    }

    private Task AcquireUserLifecycleLockAsync(long userId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({-userId})",
            cancellationToken);

    private Task<MessagingUser?> LockUserAsync(long userId, CancellationToken cancellationToken) =>
        dbContext.Users
            .FromSqlInterpolated(
                $"SELECT * FROM messenger.users WHERE user_id = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private static MessagingUser NewUser(
        long userId,
        MessagingUserStatus status,
        DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            Status = status,
            CreatedAt = now,
            DeletedAt = status == MessagingUserStatus.Deleted ? now : null
        };

    private static ProvisionUserOutcome OutcomeFor(MessagingUser user) =>
        user.Status == MessagingUserStatus.Active
            ? ProvisionUserOutcome.AlreadyActive
            : ProvisionUserOutcome.DeletedTombstone;

    private void AddOutboxEvent(RealtimeEvent realtimeEvent, string topic, DateTimeOffset now)
    {
        dbContext.OutboxEvents.Add(new OutboxEvent
        {
            Id = Guid.NewGuid(),
            Topic = topic,
            Kind = realtimeEvent.Kind,
            PayloadJson = JsonSerializer.Serialize(realtimeEvent, JsonOptions),
            ConversationId = realtimeEvent.ConversationId,
            ActorUserId = realtimeEvent.UserId,
            OccurredAt = now,
            CreatedAt = now
        });
    }

    private static void EnsurePositive(long userId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
    }
}
