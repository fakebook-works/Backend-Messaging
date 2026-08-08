using System.Globalization;
using System.Text;

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

            if (leaf.Length == 0 || leaf is "." or ".." || leaf.Contains('/') || leaf.Contains('\\'))
            {
                return false;
            }

            foreach (var rune in leaf.EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);
                if (rune.Value == '\uFFFD' || category is
                        UnicodeCategory.Control or
                        UnicodeCategory.Format or
                        UnicodeCategory.Surrogate or
                        UnicodeCategory.PrivateUse or
                        UnicodeCategory.OtherNotAssigned or
                        UnicodeCategory.NonSpacingMark or
                        UnicodeCategory.SpacingCombiningMark or
                        UnicodeCategory.EnclosingMark)
                {
                    return false;
                }
            }

            return true;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               !string.IsNullOrWhiteSpace(uri.Host) &&
               allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}
