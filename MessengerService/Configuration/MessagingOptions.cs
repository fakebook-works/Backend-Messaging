namespace MessengerService.Configuration;

using Microsoft.Extensions.Options;

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public string[] AllowedAttachmentHosts { get; set; } = [];

    public int MaxGroupParticipants { get; set; } = 250;

    public int PresenceTtlSeconds { get; set; } = 60;

    public int PresenceSweepSeconds { get; set; } = 15;

    public int OutboxPollMilliseconds { get; set; } = 250;

    public int OutboxRetentionHours { get; set; } = 24;

    public int OutboxCleanupIntervalMinutes { get; set; } = 30;
}

public sealed class MessagingOptionsValidator : IValidateOptions<MessagingOptions>
{
    public ValidateOptionsResult Validate(string? name, MessagingOptions options)
    {
        var failures = new List<string>();

        if (options.MaxGroupParticipants is < 3 or > 1_000)
        {
            failures.Add("Messaging:MaxGroupParticipants must be between 3 and 1000.");
        }

        if (options.PresenceTtlSeconds is < 15 or > 600)
        {
            failures.Add("Messaging:PresenceTtlSeconds must be between 15 and 600.");
        }

        if (options.PresenceSweepSeconds <= 0 ||
            options.PresenceSweepSeconds >= options.PresenceTtlSeconds)
        {
            failures.Add("Messaging:PresenceSweepSeconds must be positive and less than the presence TTL.");
        }

        if (options.OutboxPollMilliseconds is < 50 or > 60_000)
        {
            failures.Add("Messaging:OutboxPollMilliseconds must be between 50 and 60000.");
        }

        if (options.OutboxRetentionHours is < 1 or > 720)
        {
            failures.Add("Messaging:OutboxRetentionHours must be between 1 and 720.");
        }

        if (options.OutboxCleanupIntervalMinutes is < 1 or > 1_440)
        {
            failures.Add("Messaging:OutboxCleanupIntervalMinutes must be between 1 and 1440.");
        }

        var normalizedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredHost in options.AllowedAttachmentHosts)
        {
            var host = configuredHost?.Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(host) ||
                host.Contains('/') ||
                host.Contains(':') ||
                !Uri.CheckHostName(host).Equals(UriHostNameType.Dns))
            {
                failures.Add($"Messaging:AllowedAttachmentHosts contains an invalid DNS hostname: '{configuredHost}'.");
                continue;
            }

            normalizedHosts.Add(host.ToLowerInvariant());
        }

        options.AllowedAttachmentHosts = normalizedHosts.ToArray();

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
