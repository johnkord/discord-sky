using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiscordSky.Bot.Integrations.Safety;

/// <summary>
/// Small, dependency-free helpers for pulling hosts out of message text and normalizing them so that the
/// scam detector can (a) check real registered domains against a blocklist and (b) see through the two cheapest
/// evasions: homoglyph swaps (Cyrillic/Greek look-alikes) and punycode.
/// </summary>
public static class DomainUtilities
{
    // Host-like tokens: one or more dot-separated labels ending in a 2+ char alphabetic TLD. The scheme, path,
    // and credentials are intentionally not consumed; the host substring is found wherever it sits in the text.
    // The atomic group prevents backtracking on adversarial input.
    private static readonly Regex HostTokenRegex = new(
        @"(?>(?:[a-z0-9\u00a1-\uffff][a-z0-9\u00a1-\uffff-]*\.)+)[a-z\u00a1-\uffff][a-z0-9\u00a1-\uffff-]{1,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> Shorteners = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "is.gd", "cutt.ly", "rb.gy", "shorturl.at",
        "ow.ly", "buff.ly", "rebrand.ly", "t.ly", "tiny.cc", "soo.gd", "s.id", "lnkd.in",
    };

    // Look-alike characters folded to a canonical ASCII letter so "dlscord", "disсord" (Cyrillic c), and
    // "d1scord" all collapse to the same skeleton for lookalike matching.
    private static readonly Dictionary<char, char> Confusables = new()
    {
        ['0'] = 'o', ['1'] = 'l', ['3'] = 'e', ['4'] = 'a', ['5'] = 's', ['7'] = 't',
        ['\u0430'] = 'a', ['\u0435'] = 'e', ['\u043e'] = 'o', ['\u0440'] = 'p', ['\u0441'] = 'c',
        ['\u0443'] = 'y', ['\u0445'] = 'x', ['\u0456'] = 'i', ['\u0501'] = 'd',
        ['\u03bf'] = 'o', ['\u03b1'] = 'a', ['\u03b9'] = 'i', ['\u03c1'] = 'p', ['\u03c5'] = 'u',
    };

    /// <summary>All distinct host tokens in the text, lower-cased. Empty when the text has no link.</summary>
    public static IReadOnlyList<string> ExtractHosts(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        var hosts = new List<string>();
        foreach (Match m in HostTokenRegex.Matches(content))
        {
            var host = m.Value.Trim().TrimEnd('.').ToLowerInvariant();
            if (host.IndexOf('.') > 0 && !hosts.Contains(host))
            {
                hosts.Add(host);
            }
        }

        return hosts;
    }

    /// <summary>
    /// The host plus each parent suffix down to (but excluding) the bare TLD, so a blocklist hit on
    /// "evil.ru" still fires for "login.evil.ru". Capped to keep pathological hosts cheap.
    /// </summary>
    public static IEnumerable<string> SuffixCandidates(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            yield break;
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var emitted = 0;
        for (var i = 0; i <= labels.Length - 2 && emitted < 6; i++, emitted++)
        {
            yield return string.Join('.', labels[i..]);
        }
    }

    /// <summary>Lower-cases and folds confusable characters to ASCII. No punycode handling.</summary>
    public static string FoldConfusables(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lower = text.ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            sb.Append(Confusables.TryGetValue(ch, out var folded) ? folded : ch);
        }

        return sb.ToString();
    }

    /// <summary>Decodes punycode labels (xn--) to unicode, then folds confusables. For per-host lookalike checks.</summary>
    public static string Skeleton(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        var s = host.Trim().ToLowerInvariant();
        if (s.Contains("xn--", StringComparison.Ordinal))
        {
            try
            {
                s = new IdnMapping().GetUnicode(s);
            }
            catch (ArgumentException)
            {
                // Malformed punycode; fall through with the raw value.
            }
        }

        return FoldConfusables(s);
    }

    public static bool IsShortener(string? host) =>
        !string.IsNullOrWhiteSpace(host) && Shorteners.Contains(host);
}
