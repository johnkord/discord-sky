using System.Text;

namespace DiscordSky.Bot.Memory.Scoring;

/// <summary>
/// Minimal lexical preprocessing used by <see cref="LexicalMemoryScorer"/>.
/// Deliberately boring: lowercase, split on non-alphanumerics, drop short and stopword tokens, trim three common suffixes.
/// We want cheap and predictable behaviour, not linguistic accuracy.
/// </summary>
internal static class TokenUtilities
{
    // NLTK English stopwords (trimmed of punctuation-only entries). MIT-licensed.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "about", "above", "after", "again", "against", "all", "am", "an", "and", "any", "are", "as", "at",
        "be", "because", "been", "before", "being", "below", "between", "both", "but", "by",
        "could", "did", "do", "does", "doing", "down", "during",
        "each",
        "few", "for", "from", "further",
        "had", "has", "have", "having", "he", "her", "here", "hers", "herself", "him", "himself", "his", "how",
        "i", "if", "in", "into", "is", "it", "its", "itself",
        "just",
        "me", "might", "more", "most", "must", "my", "myself",
        "no", "nor", "not", "now",
        "of", "off", "on", "once", "only", "or", "other", "our", "ours", "ourselves", "out", "over", "own",
        "same", "she", "should", "so", "some", "such",
        "than", "that", "the", "their", "theirs", "them", "themselves", "then", "there", "these", "they", "this",
        "those", "through", "to", "too",
        "under", "until", "up",
        "very",
        "was", "we", "were", "what", "when", "where", "which", "while", "who", "whom", "why", "will", "with", "would",
        "you", "your", "yours", "yourself", "yourselves",
        // Discord/bot-filler
        "yeah", "yes", "ok", "okay", "lol", "lmao", "gonna", "wanna",
    };

    public static HashSet<string> ExtractContentTokens(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return set;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                AddIfContentful(set, sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
        {
            AddIfContentful(set, sb.ToString());
        }

        return set;
    }

    private static void AddIfContentful(HashSet<string> target, string tok)
    {
        if (tok.Length < 3) return;
        if (Stopwords.Contains(tok)) return;
        target.Add(Stem(tok));
    }

    private static string Stem(string tok)
    {
        // Trivial suffix stripper: enough to fold singulars/plurals and simple verb forms without building a Porter stemmer.
        if (tok.Length > 5 && tok.EndsWith("ing", StringComparison.Ordinal)) return tok[..^3];
        if (tok.Length > 4 && tok.EndsWith("ed", StringComparison.Ordinal)) return tok[..^2];
        if (tok.Length > 3 && tok.EndsWith("s", StringComparison.Ordinal) && !tok.EndsWith("ss", StringComparison.Ordinal)) return tok[..^1];
        return tok;
    }

    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        int intersect = 0;
        // iterate the smaller set
        var (small, big) = a.Count <= b.Count ? (a, b) : (b, a);
        foreach (var t in small)
        {
            if (big.Contains(t)) intersect++;
        }
        int union = a.Count + b.Count - intersect;
        return union == 0 ? 0.0 : (double)intersect / union;
    }
}
