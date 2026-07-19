using HotChocolate;
using MessengerService.Domain.Enums;

namespace MessengerService.GraphQL.Inputs;

public sealed record CreateDirectConversationInput(long TargetUserId);

public sealed record CreateGroupConversationInput(string Title, IReadOnlyList<long> MemberUserIds, string? AvatarUrl);

public sealed record UpdateGroupConversationInput(
    Guid ConversationId,
    Optional<string?> Title,
    Optional<string?> AvatarUrl);

public sealed record AddConversationMembersInput(Guid ConversationId, IReadOnlyList<long> UserIds);

public sealed record RemoveConversationMemberInput(Guid ConversationId, long UserId);

public sealed record SetConversationMemberRoleInput(Guid ConversationId, long UserId, ParticipantRole Role);

/// <summary>
/// Optional upload metadata carried with a message attachment. Url remains
/// optional here so a caller can use AttachmentUrls as a compatibility
/// fallback while gradually adopting this contract.
/// </summary>
public sealed record SendMessageAttachmentInput(
    string? Url,
    string? AssetId,
    string? MediaType,
    string? ContentType,
    string? OriginalName,
    long? SizeBytes,
    int? Width,
    int? Height,
    long? DurationMs,
    string? ThumbnailUrl);

public sealed record SendMessageInput(
    Guid ConversationId,
    Guid ClientMessageId,
    string? Text,
    IReadOnlyList<string>? AttachmentUrls,
    Guid? ReplyToMessageId,
    IReadOnlyList<SendMessageAttachmentInput>? Attachments = null);

public sealed record EditMessageInput(Guid MessageId, string Text);

public sealed record DeleteMessageInput(Guid MessageId);

public sealed record SetMessageReactionInput(Guid MessageId, string? Emoji);

public sealed record MarkConversationReceiptInput(Guid ConversationId, long Sequence);

public sealed record SetTypingInput(Guid ConversationId, bool IsTyping);
