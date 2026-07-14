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

public sealed record SendMessageCommand(
    Guid ConversationId,
    Guid ClientMessageId,
    string? Text,
    IReadOnlyList<string> AttachmentUrls,
    Guid? ReplyToMessageId);

public sealed record EditMessageCommand(Guid MessageId, string Text);

public sealed record DeleteMessageCommand(Guid MessageId);

public sealed record SetMessageReactionCommand(Guid MessageId, string? Emoji);

public sealed record MarkConversationReceiptCommand(Guid ConversationId, long Sequence);

public sealed record SetTypingCommand(Guid ConversationId, bool IsTyping);
