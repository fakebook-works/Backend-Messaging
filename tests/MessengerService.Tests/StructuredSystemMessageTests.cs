using MessengerService.Application;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Models;
using MessengerService.Application.Realtime;
using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using MessengerService.Infrastructure.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace MessengerService.Tests;

public sealed class StructuredSystemMessageTests
{
    [Fact]
    public async Task SendingMessage_AdvancesTheSendersOwnReceipt()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db, 11, 22);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var message = await NewService(db).SendMessageAsync(
            11,
            new SendMessageCommand(conversation.Id, Guid.NewGuid(), "hello", [], null),
            TestContext.Current.CancellationToken);

        var sender = await db.ConversationParticipants.SingleAsync(
            participant => participant.ConversationId == conversation.Id && participant.UserId == 11,
            TestContext.Current.CancellationToken);
        Assert.Equal(message.Sequence, sender.LastDeliveredSequence);
        Assert.Equal(message.Sequence, sender.LastReadSequence);
    }

    [Fact]
    public async Task EditingMessage_ReturnsDecodedHistoryAndNeverLeaksStorageEnvelope()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db, 11, 22);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = NewService(db);
        var message = await service.SendMessageAsync(
            11,
            new SendMessageCommand(conversation.Id, Guid.NewGuid(), "first", [], null),
            TestContext.Current.CancellationToken);

        var second = await service.EditMessageAsync(
            11,
            new EditMessageCommand(message.Id, "second"),
            TestContext.Current.CancellationToken);
        var third = await service.EditMessageAsync(
            11,
            new EditMessageCommand(message.Id, "third"),
            TestContext.Current.CancellationToken);

        Assert.Equal("third", third.Text);
        Assert.Equal(["first", "second"], third.EditHistory.Select(item => item.Text).ToArray());
        Assert.DoesNotContain(MessageTextHistoryCodec.ReservedPrefix, third.Text, StringComparison.Ordinal);
        var stored = await db.Messages.AsNoTracking().SingleAsync(
            item => item.Id == message.Id,
            TestContext.Current.CancellationToken);
        Assert.StartsWith(MessageTextHistoryCodec.ReservedPrefix, stored.Text, StringComparison.Ordinal);
        Assert.Equal("second", second.Text);
        Assert.Single(second.EditHistory);
    }

    [Fact]
    public async Task PublicSendAndEdit_RejectTheReservedStoragePrefix()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db, 11, 22);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = NewService(db);
        var sent = await service.SendMessageAsync(
            11,
            new SendMessageCommand(conversation.Id, Guid.NewGuid(), "safe", [], null),
            TestContext.Current.CancellationToken);
        var forged = MessageTextHistoryCodec.ReservedPrefix + "payload";

        var sendError = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.SendMessageAsync(
            11,
            new SendMessageCommand(conversation.Id, Guid.NewGuid(), forged, [], null),
            TestContext.Current.CancellationToken));
        var editError = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.EditMessageAsync(
            11,
            new EditMessageCommand(sent.Id, forged),
            TestContext.Current.CancellationToken));

        Assert.Equal(MessagingErrorCodes.InvalidInput, sendError.Code);
        Assert.Equal(MessagingErrorCodes.InvalidInput, editError.Code);
    }

    [Fact]
    public async Task AddingMember_PersistsStructuredSystemMessage_AndPublishesIt()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db, 11, 22);
        db.Users.Add(new MessagingUser { UserId = 33, Status = MessagingUserStatus.Active });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await NewService(db).AddConversationMembersAsync(
            11,
            new AddConversationMembersCommand(conversation.Id, [33]),
            TestContext.Current.CancellationToken);

        var systemMessage = await db.Messages.SingleAsync(
            message => message.ConversationId == conversation.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(MessageKind.System, systemMessage.Kind);
        Assert.Equal(SystemMessageEvent.MemberAdded, systemMessage.SystemEvent);
        Assert.Equal(11, systemMessage.SenderUserId);
        Assert.Equal(33, systemMessage.SystemSubjectUserId);
        Assert.Null(systemMessage.Text);
        Assert.Equal(systemMessage.Sequence, result.CurrentSequence);

        var sender = await db.ConversationParticipants.SingleAsync(
            participant => participant.ConversationId == conversation.Id && participant.UserId == 11,
            TestContext.Current.CancellationToken);
        Assert.Equal(systemMessage.Sequence, sender.LastReadSequence);
        Assert.Contains(
            await db.OutboxEvents.ToListAsync(TestContext.Current.CancellationToken),
            item => item.Kind == RealtimeEventKinds.MessageAdded && item.MessageId == systemMessage.Id);
    }

    [Fact]
    public async Task SystemMessage_CannotBeEditedDeletedReactedToOrRepliedTo()
    {
        await using var db = NewDb();
        var conversation = SeedGroup(db, 11, 22);
        var systemMessage = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = 11,
            Sequence = 1,
            ClientMessageId = Guid.NewGuid(),
            Kind = MessageKind.System,
            SystemEvent = SystemMessageEvent.AdminGranted,
            SystemSubjectUserId = 22,
            CreatedAt = DateTimeOffset.UtcNow
        };
        conversation.CurrentSequence = 1;
        db.Messages.Add(systemMessage);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = NewService(db);

        var edit = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.EditMessageAsync(
            11,
            new EditMessageCommand(systemMessage.Id, "forged"),
            TestContext.Current.CancellationToken));
        var delete = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.DeleteMessageAsync(
            11,
            new DeleteMessageCommand(systemMessage.Id),
            TestContext.Current.CancellationToken));
        var react = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.SetMessageReactionAsync(
            11,
            new SetMessageReactionCommand(systemMessage.Id, "like"),
            TestContext.Current.CancellationToken));
        var reply = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.SendMessageAsync(
            11,
            new SendMessageCommand(conversation.Id, Guid.NewGuid(), "reply", [], systemMessage.Id),
            TestContext.Current.CancellationToken));

        Assert.Equal(MessagingErrorCodes.InvalidInput, edit.Code);
        Assert.Equal(MessagingErrorCodes.InvalidInput, delete.Code);
        Assert.Equal(MessagingErrorCodes.InvalidInput, react.Code);
        Assert.Equal(MessagingErrorCodes.InvalidInput, reply.Code);
    }

    private static MessagingDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MessagingDbContext(options);
    }

    private static MessagingApplicationService NewService(MessagingDbContext db) =>
        new(
            db,
            new AllowAllSocialGraphPermissionClient(),
            new FakeProvisioningService(),
            null!,
            new OutboxWakeSignal(),
            TimeProvider.System,
            Options.Create(new MessagingRulesOptions()));

    private static Conversation SeedGroup(MessagingDbContext db, long adminUserId, long memberUserId)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Type = ConversationType.Group,
            Title = "Test group",
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

    private sealed class AllowAllSocialGraphPermissionClient : ISocialGraphPermissionClient
    {
        public Task<SocialGraphPermissionCheckResult> CheckAsync(
            long actorUserId,
            IReadOnlyCollection<long> targetUserIds,
            SocialGraphPermissionAction action,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SocialGraphPermissionCheckResult(
                targetUserIds.Select(userId => new SocialGraphPermissionDecision(
                    userId,
                    true,
                    true,
                    false,
                    null)).ToArray()));
    }
}
