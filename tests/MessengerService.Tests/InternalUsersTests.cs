using MessengerService.Contracts.Internal;
using MessengerService.Controllers;
using MessengerService.Application.Realtime;
using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MessengerService.Tests;

public sealed class InternalUsersControllerTests
{
    [Theory]
    [InlineData(ProvisionUserOutcome.Created, StatusCodes.Status201Created)]
    [InlineData(ProvisionUserOutcome.AlreadyActive, StatusCodes.Status200OK)]
    public async Task Create_ReturnsIdempotentSuccess(
        ProvisionUserOutcome outcome,
        int expectedStatus)
    {
        var users = new FakeProvisioningService { ProvisionOutcome = outcome };
        var controller = CreateController(users);

        var result = await controller.Create(
            new CreateInternalUserRequest(42),
            CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        Assert.Equal(new InternalUserResponse(42), objectResult.Value);
        Assert.Equal([42L], users.ProvisionedUserIds);
    }

    [Fact]
    public async Task Create_DeletedTombstoneReturnsConflict()
    {
        var users = new FakeProvisioningService
        {
            ProvisionOutcome = ProvisionUserOutcome.DeletedTombstone
        };
        var controller = CreateController(users);

        var result = await controller.Create(
            new CreateInternalUserRequest(42),
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal("USER_DELETED", problem.Extensions["code"]);
        Assert.Equal(42L, problem.Extensions["userId"]);
    }

    [Fact]
    public async Task Create_InvalidUserIdDoesNotCallService()
    {
        var users = new FakeProvisioningService();
        var controller = CreateController(users);

        var result = await controller.Create(
            new CreateInternalUserRequest(0),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal("INVALID_USER_ID", problem.Extensions["code"]);
        Assert.Empty(users.ProvisionedUserIds);
    }

    [Fact]
    public async Task Delete_IsIdempotentNoContent()
    {
        var users = new FakeProvisioningService();
        var controller = CreateController(users);

        var result = await controller.Delete(42, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal([42L], users.TombstonedUserIds);
    }

    private static InternalUsersController CreateController(FakeProvisioningService users)
    {
        var controller = new InternalUsersController(users)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.HttpContext.Request.Path = "/internal/users";
        controller.HttpContext.TraceIdentifier = "test-trace";
        return controller;
    }
}

public sealed class MessagingUserProvisioningServiceTests
{
    [Fact]
    public async Task ProvisionAndTombstone_AreDurablyIdempotent()
    {
        await using var dbContext = CreateContext();
        var service = new MessagingUserProvisioningService(dbContext);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(
            ProvisionUserOutcome.Created,
            await service.ProvisionAsync(42, cancellationToken));
        Assert.Equal(
            ProvisionUserOutcome.AlreadyActive,
            await service.ProvisionAsync(42, cancellationToken));
        Assert.True(await service.IsActiveAsync(42, cancellationToken));

        await service.TombstoneAsync(42, cancellationToken);
        await service.TombstoneAsync(42, cancellationToken);

        Assert.False(await service.IsActiveAsync(42, cancellationToken));
        Assert.Equal(
            ProvisionUserOutcome.DeletedTombstone,
            await service.ProvisionAsync(42, cancellationToken));

        var user = await dbContext.Users.SingleAsync(
            candidate => candidate.UserId == 42,
            cancellationToken);
        Assert.Equal(MessagingUserStatus.Deleted, user.Status);
        Assert.NotNull(user.DeletedAt);
        Assert.Equal(
            1,
            await dbContext.OutboxEvents.CountAsync(
                value => value.Kind == RealtimeEventKinds.AccessRevoked,
                cancellationToken));
        Assert.Equal(
            1,
            await dbContext.OutboxEvents.CountAsync(
                value => value.Kind == RealtimeEventKinds.PresenceChanged,
                cancellationToken));
    }

    [Fact]
    public async Task TombstoneUnknownUser_PreventsLateProvision()
    {
        await using var dbContext = CreateContext();
        var service = new MessagingUserProvisioningService(dbContext);
        var cancellationToken = TestContext.Current.CancellationToken;

        await service.TombstoneAsync(84, cancellationToken);

        Assert.Equal(
            ProvisionUserOutcome.DeletedTombstone,
            await service.ProvisionAsync(84, cancellationToken));
        Assert.False(await service.IsActiveAsync(84, cancellationToken));
    }

    [Fact]
    public async Task Tombstone_RevokesMembershipAndPromotesOldestRemainingGroupMember()
    {
        await using var dbContext = CreateContext();
        var service = new MessagingUserProvisioningService(dbContext);
        var cancellationToken = TestContext.Current.CancellationToken;
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Users.AddRange(
            new MessagingUser { UserId = 1, CreatedAt = now.AddDays(-3) },
            new MessagingUser { UserId = 2, CreatedAt = now.AddDays(-2) },
            new MessagingUser { UserId = 3, CreatedAt = now.AddDays(-1) });
        dbContext.Conversations.Add(new Conversation
        {
            Id = conversationId,
            Type = ConversationType.Group,
            Title = "Group",
            CreatedAt = now.AddDays(-3),
            UpdatedAt = now.AddDays(-3)
        });
        dbContext.ConversationParticipants.AddRange(
            new ConversationParticipant
            {
                ConversationId = conversationId,
                UserId = 1,
                Role = ParticipantRole.Admin,
                JoinedAt = now.AddDays(-3)
            },
            new ConversationParticipant
            {
                ConversationId = conversationId,
                UserId = 2,
                Role = ParticipantRole.Member,
                JoinedAt = now.AddDays(-2)
            },
            new ConversationParticipant
            {
                ConversationId = conversationId,
                UserId = 3,
                Role = ParticipantRole.Member,
                JoinedAt = now.AddDays(-1)
            });
        dbContext.UserPresences.Add(new UserPresence
        {
            UserId = 1,
            IsOnline = true,
            ExpiresAt = now.AddMinutes(1),
            UpdatedAt = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        await service.TombstoneAsync(1, cancellationToken);

        var deletedUser = await dbContext.Users.SingleAsync(value => value.UserId == 1, cancellationToken);
        var deletedMembership = await dbContext.ConversationParticipants.SingleAsync(
            value => value.ConversationId == conversationId && value.UserId == 1,
            cancellationToken);
        var successor = await dbContext.ConversationParticipants.SingleAsync(
            value => value.ConversationId == conversationId && value.UserId == 2,
            cancellationToken);
        var presence = await dbContext.UserPresences.SingleAsync(value => value.UserId == 1, cancellationToken);
        var outbox = await dbContext.OutboxEvents.ToListAsync(cancellationToken);

        Assert.Equal(MessagingUserStatus.Deleted, deletedUser.Status);
        Assert.NotNull(deletedMembership.LeftAt);
        Assert.Equal(ParticipantRole.Admin, successor.Role);
        Assert.False(presence.IsOnline);
        Assert.Contains(outbox, value =>
            value.Kind == RealtimeEventKinds.MemberRemoved &&
            value.Topic == RealtimeTopics.Conversation(conversationId));
        Assert.Contains(outbox, value =>
            value.Kind == RealtimeEventKinds.MemberRemoved &&
            value.Topic == RealtimeTopics.Inbox(2));
        Assert.Contains(outbox, value =>
            value.Kind == RealtimeEventKinds.AccessRevoked &&
            value.Topic == RealtimeTopics.Inbox(1));
        Assert.Contains(outbox, value =>
            value.Kind == RealtimeEventKinds.PresenceChanged &&
            value.Topic == RealtimeTopics.Presence);
    }

    [Fact]
    public async Task TombstoneRetry_RepairsAccessForDeletedUserAndBreaksSuccessorTieByUserId()
    {
        await using var dbContext = CreateContext();
        var service = new MessagingUserProvisioningService(dbContext);
        var cancellationToken = TestContext.Current.CancellationToken;
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var sameJoinedAt = now.AddDays(-1);

        dbContext.Users.AddRange(
            new MessagingUser
            {
                UserId = 1,
                Status = MessagingUserStatus.Deleted,
                CreatedAt = now.AddDays(-3),
                DeletedAt = now.AddDays(-2)
            },
            new MessagingUser { UserId = 2, CreatedAt = now.AddDays(-2) },
            new MessagingUser { UserId = 3, CreatedAt = now.AddDays(-2) });
        dbContext.Conversations.Add(new Conversation
        {
            Id = conversationId,
            Type = ConversationType.Group,
            Title = "Recovery group",
            CreatedAt = now.AddDays(-3),
            UpdatedAt = now.AddDays(-3)
        });
        dbContext.ConversationParticipants.AddRange(
            new ConversationParticipant
            {
                ConversationId = conversationId,
                UserId = 1,
                Role = ParticipantRole.Admin,
                JoinedAt = now.AddDays(-3)
            },
            new ConversationParticipant
            {
                ConversationId = conversationId,
                UserId = 3,
                Role = ParticipantRole.Member,
                JoinedAt = sameJoinedAt
            },
            new ConversationParticipant
            {
                ConversationId = conversationId,
                UserId = 2,
                Role = ParticipantRole.Member,
                JoinedAt = sameJoinedAt
            });
        await dbContext.SaveChangesAsync(cancellationToken);

        await service.TombstoneAsync(1, cancellationToken);

        var deletedMembership = await dbContext.ConversationParticipants.SingleAsync(
            value => value.ConversationId == conversationId && value.UserId == 1,
            cancellationToken);
        var successor = await dbContext.ConversationParticipants.SingleAsync(
            value => value.ConversationId == conversationId && value.Role == ParticipantRole.Admin &&
                     value.LeftAt == null,
            cancellationToken);
        var memberRemovedEvents = await dbContext.OutboxEvents.CountAsync(
            value => value.Kind == RealtimeEventKinds.MemberRemoved,
            cancellationToken);

        Assert.NotNull(deletedMembership.LeftAt);
        Assert.Equal(2, successor.UserId);
        Assert.Equal(3, memberRemovedEvents);

        await service.TombstoneAsync(1, cancellationToken);

        Assert.Equal(
            memberRemovedEvents,
            await dbContext.OutboxEvents.CountAsync(
                value => value.Kind == RealtimeEventKinds.MemberRemoved,
                cancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task LifecycleMutations_RejectNonPositiveIds(long userId)
    {
        await using var dbContext = CreateContext();
        var service = new MessagingUserProvisioningService(dbContext);
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ProvisionAsync(userId, cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.TombstoneAsync(userId, cancellationToken));
        Assert.False(await service.IsActiveAsync(userId, cancellationToken));
    }

    private static MessagingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MessagingDbContext(options);
    }
}
