using MessengerService.Application;
using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using MessengerService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MessengerService.Tests;

public sealed class DirectContactQueryServiceTests
{
    [Fact]
    public async Task GetContactIds_ReturnsDistinctActiveDirectCounterpartsOnly()
    {
        await using var db = CreateContext();
        var now = DateTimeOffset.UtcNow;
        db.Users.AddRange(
            new MessagingUser { UserId = 1, Status = MessagingUserStatus.Active },
            new MessagingUser { UserId = 2, Status = MessagingUserStatus.Active },
            new MessagingUser { UserId = 3, Status = MessagingUserStatus.Active },
            new MessagingUser { UserId = 4, Status = MessagingUserStatus.Deleted },
            new MessagingUser { UserId = 5, Status = MessagingUserStatus.Active });
        db.Conversations.AddRange(
            Direct(1, 2, now),
            Direct(1, 3, now.AddMinutes(-1)),
            Direct(1, 4, now.AddMinutes(-2)),
            Direct(2, 5, now.AddMinutes(-3)),
            new Conversation
            {
                Type = ConversationType.Group,
                Title = "Not a contact source",
                CreatedAt = now,
                UpdatedAt = now
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new DirectContactQueryService(db);

        var result = await service.GetContactIdsAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(new long[] { 2, 3 }, result);
    }

    [Fact]
    public async Task GetContactIds_InactiveViewerReturnsEmpty()
    {
        await using var db = CreateContext();
        db.Users.Add(new MessagingUser { UserId = 1, Status = MessagingUserStatus.Deleted });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new DirectContactQueryService(db)
            .GetContactIdsAsync(1, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    private static Conversation Direct(long first, long second, DateTimeOffset updatedAt) => new()
    {
        Type = ConversationType.Direct,
        DirectUserLowId = Math.Min(first, second),
        DirectUserHighId = Math.Max(first, second),
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt
    };

    private static MessagingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MessagingDbContext(options);
    }
}
