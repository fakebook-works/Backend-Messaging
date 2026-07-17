using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Models;
using MessengerService.Application.Media;
using MessengerService.Application.Realtime;
using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using HotChocolate.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessengerService.Application;

public sealed class MessagingApplicationService(
    MessagingDbContext db,
    ISocialGraphPermissionClient socialGraph,
    ITopicEventSender topicSender,
    TimeProvider timeProvider,
    IOptions<MessagingRulesOptions> rulesOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly MessagingRulesOptions _rules = rulesOptions.Value;

    public async Task<ConversationPage> GetMyConversationsAsync(
        long userId,
        int first,
        string? after,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(userId, cancellationToken);
        first = RequirePageSize(first, 100);
        var offset = DecodeOffset(after);

        var query = db.Conversations.AsNoTracking()
            .Where(c => c.Participants.Any(p => p.UserId == userId && p.LeftAt == null))
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.Id);

        var rows = await query.Skip(offset).Take(first + 1).ToListAsync(cancellationToken);
        var hasNext = rows.Count > first;
        if (hasNext)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = new List<ConversationView>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(await MapConversationAsync(row, cancellationToken));
        }

        return new ConversationPage(
            items,
            new PageInfo(
                items.Count == 0 ? null : EncodeOffset(offset),
                items.Count == 0 ? null : EncodeOffset(offset + items.Count),
                hasNext,
                offset > 0));
    }

    public async Task<ConversationView> GetConversationAsync(
        long userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(userId, cancellationToken);
        await RequireParticipantAsync(conversationId, userId, cancellationToken);
        var conversation = await FindConversationAsync(conversationId, cancellationToken);
        return await MapConversationAsync(conversation, cancellationToken);
    }

    public async Task<MessagePage> GetMessagesAsync(
        long userId,
        Guid conversationId,
        int last,
        string? before,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(userId, cancellationToken);
        await RequireParticipantAsync(conversationId, userId, cancellationToken);
        last = RequirePageSize(last, 100);
        var beforeSequence = DecodeSequence(before) ?? long.MaxValue;

        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Sequence < beforeSequence)
            .OrderByDescending(m => m.Sequence)
            .Take(last + 1)
            .ToListAsync(cancellationToken);

        var hasPrevious = rows.Count > last;
        if (hasPrevious)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        rows.Reverse();
        var items = await MapMessagesAsync(rows, cancellationToken);
        return new MessagePage(
            items,
            new PageInfo(
                items.Count == 0 ? null : EncodeSequence(items[0].Sequence),
                items.Count == 0 ? null : EncodeSequence(items[^1].Sequence),
                false,
                hasPrevious));
    }

    public async Task<IReadOnlyList<UserPresenceView>> GetPresenceAsync(
        long userId,
        IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(userId, cancellationToken);
        var ids = NormalizeUserIds(userIds, allowEmpty: false);
        RequirePresenceListLimit(ids);
        await RequirePresenceVisibilityAsync(userId, ids, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var values = await db.UserPresences.AsNoTracking()
            .Where(p => ids.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, cancellationToken);

        return ids.Select(id => values.TryGetValue(id, out var value)
                ? new UserPresenceView(id, value.IsOnline && value.ExpiresAt > now,
                    value.IsOnline && value.ExpiresAt > now ? value.ExpiresAt : null, value.UpdatedAt)
                : new UserPresenceView(id, false, null, now))
            .ToArray();
    }

    public async Task<ConversationView> CreateDirectConversationAsync(
        long actorUserId,
        CreateDirectConversationCommand command,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        if (command.TargetUserId <= 0 || command.TargetUserId == actorUserId)
        {
            Throw(MessagingErrorCodes.InvalidInput, "A direct conversation requires another valid user.");
        }

        await RequireActiveUsersAsync([command.TargetUserId], cancellationToken);
        await RequireSocialPermissionAsync(actorUserId, [command.TargetUserId],
            SocialGraphPermissionAction.CreateDirect, cancellationToken);

        var low = Math.Min(actorUserId, command.TargetUserId);
        var high = Math.Max(actorUserId, command.TargetUserId);
        Conversation? existing;

        var now = timeProvider.GetUtcNow();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Type = ConversationType.Direct,
            DirectUserLowId = low,
            DirectUserHighId = high,
            CreatedAt = now,
            UpdatedAt = now
        };
        conversation.Participants.Add(NewParticipant(conversation.Id, actorUserId, ParticipantRole.Member, now));
        conversation.Participants.Add(NewParticipant(conversation.Id, command.TargetUserId, ParticipantRole.Member, now));

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockActiveUsersAsync([actorUserId, command.TargetUserId], cancellationToken);
        existing = await db.Conversations.FirstOrDefaultAsync(
            c => c.Type == ConversationType.Direct && c.DirectUserLowId == low && c.DirectUserHighId == high,
            cancellationToken);
        if (existing is not null)
        {
            var result = await MapConversationAsync(existing, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        db.Conversations.Add(conversation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            existing = await db.Conversations.FirstOrDefaultAsync(
                c => c.Type == ConversationType.Direct && c.DirectUserLowId == low && c.DirectUserHighId == high,
                cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return await MapConversationAsync(existing, cancellationToken);
        }

        EnqueueEvent(NewEvent(RealtimeEventKinds.ConversationCreated, now, conversation.Id, userId: actorUserId),
            RealtimeTopics.Conversation(conversation.Id), RealtimeTopics.Inbox(actorUserId),
            RealtimeTopics.Inbox(command.TargetUserId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapConversationAsync(conversation, cancellationToken);
    }

    public async Task<ConversationView> CreateGroupConversationAsync(
        long actorUserId,
        CreateGroupConversationCommand command,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        var members = NormalizeUserIds(command.MemberUserIds, allowEmpty: false)
            .Where(id => id != actorUserId).ToArray();
        if (members.Length < 2 || members.Length + 1 > _rules.MaxGroupParticipants)
        {
            Throw(MessagingErrorCodes.InvalidInput,
                $"A group requires 3 to {_rules.MaxGroupParticipants} participants.");
        }

        var title = RequireText(command.Title, 120, "Group title");
        ValidateOptionalMediaUrl(command.AvatarUrl);
        await RequireActiveUsersAsync(members, cancellationToken);
        await RequireSocialPermissionAsync(actorUserId, members,
            SocialGraphPermissionAction.AddGroupMembers, cancellationToken);

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockActiveUsersAsync(members.Append(actorUserId), cancellationToken);

        var now = timeProvider.GetUtcNow();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Type = ConversationType.Group,
            Title = title,
            AvatarUrl = NormalizeOptional(command.AvatarUrl),
            CreatedAt = now,
            UpdatedAt = now
        };
        conversation.Participants.Add(NewParticipant(conversation.Id, actorUserId, ParticipantRole.Admin, now));
        foreach (var member in members)
        {
            conversation.Participants.Add(NewParticipant(conversation.Id, member, ParticipantRole.Member, now));
        }

        db.Conversations.Add(conversation);
        var ev = NewEvent(RealtimeEventKinds.ConversationCreated, now, conversation.Id, userId: actorUserId);
        EnqueueEvent(ev, [RealtimeTopics.Conversation(conversation.Id), .. members.Append(actorUserId).Select(RealtimeTopics.Inbox)]);
        await db.SaveChangesAsync(cancellationToken);
        var result = await MapConversationAsync(conversation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ConversationView> UpdateGroupConversationAsync(
        long actorUserId,
        UpdateGroupConversationCommand command,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        if (!command.HasTitle && !command.HasAvatarUrl)
        {
            Throw(MessagingErrorCodes.InvalidInput, "At least one group field must be supplied.");
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var conversation = await LockGroupConversationForAdminAsync(
            command.ConversationId,
            actorUserId,
            cancellationToken);

        if (command.HasTitle)
        {
            conversation.Title = RequireText(command.Title, 120, "Group title");
        }

        if (command.HasAvatarUrl)
        {
            ValidateOptionalMediaUrl(command.AvatarUrl);
            conversation.AvatarUrl = NormalizeOptional(command.AvatarUrl);
        }

        var now = timeProvider.GetUtcNow();
        conversation.UpdatedAt = now;
        var recipients = await ActiveParticipantIdsAsync(conversation.Id, cancellationToken);
        EnqueueEvent(NewEvent(RealtimeEventKinds.ConversationUpdated, now, conversation.Id, userId: actorUserId),
            [RealtimeTopics.Conversation(conversation.Id), .. recipients.Select(RealtimeTopics.Inbox)]);
        await db.SaveChangesAsync(cancellationToken);
        var result = await MapConversationAsync(conversation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ConversationView> AddConversationMembersAsync(
        long actorUserId,
        AddConversationMembersCommand command,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        var requested = NormalizeUserIds(command.UserIds, allowEmpty: false);

        // Run the remote permission check before taking the database lock. The
        // membership set is recomputed after the lock to protect local limits.
        var initialConversation = await RequireGroupAdminAsync(
            command.ConversationId,
            actorUserId,
            cancellationToken);
        var active = await db.ConversationParticipants
            .Where(p => p.ConversationId == initialConversation.Id && p.LeftAt == null)
            .Select(p => p.UserId).ToListAsync(cancellationToken);
        var toAdd = requested.Except(active).ToArray();
        var authorizedToAdd = new HashSet<long>();
        if (toAdd.Length > 0)
        {
            await RequireActiveUsersAsync(toAdd, cancellationToken);
            await RequireSocialPermissionAsync(actorUserId, toAdd,
                SocialGraphPermissionAction.AddGroupMembers, cancellationToken);
            authorizedToAdd.UnionWith(toAdd);
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockActiveUsersAsync(requested.Append(actorUserId), cancellationToken);
        var conversation = await LockGroupConversationForAdminAsync(
            command.ConversationId,
            actorUserId,
            cancellationToken);
        active = await db.ConversationParticipants
            .Where(p => p.ConversationId == conversation.Id && p.LeftAt == null)
            .Select(p => p.UserId)
            .ToListAsync(cancellationToken);
        toAdd = requested.Except(active).ToArray();
        if (toAdd.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return await MapConversationAsync(conversation, cancellationToken);
        }

        if (active.Count + toAdd.Length > _rules.MaxGroupParticipants)
        {
            Throw(MessagingErrorCodes.InvalidInput, "The group participant limit would be exceeded.");
        }

        // A concurrent leave can make a requested ID newly addable after the
        // preflight check. Fail closed instead of adding it without permission.
        if (toAdd.Any(userId => !authorizedToAdd.Contains(userId)))
        {
            Throw(MessagingErrorCodes.Conflict, "Group membership changed; retry the operation.");
        }

        await RequireActiveUsersAsync(toAdd, cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var userId in toAdd)
        {
            var participant = await db.ConversationParticipants.FindAsync(
                [conversation.Id, userId], cancellationToken);
            if (participant is null)
            {
                db.ConversationParticipants.Add(NewParticipant(conversation.Id, userId, ParticipantRole.Member, now));
            }
            else
            {
                participant.Role = ParticipantRole.Member;
                participant.JoinedAt = now;
                participant.LeftAt = null;
                participant.LastDeliveredSequence = 0;
                participant.LastReadSequence = 0;
            }
        }

        conversation.UpdatedAt = now;
        var recipients = active.Concat(toAdd).Distinct().ToArray();
        EnqueueEvent(NewEvent(RealtimeEventKinds.MemberAdded, now, conversation.Id, userId: actorUserId),
            [RealtimeTopics.Conversation(conversation.Id), .. recipients.Select(RealtimeTopics.Inbox)]);
        await db.SaveChangesAsync(cancellationToken);
        var result = await MapConversationAsync(conversation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<ConversationView> RemoveConversationMemberAsync(
        long actorUserId,
        RemoveConversationMemberCommand command,
        CancellationToken cancellationToken) =>
        RemoveOrLeaveAsync(actorUserId, command.ConversationId, command.UserId, requireAdmin: true, cancellationToken);

    public Task<ConversationView> LeaveConversationAsync(
        long actorUserId,
        Guid conversationId,
        CancellationToken cancellationToken) =>
        RemoveOrLeaveAsync(actorUserId, conversationId, actorUserId, requireAdmin: false, cancellationToken);

    public async Task<ConversationView> SetConversationMemberRoleAsync(
        long actorUserId,
        SetConversationMemberRoleCommand command,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var conversation = await LockGroupConversationForAdminAsync(
            command.ConversationId,
            actorUserId,
            cancellationToken);
        var participant = await db.ConversationParticipants.SingleOrDefaultAsync(
            p => p.ConversationId == conversation.Id && p.UserId == command.UserId && p.LeftAt == null,
            cancellationToken);
        if (participant is null)
        {
            Throw(MessagingErrorCodes.NotParticipant, "The target user is not an active participant.");
        }

        if (participant.Role == ParticipantRole.Admin && command.Role == ParticipantRole.Member)
        {
            await EnsureNotLastAdminAsync(conversation.Id, participant.UserId, cancellationToken);
        }

        participant.Role = command.Role;
        var now = timeProvider.GetUtcNow();
        conversation.UpdatedAt = now;
        var recipients = await ActiveParticipantIdsAsync(conversation.Id, cancellationToken);
        EnqueueEvent(NewEvent(RealtimeEventKinds.MemberRoleChanged, now, conversation.Id, userId: command.UserId),
            [RealtimeTopics.Conversation(conversation.Id), .. recipients.Select(RealtimeTopics.Inbox)]);
        await db.SaveChangesAsync(cancellationToken);
        var result = await MapConversationAsync(conversation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<MessageView> SendMessageAsync(
        long actorUserId,
        SendMessageCommand command,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        if (command.ClientMessageId == Guid.Empty)
        {
            Throw(MessagingErrorCodes.InvalidInput, "Client message ID cannot be empty.");
        }

        var text = NormalizeOptional(command.Text);
        if (text is not null && text.Length > _rules.MaxMessageLength)
        {
            Throw(MessagingErrorCodes.InvalidInput, $"Message text cannot exceed {_rules.MaxMessageLength} characters.");
        }

        if (command.AttachmentUrls.Count > _rules.MaxAttachmentsPerMessage)
        {
            Throw(MessagingErrorCodes.InvalidInput, $"A message supports at most {_rules.MaxAttachmentsPerMessage} attachments.");
        }

        foreach (var url in command.AttachmentUrls)
        {
            ValidateMediaUrl(url);
        }

        if (text is null && command.AttachmentUrls.Count == 0)
        {
            Throw(MessagingErrorCodes.InvalidInput, "A message requires text or an attachment.");
        }

        var conversation = await FindConversationAsync(command.ConversationId, cancellationToken);
        await RequireParticipantAsync(conversation.Id, actorUserId, cancellationToken);

        var idempotent = await db.Messages.AsNoTracking().SingleOrDefaultAsync(
            m => m.SenderUserId == actorUserId && m.ClientMessageId == command.ClientMessageId,
            cancellationToken);
        if (idempotent is not null)
        {
            if (idempotent.ConversationId != command.ConversationId)
            {
                Throw(MessagingErrorCodes.Conflict, "The client message ID is already used in another conversation.");
            }

            return await MapMessageAsync(idempotent, cancellationToken);
        }

        if (conversation.Type == ConversationType.Direct)
        {
            var otherUserId = conversation.DirectUserLowId == actorUserId
                ? conversation.DirectUserHighId!.Value
                : conversation.DirectUserLowId!.Value;
            await RequireActiveUserAsync(otherUserId, cancellationToken);
            await RequireSocialPermissionAsync(actorUserId, [otherUserId],
                SocialGraphPermissionAction.SendDirect, cancellationToken);
        }

        if (command.ReplyToMessageId is { } replyId && !await db.Messages.AsNoTracking()
                .AnyAsync(m => m.Id == replyId && m.ConversationId == conversation.Id, cancellationToken))
        {
            Throw(MessagingErrorCodes.InvalidInput, "The replied-to message does not belong to this conversation.");
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var usersToLock = conversation.Type == ConversationType.Direct
            ? new[]
            {
                actorUserId,
                conversation.DirectUserLowId == actorUserId
                    ? conversation.DirectUserHighId!.Value
                    : conversation.DirectUserLowId!.Value
            }
            : [actorUserId];
        await LockActiveUsersAsync(usersToLock, cancellationToken);
        conversation = await db.Conversations.FromSqlInterpolated(
                $"SELECT * FROM messenger.conversations WHERE id = {command.ConversationId} FOR UPDATE")
            .SingleAsync(cancellationToken);
        await RequireParticipantAsync(conversation.Id, actorUserId, cancellationToken);
        conversation.CurrentSequence++;
        var now = timeProvider.GetUtcNow();
        conversation.UpdatedAt = now;
        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = actorUserId,
            Sequence = conversation.CurrentSequence,
            ClientMessageId = command.ClientMessageId,
            Text = text,
            ReplyToMessageId = command.ReplyToMessageId,
            CreatedAt = now
        };
        for (var index = 0; index < command.AttachmentUrls.Count; index++)
        {
            message.Attachments.Add(new MessageAttachment
            {
                MessageId = message.Id,
                Ordinal = index,
                Url = command.AttachmentUrls[index]
            });
        }

        db.Messages.Add(message);
        var recipients = await ActiveParticipantIdsAsync(conversation.Id, cancellationToken);
        var ev = NewEvent(RealtimeEventKinds.MessageAdded, now, conversation.Id, message.Id,
            actorUserId, message.Sequence);
        EnqueueEvent(ev, [RealtimeTopics.Conversation(conversation.Id), .. recipients.Select(RealtimeTopics.Inbox)]);
        var finalizeMediaEvent = MediaLifecycleOutbox.Create(
            MediaLifecycleEventKinds.Finalize,
            command.AttachmentUrls,
            now,
            conversation.Id,
            message.Id,
            actorUserId);
        if (finalizeMediaEvent is not null)
        {
            db.OutboxEvents.Add(finalizeMediaEvent);
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var winner = await db.Messages.AsNoTracking().SingleOrDefaultAsync(
                m => m.SenderUserId == actorUserId && m.ClientMessageId == command.ClientMessageId,
                cancellationToken);
            if (winner is null)
            {
                throw;
            }

            if (winner.ConversationId != command.ConversationId)
            {
                Throw(MessagingErrorCodes.Conflict, "The client message ID is already used in another conversation.");
            }

            return await MapMessageAsync(winner, cancellationToken);
        }

        return await MapMessageAsync(message, cancellationToken);
    }

    public async Task<MessageView> EditMessageAsync(
        long actorUserId,
        EditMessageCommand command,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        var message = await RequireMessageAsync(command.MessageId, cancellationToken);
        var conversationId = message.ConversationId;
        var text = RequireText(command.Text, _rules.MaxMessageLength, "Message text");

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockConversationForActiveParticipantAsync(actorUserId, conversationId, cancellationToken);
        message = await RequireMessageAsync(command.MessageId, cancellationToken);
        if (message.SenderUserId != actorUserId)
        {
            Throw(MessagingErrorCodes.Forbidden, "Only the message author can edit it.");
        }

        if (message.DeletedAt is not null)
        {
            Throw(MessagingErrorCodes.MessageDeleted, "A deleted message cannot be edited.");
        }

        var now = timeProvider.GetUtcNow();
        if (message.CreatedAt.AddMinutes(_rules.EditWindowMinutes) < now)
        {
            Throw(MessagingErrorCodes.EditWindowExpired, "The message edit window has expired.");
        }

        message.Text = text;
        message.EditedAt = now;
        EnqueueEvent(NewEvent(RealtimeEventKinds.MessageEdited, now, message.ConversationId,
            message.Id, actorUserId, message.Sequence), RealtimeTopics.Conversation(message.ConversationId));
        await db.SaveChangesAsync(cancellationToken);
        var result = await MapMessageAsync(message, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<MessageView> DeleteMessageAsync(
        long actorUserId,
        DeleteMessageCommand command,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        var message = await RequireMessageAsync(command.MessageId, cancellationToken);
        var conversationId = message.ConversationId;

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockConversationForActiveParticipantAsync(actorUserId, conversationId, cancellationToken);
        message = await RequireMessageAsync(command.MessageId, cancellationToken);
        if (message.SenderUserId != actorUserId)
        {
            Throw(MessagingErrorCodes.Forbidden, "Only the message author can delete it.");
        }

        if (message.DeletedAt is null)
        {
            var now = timeProvider.GetUtcNow();
            var attachmentUrls = await db.MessageAttachments
                .AsNoTracking()
                .Where(attachment => attachment.MessageId == message.Id)
                .Select(attachment => attachment.Url)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            var sharedUrls = attachmentUrls.Length == 0
                ? []
                : await db.MessageAttachments
                    .AsNoTracking()
                    .Where(attachment =>
                        attachment.MessageId != message.Id &&
                        attachmentUrls.Contains(attachment.Url) &&
                        attachment.Message.DeletedAt == null)
                    .Select(attachment => attachment.Url)
                    .Distinct()
                    .ToArrayAsync(cancellationToken);
            message.DeletedAt = now;
            EnqueueEvent(NewEvent(RealtimeEventKinds.MessageDeleted, now, message.ConversationId,
                message.Id, actorUserId, message.Sequence), RealtimeTopics.Conversation(message.ConversationId));
            var deleteMediaEvent = MediaLifecycleOutbox.Create(
                MediaLifecycleEventKinds.Delete,
                attachmentUrls.Except(sharedUrls, StringComparer.OrdinalIgnoreCase),
                now,
                message.ConversationId,
                message.Id,
                actorUserId);
            if (deleteMediaEvent is not null)
            {
                db.OutboxEvents.Add(deleteMediaEvent);
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        var result = await MapMessageAsync(message, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<MessageView> SetMessageReactionAsync(
        long actorUserId,
        SetMessageReactionCommand command,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        var message = await RequireMessageAsync(command.MessageId, cancellationToken);
        var emoji = NormalizeOptional(command.Emoji);
        if (emoji is { Length: > 32 })
        {
            Throw(MessagingErrorCodes.InvalidInput, "A reaction cannot exceed 32 characters.");
        }

        var conversationId = message.ConversationId;
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockConversationForActiveParticipantAsync(actorUserId, conversationId, cancellationToken);
        message = await RequireMessageAsync(command.MessageId, cancellationToken);
        if (message.DeletedAt is not null)
        {
            Throw(MessagingErrorCodes.MessageDeleted, "A deleted message cannot be reacted to.");
        }

        var reaction = await db.MessageReactions.FindAsync([message.Id, actorUserId], cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (emoji is null)
        {
            if (reaction is not null)
            {
                db.MessageReactions.Remove(reaction);
            }
        }
        else if (reaction is null)
        {
            db.MessageReactions.Add(new MessageReaction
            {
                MessageId = message.Id,
                UserId = actorUserId,
                Emoji = emoji,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            reaction.Emoji = emoji;
            reaction.UpdatedAt = now;
        }

        EnqueueEvent(NewEvent(RealtimeEventKinds.ReactionChanged, now, message.ConversationId,
            message.Id, actorUserId, message.Sequence), RealtimeTopics.Conversation(message.ConversationId));
        await db.SaveChangesAsync(cancellationToken);
        var result = await MapMessageAsync(message, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<ConversationReceiptView> MarkDeliveredAsync(long actorUserId,
        MarkConversationReceiptCommand command, CancellationToken cancellationToken) =>
        MarkReceiptAsync(actorUserId, command, markRead: false, cancellationToken);

    public Task<ConversationReceiptView> MarkReadAsync(long actorUserId,
        MarkConversationReceiptCommand command, CancellationToken cancellationToken) =>
        MarkReceiptAsync(actorUserId, command, markRead: true, cancellationToken);

    public async Task<TypingView> SetTypingAsync(
        long actorUserId,
        SetTypingCommand command,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockConversationForActiveParticipantAsync(
            actorUserId,
            command.ConversationId,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var expiresAt = command.IsTyping ? now.AddSeconds(_rules.TypingTtlSeconds) : now;
        var ev = NewEvent(RealtimeEventKinds.TypingChanged, now, command.ConversationId,
            userId: actorUserId, expiresAt: expiresAt);
        await topicSender.SendAsync(RealtimeTopics.Conversation(command.ConversationId), ev, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TypingView(command.ConversationId, actorUserId, command.IsTyping, expiresAt);
    }

    public async Task<UserPresenceView> HeartbeatPresenceAsync(long actorUserId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM messenger.users WHERE user_id = {actorUserId} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            Throw(MessagingErrorCodes.UserNotFound, "The messaging user does not exist.");
        }

        if (user.Status != MessagingUserStatus.Active)
        {
            Throw(MessagingErrorCodes.UserDeleted, "The messaging user is deleted.");
        }

        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddSeconds(_rules.PresenceTtlSeconds);
        var presence = await db.UserPresences.FindAsync([actorUserId], cancellationToken);
        var becameOnline = presence is null || !presence.IsOnline || presence.ExpiresAt <= now;
        if (presence is null)
        {
            presence = new UserPresence { UserId = actorUserId };
            db.UserPresences.Add(presence);
        }

        presence.IsOnline = true;
        presence.ExpiresAt = expiresAt;
        presence.UpdatedAt = now;
        if (becameOnline)
        {
            EnqueueEvent(NewEvent(RealtimeEventKinds.PresenceChanged, now, userId: actorUserId,
                expiresAt: expiresAt), RealtimeTopics.Presence);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UserPresenceView(actorUserId, true, expiresAt, now);
    }

    public async Task AuthorizeConversationSubscriptionAsync(long userId, Guid conversationId,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(userId, cancellationToken);
        await RequireParticipantAsync(conversationId, userId, cancellationToken);
    }

    public Task AuthorizeInboxSubscriptionAsync(long userId, CancellationToken cancellationToken) =>
        RequireActiveUserAsync(userId, cancellationToken);

    public async Task<IReadOnlySet<long>> AuthorizePresenceSubscriptionAsync(long userId,
        IReadOnlyCollection<long> userIds, CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(userId, cancellationToken);
        var ids = NormalizeUserIds(userIds, allowEmpty: false);
        RequirePresenceListLimit(ids);
        await RequirePresenceVisibilityAsync(userId, ids, cancellationToken);
        return ids.ToHashSet();
    }

    private async Task<ConversationReceiptView> MarkReceiptAsync(long actorUserId,
        MarkConversationReceiptCommand command, bool markRead, CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        var conversation = await FindConversationAsync(command.ConversationId, cancellationToken);
        if (command.Sequence < 0 || command.Sequence > conversation.CurrentSequence)
        {
            Throw(MessagingErrorCodes.InvalidInput, "Receipt sequence is outside the conversation range.");
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var participant = await db.ConversationParticipants
            .FromSqlInterpolated(
                $"""
                 SELECT *
                 FROM messenger.conversation_participants
                 WHERE conversation_id = {conversation.Id} AND user_id = {actorUserId}
                 FOR UPDATE
                 """)
            .SingleOrDefaultAsync(cancellationToken);
        if (participant?.LeftAt is not null || participant is null)
        {
            Throw(MessagingErrorCodes.NotParticipant, "The user is not an active conversation participant.");
        }

        if (markRead)
        {
            participant.LastReadSequence = Math.Max(participant.LastReadSequence, command.Sequence);
            participant.LastDeliveredSequence = Math.Max(participant.LastDeliveredSequence, command.Sequence);
        }
        else
        {
            participant.LastDeliveredSequence = Math.Max(participant.LastDeliveredSequence, command.Sequence);
        }

        var now = timeProvider.GetUtcNow();
        var effectiveSequence = markRead
            ? participant.LastReadSequence
            : participant.LastDeliveredSequence;
        EnqueueEvent(NewEvent(RealtimeEventKinds.ReceiptChanged, now, conversation.Id,
            userId: actorUserId, sequence: effectiveSequence), RealtimeTopics.Conversation(conversation.Id));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ConversationReceiptView(conversation.Id, actorUserId,
            participant.LastDeliveredSequence, participant.LastReadSequence);
    }

    private async Task<ConversationView> RemoveOrLeaveAsync(long actorUserId, Guid conversationId,
        long targetUserId, bool requireAdmin, CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        if (!requireAdmin && targetUserId != actorUserId)
        {
            Throw(MessagingErrorCodes.Forbidden, "A member can only leave as themselves.");
        }

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var conversation = await LockGroupConversationAsync(conversationId, cancellationToken);
        if (requireAdmin)
        {
            await RequireAdminParticipantAsync(conversation.Id, actorUserId, cancellationToken);
        }

        var participant = await RequireParticipantAsync(conversation.Id, targetUserId, cancellationToken);
        if (participant.Role == ParticipantRole.Admin)
        {
            await EnsureNotLastAdminAsync(conversation.Id, participant.UserId, cancellationToken);
        }

        var recipients = await ActiveParticipantIdsAsync(conversation.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        participant.LeftAt = now;
        conversation.UpdatedAt = now;
        EnqueueEvent(NewEvent(RealtimeEventKinds.MemberRemoved, now, conversation.Id, userId: targetUserId),
            [RealtimeTopics.Conversation(conversation.Id), .. recipients.Select(RealtimeTopics.Inbox)]);
        await db.SaveChangesAsync(cancellationToken);
        var result = await MapConversationAsync(conversation, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<Conversation> RequireGroupAdminAsync(Guid conversationId, long userId,
        CancellationToken cancellationToken)
    {
        var conversation = await FindConversationAsync(conversationId, cancellationToken);
        if (conversation.Type != ConversationType.Group)
        {
            Throw(MessagingErrorCodes.InvalidInput, "This operation is only available for group conversations.");
        }

        var participant = await RequireParticipantAsync(conversationId, userId, cancellationToken);
        if (participant.Role != ParticipantRole.Admin)
        {
            Throw(MessagingErrorCodes.Forbidden, "A group administrator is required.");
        }

        return conversation;
    }

    private async Task<Conversation> LockGroupConversationForAdminAsync(
        Guid conversationId,
        long userId,
        CancellationToken cancellationToken)
    {
        var conversation = await LockGroupConversationAsync(conversationId, cancellationToken);
        await RequireAdminParticipantAsync(conversationId, userId, cancellationToken);
        return conversation;
    }

    private async Task<Conversation> LockGroupConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await LockConversationAsync(conversationId, cancellationToken);

        if (conversation.Type != ConversationType.Group)
        {
            Throw(MessagingErrorCodes.InvalidInput, "This operation is only available for group conversations.");
        }

        return conversation;
    }

    private async Task<Conversation> LockConversationForActiveParticipantAsync(
        long userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await LockActiveUsersAsync([userId], cancellationToken);
        var conversation = await LockConversationAsync(conversationId, cancellationToken);
        await RequireParticipantAsync(conversationId, userId, cancellationToken);
        return conversation;
    }

    private async Task<Conversation> LockConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations
            .FromSqlInterpolated(
                $"SELECT * FROM messenger.conversations WHERE id = {conversationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (conversation is null)
        {
            Throw(MessagingErrorCodes.ConversationNotFound, "The conversation does not exist.");
        }

        return conversation;
    }

    private async Task RequireAdminParticipantAsync(
        Guid conversationId,
        long userId,
        CancellationToken cancellationToken)
    {
        var participant = await RequireParticipantAsync(conversationId, userId, cancellationToken);
        if (participant.Role != ParticipantRole.Admin)
        {
            Throw(MessagingErrorCodes.Forbidden, "A group administrator is required.");
        }
    }

    private async Task EnsureNotLastAdminAsync(Guid conversationId, long userId,
        CancellationToken cancellationToken)
    {
        var otherAdminExists = await db.ConversationParticipants.AsNoTracking().AnyAsync(
            p => p.ConversationId == conversationId && p.UserId != userId &&
                 p.LeftAt == null && p.Role == ParticipantRole.Admin, cancellationToken);
        if (!otherAdminExists)
        {
            Throw(MessagingErrorCodes.LastAdmin, "The final group administrator cannot leave or be demoted.");
        }
    }

    private async Task RequireSocialPermissionAsync(long actorUserId, IReadOnlyCollection<long> targets,
        SocialGraphPermissionAction action, CancellationToken cancellationToken)
    {
        SocialGraphPermissionCheckResult result;
        try
        {
            result = await socialGraph.CheckAsync(actorUserId, targets, action, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Throw(MessagingErrorCodes.SocialGraphUnavailable, "SocialGraph permission check timed out.");
            return;
        }
        catch (Exception)
        {
            Throw(MessagingErrorCodes.SocialGraphUnavailable, "SocialGraph permission check is unavailable.");
            return;
        }

        var decisions = result.Decisions.ToDictionary(d => d.TargetUserId);
        if (targets.Any(target => !decisions.ContainsKey(target)))
        {
            Throw(MessagingErrorCodes.SocialGraphUnavailable, "SocialGraph returned an incomplete permission result.");
        }

        if (targets.Any(target => !decisions[target].Allowed))
        {
            Throw(MessagingErrorCodes.DirectMessageForbidden,
                "Friendship or block rules do not allow this messaging operation.");
        }
    }

    private async Task RequirePresenceVisibilityAsync(long viewerId, IReadOnlyCollection<long> targetIds,
        CancellationToken cancellationToken)
    {
        var otherIds = targetIds.Where(id => id != viewerId).Distinct().ToArray();
        if (otherIds.Length == 0)
        {
            return;
        }

        var visibleIds = await db.ConversationParticipants.AsNoTracking()
            .Where(target => otherIds.Contains(target.UserId) &&
                             target.LeftAt == null &&
                             db.ConversationParticipants.Any(viewer =>
                                 viewer.ConversationId == target.ConversationId &&
                                 viewer.UserId == viewerId &&
                                 viewer.LeftAt == null))
            .Select(target => target.UserId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        if (visibleIds.Length != otherIds.Length)
        {
            Throw(MessagingErrorCodes.Forbidden, "Presence is visible only to users sharing a conversation.");
        }
    }

    private void RequirePresenceListLimit(IReadOnlyCollection<long> ids)
    {
        if (ids.Count > _rules.MaxPresenceUserIds)
        {
            Throw(
                MessagingErrorCodes.InvalidInput,
                $"Presence requests support at most {_rules.MaxPresenceUserIds} user IDs.");
        }
    }

    private async Task RequireActiveUserAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == userId, cancellationToken);
        if (user is null)
        {
            Throw(MessagingErrorCodes.UserNotFound, "The messaging user does not exist.");
        }

        if (user.Status != MessagingUserStatus.Active)
        {
            Throw(MessagingErrorCodes.UserDeleted, "The messaging user is deleted.");
        }
    }

    private async Task RequireActiveUsersAsync(IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken)
    {
        var rows = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, cancellationToken);
        foreach (var userId in userIds)
        {
            if (!rows.TryGetValue(userId, out var user))
            {
                Throw(MessagingErrorCodes.UserNotFound, $"Messaging user {userId} does not exist.");
            }

            if (user.Status != MessagingUserStatus.Active)
            {
                Throw(MessagingErrorCodes.UserDeleted, $"Messaging user {userId} is deleted.");
            }
        }
    }

    private async Task LockActiveUsersAsync(
        IEnumerable<long> userIds,
        CancellationToken cancellationToken)
    {
        var orderedIds = userIds.Distinct().Order().ToArray();
        var rows = await db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM messenger.users WHERE user_id = ANY ({orderedIds}) ORDER BY user_id FOR UPDATE")
            .AsNoTracking()
            .ToDictionaryAsync(user => user.UserId, cancellationToken);

        foreach (var userId in orderedIds)
        {
            if (!rows.TryGetValue(userId, out var user))
            {
                Throw(MessagingErrorCodes.UserNotFound, $"Messaging user {userId} does not exist.");
            }

            if (user.Status != MessagingUserStatus.Active)
            {
                Throw(MessagingErrorCodes.UserDeleted, $"Messaging user {userId} is deleted.");
            }
        }
    }

    private async Task<ConversationParticipant> RequireParticipantAsync(Guid conversationId, long userId,
        CancellationToken cancellationToken)
    {
        var participant = await db.ConversationParticipants.SingleOrDefaultAsync(
            p => p.ConversationId == conversationId && p.UserId == userId && p.LeftAt == null,
            cancellationToken);
        if (participant is null)
        {
            Throw(MessagingErrorCodes.NotParticipant, "The user is not an active conversation participant.");
        }

        return participant;
    }

    private async Task<Conversation> FindConversationAsync(Guid conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            Throw(MessagingErrorCodes.ConversationNotFound, "The conversation does not exist.");
        }

        return conversation;
    }

    private async Task<Message> RequireMessageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null)
        {
            Throw(MessagingErrorCodes.MessageNotFound, "The message does not exist.");
        }

        return message;
    }

    private async Task<ConversationView> MapConversationAsync(Conversation conversation,
        CancellationToken cancellationToken)
    {
        var participants = await db.ConversationParticipants.AsNoTracking()
            .Where(p => p.ConversationId == conversation.Id && p.LeftAt == null)
            .OrderBy(p => p.JoinedAt)
            .Select(p => new ConversationParticipantView(p.UserId, p.Role, p.JoinedAt, p.LeftAt,
                p.LastDeliveredSequence, p.LastReadSequence))
            .ToListAsync(cancellationToken);
        var lastMessage = await db.Messages.AsNoTracking().Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.Sequence).FirstOrDefaultAsync(cancellationToken);
        return new ConversationView(conversation.Id, conversation.Type, conversation.Title, conversation.AvatarUrl,
            conversation.CreatedAt, conversation.UpdatedAt, conversation.CurrentSequence, participants,
            lastMessage is null ? null : await MapMessageAsync(lastMessage, cancellationToken));
    }

    private async Task<MessageView> MapMessageAsync(Message message, CancellationToken cancellationToken)
    {
        var list = await MapMessagesAsync([message], cancellationToken);
        return list[0];
    }

    private async Task<IReadOnlyList<MessageView>> MapMessagesAsync(IReadOnlyCollection<Message> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        var ids = messages.Select(m => m.Id).ToArray();
        var attachments = await db.MessageAttachments.AsNoTracking().Where(a => ids.Contains(a.MessageId))
            .OrderBy(a => a.Ordinal).ToListAsync(cancellationToken);
        var reactions = await db.MessageReactions.AsNoTracking().Where(r => ids.Contains(r.MessageId))
            .OrderBy(r => r.UserId).ToListAsync(cancellationToken);

        return messages.Select(message =>
        {
            var deleted = message.DeletedAt is not null;
            return new MessageView(message.Id, message.ConversationId, message.SenderUserId, message.Sequence,
                message.ClientMessageId, deleted ? null : message.Text, message.ReplyToMessageId,
                message.CreatedAt, message.EditedAt, message.DeletedAt,
                deleted ? [] : attachments.Where(a => a.MessageId == message.Id)
                    .Select(a => new MessageAttachmentView(a.Ordinal, a.Url)).ToArray(),
                deleted ? [] : reactions.Where(r => r.MessageId == message.Id)
                    .Select(r => new MessageReactionView(r.UserId, r.Emoji, r.UpdatedAt)).ToArray());
        }).ToArray();
    }

    private async Task<long[]> ActiveParticipantIdsAsync(Guid conversationId, CancellationToken cancellationToken) =>
        await db.ConversationParticipants.AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.LeftAt == null)
            .Select(p => p.UserId).ToArrayAsync(cancellationToken);

    private void EnqueueEvent(RealtimeEvent realtimeEvent, params string[] topics) =>
        EnqueueEvent(realtimeEvent, (IEnumerable<string>)topics);

    private void EnqueueEvent(RealtimeEvent realtimeEvent, IEnumerable<string> topics)
    {
        var payload = JsonSerializer.Serialize(realtimeEvent, JsonOptions);
        foreach (var topic in topics.Distinct(StringComparer.Ordinal))
        {
            db.OutboxEvents.Add(new OutboxEvent
            {
                Id = Guid.NewGuid(),
                Topic = topic,
                Kind = realtimeEvent.Kind,
                PayloadJson = payload,
                ConversationId = realtimeEvent.ConversationId,
                MessageId = realtimeEvent.MessageId,
                ActorUserId = realtimeEvent.UserId,
                Sequence = realtimeEvent.Sequence,
                OccurredAt = realtimeEvent.OccurredAt,
                CreatedAt = realtimeEvent.OccurredAt
            });
        }
    }

    private static RealtimeEvent NewEvent(string kind, DateTimeOffset occurredAt,
        Guid? conversationId = null, Guid? messageId = null, long? userId = null,
        long? sequence = null, DateTimeOffset? expiresAt = null) =>
        new(Guid.NewGuid(), kind, conversationId, messageId, userId, sequence, occurredAt, expiresAt);

    private void ValidateOptionalMediaUrl(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            ValidateMediaUrl(value);
        }
    }

    private void ValidateMediaUrl(string value)
    {
        if (!AttachmentUrlPolicy.IsAllowed(
                value,
                _rules.MaxAttachmentUrlLength,
                _rules.AllowedAttachmentHosts))
        {
            Throw(MessagingErrorCodes.AttachmentUrlNotAllowed,
                "Media URLs must be a managed /media/files path or use HTTPS and an explicitly allowed host.");
        }
    }

    private static ConversationParticipant NewParticipant(Guid conversationId, long userId,
        ParticipantRole role, DateTimeOffset now) => new()
        {
            ConversationId = conversationId,
            UserId = userId,
            Role = role,
            JoinedAt = now
        };

    private static long[] NormalizeUserIds(IReadOnlyCollection<long> ids, bool allowEmpty)
    {
        var normalized = ids.Where(id => id > 0).Distinct().ToArray();
        if ((!allowEmpty && normalized.Length == 0) || normalized.Length != ids.Count)
        {
            Throw(MessagingErrorCodes.InvalidInput, "User IDs must be unique positive integers.");
        }

        return normalized;
    }

    private static string RequireText(string? value, int maxLength, string field)
    {
        var text = NormalizeOptional(value);
        if (text is null || text.Length > maxLength)
        {
            Throw(MessagingErrorCodes.InvalidInput, $"{field} is required and cannot exceed {maxLength} characters.");
        }

        return text;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int RequirePageSize(int value, int maximum)
    {
        if (value is < 1 || value > maximum)
        {
            Throw(MessagingErrorCodes.InvalidInput, $"Page size must be between 1 and {maximum}.");
        }

        return value;
    }

    private static string EncodeOffset(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"offset:{offset}"));

    private static int DecodeOffset(string? cursor)
    {
        if (cursor is null)
        {
            return 0;
        }

        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return value.StartsWith("offset:", StringComparison.Ordinal) &&
                   int.TryParse(value[7..], out var offset) && offset >= 0
                ? offset
                : throw new FormatException();
        }
        catch (FormatException)
        {
            Throw(MessagingErrorCodes.InvalidInput, "The conversation cursor is invalid.");
            return 0;
        }
    }

    private static string EncodeSequence(long sequence) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"sequence:{sequence}"));

    private static long? DecodeSequence(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return value.StartsWith("sequence:", StringComparison.Ordinal) &&
                   long.TryParse(value[9..], out var sequence) && sequence > 0
                ? sequence
                : throw new FormatException();
        }
        catch (FormatException)
        {
            Throw(MessagingErrorCodes.InvalidInput, "The message cursor is invalid.");
            return null;
        }
    }

    [DoesNotReturn]
    private static void Throw(string code, string message) =>
        throw new MessagingApplicationException(code, message);
}
