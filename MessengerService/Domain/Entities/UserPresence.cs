namespace MessengerService.Domain.Entities;

public sealed class UserPresence
{
    public long UserId { get; set; }

    public bool IsOnline { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MessagingUser User { get; set; } = null!;
}
