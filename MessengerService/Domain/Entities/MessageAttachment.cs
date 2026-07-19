namespace MessengerService.Domain.Entities;

public sealed class MessageAttachment
{
    public Guid MessageId { get; set; }

    public int Ordinal { get; set; }

    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Stable identifier assigned by Upload Server. Nullable for legacy/external
    /// attachments that predate the metadata contract.
    /// </summary>
    public string? AssetId { get; set; }

    /// <summary>
    /// Logical media category (image, video, audio, or file).
    /// </summary>
    public string? MediaType { get; set; }

    public string? ContentType { get; set; }

    public string? OriginalName { get; set; }

    public long? SizeBytes { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public long? DurationMs { get; set; }

    public string? ThumbnailUrl { get; set; }

    public Message Message { get; set; } = null!;
}
