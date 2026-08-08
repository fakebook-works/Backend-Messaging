namespace MessengerService.Application;

public sealed class MessagingRulesOptions
{
    public const string SectionName = "Messaging";

    public int MaxGroupParticipants { get; set; } = 250;

    /// <summary>Maximum number of Unicode scalar values in a user message.</summary>
    public int MaxMessageLength { get; set; } = 20_000;

    public int MaxAttachmentsPerMessage { get; set; } = 10;

    public int MaxAttachmentUrlLength { get; set; } = 2_048;

    public int MaxPresenceUserIds { get; set; } = 250;

    public int EditWindowMinutes { get; set; } = 15;

    public int PresenceTtlSeconds { get; set; } = 60;

    public int TypingTtlSeconds { get; set; } = 8;

    public string[] AllowedAttachmentHosts { get; set; } = [];
}
