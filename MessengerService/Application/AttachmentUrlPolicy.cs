namespace MessengerService.Application;

public static class AttachmentUrlPolicy
{
    private const string ManagedPrefix = "/media/files/";

    public static bool IsAllowed(string value, int maximumLength, IReadOnlyCollection<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            return false;
        }

        if (value.StartsWith(ManagedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (value.Contains('?') ||
                value.Contains('#') ||
                value.Contains('\\'))
            {
                return false;
            }

            var encodedLeaf = value[ManagedPrefix.Length..];
            if (encodedLeaf.Length == 0 || encodedLeaf.Contains('/'))
            {
                return false;
            }

            string leaf;
            try
            {
                leaf = Uri.UnescapeDataString(encodedLeaf);
            }
            catch (UriFormatException)
            {
                return false;
            }

            return leaf.Length > 0 &&
                   leaf is not "." and not ".." &&
                   !leaf.Contains('/') &&
                   !leaf.Contains('\\');
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               !string.IsNullOrWhiteSpace(uri.Host) &&
               allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}
