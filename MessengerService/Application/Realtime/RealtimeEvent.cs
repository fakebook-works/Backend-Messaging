namespace MessengerService.Application.Realtime;

public sealed record RealtimeEvent(
    Guid EventId,
    string Kind,
    Guid? ConversationId,
    Guid? MessageId,
    long? UserId,
    long? Sequence,
    DateTimeOffset OccurredAt,
    DateTimeOffset? ExpiresAt = null);

public static class RealtimeEventKinds
{
    public const string ConversationCreated = "CONVERSATION_CREATED";
    public const string ConversationUpdated = "CONVERSATION_UPDATED";
    public const string MemberAdded = "MEMBER_ADDED";
    public const string MemberRemoved = "MEMBER_REMOVED";
    public const string MemberRoleChanged = "MEMBER_ROLE_CHANGED";
    public const string MessageAdded = "MESSAGE_ADDED";
    public const string MessageEdited = "MESSAGE_EDITED";
    public const string MessageDeleted = "MESSAGE_DELETED";
    public const string ReactionChanged = "REACTION_CHANGED";
    public const string ReceiptChanged = "RECEIPT_CHANGED";
    public const string TypingChanged = "TYPING_CHANGED";
    public const string PresenceChanged = "PRESENCE_CHANGED";
    public const string AccessRevoked = "ACCESS_REVOKED";
}

public static class RealtimeTopics
{
    public const string Presence = "messaging:presence";

    public static string Conversation(Guid conversationId) => $"messaging:conversation:{conversationId:N}";

    public static string Inbox(long userId) => $"messaging:inbox:{userId}";
}
