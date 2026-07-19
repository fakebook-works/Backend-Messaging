using MessengerService.Domain.Enums;

namespace MessengerService.Application.Models;

public sealed record CreateDirectConversationCommand(long TargetUserId);

public sealed record CreateGroupConversationCommand(
    string Title,
    IReadOnlyCollection<long> MemberUserIds,
    string? AvatarUrl);

public sealed record UpdateGroupConversationCommand(
    Guid ConversationId,
    bool HasTitle,
    string? Title,
    bool HasAvatarUrl,
    string? AvatarUrl);

public sealed record AddConversationMembersCommand(Guid ConversationId, IReadOnlyCollection<long> UserIds);

public sealed record RemoveConversationMemberCommand(Guid ConversationId, long UserId);

public sealed record SetConversationMemberRoleCommand(Guid ConversationId, long UserId, ParticipantRole Role);

/// <summary>
/// Attachment metadata supplied by the upload service when a message is sent.
/// All metadata is optional so callers can continue using the legacy URL-only
/// contract while migrating.
/// </summary>
public sealed record MessageAttachmentCommand(
    string? Url,
    string? AssetId = null,
    string? MediaType = null,
    string? ContentType = null,
    string? OriginalName = null,
    long? SizeBytes = null,
    int? Width = null,
    int? Height = null,
    long? DurationMs = null,
    string? ThumbnailUrl = null);

public sealed record SendMessageCommand(
    Guid ConversationId,
    Guid ClientMessageId,
    string? Text,
    IReadOnlyList<string> AttachmentUrls,
    Guid? ReplyToMessageId,
    IReadOnlyList<MessageAttachmentCommand>? Attachments = null);

public sealed record EditMessageCommand(Guid MessageId, string Text);

public sealed record DeleteMessageCommand(Guid MessageId);

public sealed record SetMessageReactionCommand(Guid MessageId, string? Emoji);

public sealed record MarkConversationReceiptCommand(Guid ConversationId, long Sequence);

public sealed record SetTypingCommand(Guid ConversationId, bool IsTyping);
