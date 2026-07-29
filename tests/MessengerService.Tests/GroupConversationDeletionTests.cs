using MessengerService.Application;
using MessengerService.Application.Media;
using MessengerService.Application.Realtime;
using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using MessengerService.Infrastructure.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessengerService.Tests;

public sealed class GroupConversationDeletionTests
{
    [Fact]
    public async Task Admin_CanDeleteGroup_AndEveryActiveMemberReceivesTerminalEvent()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db, ConversationType.Group, adminUserId: 11, memberUserId: 22);
        var firstMessage = NewMessage(conversation.Id, senderUserId: 11, sequence: 1);
        var secondMessage = NewMessage(conversation.Id, senderUserId: 22, sequence: 2);
        secondMessage.ReplyToMessageId = firstMessage.Id;
        db.Messages.AddRange(firstMessage, secondMessage);
        db.MessageAttachments.AddRange(
            new MessageAttachment { MessageId = firstMessage.Id, Ordinal = 0, Url = "/media/files/admin.jpg" },
            new MessageAttachment { MessageId = secondMessage.Id, Ordinal = 0, Url = "/media/files/member.jpg" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await NewService(db).DeleteGroupConversationAsync(
            11,
            conversation.Id,
            TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.False(await db.Conversations.AnyAsync(
            item => item.Id == conversation.Id,
            TestContext.Current.CancellationToken));
        var terminalEvents = await db.OutboxEvents.AsNoTracking()
            .Where(item => item.Kind == RealtimeEventKinds.ConversationDeleted)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, terminalEvents.Count);
        Assert.Contains(terminalEvents, item => item.Topic == RealtimeTopics.Inbox(11));
        Assert.Contains(terminalEvents, item => item.Topic == RealtimeTopics.Inbox(22));

        var mediaEvents = await db.OutboxEvents.AsNoTracking()
            .Where(item => item.Kind == MediaLifecycleEventKinds.Delete)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, mediaEvents.Count);
        Assert.Contains(mediaEvents, item => item.ActorUserId == 11 && item.PayloadJson.Contains("admin.jpg", StringComparison.Ordinal));
        Assert.Contains(mediaEvents, item => item.ActorUserId == 22 && item.PayloadJson.Contains("member.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Member_CannotDeleteGroup()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db, ConversationType.Group, adminUserId: 11, memberUserId: 22);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<MessagingApplicationException>(() =>
            NewService(db).DeleteGroupConversationAsync(22, conversation.Id, TestContext.Current.CancellationToken));

        Assert.Equal(MessagingErrorCodes.Forbidden, error.Code);
        Assert.True(await db.Conversations.AnyAsync(
            item => item.Id == conversation.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletingGroup_DoesNotDeleteMediaStillReferencedByAnotherActiveMessage()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db, ConversationType.Group, adminUserId: 11, memberUserId: 22);
        var survivingConversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Type = ConversationType.Group,
            Title = "Surviving group",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Conversations.Add(survivingConversation);
        var removedMessage = NewMessage(conversation.Id, senderUserId: 11, sequence: 1);
        var survivingMessage = NewMessage(survivingConversation.Id, senderUserId: 22, sequence: 1);
        db.Messages.AddRange(removedMessage, survivingMessage);
        db.MessageAttachments.AddRange(
            new MessageAttachment { MessageId = removedMessage.Id, Ordinal = 0, Url = "/media/files/shared.jpg" },
            new MessageAttachment { MessageId = survivingMessage.Id, Ordinal = 0, Url = "/media/files/shared.jpg" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await NewService(db).DeleteGroupConversationAsync(
            11,
            conversation.Id,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            await db.OutboxEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken),
            item => item.Kind == MediaLifecycleEventKinds.Delete &&
                    item.PayloadJson.Contains("shared.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonParticipant_CannotDeleteGroup()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db, ConversationType.Group, adminUserId: 11, memberUserId: 22);
        db.Users.Add(new MessagingUser { UserId = 33, Status = MessagingUserStatus.Active });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<MessagingApplicationException>(() =>
            NewService(db).DeleteGroupConversationAsync(33, conversation.Id, TestContext.Current.CancellationToken));

        Assert.Equal(MessagingErrorCodes.NotParticipant, error.Code);
    }

    [Fact]
    public async Task DirectConversation_CannotBeDeletedThroughGroupMutation()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db, ConversationType.Direct, adminUserId: 11, memberUserId: 22);
        conversation.DirectUserLowId = 11;
        conversation.DirectUserHighId = 22;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<MessagingApplicationException>(() =>
            NewService(db).DeleteGroupConversationAsync(11, conversation.Id, TestContext.Current.CancellationToken));

        Assert.Equal(MessagingErrorCodes.InvalidInput, error.Code);
    }

    private static MessagingDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MessagingDbContext(options);
    }

    private static MessagingApplicationService NewService(MessagingDbContext db) =>
        new(
            db,
            null!,
            null!,
            null!,
            new OutboxWakeSignal(),
            TimeProvider.System,
            Options.Create(new MessagingRulesOptions()));

    private static Conversation SeedConversation(
        MessagingDbContext db,
        ConversationType type,
        long adminUserId,
        long memberUserId)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Type = type,
            Title = type == ConversationType.Group ? "Security test group" : null,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Users.AddRange(
            new MessagingUser { UserId = adminUserId, Status = MessagingUserStatus.Active },
            new MessagingUser { UserId = memberUserId, Status = MessagingUserStatus.Active });
        db.Conversations.Add(conversation);
        db.ConversationParticipants.AddRange(
            new ConversationParticipant
            {
                ConversationId = conversation.Id,
                UserId = adminUserId,
                Role = ParticipantRole.Admin,
                JoinedAt = now
            },
            new ConversationParticipant
            {
                ConversationId = conversation.Id,
                UserId = memberUserId,
                Role = ParticipantRole.Member,
                JoinedAt = now
            });
        return conversation;
    }

    private static Message NewMessage(Guid conversationId, long senderUserId, long sequence) =>
        new()
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderUserId = senderUserId,
            Sequence = sequence,
            ClientMessageId = Guid.NewGuid(),
            Text = "test",
            CreatedAt = DateTimeOffset.UtcNow
        };
}
