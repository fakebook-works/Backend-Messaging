using MessengerService.Application;
using System.Text;

namespace MessengerService.Tests;

public sealed class MessageTextHistoryCodecTests
{
    [Fact]
    public void EncodeEdit_RoundTripsUnicodeAndKeepsOldestToNewestHistory()
    {
        var firstAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var secondAt = firstAt.AddMinutes(1);

        var stored = MessageTextHistoryCodec.EncodeEdit("Xin chào 👋", firstAt, "Bản thứ hai ✨");
        stored = MessageTextHistoryCodec.EncodeEdit(stored, secondAt, "Bản hiện tại 🚀");

        var snapshot = MessageTextHistoryCodec.Decode(stored);
        Assert.Equal("Bản hiện tại 🚀", snapshot.Current);
        Assert.Collection(
            snapshot.History,
            revision =>
            {
                Assert.Equal("Xin chào 👋", revision.Text);
                Assert.Equal(firstAt, revision.VersionAt);
            },
            revision =>
            {
                Assert.Equal("Bản thứ hai ✨", revision.Text);
                Assert.Equal(secondAt, revision.VersionAt);
            });
    }

    [Fact]
    public void EncodeEdit_KeepsOnlyTheTenNewestRevisions()
    {
        var versionAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var stored = "v0";
        for (var version = 1; version <= 12; version++)
        {
            stored = MessageTextHistoryCodec.EncodeEdit(
                stored,
                versionAt.AddMinutes(version - 1),
                $"v{version}");
        }

        var snapshot = MessageTextHistoryCodec.Decode(stored);
        Assert.Equal("v12", snapshot.Current);
        Assert.Equal(10, snapshot.History.Count);
        Assert.Equal("v2", snapshot.History[0].Text);
        Assert.Equal("v11", snapshot.History[^1].Text);
    }

    [Theory]
    [InlineData("\u001eFB_EDIT_V1:not-base64!")]
    [InlineData("\u001eFB_EDIT_V1:e30")]
    public void Decode_MalformedEnvelopeFallsBackToLegacyText(string stored)
    {
        var snapshot = MessageTextHistoryCodec.Decode(stored);

        Assert.Equal(stored, snapshot.Current);
        Assert.Empty(snapshot.History);
    }

    [Fact]
    public void Decode_NullRevisionInEnvelopeFallsBackWithoutBreakingTheConversation()
    {
        const string json = "{\"v\":1,\"c\":\"safe\",\"h\":[null]}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var stored = MessageTextHistoryCodec.ReservedPrefix + encoded;

        var snapshot = MessageTextHistoryCodec.Decode(stored);

        Assert.Equal(stored, snapshot.Current);
        Assert.Empty(snapshot.History);
    }

    [Fact]
    public void IsReservedInput_OnlyMatchesTheInternalPrefixAtTheStart()
    {
        Assert.True(MessageTextHistoryCodec.IsReservedInput(
            MessageTextHistoryCodec.ReservedPrefix + "payload"));
        Assert.False(MessageTextHistoryCodec.IsReservedInput(
            "normal " + MessageTextHistoryCodec.ReservedPrefix));
        Assert.False(MessageTextHistoryCodec.IsReservedInput(null));
    }

    [Fact]
    public void EncodeEdit_LargeBoundedHistoryFitsStorageAndPreservesCurrentText()
    {
        var versionAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var stored = new string('a', 10_000);
        string expectedCurrent = stored;
        for (var version = 1; version <= 12; version++)
        {
            expectedCurrent = new string((char)('a' + version), 10_000);
            stored = MessageTextHistoryCodec.EncodeEdit(
                stored,
                versionAt.AddMinutes(version - 1),
                expectedCurrent);
        }

        Assert.True(stored.Length <= MessageTextHistoryCodec.MaxStoredLength);
        var snapshot = MessageTextHistoryCodec.Decode(stored);
        Assert.Equal(expectedCurrent, snapshot.Current);
        Assert.InRange(snapshot.History.Count, 1, MessageTextHistoryCodec.MaxHistoryEntries);
    }
}
