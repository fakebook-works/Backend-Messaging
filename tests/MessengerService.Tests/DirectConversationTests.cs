using MessengerService.Application;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Models;
using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using MessengerService.Infrastructure.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace MessengerService.Tests;

public sealed class DirectConversationTests
{
    [Fact]
    public async Task CreateDirectConversation_AllowsUnblockedNonFriendAndReturnsExistingPair()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateContext();
        db.Users.AddRange(
            new MessagingUser { UserId = 10, Status = MessagingUserStatus.Active },
            new MessagingUser { UserId = 20, Status = MessagingUserStatus.Active });
        await db.SaveChangesAsync(cancellationToken);
        var permissions = new UnblockedNonFriendPermissionClient();
        var service = new MessagingApplicationService(
            db,
            permissions,
            new FakeProvisioningService(),
            null!,
            new OutboxWakeSignal(),
            TimeProvider.System,
            Options.Create(new MessagingRulesOptions()));

        var first = await service.CreateDirectConversationAsync(
            10,
            new CreateDirectConversationCommand(20),
            cancellationToken);
        var second = await service.CreateDirectConversationAsync(
            20,
            new CreateDirectConversationCommand(10),
            cancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.Conversations.CountAsync(
            conversation => conversation.Type == ConversationType.Direct,
            cancellationToken));
        Assert.Equal(2, permissions.Actions.Count(action => action == SocialGraphPermissionAction.CreateDirect));
    }

    [Fact]
    public async Task SendMessage_AllowsUnblockedNonFriendInDirectConversation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateContext();
        db.Users.AddRange(
            new MessagingUser { UserId = 10, Status = MessagingUserStatus.Active },
            new MessagingUser { UserId = 20, Status = MessagingUserStatus.Active });
        await db.SaveChangesAsync(cancellationToken);
        var permissions = new UnblockedNonFriendPermissionClient();
        var service = CreateService(db, permissions);
        var conversation = await service.CreateDirectConversationAsync(
            10,
            new CreateDirectConversationCommand(20),
            cancellationToken);

        var message = await service.SendMessageAsync(
            10,
            new SendMessageCommand(conversation.Id, Guid.NewGuid(), "hello", [], null),
            cancellationToken);

        Assert.Equal(10, message.SenderUserId);
        Assert.Equal("hello", message.Text);
        Assert.Contains(SocialGraphPermissionAction.SendDirect, permissions.Actions);
    }

    [Fact]
    public async Task SendMessage_RejectsExistingDirectConversationAfterEitherUserBlocks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateContext();
        db.Users.AddRange(
            new MessagingUser { UserId = 10, Status = MessagingUserStatus.Active },
            new MessagingUser { UserId = 20, Status = MessagingUserStatus.Active });
        await db.SaveChangesAsync(cancellationToken);
        var permissions = new UnblockedNonFriendPermissionClient();
        var service = CreateService(db, permissions);
        var conversation = await service.CreateDirectConversationAsync(
            10,
            new CreateDirectConversationCommand(20),
            cancellationToken);
        permissions.DenyDirectWrites = true;

        var error = await Assert.ThrowsAsync<MessagingApplicationException>(() => service.SendMessageAsync(
            10,
            new SendMessageCommand(conversation.Id, Guid.NewGuid(), "blocked", [], null),
            cancellationToken));

        Assert.Equal(MessagingErrorCodes.DirectMessageForbidden, error.Code);
        Assert.DoesNotContain(
            await db.Messages.AsNoTracking().ToListAsync(cancellationToken),
            message => message.ConversationId == conversation.Id && message.Text == "blocked");
    }

    private static MessagingApplicationService CreateService(
        MessagingDbContext db,
        ISocialGraphPermissionClient permissions) => new(
        db,
        permissions,
        new FakeProvisioningService(),
        null!,
        new OutboxWakeSignal(),
        TimeProvider.System,
        Options.Create(new MessagingRulesOptions()));

    private static MessagingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MessagingDbContext(options);
    }

    private sealed class UnblockedNonFriendPermissionClient : ISocialGraphPermissionClient
    {
        public List<SocialGraphPermissionAction> Actions { get; } = [];
        public bool DenyDirectWrites { get; set; }

        public Task<SocialGraphPermissionCheckResult> CheckAsync(
            long actorUserId,
            IReadOnlyCollection<long> targetUserIds,
            SocialGraphPermissionAction action,
            CancellationToken cancellationToken)
        {
            Assert.Contains(
                action,
                new[]
                {
                    SocialGraphPermissionAction.CreateDirect,
                    SocialGraphPermissionAction.SendDirect,
                    SocialGraphPermissionAction.InspectBlock
                });
            Actions.Add(action);
            var denied = DenyDirectWrites && action == SocialGraphPermissionAction.SendDirect;
            return Task.FromResult(new SocialGraphPermissionCheckResult(
                targetUserIds.Select(userId => new SocialGraphPermissionDecision(
                    userId,
                    Allowed: !denied,
                    IsFriend: false,
                    BlockedEitherDirection: denied,
                    Reason: denied ? "BLOCKED" : null)).ToArray()));
        }
    }
}
