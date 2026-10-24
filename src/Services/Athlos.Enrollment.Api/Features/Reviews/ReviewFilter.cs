using System.Text.RegularExpressions;
using Athlos.Enrollment.Api.Data;

namespace Athlos.Enrollment.Api.Features.Reviews;

// A first, automatic look at every review comment (design §2.4), before anyone reports it: contact
// details (an email address or a phone number: personal data), a link (spam), or a word on the
// Reviews:BlockedWords list (abusive). A comment it catches is hidden at once and put in the admin's
// moderation queue as a report from Athlos itself; the admin keeps it (shown again) or hides it, as
// for any report. It only holds a review back for a person to decide; it never deletes one.
public static partial class ReviewFilter
{
    // The reporter of the filter's reports: no user has this id.
    public static readonly Guid ReporterId = Guid.Empty;

    public const string Note = "Held back by the automatic check.";

    public static ReportReason? Check(string? comment, IReadOnlySet<string> blockedWords)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return null;
        }

        if (Email().IsMatch(comment) || Phone().IsMatch(comment))
        {
            return ReportReason.PersonalData;
        }

        if (Link().IsMatch(comment))
        {
            return ReportReason.Spam;
        }

        return Words().Matches(comment).Any(w => blockedWords.Contains(Plain(w.Value))) ? ReportReason.Abusive : null;
    }

    // The list from configuration, compared without case or accents ("μαλακα" is "μαλάκα").
    public static IReadOnlySet<string> BlockedWords(IConfiguration configuration) =>
        new HashSet<string>((configuration.GetSection("Reviews:BlockedWords").Get<string[]>() ?? []).Select(Plain), StringComparer.OrdinalIgnoreCase);

    // Greek letters without their accents. A table, not string.Normalize: the services run with
    // invariant globalization (small containers), where normalization changes nothing.
    private static readonly Dictionary<char, char> Unaccented = new()
    {
        ['ά'] = 'α', ['έ'] = 'ε', ['ή'] = 'η', ['ί'] = 'ι', ['ό'] = 'ο', ['ύ'] = 'υ', ['ώ'] = 'ω', ['ϊ'] = 'ι', ['ϋ'] = 'υ', ['ΐ'] = 'ι', ['ΰ'] = 'υ',
        ['Ά'] = 'Α', ['Έ'] = 'Ε', ['Ή'] = 'Η', ['Ί'] = 'Ι', ['Ό'] = 'Ο', ['Ύ'] = 'Υ', ['Ώ'] = 'Ω', ['Ϊ'] = 'Ι', ['Ϋ'] = 'Υ',
    };

    private static string Plain(string word) => string.Concat(word.Select(c => Unaccented.GetValueOrDefault(c, c)));

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")]
    private static partial Regex Email();

    // 10 to 15 digits, optionally a leading + and single spaces or dashes between them; dates and
    // counts ("12 weeks, 3x5") are shorter.
    [GeneratedRegex(@"\+?\d(?:[ -]?\d){9,14}")]
    private static partial Regex Phone();

    [GeneratedRegex(@"\bhttps?://|\bwww\.|\b[\p{L}\d-]+\.(com|net|org|gr|eu|io|me|info|biz|co|ly)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Words();
}
