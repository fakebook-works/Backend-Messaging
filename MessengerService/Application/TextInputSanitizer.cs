using System.Globalization;
using System.Text;

namespace MessengerService.Application;

/// <summary>
/// Applies the common server-side policy for user controlled text.
///
/// This is deliberately a small, allocation-bounded Unicode policy rather than an
/// HTML sanitizer. GraphQL responses are JSON and the clients render text as text;
/// the important boundary here is to reject malformed scalars, control/bidi
/// formatting characters and pathological combining-mark chains before they are
/// persisted or rendered. Form-C normalization keeps equivalent text canonical
/// while still allowing normal scripts and emoji (including ZWJ sequences).
/// </summary>
public static class TextInputSanitizer
{
    private const int MaxCombiningMarksPerRun = 4;
    private const int MaxTotalCombiningMarks = 256;
    private const int MaxJoiners = 64;
    private const int MaxTagCharacters = 16;
    private const int MaxConsecutiveLineBreaks = 4;

    public static string? NormalizeOptional(
        string? value,
        int maximumScalars,
        string field,
        bool allowLineBreaks = false)
    {
        if (value is null)
        {
            return null;
        }

        EnsureRawLength(value, maximumScalars, field);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeRequired(value, maximumScalars, field, allowLineBreaks);
    }

    public static string NormalizeRequired(
        string? value,
        int maximumScalars,
        string field,
        bool allowLineBreaks = false)
    {
        if (value is null)
        {
            RejectRequired(field, maximumScalars);
        }

        if (maximumScalars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumScalars));
        }

        var input = value!;

        // A scalar is at most two UTF-16 code units. The small allowance keeps the
        // check cheap while rejecting a hostile multi-megabyte value before NFC
        // normalization can allocate a second copy.
        EnsureRawLength(input, maximumScalars, field);

        EnsureWellFormedUtf16(input, field);

        string normalized;
        try
        {
            normalized = input.Normalize(NormalizationForm.FormC);
            normalized = normalized.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
        }
        catch (ArgumentException)
        {
            RejectUnsafe(field);
            return string.Empty;
        }

        var builder = new StringBuilder(normalized.Length);
        var scalarCount = 0;
        var combiningRun = 0;
        var combiningTotal = 0;
        var joinerCount = 0;
        var tagCount = 0;
        var lineBreakRun = 0;
        var previousWasJoiner = false;

        foreach (var rune in normalized.EnumerateRunes())
        {
            var valueOfRune = rune.Value;
            if (valueOfRune == '\n')
            {
                if (!allowLineBreaks)
                {
                    RejectUnsafe(field);
                }

                // Excessive blank lines are a cheap layout/paint-amplification
                // vector. Keep a useful bounded run instead of silently accepting
                // an arbitrarily tall message.
                if (++lineBreakRun > MaxConsecutiveLineBreaks)
                {
                    continue;
                }

                builder.Append('\n');
                scalarCount++;
                combiningRun = 0;
                previousWasJoiner = false;
                EnsureScalarLimit(scalarCount, maximumScalars, field);
                continue;
            }

            var wasTab = valueOfRune == '\t';
            if (wasTab)
            {
                // Tabs can expand to a very wide layout-dependent run. A single
                // ordinary space is sufficient for all supported text fields.
                valueOfRune = ' ';
            }

            lineBreakRun = 0;
            var category = wasTab
                ? UnicodeCategory.SpaceSeparator
                : Rune.GetUnicodeCategory(rune);

            if (valueOfRune == '\uFFFD' || category is
                    UnicodeCategory.Control or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator or
                    UnicodeCategory.Surrogate or
                    UnicodeCategory.PrivateUse or
                    UnicodeCategory.OtherNotAssigned)
            {
                RejectUnsafe(field);
            }

            if (category == UnicodeCategory.Format)
            {
                // ZWJ/ZWNJ are used by legitimate emoji and Indic/Persian text.
                // Unicode bidi controls, zero-width spaces, BOM and the remaining
                // format characters are rejected because they can spoof ordering or
                // create disproportionate rendering work. Emoji tag sequences are
                // allowed in a tightly bounded quantity.
                var isJoiner = valueOfRune is 0x200C or 0x200D;
                var isTag = valueOfRune is >= 0xE0020 and <= 0xE007F;
                if (!isJoiner && !isTag)
                {
                    RejectUnsafe(field);
                }

                if (isJoiner)
                {
                    if (++joinerCount > MaxJoiners || (previousWasJoiner && joinerCount > 16))
                    {
                        RejectUnsafe(field);
                    }

                    previousWasJoiner = true;
                }
                else
                {
                    if (++tagCount > MaxTagCharacters)
                    {
                        RejectUnsafe(field);
                    }

                    previousWasJoiner = false;
                }

                builder.Append(char.ConvertFromUtf32(valueOfRune));
                scalarCount++;
                EnsureScalarLimit(scalarCount, maximumScalars, field);
                continue;
            }

            if (category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark)
            {
                if (++combiningRun > MaxCombiningMarksPerRun)
                {
                    RejectUnsafe(field);
                }

                if (++combiningTotal > Math.Min(MaxTotalCombiningMarks, maximumScalars / 8 + 8))
                {
                    RejectUnsafe(field);
                }
            }
            else
            {
                combiningRun = 0;
                previousWasJoiner = false;
            }

            builder.Append(char.ConvertFromUtf32(valueOfRune));
            scalarCount++;
            EnsureScalarLimit(scalarCount, maximumScalars, field);
        }

        var result = builder.ToString().Trim();
        if (result.Length == 0)
        {
            RejectRequired(field, maximumScalars);
        }

        return result;
    }

    /// <summary>
    /// Checks a value whose syntax must be preserved (for example a URL) without
    /// applying NFC, trimming or replacing characters that could change its meaning.
    /// </summary>
    public static void EnsureSafeSyntax(string value, int maximumScalars, string field)
    {
        var normalized = NormalizeRequired(value, maximumScalars, field, allowLineBreaks: false);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            // Syntax-bearing values must never be accepted in one form and then
            // persisted/used in another. This also rejects edge whitespace and tabs,
            // which the display-text normalizer intentionally trims/replaces.
            RejectUnsafe(field);
        }
    }

    private static void EnsureWellFormedUtf16(string value, string field)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(value[index]) ||
                index + 1 >= value.Length ||
                !char.IsLowSurrogate(value[index + 1]))
            {
                RejectUnsafe(field);
            }

            index++;
        }
    }

    private static void EnsureScalarLimit(int scalarCount, int maximumScalars, string field)
    {
        if (scalarCount > maximumScalars)
        {
            RejectTooLong(field, maximumScalars);
        }
    }

    private static void EnsureRawLength(string value, int maximumScalars, string field)
    {
        if (maximumScalars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumScalars));
        }

        if (value.Length > checked(maximumScalars * 2 + 8))
        {
            RejectTooLong(field, maximumScalars);
        }
    }

    private static void RejectTooLong(string field, int maximumScalars) =>
        throw new MessagingApplicationException(
            MessagingErrorCodes.InvalidInput,
            $"{field} cannot exceed {maximumScalars} characters.");

    private static void RejectRequired(string field, int maximumScalars) =>
        throw new MessagingApplicationException(
            MessagingErrorCodes.InvalidInput,
            $"{field} is required and cannot exceed {maximumScalars} characters.");

    private static void RejectUnsafe(string field) =>
        throw new MessagingApplicationException(
            MessagingErrorCodes.InvalidInput,
            $"{field} contains unsupported control or formatting characters.");
}
