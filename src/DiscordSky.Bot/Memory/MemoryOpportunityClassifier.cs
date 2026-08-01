using System.Text.RegularExpressions;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Memory;

public sealed record MemoryOpportunityDecision(
    bool WouldRun,
    double Score,
    IReadOnlyList<string> ReasonCodes);

public static partial class MemoryOpportunityFeatureExtractor
{
    private static readonly string[] PreferenceIdentityChangeCues =
    [
        "i like ", "i love ", "i hate ", "i prefer ", "i am ", "i'm ", "i moved ",
        "i started ", "i stopped ", "i changed ", "actually i ", "my name ", "my job ",
        "my cat ", "my dog ", "my partner ", "we moved ", "we started ",
    ];

    public static MemoryOpportunityFeatures Extract(
        IReadOnlyList<BufferedMessage> messages,
        IReadOnlyList<UserMemory> currentMemories,
        bool isShutdownFlush,
        TimeSpan? priorExtractionAge)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(currentMemories);
        if (messages.Count == 0) throw new ArgumentException("At least one message is required.", nameof(messages));

        var nonEmpty = messages
            .Select(message => message.Content.Trim())
            .Where(content => content.Length > 0)
            .ToArray();
        var firstPersonAssertions = nonEmpty.Count(content =>
            !content.EndsWith('?')
            && FirstPersonRegex().IsMatch(content));
        var cueCount = nonEmpty.Count(content =>
        {
            var normalized = $" {content.ToLowerInvariant()} ";
            return PreferenceIdentityChangeCues.Any(cue => normalized.Contains($" {cue}", StringComparison.Ordinal));
        });
        var questionOnly = nonEmpty.Length > 0
            && nonEmpty.All(content => content.EndsWith('?'));
        var mediaOnly = messages.All(message =>
            string.IsNullOrWhiteSpace(message.Content) && message.HasMedia);
        var combinedText = string.Join(' ', nonEmpty);
        var candidateTokens = TokenUtilities.ExtractContentTokens(combinedText);
        double lexicalNovelty;
        if (candidateTokens.Count == 0)
        {
            lexicalNovelty = 0.0;
        }
        else if (currentMemories.Count == 0)
        {
            lexicalNovelty = 1.0;
        }
        else
        {
            var maxOverlap = currentMemories.Max(memory =>
                TokenUtilities.Jaccard(candidateTokens, TokenUtilities.ExtractContentTokens(memory.Content)));
            lexicalNovelty = Math.Clamp(1.0 - maxOverlap, 0.0, 1.0);
        }
        var first = messages.Min(message => message.Timestamp);
        var last = messages.Max(message => message.Timestamp);

        return new MemoryOpportunityFeatures(
            messages.Count,
            messages.Select(message => message.AuthorId).Distinct().Count(),
            messages.Sum(message => message.Content.Length),
            last - first,
            isShutdownFlush,
            firstPersonAssertions,
            cueCount,
            questionOnly,
            mediaOnly,
            lexicalNovelty,
            priorExtractionAge,
            messages.Count == 1);
    }

    [GeneratedRegex("\\b(?:i|i'm|i've|i'll|my|mine|we|we're|we've|our|ours)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirstPersonRegex();
}

public sealed class MemoryOpportunityClassifier
{
    public MemoryOpportunityDecision Classify(MemoryOpportunityFeatures features)
    {
        if (features.PreferenceIdentityChangeCount > 0)
        {
            return Run(0.98, "preference_identity_change");
        }
        if (features.FirstPersonAssertionCount > 0)
        {
            return Run(0.92, "first_person_assertion");
        }
        if (features.QuestionOnly)
        {
            return Skip(0.92, "question_only");
        }
        if (features.MediaOnly)
        {
            return Skip(0.90, "media_only");
        }
        if (features.CharacterCount == 0)
        {
            return Skip(0.95, "empty_text");
        }
        if (features.IsOneMessageWindow && features.CharacterCount < 24)
        {
            return Skip(0.86, "tiny_single_message");
        }
        if (features.LexicalNovelty < 0.15 && features.CharacterCount < 160)
        {
            return Skip(0.82, "low_lexical_novelty");
        }
        if (features.LexicalNovelty >= 0.70 && features.CharacterCount >= 30)
        {
            return Run(0.80, "high_lexical_novelty");
        }
        if (features.ParticipantCount > 1 && features.CharacterCount >= 80)
        {
            return Run(0.70, "substantive_multi_user_window");
        }
        return Run(0.55, "uncertain_default_run");
    }

    private static MemoryOpportunityDecision Run(double score, string reason) =>
        new(true, score, new[] { reason });

    private static MemoryOpportunityDecision Skip(double score, string reason) =>
        new(false, score, new[] { reason });
}