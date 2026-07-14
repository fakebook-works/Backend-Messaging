namespace MessengerService.Configuration;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string InternalSharedSecret { get; set; } = string.Empty;
}

