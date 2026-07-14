namespace MessengerService.Domain.Entities;

public sealed class OutboxEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Topic { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";

    public Guid? ConversationId { get; set; }

    public Guid? MessageId { get; set; }

    public long? ActorUserId { get; set; }

    public long? SubjectUserId { get; set; }

    public long? Sequence { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ProcessedAt { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? LastError { get; set; }
}
