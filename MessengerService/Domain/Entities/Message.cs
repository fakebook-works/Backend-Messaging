using MessengerService.Domain.Enums;

namespace MessengerService.Domain.Entities;

public sealed class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    public long SenderUserId { get; set; }

    public long Sequence { get; set; }

    public Guid ClientMessageId { get; set; }

    public MessageKind Kind { get; set; } = MessageKind.User;

    public SystemMessageEvent? SystemEvent { get; set; }

    public long? SystemSubjectUserId { get; set; }

    public string? Text { get; set; }

    public Guid? ReplyToMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EditedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public Conversation Conversation { get; set; } = null!;

    public MessagingUser Sender { get; set; } = null!;

    public MessagingUser? SystemSubject { get; set; }

    public Message? ReplyToMessage { get; set; }

    public ICollection<Message> Replies { get; set; } = [];

    public ICollection<MessageAttachment> Attachments { get; set; } = [];

    public ICollection<MessageReaction> Reactions { get; set; } = [];
}
