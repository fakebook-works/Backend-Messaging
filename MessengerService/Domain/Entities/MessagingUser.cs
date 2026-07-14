using MessengerService.Domain.Enums;

namespace MessengerService.Domain.Entities;

public sealed class MessagingUser
{
    public long UserId { get; set; }

    public MessagingUserStatus Status { get; set; } = MessagingUserStatus.Active;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<ConversationParticipant> ConversationParticipants { get; set; } = [];

    public ICollection<Message> SentMessages { get; set; } = [];

    public ICollection<MessageReaction> MessageReactions { get; set; } = [];

    public UserPresence? Presence { get; set; }
}
