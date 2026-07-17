namespace MessengerService.Configuration;

public sealed class InternalServicesOptions
{
    public const string SectionName = "InternalServices";

    public string MessengerSharedSecret { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 3;

    public SocialGraphOptions SocialGraph { get; set; } = new();

    public UploadOptions Upload { get; set; } = new();
}

public sealed class SocialGraphOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    public string SharedSecret { get; set; } = string.Empty;
}

public sealed class UploadOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    public string SharedSecret { get; set; } = string.Empty;
}
