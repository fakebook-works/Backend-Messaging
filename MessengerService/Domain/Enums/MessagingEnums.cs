namespace MessengerService.Domain.Enums;

public enum MessagingUserStatus
{
    Active = 1,
    Deleted = 2
}

public enum ConversationType
{
    Direct = 1,
    Group = 2
}

public enum ParticipantRole
{
    Admin = 1,
    Member = 2
}

public enum MessageKind
{
    User = 1,
    System = 2
}

public enum SystemMessageEvent
{
    MemberAdded = 1,
    MemberRemoved = 2,
    MemberLeft = 3,
    AdminGranted = 4,
    AdminRevoked = 5,
    GroupRenamed = 6,
    GroupAvatarChanged = 7
}
