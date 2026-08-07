using System.Text.Json;
using MessengerService.Application.Abstractions;
using MessengerService.Domain.Entities;

namespace MessengerService.Application.Media;

public sealed record MediaLifecyclePayload(
    IReadOnlyList<string>? Urls = null,
    IReadOnlyList<UploadMediaReference>? References = null,
    bool Repair = false);

public static class MediaLifecycleEventKinds
{
    public const string Finalize = "media.finalize.v1";
    public const string Delete = "media.delete.v1";
}

public static class MediaLifecycleOutbox
{
    public const string Topic = "internal:upload:media";
    public const int MaxReferencesPerEvent = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static OutboxEvent? Create(
        string kind,
        IEnumerable<UploadMediaReference> references,
        DateTimeOffset occurredAt,
        Guid? conversationId = null,
        Guid? messageId = null,
        long? actorUserId = null)
    {
        if (kind != MediaLifecycleEventKinds.Finalize && kind != MediaLifecycleEventKinds.Delete)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var candidates = references.ToArray();
        if (candidates.Any(reference =>
                IsManagedMediaUrl(reference.Url) && !IsValidReferenceId(reference.ReferenceId)))
        {
            throw new ArgumentException("A managed media reference identifier is invalid.", nameof(references));
        }

        var normalized = candidates
            .Where(reference => IsManagedMediaUrl(reference.Url))
            .DistinctBy(
                reference => reference.Url.ToUpperInvariant() + "\n" + reference.ReferenceId,
                StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            return null;
        }
        if (normalized.Length > MaxReferencesPerEvent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(references),
                $"A media lifecycle event supports at most {MaxReferencesPerEvent} references.");
        }

        return new OutboxEvent
        {
            Id = Guid.NewGuid(),
            Topic = Topic,
            Kind = kind,
            PayloadJson = JsonSerializer.Serialize(
                new MediaLifecyclePayload(References: normalized),
                JsonOptions),
            ConversationId = conversationId,
            MessageId = messageId,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt,
            CreatedAt = occurredAt
        };
    }

    public static MediaLifecyclePayload Deserialize(string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<MediaLifecyclePayload>(payloadJson, JsonOptions) ??
                      throw new JsonException("The media lifecycle outbox payload is empty.");
        if ((payload.References is null || payload.References.Count == 0) &&
            (payload.Urls is null || payload.Urls.Count == 0))
        {
            throw new JsonException("The media lifecycle outbox payload contains no media.");
        }
        if (payload.References is { Count: > 0 } && payload.Urls is { Count: > 0 })
        {
            // Never let one durable row straddle the legacy URL-only and exact-parent
            // protocols. Dispatching only one half would silently leak or detach media.
            throw new JsonException("The media lifecycle outbox payload mixes exact references and legacy URLs.");
        }
        if (payload.References is { } references)
        {
            if (references.Count > MaxReferencesPerEvent ||
                references.Any(reference =>
                    reference is null ||
                    !IsManagedMediaUrl(reference.Url) ||
                    !IsValidReferenceId(reference.ReferenceId)))
            {
                throw new JsonException("The media lifecycle outbox reference payload is invalid.");
            }

            var distinctCount = references
                .DistinctBy(
                    reference => reference.Url.ToUpperInvariant() + "\n" + reference.ReferenceId,
                    StringComparer.Ordinal)
                .Count();
            if (distinctCount != references.Count)
            {
                throw new JsonException("The media lifecycle outbox reference payload contains duplicates.");
            }
        }

        return payload;
    }

    public static async Task DispatchAsync(
        OutboxEvent outboxEvent,
        IUploadMediaClient uploadMediaClient,
        CancellationToken cancellationToken)
    {
        if (outboxEvent.Kind is not (MediaLifecycleEventKinds.Finalize or MediaLifecycleEventKinds.Delete))
        {
            throw new ArgumentOutOfRangeException(nameof(outboxEvent));
        }

        var payload = Deserialize(outboxEvent.PayloadJson);
        if (payload.Repair)
        {
            throw new JsonException("Online ownerless media repair is not supported; use the offline reconciliation tool.");
        }
        if (payload.References is { Count: > 0 } references)
        {
            if (outboxEvent.Kind == MediaLifecycleEventKinds.Finalize)
            {
                if (outboxEvent.ActorUserId is null)
                {
                    throw new JsonException("A media attach event requires its trusted owner.");
                }

                if (outboxEvent.AttemptCount > 0)
                {
                    // Renew a delayed committed parent's bounded reservation with the exact
                    // original operation time. Upload's release floor prevents this from
                    // reviving a reference after an equal/newer detach. A false decision is
                    // not terminal here: finalize still runs so Upload can acknowledge the
                    // operation as stale, while ownership/missing-file failures remain errors.
                    await uploadMediaClient.AuthorizeReferencesAsync(
                        references,
                        outboxEvent.ActorUserId.Value,
                        outboxEvent.OccurredAt,
                        cancellationToken);
                }
                await uploadMediaClient.FinalizeReferencesAsync(
                    references,
                    outboxEvent.ActorUserId,
                    outboxEvent.OccurredAt,
                    cancellationToken);
            }
            else
            {
                await uploadMediaClient.DeleteReferencesAsync(
                    references,
                    outboxEvent.ActorUserId,
                    outboxEvent.OccurredAt,
                    cancellationToken);
            }
            return;
        }

        // Compatibility for durable rows created by older deployments. Upload's
        // URL-only path is conservatively pinned and cannot destroy media that may
        // have untracked reference parents.
        var legacyUrls = payload.Urls ?? [];
        if (outboxEvent.Kind == MediaLifecycleEventKinds.Finalize)
        {
            await uploadMediaClient.FinalizeAsync(
                legacyUrls,
                outboxEvent.ActorUserId,
                cancellationToken);
        }
        else
        {
            await uploadMediaClient.DeleteAsync(
                legacyUrls,
                outboxEvent.ActorUserId,
                cancellationToken);
        }
    }

    public static UploadMediaReference ConversationAvatar(Guid conversationId, string url) =>
        new(url, $"messenger:conversation:{conversationId:N}:avatar");

    public static IReadOnlyList<UploadMediaReference> MessageAttachment(
        Guid messageId,
        int ordinal,
        string url,
        string? thumbnailUrl)
    {
        var references = new List<UploadMediaReference>(2)
        {
            new(url, $"messenger:message:{messageId:N}:attachment:{ordinal}:content")
        };
        if (!string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            references.Add(new UploadMediaReference(
                thumbnailUrl,
                $"messenger:message:{messageId:N}:attachment:{ordinal}:thumbnail"));
        }

        return references;
    }

    public static IReadOnlyList<string> ManagedUrls(IEnumerable<string?> urls) => urls
        .Where(IsManagedMediaUrl)
        .Select(url => url!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string? ManagedPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute.AbsolutePath
            : value;
        return path.StartsWith("/media/files/", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    private static bool IsManagedMediaUrl(string? value) => ManagedPath(value) is not null;

    private static bool IsValidReferenceId(string? referenceId)
    {
        if (string.IsNullOrWhiteSpace(referenceId) || referenceId.Length > 192 ||
            referenceId[0] is < 'a' or > 'z')
        {
            return false;
        }

        return referenceId.Contains(':', StringComparison.Ordinal) &&
               referenceId.All(value =>
                   char.IsAsciiLetterOrDigit(value) || value is ':' or '-' or '_' or '.');
    }
}
