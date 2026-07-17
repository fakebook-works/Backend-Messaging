using System.Text.Json;
using MessengerService.Domain.Entities;

namespace MessengerService.Application.Media;

public sealed record MediaLifecyclePayload(IReadOnlyList<string> Urls);

public static class MediaLifecycleEventKinds
{
    public const string Finalize = "media.finalize.v1";
    public const string Delete = "media.delete.v1";
}

public static class MediaLifecycleOutbox
{
    public const string Topic = "internal:upload:media";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static OutboxEvent? Create(
        string kind,
        IEnumerable<string> urls,
        DateTimeOffset occurredAt,
        Guid? conversationId = null,
        Guid? messageId = null,
        long? actorUserId = null)
    {
        if (kind != MediaLifecycleEventKinds.Finalize && kind != MediaLifecycleEventKinds.Delete)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var normalized = urls
            .Where(IsManagedMediaUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            return null;
        }

        return new OutboxEvent
        {
            Id = Guid.NewGuid(),
            Topic = Topic,
            Kind = kind,
            PayloadJson = JsonSerializer.Serialize(new MediaLifecyclePayload(normalized), JsonOptions),
            ConversationId = conversationId,
            MessageId = messageId,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            CreatedAt = occurredAt
        };
    }

    public static MediaLifecyclePayload Deserialize(string payloadJson) =>
        JsonSerializer.Deserialize<MediaLifecyclePayload>(payloadJson, JsonOptions) ??
        throw new JsonException("The media lifecycle outbox payload is empty.");

    private static bool IsManagedMediaUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var path = Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute.AbsolutePath
            : value;
        return path.StartsWith("/media/files/", StringComparison.OrdinalIgnoreCase);
    }
}
