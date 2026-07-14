using MessengerService.Domain.Enums;

namespace MessengerService.Domain.Entities;

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public ConversationType Type { get; set; }

    public string? Title { get; set; }

    public string? AvatarUrl { get; set; }

    public long? DirectUserLowId { get; set; }

    public long? DirectUserHighId { get; set; }

    public long CurrentSequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ConversationParticipant> Participants { get; set; } = [];

    public ICollection<Message> Messages { get; set; } = [];
}
