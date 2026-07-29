using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MessengerService.Application;

public sealed record MessageTextRevision(string Text, DateTimeOffset VersionAt);

public sealed record MessageTextSnapshot(string Current, IReadOnlyList<MessageTextRevision> History);

/// <summary>
/// Stores a bounded, versioned edit envelope in the existing message text column.
/// Raw envelopes are an implementation detail: GraphQL receives only the decoded
/// current text and revision list, and browser input cannot supply the reserved prefix.
/// </summary>
public static class MessageTextHistoryCodec
{
    public const string ReservedPrefix = "\u001eFB_EDIT_V1:";
    public const int MaxHistoryEntries = 10;
    public const int MaxStoredLength = 200_000;
    private const int MaxDecodedPayloadBytes = 150_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false
    };

    public static bool IsReservedInput(string? text) =>
        text?.StartsWith(ReservedPrefix, StringComparison.Ordinal) == true;

    public static MessageTextSnapshot Decode(string? storedText)
    {
        var legacy = new MessageTextSnapshot(storedText ?? string.Empty, []);
        if (string.IsNullOrEmpty(storedText) || !IsReservedInput(storedText) ||
            storedText.Length > MaxStoredLength)
        {
            return legacy;
        }

        try
        {
            var encoded = storedText[ReservedPrefix.Length..];
            var bytes = DecodeBase64Url(encoded);
            if (bytes.Length > MaxDecodedPayloadBytes)
            {
                return legacy;
            }

            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions);
            if (envelope is null || envelope.V != 1 || envelope.C is null || envelope.H is null ||
                envelope.H.Count > MaxHistoryEntries ||
                envelope.H.Any(revision => revision is null || revision.T is null))
            {
                return legacy;
            }

            return new MessageTextSnapshot(
                envelope.C,
                envelope.H.Select(revision =>
                    new MessageTextRevision(revision!.T, DateTimeOffset.FromUnixTimeMilliseconds(revision.A)))
                    .ToArray());
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            // Fail open as legacy text so one malformed historical row cannot make the
            // conversation unreadable. New public input is prevented from using the prefix.
            return legacy;
        }
    }

    public static string EncodeEdit(
        string? storedText,
        DateTimeOffset previousVersionAt,
        string newCurrentText)
    {
        var snapshot = Decode(storedText);
        var history = snapshot.History.ToList();
        history.Add(new MessageTextRevision(snapshot.Current, previousVersionAt));
        while (history.Count > MaxHistoryEntries)
        {
            history.RemoveAt(0);
        }

        while (true)
        {
            var encoded = EncodeEnvelope(newCurrentText, history);
            if (encoded.Length <= MaxStoredLength)
            {
                return encoded;
            }

            if (history.Count == 0)
            {
                throw new InvalidOperationException("The encoded message text exceeds its bounded storage limit.");
            }

            history.RemoveAt(0);
        }
    }

    private static string EncodeEnvelope(string current, IReadOnlyCollection<MessageTextRevision> history)
    {
        var envelope = new Envelope(
            1,
            current,
            history.Select(revision => (Revision?)new Revision(
                revision.Text,
                revision.VersionAt.ToUnixTimeMilliseconds())).ToList());
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        return ReservedPrefix + Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string encoded)
    {
        if (encoded.Length == 0 || encoded.Length > MaxStoredLength - ReservedPrefix.Length)
        {
            throw new FormatException("The edit envelope is empty or oversized.");
        }

        var normalized = encoded.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("The edit envelope has invalid Base64Url length.")
        };
        return Convert.FromBase64String(normalized);
    }

    private sealed record Envelope(int V, string C, List<Revision?> H);

    private sealed record Revision(string T, long A);
}
