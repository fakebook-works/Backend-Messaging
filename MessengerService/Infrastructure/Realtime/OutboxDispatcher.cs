using System.Text.Json;
using HotChocolate.Subscriptions;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Media;
using MessengerService.Application.Realtime;
using MessengerService.Configuration;
using MessengerService.Infrastructure.Persistence;
using MessengerService.Infrastructure.Http;
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
    /// Attempts after which an ordinary realtime event stops being retried.
    /// </summary>
    /// <remarks>
    /// There was no ceiling at all: the claim query only filtered on processed_at and
    /// next_attempt_at, so an event that could never succeed — a payload that fails to
    /// deserialize, for instance — was retried every sixty seconds forever, and the cleanup
    /// pass only removes rows with processed_at set, so it was never purged either. Valid
    /// media lifecycle events are the deliberate exception: their parent already committed,
    /// so a transient outage must remain scheduled beyond the ordinary retry ceiling.
    /// </remarks>
    private const int MaxAttempts = OutboxRetryPolicy.MaxOrdinaryAttempts;
    private const int MaxDurableMediaRetrySeconds = OutboxRetryPolicy.MaxDurableRetrySeconds;

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
                       AND (attempt_count < {MaxAttempts}
                            OR kind IN ({MediaLifecycleEventKinds.Finalize}, {MediaLifecycleEventKinds.Delete}))
                       AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                     ORDER BY CASE
                                WHEN kind IN ({MediaLifecycleEventKinds.Finalize}, {MediaLifecycleEventKinds.Delete}) THEN 0
                                ELSE 1
                              END,
                              created_at
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
                    await MediaLifecycleOutbox.DispatchAsync(
                        outboxEvent,
                        uploadMediaClient,
                        cancellationToken);
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
                // Poison ordinary realtime payloads retire immediately. Media lifecycle is
                // durable parent-repair state and therefore remains scheduled even when the
                // current failure is classified as permanent.
                var permanent = exception is JsonException or UploadMediaPermanentException;
                var durableMediaLifecycle =
                    outboxEvent.Kind is MediaLifecycleEventKinds.Finalize or MediaLifecycleEventKinds.Delete;
                outboxEvent.AttemptCount = permanent && !durableMediaLifecycle
                    ? MaxAttempts
                    : OutboxRetryPolicy.NextAttemptCount(
                        outboxEvent.AttemptCount,
                        durableMediaLifecycle);
                outboxEvent.LastError = Truncate(exception.Message, 4_000);
                var exhaustedOrdinaryEvent = !durableMediaLifecycle && outboxEvent.AttemptCount >= MaxAttempts;
                outboxEvent.NextAttemptAt = durableMediaLifecycle
                    ? DateTimeOffset.UtcNow.AddSeconds(Math.Min(
                        MaxDurableMediaRetrySeconds,
                        Math.Pow(2, Math.Min(outboxEvent.AttemptCount, 12))))
                    : permanent || exhaustedOrdinaryEvent
                        ? null
                        : DateTimeOffset.UtcNow.AddSeconds(Math.Min(
                            60,
                            Math.Pow(2, Math.Min(outboxEvent.AttemptCount, 12))));

                if (durableMediaLifecycle)
                {
                    // The parent state is already committed. Even malformed/permanent-class
                    // responses remain scheduled: a rolling deployment or offline repair can
                    // make them dispatchable, while retiring the row would lose the only exact
                    // attach/detach instruction.
                    logger.LogWarning(
                        exception,
                        "Could not publish durable Messaging media event {EventId} of kind {EventKind}; retry {AttemptCount} remains scheduled.",
                        outboxEvent.Id,
                        outboxEvent.Kind,
                        outboxEvent.AttemptCount);
                }
                else if (permanent || exhaustedOrdinaryEvent)
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

            var deadLetterCutoff = now.AddHours(-options.Value.OutboxDeadLetterRetentionHours);
            var deadLetters = await dbContext.OutboxEvents
                .Where(OutboxRetryPolicy.ExpiredDeadLetterPredicate(deadLetterCutoff))
                .ExecuteDeleteAsync(cancellationToken);
            if (deadLetters > 0)
            {
                logger.LogInformation(
                    "Purged {EventCount} expired Messaging dead-letter outbox rows; pending/retryable rows were retained.",
                    deadLetters);
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
