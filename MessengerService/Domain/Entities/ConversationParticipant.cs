using MessengerService.Domain.Enums;

namespace MessengerService.Domain.Entities;

public sealed class ConversationParticipant
{
    public Guid ConversationId { get; set; }

    public long UserId { get; set; }

    public ParticipantRole Role { get; set; } = ParticipantRole.Member;

    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LeftAt { get; set; }

    public long LastDeliveredSequence { get; set; }

    public long LastReadSequence { get; set; }

    public Conversation Conversation { get; set; } = null!;

    public MessagingUser User { get; set; } = null!;
}
