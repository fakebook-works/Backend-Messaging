using System.Text.Json;
using MessengerService.Application.Realtime;
using MessengerService.Configuration;
using MessengerService.Domain.Entities;
using MessengerService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessengerService.Infrastructure.Realtime;

public sealed class PresenceExpiryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    OutboxWakeSignal wakeSignal,
    ILogger<PresenceExpiryWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(options.Value.PresenceSweepSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ExpireBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not expire stale Messaging presence records.");
            }
        }
    }

    private async Task ExpireBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var stale = await dbContext.UserPresences
            .FromSqlInterpolated(
                $"""
                 SELECT *
                 FROM messenger.presence
                 WHERE is_online = TRUE AND expires_at <= {now}
                 ORDER BY expires_at
                 FOR UPDATE SKIP LOCKED
                 LIMIT 250
                 """)
            .ToListAsync(cancellationToken);

        foreach (var presence in stale)
        {
            presence.IsOnline = false;

            var realtimeEvent = new RealtimeEvent(
                Guid.NewGuid(),
                RealtimeEventKinds.PresenceChanged,
                null,
                null,
                presence.UserId,
                null,
                now);

            dbContext.OutboxEvents.Add(new OutboxEvent
            {
                Id = realtimeEvent.EventId,
                Topic = RealtimeTopics.Presence,
                Kind = realtimeEvent.Kind,
                PayloadJson = JsonSerializer.Serialize(realtimeEvent, JsonOptions),
                SubjectUserId = presence.UserId,
                OccurredAt = now,
                CreatedAt = now
            });
        }

        if (stale.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (stale.Count > 0)
        {
            wakeSignal.Pulse();
        }
    }
}
