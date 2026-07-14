using System.Security.Cryptography;
using System.Text;

namespace MessengerService.Infrastructure.Security;

public static class FixedTimeSecretComparer
{
    public const int MinimumSecretBytes = 32;

    public static bool IsStrongEnough(string? secret) =>
        secret is not null && Encoding.UTF8.GetByteCount(secret) >= MinimumSecretBytes;

    public static bool Matches(string? providedSecret, string? configuredSecret)
    {
        if (providedSecret is null || !IsStrongEnough(configuredSecret))
        {
            return false;
        }

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedSecret));
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret!));

        return CryptographicOperations.FixedTimeEquals(providedHash, configuredHash);
    }
}
