namespace MessengerService.Application;

public sealed class MessagingApplicationException : Exception
{
    public MessagingApplicationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class MessagingErrorCodes
{
    public const string InvalidInput = "INVALID_INPUT";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string UserDeleted = "USER_DELETED";
    public const string ConversationNotFound = "CONVERSATION_NOT_FOUND";
    public const string NotParticipant = "NOT_A_PARTICIPANT";
    public const string Forbidden = "FORBIDDEN";
    public const string LastAdmin = "LAST_ADMIN";
    public const string MessageNotFound = "MESSAGE_NOT_FOUND";
    public const string MessageDeleted = "MESSAGE_DELETED";
    public const string EditWindowExpired = "EDIT_WINDOW_EXPIRED";
    public const string AttachmentUrlNotAllowed = "ATTACHMENT_URL_NOT_ALLOWED";
    public const string DirectMessageForbidden = "DIRECT_MESSAGE_FORBIDDEN";
    public const string SocialGraphUnavailable = "SOCIAL_GRAPH_UNAVAILABLE";
    public const string Conflict = "CONFLICT";
}
