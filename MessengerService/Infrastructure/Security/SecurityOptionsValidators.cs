using MessengerService.Configuration;
using Microsoft.Extensions.Options;

namespace MessengerService.Infrastructure.Security;

public sealed class GatewayOptionsValidator : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options) =>
        FixedTimeSecretComparer.IsStrongEnough(options.InternalSharedSecret)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{GatewayOptions.SectionName}:InternalSharedSecret must contain at least " +
                $"{FixedTimeSecretComparer.MinimumSecretBytes} UTF-8 bytes.");
}

public sealed class InternalServicesOptionsValidator : IValidateOptions<InternalServicesOptions>
{
    public ValidateOptionsResult Validate(string? name, InternalServicesOptions options)
    {
        var failures = new List<string>();

        if (!FixedTimeSecretComparer.IsStrongEnough(options.MessengerSharedSecret))
        {
            failures.Add(
                $"{InternalServicesOptions.SectionName}:MessengerSharedSecret must contain at least " +
                $"{FixedTimeSecretComparer.MinimumSecretBytes} UTF-8 bytes.");
        }

        if (!FixedTimeSecretComparer.IsStrongEnough(options.SocialGraph.SharedSecret))
        {
            failures.Add(
                $"{InternalServicesOptions.SectionName}:SocialGraph:SharedSecret must contain at least " +
                $"{FixedTimeSecretComparer.MinimumSecretBytes} UTF-8 bytes.");
        }

        if (options.TimeoutSeconds <= 0)
        {
            failures.Add($"{InternalServicesOptions.SectionName}:TimeoutSeconds must be greater than zero.");
        }

        if (!Uri.TryCreate(options.SocialGraph.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add(
                $"{InternalServicesOptions.SectionName}:SocialGraph:BaseUrl must be an absolute HTTP(S) URL.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
