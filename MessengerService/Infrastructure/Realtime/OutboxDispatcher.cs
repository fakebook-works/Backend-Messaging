using System.Text.Json;
using HotChocolate.Subscriptions;
using MessengerService.Application.Realtime;
using MessengerService.Configuration;
using MessengerService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessengerService.Infrastructure.Realtime;

public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ITopicEventSender eventSender,
    IOptions<MessagingOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private DateTimeOffset _nextCleanupAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatched = 0;
            try
            {
                dispatched = await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The Messaging outbox dispatcher failed to process a batch.");
            }

            await TryPurgeProcessedEventsAsync(stoppingToken);

            if (dispatched == 0)
            {
                await Task.Delay(options.Value.OutboxPollMilliseconds, stoppingToken);
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var events = await dbContext.OutboxEvents
            .FromSqlInterpolated(
                $"""
                 SELECT *
                 FROM messenger.outbox_events
                 WHERE processed_at IS NULL
                   AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                 ORDER BY created_at
                 FOR UPDATE SKIP LOCKED
                 LIMIT {BatchSize}
                 """)
            .ToListAsync(cancellationToken);

        foreach (var outboxEvent in events)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<RealtimeEvent>(
                    outboxEvent.PayloadJson,
                    JsonOptions) ?? throw new JsonException("The outbox payload is empty.");

                await eventSender.SendAsync(outboxEvent.Topic, payload, cancellationToken);
                outboxEvent.ProcessedAt = DateTimeOffset.UtcNow;
                outboxEvent.LastError = null;
                outboxEvent.NextAttemptAt = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                outboxEvent.AttemptCount++;
                outboxEvent.LastError = Truncate(exception.Message, 4_000);
                outboxEvent.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Min(60, Math.Pow(2, Math.Min(outboxEvent.AttemptCount, 6))));

                logger.LogWarning(
                    exception,
                    "Could not publish Messaging outbox event {EventId} of kind {EventKind}; retry {AttemptCount} scheduled.",
                    outboxEvent.Id,
                    outboxEvent.Kind,
                    outboxEvent.AttemptCount);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return events.Count;
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private async Task TryPurgeProcessedEventsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextCleanupAt)
        {
            return;
        }

        _nextCleanupAt = now.AddMinutes(options.Value.OutboxCleanupIntervalMinutes);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var cutoff = now.AddHours(-options.Value.OutboxRetentionHours);
            var deleted = await dbContext.OutboxEvents
                .Where(value => value.ProcessedAt != null && value.ProcessedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
            if (deleted > 0)
            {
                logger.LogInformation("Purged {EventCount} processed Messaging outbox events.", deleted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not purge processed Messaging outbox events.");
        }
    }
}
