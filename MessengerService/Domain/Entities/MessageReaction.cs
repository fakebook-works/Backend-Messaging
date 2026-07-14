namespace MessengerService.Domain.Entities;

public sealed class MessageReaction
{
    public Guid MessageId { get; set; }

    public long UserId { get; set; }

    public string Emoji { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Message Message { get; set; } = null!;

    public MessagingUser User { get; set; } = null!;
}
