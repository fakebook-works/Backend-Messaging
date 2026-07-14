namespace MessengerService.Domain.Entities;

public sealed class MessageAttachment
{
    public Guid MessageId { get; set; }

    public int Ordinal { get; set; }

    public string Url { get; set; } = string.Empty;

    public Message Message { get; set; } = null!;
}
