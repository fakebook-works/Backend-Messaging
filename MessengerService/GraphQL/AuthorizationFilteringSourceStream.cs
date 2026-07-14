using System.Runtime.CompilerServices;
using HotChocolate.Execution;
using MessengerService.Application.Abstractions;
using MessengerService.Application.Realtime;

namespace MessengerService.GraphQL;

public sealed class AuthorizationFilteringSourceStream(
    ISourceStream<RealtimeEvent> inner,
    Func<RealtimeEvent, CancellationToken, Task<SubscriptionEventAuthorization>> authorize)
    : ISourceStream<RealtimeEvent>
{
    public IAsyncEnumerable<RealtimeEvent> ReadEventsAsync() => ReadAuthorizedEventsAsync();

    async IAsyncEnumerable<object?> ISourceStream.ReadEventsAsync()
    {
        await foreach (var message in ReadAuthorizedEventsAsync())
        {
            yield return message;
        }
    }

    private async IAsyncEnumerable<RealtimeEvent> ReadAuthorizedEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in inner.ReadEventsAsync().WithCancellation(cancellationToken))
        {
            switch (await authorize(message, cancellationToken))
            {
                case SubscriptionEventAuthorization.Allow:
                    yield return message;
                    break;
                case SubscriptionEventAuthorization.Skip:
                    break;
                case SubscriptionEventAuthorization.Terminate:
                    yield break;
                default:
                    throw new InvalidOperationException("Unknown subscription authorization decision.");
            }
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
