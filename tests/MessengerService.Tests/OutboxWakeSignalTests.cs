using MessengerService.Infrastructure.Realtime;

namespace MessengerService.Tests;

public sealed class OutboxWakeSignalTests
{
    [Fact]
    public async Task WaitAsync_ReturnsImmediatelyAfterPulse()
    {
        var signal = new OutboxWakeSignal();
        signal.Pulse();

        var wasWoken = await signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(wasWoken);
    }

    [Fact]
    public async Task WaitAsync_ReturnsFalseWhenTheFallbackPollExpires()
    {
        var signal = new OutboxWakeSignal();

        var wasWoken = await signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);

        Assert.False(wasWoken);
    }
}
