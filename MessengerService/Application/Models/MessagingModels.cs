using MessengerService.Domain.Enums;

namespace MessengerService.Application.Models;

public sealed record PageInfo(string? StartCursor, string? EndCursor, bool HasNextPage, bool HasPreviousPage);

public sealed record ConversationPage(IReadOnlyList<ConversationView> Items, PageInfo PageInfo);

public sealed record MessagePage(IReadOnlyList<MessageView> Items, PageInfo PageInfo);

public sealed record ConversationView(
    Guid Id,
    ConversationType Type,
    string? Title,
    string? AvatarUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long CurrentSequence,
    IReadOnlyList<ConversationParticipantView> Participants,
    MessageView? LastMessage);

public sealed record ConversationParticipantView(
    long UserId,
    ParticipantRole Role,
    DateTimeOffset JoinedAt,
    DateTimeOffset? LeftAt,
    long LastDeliveredSequence,
    long LastReadSequence);

public sealed record MessageView(
    Guid Id,
    Guid ConversationId,
    long SenderUserId,
    long Sequence,
    Guid ClientMessageId,
    string? Text,
    Guid? ReplyToMessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    DateTimeOffset? DeletedAt,
    IReadOnlyList<MessageAttachmentView> Attachments,
    IReadOnlyList<MessageReactionView> Reactions)
{
    public bool Deleted => DeletedAt is not null;
}

public sealed record MessageAttachmentView(
    int Ordinal,
    string Url,
    string? AssetId = null,
    string? MediaType = null,
    string? ContentType = null,
    string? OriginalName = null,
    long? SizeBytes = null,
    int? Width = null,
    int? Height = null,
    long? DurationMs = null,
    string? ThumbnailUrl = null);

public sealed record MessageReactionView(long UserId, string Emoji, DateTimeOffset UpdatedAt);

public sealed record ConversationReceiptView(
    Guid ConversationId,
    long UserId,
    long LastDeliveredSequence,
    long LastReadSequence);

public sealed record UserPresenceView(long UserId, bool IsOnline, DateTimeOffset? ExpiresAt, DateTimeOffset UpdatedAt);

public sealed record TypingView(Guid ConversationId, long UserId, bool IsTyping, DateTimeOffset ExpiresAt);
