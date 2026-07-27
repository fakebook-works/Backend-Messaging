using System.Text.Json;
using HotChocolate.Subscriptions;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Media;
using MessengerService.Application.Realtime;
using MessengerService.Configuration;
using MessengerService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessengerService.Infrastructure.Realtime;

public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ITopicEventSender eventSender,
    IUploadMediaClient uploadMediaClient,
    IOptions<MessagingOptions> options,
    OutboxWakeSignal wakeSignal,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 50;

    /// <summary>
    /// Attempts after which an event stops being retried.
    /// </summary>
    /// <remarks>
    /// There was no ceiling at all: the claim query only filtered on processed_at and
    /// next_attempt_at, so an event that could never succeed — a payload that fails to
    /// deserialize, for instance — was retried every sixty seconds forever, and the cleanup
    /// pass only removes rows with processed_at set, so it was never purged either.
    /// </remarks>
    private const int MaxAttempts = 10;

    /// <summary>
    /// How long a claimed batch is hidden from other workers while it is being dispatched.
    /// </summary>
    /// <remarks>
    /// Claiming used to be FOR UPDATE SKIP LOCKED inside the same transaction that then made
    /// the HTTP calls, so row locks were held across up to fifty round trips to the upload
    /// service. Claiming now commits immediately and leases the rows instead, so dispatch
    /// happens with no transaction open and no locks held.
    /// </remarks>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private DateTimeOffset _nextCleanupAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollMilliseconds = options.Value.OutboxPollMilliseconds;
        var maxIdlePollMilliseconds = options.Value.OutboxMaxIdlePollMilliseconds;
        var idlePollMilliseconds = pollMilliseconds;

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
                var wasWoken = await wakeSignal.WaitAsync(
                    TimeSpan.FromMilliseconds(idlePollMilliseconds),
                    stoppingToken);
                idlePollMilliseconds = wasWoken
                    ? pollMilliseconds
                    : Math.Min(maxIdlePollMilliseconds, idlePollMilliseconds * 2);
            }
            else
            {
                idlePollMilliseconds = pollMilliseconds;
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var now = DateTimeOffset.UtcNow;

        // Claim in its own short transaction, then dispatch with nothing held. The lease on
        // next_attempt_at is what keeps another worker off these rows once the lock is gone;
        // if this process dies mid-batch the lease simply expires and they are picked up again.
        List<MessengerService.Domain.Entities.OutboxEvent> events;
        await using (var claim = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            events = await dbContext.OutboxEvents
                .FromSqlInterpolated(
                    $"""
                     SELECT *
                     FROM messenger.outbox_events
                     WHERE processed_at IS NULL
                       AND attempt_count < {MaxAttempts}
                       AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                     ORDER BY created_at
                     FOR UPDATE SKIP LOCKED
                     LIMIT {BatchSize}
                     """)
                .ToListAsync(cancellationToken);
            if (events.Count == 0)
            {
                await claim.CommitAsync(cancellationToken);
                return 0;
            }

            var leaseUntil = now.Add(ClaimLease);
            foreach (var claimed in events)
            {
                claimed.NextAttemptAt = leaseUntil;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await claim.CommitAsync(cancellationToken);
        }

        foreach (var outboxEvent in events)
        {
            try
            {
                if (outboxEvent.Kind is MediaLifecycleEventKinds.Finalize or MediaLifecycleEventKinds.Delete)
                {
                    var mediaPayload = MediaLifecycleOutbox.Deserialize(outboxEvent.PayloadJson);
                    if (outboxEvent.Kind == MediaLifecycleEventKinds.Finalize)
                    {
                        await uploadMediaClient.FinalizeAsync(mediaPayload.Urls, cancellationToken);
                    }
                    else
                    {
                        await uploadMediaClient.DeleteAsync(mediaPayload.Urls, cancellationToken);
                    }
                }
                else
                {
                    var payload = JsonSerializer.Deserialize<RealtimeEvent>(
                        outboxEvent.PayloadJson,
                        JsonOptions) ?? throw new JsonException("The outbox payload is empty.");

                    await eventSender.SendAsync(outboxEvent.Topic, payload, cancellationToken);
                }
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
                // A payload that will not deserialize cannot succeed on any later attempt, so
                // it is retired immediately rather than retried every minute until the ceiling.
                var permanent = exception is JsonException;
                outboxEvent.AttemptCount = permanent ? MaxAttempts : outboxEvent.AttemptCount + 1;
                outboxEvent.LastError = Truncate(exception.Message, 4_000);
                outboxEvent.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Min(60, Math.Pow(2, Math.Min(outboxEvent.AttemptCount, 6))));

                if (outboxEvent.AttemptCount >= MaxAttempts)
                {
                    // Left in the table with its error rather than deleted: it is the only
                    // record of an event that was never delivered.
                    logger.LogError(
                        exception,
                        "Messaging outbox event {EventId} of kind {EventKind} is dead-lettered after {AttemptCount} attempts and will not be retried.",
                        outboxEvent.Id,
                        outboxEvent.Kind,
                        outboxEvent.AttemptCount);
                }
                else
                {
                    logger.LogWarning(
                        exception,
                        "Could not publish Messaging outbox event {EventId} of kind {EventKind}; retry {AttemptCount} scheduled.",
                        outboxEvent.Id,
                        outboxEvent.Kind,
                        outboxEvent.AttemptCount);
                }
            }
        }

        // Results are written outside any transaction; the claim lease already reserved
        // these rows, so nothing else can be working on them.
        await dbContext.SaveChangesAsync(cancellationToken);
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
