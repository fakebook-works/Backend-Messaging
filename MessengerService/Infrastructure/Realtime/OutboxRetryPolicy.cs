using System.Linq.Expressions;
using MessengerService.Application.Media;
using MessengerService.Domain.Entities;

namespace MessengerService.Infrastructure.Realtime;

internal static class OutboxRetryPolicy
{
    internal const int MaxOrdinaryAttempts = 10;
    internal const int MaxDurableAttemptCount = int.MaxValue - 1;
    internal const int MaxDurableRetrySeconds = 3_600;

    internal static bool IsDurableMediaLifecycle(string kind) =>
        kind is MediaLifecycleEventKinds.Finalize or MediaLifecycleEventKinds.Delete;

    internal static int NextAttemptCount(int current, bool durable) => durable
        ? current >= MaxDurableAttemptCount ? MaxDurableAttemptCount : current + 1
        : current + 1;

    internal static Expression<Func<OutboxEvent, bool>> ExpiredDeadLetterPredicate(
        DateTimeOffset cutoff) =>
        value => value.ProcessedAt == null &&
                 value.NextAttemptAt == null &&
                 value.LastError != null &&
                 value.AttemptCount >= MaxOrdinaryAttempts &&
                 value.Kind != MediaLifecycleEventKinds.Finalize &&
                 value.Kind != MediaLifecycleEventKinds.Delete &&
                 value.CreatedAt < cutoff;
}
