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

    /// <summary>
    /// Like <see cref="ExtractContentTokens"/> but preserves repeats (term frequency) and order.
    /// Used by BM25 ranking, which needs per-document term counts and lengths.
    /// </summary>
    public static List<string> ExtractContentTokenList(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0)
            {
                AddIfContentful(list, sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
        {
            AddIfContentful(list, sb.ToString());
        }

        return list;
    }

    private static void AddIfContentful(HashSet<string> target, string tok)
    {
        if (tok.Length < 3) return;
        if (Stopwords.Contains(tok)) return;
        target.Add(Stem(tok));
    }

    private static void AddIfContentful(List<string> target, string tok)
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

/// <summary>
/// Okapi BM25 ranking over a small in-memory corpus (one user's memory notes).
/// Replaces the previous Jaccard overlap, which produced near-zero, nearly indistinguishable
/// scores on short memories (production top_score was 0.0 to 0.11), so recall could not rank.
/// BM25 accounts for term rarity (IDF) and document length, which is exactly what short-note
/// ranking needs. See docs/improvement_opportunities_2026-06-10.md F2.
/// </summary>
internal static class Bm25
{
    private const double K1 = 1.5;
    private const double B = 0.75;

    /// <summary>
    /// Returns a raw BM25 score per document (same order as <paramref name="documents"/>) for
    /// <paramref name="query"/>. Scores are unbounded and not normalized; callers normalize as needed.
    /// </summary>
    public static double[] ScoreAll(IReadOnlyList<string> documents, string query)
    {
        var n = documents.Count;
        var scores = new double[n];
        if (n == 0) return scores;

        var queryTerms = TokenUtilities.ExtractContentTokenList(query).Distinct().ToList();
        if (queryTerms.Count == 0) return scores;

        var docTokens = new List<List<string>>(n);
        double totalLen = 0;
        foreach (var doc in documents)
        {
            var toks = TokenUtilities.ExtractContentTokenList(doc);
            docTokens.Add(toks);
            totalLen += toks.Count;
        }
        var avgdl = totalLen > 0 ? totalLen / n : 1.0;

        // Document frequency per query term.
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var q in queryTerms)
        {
            int c = 0;
            foreach (var toks in docTokens)
            {
                if (toks.Contains(q)) c++;
            }
            df[q] = c;
        }

        for (int i = 0; i < n; i++)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in docTokens[i])
            {
                counts[t] = counts.GetValueOrDefault(t) + 1;
            }

            double dl = docTokens[i].Count;
            double s = 0.0;
            foreach (var q in queryTerms)
            {
                if (!counts.TryGetValue(q, out var tf) || tf == 0) continue;
                var idf = Math.Log(1.0 + (n - df[q] + 0.5) / (df[q] + 0.5));
                s += idf * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * (dl / avgdl)));
            }
            scores[i] = s;
        }

        return scores;
    }
}
