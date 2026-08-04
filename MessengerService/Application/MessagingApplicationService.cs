using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Models;
using MessengerService.Application.Media;
using MessengerService.Application.Realtime;
using MessengerService.Contracts.Internal;
using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using MessengerService.Infrastructure.Realtime;
using HotChocolate.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using DomainMessageKind = MessengerService.Domain.Enums.MessageKind;

namespace MessengerService.Application;

public sealed class MessagingApplicationService(
    MessagingDbContext db,
    ISocialGraphPermissionClient socialGraph,
    IMessagingUserProvisioningService userProvisioning,
    ITopicEventSender topicSender,
    OutboxWakeSignal outboxWakeSignal,
    TimeProvider timeProvider,
    IOptions<MessagingRulesOptions> rulesOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int PermissionBatchSize = 100;
    private static readonly IReadOnlySet<long> EmptyUserIdSet = new HashSet<long>();
    private readonly MessagingRulesOptions _rules = rulesOptions.Value;

    public async Task<ConversationPage> GetMyConversationsAsync(
        long userId,
        int first,
        string? after,
        CancellationToken cancellationToken) =>
        await GetMyConversationsAsync(userId, first, after, directOnly: false, cancellationToken);

    public async Task<ConversationPage> GetMyDirectConversationsAsync(
        long userId,
        int first,
        string? after,
        CancellationToken cancellationToken) =>
        await GetMyConversationsAsync(userId, first, after, directOnly: true, cancellationToken);

    private async Task<ConversationPage> GetMyConversationsAsync(
        long userId,
        int first,
        string? after,
        bool directOnly,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(userId, cancellationToken);
        first = RequirePageSize(first, 100);
        var offset = DecodeOffset(after);

        var conversations = db.Conversations.AsNoTracking()
            .Where(c => c.Participants.Any(p => p.UserId == userId && p.LeftAt == null));
        if (directOnly)
        {
            conversations = conversations.Where(c => c.Type == ConversationType.Direct);
        }

        var query = conversations
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.Id);

        var rows = await query.Skip(offset).Take(first + 1).ToListAsync(cancellationToken);
        var hasNext = rows.Count > first;
        if (hasNext)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = new List<ConversationView>(rows.Count);
        var directBlockStates = await GetDirectBlockStatesAsync(userId, rows, cancellationToken);
        foreach (var row in rows)
        {
            items.Add(await MapConversationAsync(row, cancellationToken, userId, directBlockStates));
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
        return await MapConversationAsync(conversation, cancellationToken, userId);
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
        var conversation = await FindConversationAsync(conversationId, cancellationToken);
        last = RequirePageSize(last, 100);
        var beforeSequence = DecodeSequence(before) ?? long.MaxValue;
        var blockedUserIds = conversation.Type == ConversationType.Group
            ? await GetBlockedUserIdsAsync(
                userId,
                await ActiveParticipantIdsAsync(conversation.Id, cancellationToken),
                cancellationToken)
            : EmptyUserIdSet;

        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId &&
                        m.Sequence < beforeSequence &&
                        !blockedUserIds.Contains(m.SenderUserId))
            .OrderByDescending(m => m.Sequence)
            .Take(last + 1)
            .ToListAsync(cancellationToken);

        var hasPrevious = rows.Count > last;
        if (hasPrevious)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        rows.Reverse();
        var items = await MapMessagesAsync(rows, cancellationToken, blockedUserIds);
        return new MessagePage(
            items,
            new PageInfo(
                items.Count == 0 ? null : EncodeSequence(items[0].Sequence),
                items.Count == 0 ? null : EncodeSequence(items[^1].Sequence),
                false,
                hasPrevious));
    }

    public async Task<MessageView> GetMessageAsync(
        long userId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(userId, cancellationToken);
        var message = await db.Messages.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == messageId, cancellationToken);
        if (message is null)
        {
            Throw(MessagingErrorCodes.MessageNotFound, "The message does not exist.");
        }

        await RequireParticipantAsync(message.ConversationId, userId, cancellationToken);
        await EnsureMessageVisibleAsync(userId, message, cancellationToken);
        var hiddenReactionUserIds = await GetHiddenUserIdsForMessageAsync(userId, message, cancellationToken);
        return await MapMessageAsync(message, cancellationToken, hiddenReactionUserIds);
    }

    public async Task<IReadOnlyList<UserPresenceView>> GetPresenceAsync(
        long userId,
        IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(userId, cancellationToken);
        var ids = NormalizeUserIds(userIds, allowEmpty: false);
        RequirePresenceListLimit(ids);
        var blockedUserIds = await RequirePresenceVisibilityAsync(userId, ids, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var values = await db.UserPresences.AsNoTracking()
            .Where(p => ids.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, cancellationToken);

        return ids.Select(id => blockedUserIds.Contains(id)
                ? new UserPresenceView(id, false, null, null)
                : values.TryGetValue(id, out var value)
                ? new UserPresenceView(id, value.IsOnline && value.ExpiresAt > now,
                    value.IsOnline && value.ExpiresAt > now ? value.ExpiresAt : null, value.UpdatedAt)
                : new UserPresenceView(id, false, null, null))
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

        await RequireSocialPermissionAsync(actorUserId, [command.TargetUserId],
            SocialGraphPermissionAction.CreateDirect, cancellationToken);
        await EnsureActiveUserProjectionsAsync([command.TargetUserId], cancellationToken);

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
            var result = await MapConversationAsync(existing, cancellationToken, actorUserId);
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

            return await MapConversationAsync(existing, cancellationToken, actorUserId);
        }

        EnqueueEvent(NewEvent(RealtimeEventKinds.ConversationCreated, now, conversation.Id, userId: actorUserId),
            RealtimeTopics.Conversation(conversation.Id), RealtimeTopics.Inbox(actorUserId),
            RealtimeTopics.Inbox(command.TargetUserId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapConversationAsync(conversation, cancellationToken, actorUserId);
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
        await RequireSocialPermissionAsync(actorUserId, members,
            SocialGraphPermissionAction.AddGroupMembers, cancellationToken);
        await EnsureActiveUserProjectionsAsync(members, cancellationToken);

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
        var result = await MapConversationAsync(conversation, cancellationToken, actorUserId);
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

        var titleChanged = false;
        if (command.HasTitle)
        {
            var title = RequireText(command.Title, 120, "Group title");
            titleChanged = !string.Equals(conversation.Title, title, StringComparison.Ordinal);
            conversation.Title = title;
        }

        var avatarChanged = false;
        if (command.HasAvatarUrl)
        {
            ValidateOptionalMediaUrl(command.AvatarUrl);
            var avatarUrl = NormalizeOptional(command.AvatarUrl);
            avatarChanged = !string.Equals(conversation.AvatarUrl, avatarUrl, StringComparison.Ordinal);
            conversation.AvatarUrl = avatarUrl;
        }

        var now = timeProvider.GetUtcNow();
        conversation.UpdatedAt = now;
        var recipients = await ActiveParticipantIdsAsync(conversation.Id, cancellationToken);
        if (titleChanged)
        {
            AppendSystemMessage(
                conversation,
                actorUserId,
                SystemMessageEvent.GroupRenamed,
                null,
                now,
                recipients);
        }
        if (avatarChanged)
        {
            AppendSystemMessage(
                conversation,
                actorUserId,
                SystemMessageEvent.GroupAvatarChanged,
                null,
                now,
                recipients);
        }
        if (titleChanged || avatarChanged)
        {
            var actorParticipant = await RequireParticipantAsync(conversation.Id, actorUserId, cancellationToken);
            AdvanceReceipt(actorParticipant, conversation.CurrentSequence);
        }
        EnqueueEvent(NewEvent(RealtimeEventKinds.ConversationUpdated, now, conversation.Id, userId: actorUserId),
            [RealtimeTopics.Conversation(conversation.Id), .. recipients.Select(RealtimeTopics.Inbox)]);
        if (command.HasAvatarUrl && conversation.AvatarUrl is not null)
        {
            var finalizeAvatarEvent = MediaLifecycleOutbox.Create(
                MediaLifecycleEventKinds.Finalize,
                [conversation.AvatarUrl],
                now,
                conversation.Id,
                actorUserId: actorUserId);
            if (finalizeAvatarEvent is not null)
            {
                db.OutboxEvents.Add(finalizeAvatarEvent);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        var result = await MapConversationAsync(conversation, cancellationToken, actorUserId);
        await transaction.CommitAsync(cancellationToken);
        if (command.HasAvatarUrl)
        {
            outboxWakeSignal.Pulse();
        }
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
            await RequireSocialPermissionAsync(actorUserId, toAdd,
                SocialGraphPermissionAction.AddGroupMembers, cancellationToken);
            await EnsureActiveUserProjectionsAsync(toAdd, cancellationToken);
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
            return await MapConversationAsync(conversation, cancellationToken, actorUserId);
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

        var recipients = active.Concat(toAdd).Distinct().ToArray();
        var actorParticipant = await RequireParticipantAsync(conversation.Id, actorUserId, cancellationToken);
        foreach (var userId in toAdd)
        {
            AppendSystemMessage(
                conversation,
                actorUserId,
                SystemMessageEvent.MemberAdded,
                userId,
                now,
                recipients);
        }
        AdvanceReceipt(actorParticipant, conversation.CurrentSequence);
        EnqueueEvent(NewEvent(RealtimeEventKinds.MemberAdded, now, conversation.Id, userId: actorUserId),
            [RealtimeTopics.Conversation(conversation.Id), .. recipients.Select(RealtimeTopics.Inbox)]);
        await db.SaveChangesAsync(cancellationToken);
        var result = await MapConversationAsync(conversation, cancellationToken, actorUserId);
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

    public async Task<bool> DeleteGroupConversationAsync(
        long actorUserId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        db.ChangeTracker.Clear();

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var conversation = db.Database.IsRelational()
            ? await LockGroupConversationForAdminAsync(conversationId, actorUserId, cancellationToken)
            : await RequireGroupAdminAsync(conversationId, actorUserId, cancellationToken);

        var recipients = await ActiveParticipantIdsAsync(conversation.Id, cancellationToken);
        var conversationMedia = await db.MessageAttachments.AsNoTracking()
            .Where(attachment => attachment.Message.ConversationId == conversation.Id)
            .Select(attachment => new
            {
                attachment.Url,
                attachment.ThumbnailUrl
            })
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var candidateUrls = conversationMedia
            .SelectMany(media => new[] { media.Url, media.ThumbnailUrl })
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var survivingRows = candidateUrls.Length == 0
            ? []
            : await db.MessageAttachments.AsNoTracking()
                .Where(attachment =>
                    attachment.Message.ConversationId != conversation.Id &&
                    attachment.Message.DeletedAt == null &&
                    (candidateUrls.Contains(attachment.Url) ||
                     (attachment.ThumbnailUrl != null && candidateUrls.Contains(attachment.ThumbnailUrl))))
                .Select(attachment => new { attachment.Url, attachment.ThumbnailUrl })
                .ToListAsync(cancellationToken);
        var survivingUrls = survivingRows
            .SelectMany(media => new[] { media.Url, media.ThumbnailUrl })
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletableUrls = candidateUrls
            .Where(url => !survivingUrls.Contains(url))
            .ToArray();
        var ownershipRows = deletableUrls.Length == 0
            ? []
            : await db.MessageAttachments.AsNoTracking()
                .Where(attachment =>
                    deletableUrls.Contains(attachment.Url) ||
                    (attachment.ThumbnailUrl != null && deletableUrls.Contains(attachment.ThumbnailUrl)))
                .Select(attachment => new
                {
                    attachment.Url,
                    attachment.ThumbnailUrl,
                    attachment.Message.SenderUserId,
                    attachment.Message.CreatedAt
                })
                .ToListAsync(cancellationToken);
        var ownerByUrl = ownershipRows
            .SelectMany(media => new (string? Url, long SenderUserId, DateTimeOffset CreatedAt)[]
            {
                (media.Url, media.SenderUserId, media.CreatedAt),
                (media.ThumbnailUrl, media.SenderUserId, media.CreatedAt)
            })
            .Where(media => media.Url is not null && deletableUrls.Contains(media.Url))
            .GroupBy(media => media.Url!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(media => media.CreatedAt).ThenBy(media => media.SenderUserId).First().SenderUserId,
                StringComparer.OrdinalIgnoreCase);
        foreach (var ownerGroup in deletableUrls
                     .Where(ownerByUrl.ContainsKey)
                     .GroupBy(url => ownerByUrl[url]))
        {
            var mediaEvent = MediaLifecycleOutbox.Create(
                MediaLifecycleEventKinds.Delete,
                ownerGroup,
                now,
                conversation.Id,
                actorUserId: ownerGroup.Key);
            if (mediaEvent is not null)
            {
                db.OutboxEvents.Add(mediaEvent);
            }
        }

        // Do not publish only on the conversation topic: once the row is gone the
        // subscription authorizer must (correctly) deny that topic. Every active
        // participant receives the terminal event through their private inbox.
        EnqueueEvent(
            NewEvent(RealtimeEventKinds.ConversationDeleted, now, conversation.Id, userId: actorUserId),
            recipients.Select(RealtimeTopics.Inbox));
        if (db.Database.IsRelational())
        {
            // Message replies use a restrictive self-reference so a single message can
            // never be physically removed beneath its replies. Group deletion removes
            // the whole aggregate, therefore clear those internal edges first inside
            // the same transaction and let the conversation cascades do the rest.
            await db.Messages
                .Where(message => message.ConversationId == conversation.Id && message.ReplyToMessageId != null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(message => message.ReplyToMessageId, (Guid?)null),
                    cancellationToken);
        }
        db.Conversations.Remove(conversation);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        outboxWakeSignal.Pulse();
        return true;
    }

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

        if (participant.Role == command.Role)
        {
            await transaction.CommitAsync(cancellationToken);
            return await MapConversationAsync(conversation, cancellationToken, actorUserId);
        }

        participant.Role = command.Role;
        var now = timeProvider.GetUtcNow();
        var recipients = await ActiveParticipantIdsAsync(conversation.Id, cancellationToken);
        AppendSystemMessage(
            conversation,
            actorUserId,
            command.Role == ParticipantRole.Admin
                ? SystemMessageEvent.AdminGranted
                : SystemMessageEvent.AdminRevoked,
            command.UserId,
            now,
            recipients);
        var actorParticipant = await RequireParticipantAsync(conversation.Id, actorUserId, cancellationToken);
        AdvanceReceipt(actorParticipant, conversation.CurrentSequence);
        EnqueueEvent(NewEvent(RealtimeEventKinds.MemberRoleChanged, now, conversation.Id, userId: command.UserId),
            [RealtimeTopics.Conversation(conversation.Id), .. recipients.Select(RealtimeTopics.Inbox)]);
        await db.SaveChangesAsync(cancellationToken);
        var result = await MapConversationAsync(conversation, cancellationToken, actorUserId);
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

        if (MessageTextHistoryCodec.IsReservedInput(text))
        {
            Throw(MessagingErrorCodes.InvalidInput, "Message text uses a reserved internal prefix.");
        }

        var attachments = NormalizeAttachments(command);
        if (attachments.Count > _rules.MaxAttachmentsPerMessage)
        {
            Throw(MessagingErrorCodes.InvalidInput, $"A message supports at most {_rules.MaxAttachmentsPerMessage} attachments.");
        }

        foreach (var attachment in attachments)
        {
            ValidateAttachment(attachment);
        }

        if (text is null && attachments.Count == 0)
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

        if (command.ReplyToMessageId is { } replyId)
        {
            var repliedMessage = await db.Messages.AsNoTracking().SingleOrDefaultAsync(
                m => m.Id == replyId && m.ConversationId == conversation.Id &&
                     m.Kind == DomainMessageKind.User,
                cancellationToken);
            if (repliedMessage is null)
            {
                Throw(MessagingErrorCodes.InvalidInput,
                    "The replied-to message does not belong to this conversation or cannot be replied to.");
            }

            await EnsureMessageVisibleAsync(actorUserId, repliedMessage, cancellationToken);
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
        conversation = db.Database.IsRelational()
            ? await db.Conversations.FromSqlInterpolated(
                    $"SELECT * FROM messenger.conversations WHERE id = {command.ConversationId} FOR UPDATE")
                .SingleAsync(cancellationToken)
            : await db.Conversations.SingleAsync(
                value => value.Id == command.ConversationId,
                cancellationToken);
        var senderParticipant = await RequireParticipantAsync(conversation.Id, actorUserId, cancellationToken);
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
        AdvanceReceipt(senderParticipant, message.Sequence);
        for (var index = 0; index < attachments.Count; index++)
        {
            var attachment = attachments[index];
            message.Attachments.Add(new MessageAttachment
            {
                MessageId = message.Id,
                Ordinal = index,
                Url = attachment.Url!,
                AssetId = attachment.AssetId,
                MediaType = attachment.MediaType,
                ContentType = attachment.ContentType,
                OriginalName = attachment.OriginalName,
                SizeBytes = attachment.SizeBytes,
                Width = attachment.Width,
                Height = attachment.Height,
                DurationMs = attachment.DurationMs,
                ThumbnailUrl = attachment.ThumbnailUrl
            });
        }

        db.Messages.Add(message);
        var recipients = await ActiveParticipantIdsAsync(conversation.Id, cancellationToken);
        var ev = NewEvent(RealtimeEventKinds.MessageAdded, now, conversation.Id, message.Id,
            actorUserId, message.Sequence);
        EnqueueEvent(ev, [RealtimeTopics.Conversation(conversation.Id), .. recipients.Select(RealtimeTopics.Inbox)]);
        var finalizeMediaEvent = MediaLifecycleOutbox.Create(
            MediaLifecycleEventKinds.Finalize,
            attachments.Select(attachment => attachment.Url!),
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

        outboxWakeSignal.Pulse();
        return new MessageView(
            message.Id,
            message.ConversationId,
            message.SenderUserId,
            message.Sequence,
            message.ClientMessageId,
            message.Kind,
            message.SystemEvent,
            message.SystemSubjectUserId,
            message.Text,
            message.ReplyToMessageId,
            message.CreatedAt,
            null,
            [],
            null,
            attachments
                .Select((attachment, ordinal) => ToAttachmentView(ordinal, attachment))
                .ToArray(),
            []);
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
        if (MessageTextHistoryCodec.IsReservedInput(text))
        {
            Throw(MessagingErrorCodes.InvalidInput, "Message text uses a reserved internal prefix.");
        }

        var conversation = await FindConversationAsync(conversationId, cancellationToken);
        await RequireParticipantAsync(conversationId, actorUserId, cancellationToken);
        await RequireDirectInteractionPermissionAsync(actorUserId, conversation, cancellationToken);

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockConversationForActiveParticipantAsync(
            actorUserId,
            conversationId,
            cancellationToken);
        message = await RequireMessageAsync(command.MessageId, cancellationToken);
        await EnsureMessageVisibleAsync(actorUserId, message, cancellationToken);
        if (message.SenderUserId != actorUserId)
        {
            Throw(MessagingErrorCodes.Forbidden, "Only the message author can edit it.");
        }

        if (message.Kind == DomainMessageKind.System)
        {
            Throw(MessagingErrorCodes.InvalidInput, "System messages cannot be edited.");
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

        var snapshot = MessageTextHistoryCodec.Decode(message.Text);
        if (string.Equals(snapshot.Current, text, StringComparison.Ordinal))
        {
            var unchanged = await MapMessageAsync(message, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return unchanged;
        }

        message.Text = MessageTextHistoryCodec.EncodeEdit(
            message.Text,
            message.EditedAt ?? message.CreatedAt,
            text);
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
        var conversation = await FindConversationAsync(conversationId, cancellationToken);
        await RequireParticipantAsync(conversationId, actorUserId, cancellationToken);
        await RequireDirectInteractionPermissionAsync(actorUserId, conversation, cancellationToken);

        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockConversationForActiveParticipantAsync(
            actorUserId,
            conversationId,
            cancellationToken);
        message = await RequireMessageAsync(command.MessageId, cancellationToken);
        await EnsureMessageVisibleAsync(actorUserId, message, cancellationToken);
        if (message.SenderUserId != actorUserId)
        {
            Throw(MessagingErrorCodes.Forbidden, "Only the message author can delete it.");
        }

        if (message.Kind == DomainMessageKind.System)
        {
            Throw(MessagingErrorCodes.InvalidInput, "System messages cannot be deleted.");
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
        var conversation = await FindConversationAsync(conversationId, cancellationToken);
        await RequireParticipantAsync(conversationId, actorUserId, cancellationToken);
        await RequireDirectInteractionPermissionAsync(actorUserId, conversation, cancellationToken);
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockConversationForActiveParticipantAsync(
            actorUserId,
            conversationId,
            cancellationToken);
        message = await RequireMessageAsync(command.MessageId, cancellationToken);
        await EnsureMessageVisibleAsync(actorUserId, message, cancellationToken);
        if (message.Kind == DomainMessageKind.System)
        {
            Throw(MessagingErrorCodes.InvalidInput, "System messages cannot be reacted to.");
        }
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
        await RequireActiveUserAsync(actorUserId, cancellationToken);
        var conversation = await FindConversationAsync(command.ConversationId, cancellationToken);
        await RequireParticipantAsync(conversation.Id, actorUserId, cancellationToken);
        if (conversation.Type == ConversationType.Direct)
        {
            var otherUserId = conversation.DirectUserLowId == actorUserId
                ? conversation.DirectUserHighId
                : conversation.DirectUserLowId;
            if (otherUserId is { } targetUserId)
            {
                await RequireSocialPermissionAsync(
                    actorUserId,
                    [targetUserId],
                    SocialGraphPermissionAction.SendDirect,
                    cancellationToken);
            }
        }
        var now = timeProvider.GetUtcNow();
        var expiresAt = command.IsTyping ? now.AddSeconds(_rules.TypingTtlSeconds) : now;
        var ev = NewEvent(RealtimeEventKinds.TypingChanged, now, command.ConversationId,
            userId: actorUserId, expiresAt: expiresAt);
        await topicSender.SendAsync(RealtimeTopics.Conversation(command.ConversationId), ev, cancellationToken);
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
        if (becameOnline)
        {
            outboxWakeSignal.Pulse();
        }

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
        var blockedUserIds = await RequirePresenceVisibilityAsync(userId, ids, cancellationToken);
        return ids.Where(id => !blockedUserIds.Contains(id)).ToHashSet();
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
        var participant = db.Database.IsRelational()
            ? await db.ConversationParticipants
                .FromSqlInterpolated(
                    $"""
                     SELECT *
                     FROM messenger.conversation_participants
                     WHERE conversation_id = {conversation.Id} AND user_id = {actorUserId}
                     FOR UPDATE
                     """)
                .SingleOrDefaultAsync(cancellationToken)
            : await db.ConversationParticipants.SingleOrDefaultAsync(
                value => value.ConversationId == conversation.Id && value.UserId == actorUserId,
                cancellationToken);
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
        var remainingRecipients = recipients.Where(userId => userId != targetUserId).ToArray();
        AppendSystemMessage(
            conversation,
            actorUserId,
            requireAdmin ? SystemMessageEvent.MemberRemoved : SystemMessageEvent.MemberLeft,
            targetUserId,
            now,
            remainingRecipients);
        if (actorUserId != targetUserId)
        {
            var actorParticipant = await RequireParticipantAsync(conversation.Id, actorUserId, cancellationToken);
            AdvanceReceipt(actorParticipant, conversation.CurrentSequence);
        }
        EnqueueEvent(NewEvent(RealtimeEventKinds.MemberRemoved, now, conversation.Id, userId: targetUserId),
            [RealtimeTopics.Conversation(conversation.Id), .. recipients.Select(RealtimeTopics.Inbox)]);
        await db.SaveChangesAsync(cancellationToken);
        var result = await MapConversationAsync(conversation, cancellationToken, actorUserId);
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
        var conversation = db.Database.IsRelational()
            ? await db.Conversations
                .FromSqlInterpolated(
                    $"SELECT * FROM messenger.conversations WHERE id = {conversationId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.Conversations.SingleOrDefaultAsync(
                value => value.Id == conversationId,
                cancellationToken);
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

    private async Task RequireDirectInteractionPermissionAsync(
        long actorUserId,
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        if (conversation.Type != ConversationType.Direct)
        {
            return;
        }

        var targetUserId = conversation.DirectUserLowId == actorUserId
            ? conversation.DirectUserHighId
            : conversation.DirectUserLowId;
        if (targetUserId is null || targetUserId <= 0 || targetUserId == actorUserId)
        {
            Throw(MessagingErrorCodes.DirectMessageForbidden,
                "The direct conversation participants are invalid.");
        }
        var target = targetUserId.Value;

        await RequireSocialPermissionAsync(
            actorUserId,
            [target],
            SocialGraphPermissionAction.SendDirect,
            cancellationToken);
    }

    private async Task<IReadOnlySet<long>> RequirePresenceVisibilityAsync(long viewerId, IReadOnlyCollection<long> targetIds,
        CancellationToken cancellationToken)
    {
        var otherIds = targetIds.Where(id => id != viewerId).Distinct().ToArray();
        if (otherIds.Length == 0)
        {
            return EmptyUserIdSet;
        }

        var blockedIds = await GetBlockedUserIdsAsync(viewerId, otherIds, cancellationToken);

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
        var friendIdsWithoutConversation = otherIds
            .Except(blockedIds)
            .Except(visibleIds)
            .ToArray();
        if (friendIdsWithoutConversation.Length > 0)
        {
            // Friends may see one another's active status before the first direct
            // conversation is created. SocialGraph remains the source of truth for
            // friendship and block rules; non-friends still fail closed.
            await RequireSocialPermissionAsync(
                viewerId,
                friendIdsWithoutConversation,
                SocialGraphPermissionAction.CreateDirect,
                cancellationToken);
        }

        return blockedIds;
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

    private async Task EnsureActiveUserProjectionsAsync(
        IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken)
    {
        foreach (var userId in userIds.Distinct())
        {
            var outcome = await userProvisioning.ProvisionAsync(userId, cancellationToken);
            if (outcome == ProvisionUserOutcome.DeletedTombstone)
            {
                Throw(MessagingErrorCodes.UserDeleted, $"Messaging user {userId} is deleted.");
            }
        }

        await RequireActiveUsersAsync(userIds, cancellationToken);
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
        var rows = db.Database.IsRelational()
            ? await db.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM messenger.users WHERE user_id = ANY ({orderedIds}) ORDER BY user_id FOR UPDATE")
                .AsNoTracking()
                .ToDictionaryAsync(user => user.UserId, cancellationToken)
            : await db.Users.AsNoTracking()
                .Where(user => orderedIds.Contains(user.UserId))
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

    private async Task<IReadOnlySet<long>> GetBlockedUserIdsAsync(
        long viewerUserId,
        IEnumerable<long> targetUserIds,
        CancellationToken cancellationToken)
    {
        var targets = targetUserIds
            .Where(id => id > 0 && id != viewerUserId)
            .Distinct()
            .ToArray();
        if (targets.Length == 0)
        {
            return EmptyUserIdSet;
        }

        var blocked = new HashSet<long>();
        foreach (var chunk in targets.Chunk(PermissionBatchSize))
        {
            var result = await socialGraph.CheckAsync(
                viewerUserId,
                chunk,
                SocialGraphPermissionAction.InspectBlock,
                cancellationToken);
            var decisions = result.Decisions.ToDictionary(decision => decision.TargetUserId);
            if (chunk.Any(targetId => !decisions.ContainsKey(targetId)))
            {
                Throw(MessagingErrorCodes.SocialGraphUnavailable,
                    "SocialGraph returned an incomplete block-state result.");
            }

            blocked.UnionWith(decisions.Values
                .Where(decision => decision.BlockedEitherDirection)
                .Select(decision => decision.TargetUserId));
        }

        return blocked;
    }

    private async Task<DirectBlockState?> GetDirectBlockStateAsync(
        Conversation conversation,
        long viewerUserId,
        CancellationToken cancellationToken)
    {
        var otherUserId = conversation.DirectUserLowId == viewerUserId
            ? conversation.DirectUserHighId
            : conversation.DirectUserLowId;
        if (otherUserId is not { } targetUserId || targetUserId <= 0 || targetUserId == viewerUserId)
        {
            return null;
        }

        try
        {
            var result = await socialGraph.CheckAsync(
                viewerUserId,
                [targetUserId],
                SocialGraphPermissionAction.InspectBlock,
                cancellationToken);
            var decision = result.Decisions.SingleOrDefault(value => value.TargetUserId == targetUserId);
            return decision is null
                ? null
                : new DirectBlockState(decision.ActorBlockedTarget, decision.TargetBlockedActor);
        }
        catch (MessagingApplicationException exception)
            when (exception.Code == MessagingErrorCodes.SocialGraphUnavailable)
        {
            // A blocked profile may be hidden by SocialGraph during a brief
            // restart. Keep the inbox readable; all write paths still fail
            // closed through RequireSocialPermissionAsync.
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<long, DirectBlockState>> GetDirectBlockStatesAsync(
        long viewerUserId,
        IReadOnlyCollection<Conversation> conversations,
        CancellationToken cancellationToken)
    {
        var targetIds = conversations
            .Where(conversation => conversation.Type == ConversationType.Direct)
            .Select(conversation => conversation.DirectUserLowId == viewerUserId
                ? conversation.DirectUserHighId
                : conversation.DirectUserLowId)
            .OfType<long>()
            .Where(targetId => targetId > 0 && targetId != viewerUserId)
            .Distinct()
            .ToArray();
        if (targetIds.Length == 0)
        {
            return new Dictionary<long, DirectBlockState>();
        }

        try
        {
            var states = new Dictionary<long, DirectBlockState>();
            foreach (var chunk in targetIds.Chunk(PermissionBatchSize))
            {
                var result = await socialGraph.CheckAsync(
                    viewerUserId,
                    chunk,
                    SocialGraphPermissionAction.InspectBlock,
                    cancellationToken);
                foreach (var decision in result.Decisions)
                {
                    states[decision.TargetUserId] = new DirectBlockState(
                        decision.ActorBlockedTarget,
                        decision.TargetBlockedActor);
                }
            }

            return states;
        }
        catch (MessagingApplicationException exception)
            when (exception.Code == MessagingErrorCodes.SocialGraphUnavailable)
        {
            return new Dictionary<long, DirectBlockState>();
        }
    }

    private async Task EnsureMessageVisibleAsync(
        long viewerUserId,
        Message message,
        CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == message.ConversationId, cancellationToken);
        if (conversation?.Type != ConversationType.Group || message.SenderUserId == viewerUserId)
        {
            return;
        }

        var blocked = await GetBlockedUserIdsAsync(viewerUserId, [message.SenderUserId], cancellationToken);
        if (blocked.Contains(message.SenderUserId))
        {
            Throw(MessagingErrorCodes.MessageNotFound, "The message is not available to this user.");
        }
    }

    private async Task<IReadOnlySet<long>> GetHiddenUserIdsForMessageAsync(
        long viewerUserId,
        Message message,
        CancellationToken cancellationToken)
    {
        var conversationType = await db.Conversations.AsNoTracking()
            .Where(value => value.Id == message.ConversationId)
            .Select(value => value.Type)
            .SingleOrDefaultAsync(cancellationToken);
        if (conversationType != ConversationType.Group)
        {
            return EmptyUserIdSet;
        }

        var participantIds = await ActiveParticipantIdsAsync(message.ConversationId, cancellationToken);
        return await GetBlockedUserIdsAsync(viewerUserId, participantIds, cancellationToken);
    }

    private async Task<ConversationView> MapConversationAsync(Conversation conversation,
        CancellationToken cancellationToken,
        long? viewerUserId = null,
        IReadOnlyDictionary<long, DirectBlockState>? directBlockStates = null)
    {
        var participants = await db.ConversationParticipants.AsNoTracking()
            .Where(p => p.ConversationId == conversation.Id && p.LeftAt == null)
            .OrderBy(p => p.JoinedAt)
            .Select(p => new ConversationParticipantView(p.UserId, p.Role, p.JoinedAt, p.LeftAt,
                p.LastDeliveredSequence, p.LastReadSequence))
            .ToListAsync(cancellationToken);
        var blockedUserIds = conversation.Type == ConversationType.Group && viewerUserId is { } viewer
            ? await GetBlockedUserIdsAsync(viewer, participants.Select(p => p.UserId), cancellationToken)
            : EmptyUserIdSet;
        DirectBlockState? directBlockState = null;
        if (conversation.Type == ConversationType.Direct && viewerUserId is { } directViewer)
        {
            var targetUserId = conversation.DirectUserLowId == directViewer
                ? conversation.DirectUserHighId
                : conversation.DirectUserLowId;
            if (targetUserId is { } target && directBlockStates?.TryGetValue(target, out var knownState) == true)
            {
                directBlockState = knownState;
            }
            else
            {
                directBlockState = await GetDirectBlockStateAsync(conversation, directViewer, cancellationToken);
            }
        }
        if (conversation.Type == ConversationType.Group && blockedUserIds.Count > 0)
        {
            // Keep the member rows so the group roster remains intact, but do
            // not expose read/delivery cursors from a blocked member. Those
            // cursors are presence-like data and would otherwise reveal that
            // member's activity through group receipts.
            participants = participants
                .Select(participant => blockedUserIds.Contains(participant.UserId)
                    ? participant with { LastDeliveredSequence = 0, LastReadSequence = 0 }
                    : participant)
                .ToList();
        }
        var directBlocked = directBlockState is { } state &&
                            (state.ActorBlockedTarget || state.TargetBlockedActor);
        if (directBlocked && viewerUserId is { } directViewerId)
        {
            participants = participants
                .Select(participant => participant.UserId == directViewerId
                    ? participant
                    : participant with { LastDeliveredSequence = 0, LastReadSequence = 0 })
                .ToList();
        }
        var visibleMessages = db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && !blockedUserIds.Contains(m.SenderUserId));
        var lastMessage = await visibleMessages
            .OrderByDescending(m => m.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        var currentSequence = conversation.Type == ConversationType.Group
            ? lastMessage?.Sequence ?? 0
            : directBlocked && viewerUserId is { } blockedViewer
                ? participants.FirstOrDefault(participant => participant.UserId == blockedViewer)?.LastReadSequence ?? 0
                : conversation.CurrentSequence;
        return new ConversationView(conversation.Id, conversation.Type, conversation.Title, conversation.AvatarUrl,
            conversation.CreatedAt, conversation.UpdatedAt, currentSequence, participants,
            lastMessage is null ? null : await MapMessageAsync(lastMessage, cancellationToken, blockedUserIds),
            directBlockState?.ActorBlockedTarget ?? false,
            directBlockState?.TargetBlockedActor ?? false);
    }

    private async Task<MessageView> MapMessageAsync(
        Message message,
        CancellationToken cancellationToken,
        IReadOnlySet<long>? hiddenUserIds = null)
    {
        var list = await MapMessagesAsync([message], cancellationToken, hiddenUserIds);
        return list[0];
    }

    private async Task<IReadOnlyList<MessageView>> MapMessagesAsync(
        IReadOnlyCollection<Message> messages,
        CancellationToken cancellationToken,
        IReadOnlySet<long>? hiddenUserIds = null)
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
            var snapshot = deleted
                ? new MessageTextSnapshot(string.Empty, [])
                : MessageTextHistoryCodec.Decode(message.Text);
            return new MessageView(message.Id, message.ConversationId, message.SenderUserId, message.Sequence,
                message.ClientMessageId, message.Kind, message.SystemEvent, message.SystemSubjectUserId,
                deleted ? null : snapshot.Current, message.ReplyToMessageId,
                message.CreatedAt, message.EditedAt,
                deleted
                    ? []
                    : snapshot.History.Select(revision =>
                        new MessageEditRevisionView(revision.Text, revision.VersionAt)).ToArray(),
                message.DeletedAt,
                deleted ? [] : attachments.Where(a => a.MessageId == message.Id)
                    .Select(ToAttachmentView).ToArray(),
                deleted ? [] : reactions.Where(r => r.MessageId == message.Id &&
                    (hiddenUserIds is null || !hiddenUserIds.Contains(r.UserId)))
                    .Select(r => new MessageReactionView(r.UserId, r.Emoji, r.UpdatedAt)).ToArray());
        }).ToArray();
    }

    private sealed record DirectBlockState(bool ActorBlockedTarget, bool TargetBlockedActor);

    private async Task<long[]> ActiveParticipantIdsAsync(Guid conversationId, CancellationToken cancellationToken) =>
        await db.ConversationParticipants.AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.LeftAt == null)
            .Select(p => p.UserId).ToArrayAsync(cancellationToken);

    private Message AppendSystemMessage(
        Conversation conversation,
        long actorUserId,
        SystemMessageEvent systemEvent,
        long? subjectUserId,
        DateTimeOffset now,
        IReadOnlyCollection<long> recipients)
    {
        conversation.CurrentSequence++;
        conversation.UpdatedAt = now;
        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = actorUserId,
            Sequence = conversation.CurrentSequence,
            ClientMessageId = Guid.NewGuid(),
            Kind = DomainMessageKind.System,
            SystemEvent = systemEvent,
            SystemSubjectUserId = subjectUserId,
            CreatedAt = now
        };
        db.Messages.Add(message);
        var realtimeEvent = NewEvent(
            RealtimeEventKinds.MessageAdded,
            now,
            conversation.Id,
            message.Id,
            actorUserId,
            message.Sequence);
        EnqueueEvent(
            realtimeEvent,
            [RealtimeTopics.Conversation(conversation.Id), .. recipients.Select(RealtimeTopics.Inbox)]);
        return message;
    }

    private static void AdvanceReceipt(ConversationParticipant participant, long sequence)
    {
        participant.LastDeliveredSequence = Math.Max(participant.LastDeliveredSequence, sequence);
        participant.LastReadSequence = Math.Max(participant.LastReadSequence, sequence);
    }

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

    private IReadOnlyList<MessageAttachmentCommand> NormalizeAttachments(SendMessageCommand command)
    {
        var urls = command.AttachmentUrls ?? [];
        var supplied = command.Attachments ?? [];

        if (supplied.Count == 0)
        {
            return urls
                .Select(url => new MessageAttachmentCommand(url))
                .ToArray();
        }

        if (urls.Count > 0 && urls.Count != supplied.Count)
        {
            Throw(MessagingErrorCodes.InvalidInput,
                "AttachmentUrls and Attachments must contain the same number of items when both are supplied.");
        }

        var normalized = new MessageAttachmentCommand[supplied.Count];
        for (var index = 0; index < supplied.Count; index++)
        {
            var suppliedAttachment = supplied[index];
            var suppliedUrl = NormalizeOptional(suppliedAttachment.Url);
            var fallbackUrl = urls.Count == 0 ? null : NormalizeOptional(urls[index]);

            if (suppliedUrl is not null && fallbackUrl is not null &&
                !string.Equals(suppliedUrl, fallbackUrl, StringComparison.Ordinal))
            {
                Throw(MessagingErrorCodes.InvalidInput,
                    "Attachment URL values must match when both attachment contracts are supplied.");
            }

            normalized[index] = suppliedAttachment with { Url = suppliedUrl ?? fallbackUrl };
        }

        return normalized;
    }

    private void ValidateAttachment(MessageAttachmentCommand attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.Url))
        {
            Throw(MessagingErrorCodes.InvalidInput, "Every attachment requires a URL.");
        }

        ValidateMediaUrl(attachment.Url);

        if (!string.IsNullOrWhiteSpace(attachment.AssetId))
        {
            var assetId = attachment.AssetId.Trim();
            if (assetId.Length > 128 || assetId.Any(char.IsWhiteSpace))
            {
                Throw(MessagingErrorCodes.InvalidInput, "Attachment asset ID is invalid.");
            }
        }

        if (!string.IsNullOrWhiteSpace(attachment.MediaType) &&
            !IsSupportedMediaType(attachment.MediaType))
        {
            Throw(MessagingErrorCodes.InvalidInput,
                "Attachment media type must be image, video, audio, or file.");
        }

        if (!string.IsNullOrWhiteSpace(attachment.ContentType) &&
            attachment.ContentType.Trim().Length > 128)
        {
            Throw(MessagingErrorCodes.InvalidInput, "Attachment content type is too long.");
        }

        if (!string.IsNullOrWhiteSpace(attachment.OriginalName) &&
            attachment.OriginalName.Trim().Length > 255)
        {
            Throw(MessagingErrorCodes.InvalidInput, "Attachment original name is too long.");
        }

        if (attachment.SizeBytes is < 0 || attachment.Width is < 0 || attachment.Height is < 0 ||
            attachment.DurationMs is < 0)
        {
            Throw(MessagingErrorCodes.InvalidInput, "Attachment metadata cannot contain negative values.");
        }

        if (attachment.Width is > 100_000 || attachment.Height is > 100_000)
        {
            Throw(MessagingErrorCodes.InvalidInput, "Attachment dimensions are out of range.");
        }

        if (!string.IsNullOrWhiteSpace(attachment.ThumbnailUrl))
        {
            ValidateMediaUrl(attachment.ThumbnailUrl);
        }
    }

    private static MessageAttachmentView ToAttachmentView(MessageAttachment attachment) =>
        new(
            attachment.Ordinal,
            attachment.Url,
            attachment.AssetId,
            attachment.MediaType ?? InferMediaType(attachment.Url, attachment.ContentType),
            attachment.ContentType ?? InferContentType(attachment.Url),
            attachment.OriginalName,
            attachment.SizeBytes,
            attachment.Width,
            attachment.Height,
            attachment.DurationMs,
            attachment.ThumbnailUrl);

    private static MessageAttachmentView ToAttachmentView(int ordinal, MessageAttachmentCommand attachment) =>
        new(
            ordinal,
            attachment.Url!,
            attachment.AssetId,
            attachment.MediaType ?? InferMediaType(attachment.Url!, attachment.ContentType),
            attachment.ContentType ?? InferContentType(attachment.Url!),
            attachment.OriginalName,
            attachment.SizeBytes,
            attachment.Width,
            attachment.Height,
            attachment.DurationMs,
            attachment.ThumbnailUrl);

    private static bool IsSupportedMediaType(string value) =>
        value.Trim().ToLowerInvariant() is "image" or "video" or "audio" or "file";

    private static string? InferMediaType(string? url, string? contentType)
    {
        var normalizedContentType = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (normalizedContentType is not null)
        {
            if (normalizedContentType.StartsWith("image/", StringComparison.Ordinal)) return "image";
            if (normalizedContentType.StartsWith("video/", StringComparison.Ordinal)) return "video";
            if (normalizedContentType.StartsWith("audio/", StringComparison.Ordinal)) return "audio";
        }

        var extension = GetUrlExtension(url);
        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => "image",
            ".mp4" or ".webm" or ".mov" or ".m4v" => ".webm" == extension ? "audio" : "video",
            ".mp3" or ".m4a" or ".wav" or ".ogg" or ".oga" => "audio",
            _ => "file"
        };
    }

    private static string? InferContentType(string? url)
    {
        return GetUrlExtension(url) switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "audio/webm",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".ogg" or ".oga" => "audio/ogg",
            ".pdf" => "application/pdf",
            _ => null
        };
    }

    private static string? GetUrlExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute.AbsolutePath
            : value.Split('?', '#')[0];
        return System.IO.Path.GetExtension(path).ToLowerInvariant();
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
