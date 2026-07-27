using System.Runtime.CompilerServices;
using System.Threading.Channels;
using HotChocolate.Execution;
using MessengerService.Application.Realtime;

namespace MessengerService.GraphQL;

/// <summary>
/// Presents several topic streams as one.
/// </summary>
/// <remarks>
/// A subscription used to be opened per conversation, and each one holds a server-sent
/// events connection open for as long as the client is looking at that chat. Browsers cap
/// concurrent connections per origin, so a few open chats plus the inbox, presence and
/// notification streams exhausted the budget and every other request queued behind them.
/// Merging lets one connection carry every conversation the client is watching.
///
/// Each inner stream is drained by its own reader into a channel, so a quiet conversation
/// never holds up a busy one. Readers are cancelled and every inner stream disposed when
/// the consumer stops reading.
/// </remarks>
public sealed class MergedSourceStream : ISourceStream<RealtimeEvent>
{
    private readonly IReadOnlyList<ISourceStream<RealtimeEvent>> _inner;

    public MergedSourceStream(IReadOnlyList<ISourceStream<RealtimeEvent>> inner)
    {
        _inner = inner;
    }

    public IAsyncEnumerable<RealtimeEvent> ReadEventsAsync() => ReadMergedEventsAsync();

    async IAsyncEnumerable<object?> ISourceStream.ReadEventsAsync()
    {
        await foreach (var message in ReadMergedEventsAsync())
        {
            yield return message;
        }
    }

    private async IAsyncEnumerable<RealtimeEvent> ReadMergedEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_inner.Count == 0)
        {
            yield break;
        }

        if (_inner.Count == 1)
        {
            // Nothing to merge; avoid the extra channel hop entirely.
            await foreach (var message in _inner[0].ReadEventsAsync().WithCancellation(cancellationToken))
            {
                yield return message;
            }

            yield break;
        }

        var channel = Channel.CreateUnbounded<RealtimeEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readers = _inner
            .Select(stream => DrainAsync(stream, channel.Writer, readerCancellation.Token))
            .ToArray();

        // Completing the channel is what ends the loop below once every topic is done.
        var completion = Task.Run(
            async () =>
            {
                try
                {
                    await Task.WhenAll(readers);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }
        finally
        {
            // The consumer stopped reading — unblock every drain task before returning, so
            // this cannot leave readers parked on a channel nobody will drain again.
            await readerCancellation.CancelAsync();
            channel.Writer.TryComplete();
            await completion;
        }
    }

    private static async Task DrainAsync(
        ISourceStream<RealtimeEvent> stream,
        ChannelWriter<RealtimeEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in stream.ReadEventsAsync().WithCancellation(cancellationToken))
            {
                await writer.WriteAsync(message, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The subscription is shutting down.
        }
        catch (ChannelClosedException)
        {
            // The consumer stopped first.
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var stream in _inner)
        {
            await stream.DisposeAsync();
        }
    }
}
