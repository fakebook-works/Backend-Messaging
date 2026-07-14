using HotChocolate.Execution;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Realtime;
using MessengerService.Domain.Entities;
using MessengerService.Domain.Enums;
using MessengerService.GraphQL;
using MessengerService.Infrastructure.Persistence;
using MessengerService.Infrastructure.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessengerService.Tests;

public sealed class SubscriptionAuthorizationTests
{
    [Fact]
    public async Task ConversationAuthorization_IsRecheckedAfterMembershipIsRevoked()
    {
        await using var provider = CreateProvider();
        var conversationId = await SeedSharedConversationAsync(provider);
        var checker = provider.GetRequiredService<ISubscriptionAuthorizationChecker>();
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(
            SubscriptionEventAuthorization.Allow,
            await checker.AuthorizeConversationEventAsync(1, conversationId, cancellationToken));

        await SetLeftAtAsync(provider, conversationId, 1);

        Assert.Equal(
            SubscriptionEventAuthorization.Terminate,
            await checker.AuthorizeConversationEventAsync(1, conversationId, cancellationToken));
    }

    [Fact]
    public async Task InboxAuthorization_DropsDelayedConversationEventsButAllowsOwnRemoval()
    {
        await using var provider = CreateProvider();
        var conversationId = await SeedSharedConversationAsync(provider);
        await SetLeftAtAsync(provider, conversationId, 1);
        var checker = provider.GetRequiredService<ISubscriptionAuthorizationChecker>();
        var cancellationToken = TestContext.Current.CancellationToken;
        var occurredAt = DateTimeOffset.UtcNow;

        var delayedMessage = new RealtimeEvent(
            Guid.NewGuid(),
            RealtimeEventKinds.MessageAdded,
            conversationId,
            Guid.NewGuid(),
            2,
            10,
            occurredAt);
        var ownRemoval = new RealtimeEvent(
            Guid.NewGuid(),
            RealtimeEventKinds.MemberRemoved,
            conversationId,
            null,
            1,
            null,
            occurredAt);

        Assert.Equal(
            SubscriptionEventAuthorization.Skip,
            await checker.AuthorizeInboxEventAsync(1, delayedMessage, cancellationToken));
        Assert.Equal(
            SubscriptionEventAuthorization.Allow,
            await checker.AuthorizeInboxEventAsync(1, ownRemoval, cancellationToken));
    }

    [Fact]
    public async Task PresenceAuthorization_IsRecheckedAfterSharedMembershipEnds()
    {
        await using var provider = CreateProvider();
        var conversationId = await SeedSharedConversationAsync(provider);
        var checker = provider.GetRequiredService<ISubscriptionAuthorizationChecker>();
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(
            SubscriptionEventAuthorization.Allow,
            await checker.AuthorizePresenceEventAsync(1, 2, cancellationToken));

        await SetLeftAtAsync(provider, conversationId, 1);

        Assert.Equal(
            SubscriptionEventAuthorization.Skip,
            await checker.AuthorizePresenceEventAsync(1, 2, cancellationToken));
    }

    [Fact]
    public async Task DeletedViewer_TerminatesLongLivedSubscriptions()
    {
        await using var provider = CreateProvider();
        var conversationId = await SeedSharedConversationAsync(provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var user = await dbContext.Users.SingleAsync(
                value => value.UserId == 1,
                TestContext.Current.CancellationToken);
            user.Status = MessagingUserStatus.Deleted;
            user.DeletedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var checker = provider.GetRequiredService<ISubscriptionAuthorizationChecker>();
        var message = new RealtimeEvent(
            Guid.NewGuid(),
            RealtimeEventKinds.MessageAdded,
            conversationId,
            Guid.NewGuid(),
            2,
            11,
            DateTimeOffset.UtcNow);

        Assert.Equal(
            SubscriptionEventAuthorization.Terminate,
            await checker.AuthorizeConversationEventAsync(
                1, conversationId, TestContext.Current.CancellationToken));
        Assert.Equal(
            SubscriptionEventAuthorization.Terminate,
            await checker.AuthorizeInboxEventAsync(
                1, message, TestContext.Current.CancellationToken));
        Assert.Equal(
            SubscriptionEventAuthorization.Terminate,
            await checker.AuthorizePresenceEventAsync(
                1, 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FilteringStream_SkipsDeniedEventAndStopsOnTermination()
    {
        var events = Enumerable.Range(1, 4)
            .Select(sequence => new RealtimeEvent(
                Guid.NewGuid(),
                RealtimeEventKinds.MessageAdded,
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                sequence,
                DateTimeOffset.UtcNow))
            .ToArray();
        var calls = 0;
        await using var stream = new AuthorizationFilteringSourceStream(
            new TestSourceStream(events),
            (_, _) => Task.FromResult(++calls switch
            {
                1 => SubscriptionEventAuthorization.Allow,
                2 => SubscriptionEventAuthorization.Skip,
                _ => SubscriptionEventAuthorization.Terminate
            }));

        var received = new List<RealtimeEvent>();
        await foreach (var message in stream.ReadEventsAsync()
                           .WithCancellation(TestContext.Current.CancellationToken))
        {
            received.Add(message);
        }

        Assert.Single(received);
        Assert.Same(events[0], received[0]);
        Assert.Equal(3, calls);
    }

    private static ServiceProvider CreateProvider()
    {
        var databaseName = $"subscription-authorization-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<MessagingDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<ISubscriptionAuthorizationChecker, SubscriptionAuthorizationChecker>();
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> SeedSharedConversationAsync(ServiceProvider provider)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        dbContext.Users.AddRange(
            new MessagingUser { UserId = 1, Status = MessagingUserStatus.Active, CreatedAt = now },
            new MessagingUser { UserId = 2, Status = MessagingUserStatus.Active, CreatedAt = now });
        dbContext.Conversations.Add(new Conversation
        {
            Id = conversationId,
            Type = ConversationType.Group,
            CreatedAt = now,
            UpdatedAt = now
        });
        dbContext.ConversationParticipants.AddRange(
            new ConversationParticipant
            {
                ConversationId = conversationId,
                UserId = 1,
                JoinedAt = now,
                Role = ParticipantRole.Member
            },
            new ConversationParticipant
            {
                ConversationId = conversationId,
                UserId = 2,
                JoinedAt = now,
                Role = ParticipantRole.Member
            });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return conversationId;
    }

    private static async Task SetLeftAtAsync(ServiceProvider provider, Guid conversationId, long userId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var participant = await dbContext.ConversationParticipants.SingleAsync(
            value => value.ConversationId == conversationId && value.UserId == userId,
            TestContext.Current.CancellationToken);
        participant.LeftAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private sealed class TestSourceStream(IReadOnlyList<RealtimeEvent> events)
        : ISourceStream<RealtimeEvent>
    {
        public IAsyncEnumerable<RealtimeEvent> ReadEventsAsync() => ReadTypedEventsAsync();

        async IAsyncEnumerable<object?> ISourceStream.ReadEventsAsync()
        {
            await foreach (var message in ReadTypedEventsAsync())
            {
                yield return message;
            }
        }

        private async IAsyncEnumerable<RealtimeEvent> ReadTypedEventsAsync()
        {
            foreach (var message in events)
            {
                await Task.Yield();
                yield return message;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
