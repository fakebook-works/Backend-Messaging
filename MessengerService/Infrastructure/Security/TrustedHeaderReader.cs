using Microsoft.Extensions.Primitives;

namespace MessengerService.Infrastructure.Security;

internal static class TrustedHeaderReader
{
    public static bool TryReadSingle(
        IHeaderDictionary headers,
        string name,
        out string value)
    {
        value = string.Empty;

        if (!headers.TryGetValue(name, out StringValues values) || values.Count != 1)
        {
            return false;
        }

        var candidate = values[0];
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }
}
