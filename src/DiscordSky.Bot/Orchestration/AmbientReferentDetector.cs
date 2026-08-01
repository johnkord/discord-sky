using System.Text.RegularExpressions;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Orchestration;

public static partial class AmbientReferentDetector
{
    public static ReferentRequirement Detect(
        string? text,
        bool hasExplicitReply,
        bool hasSelfContainedMedia)
    {
        if (hasExplicitReply) return new ReferentRequirement(false, "explicit_reply");

        var withoutQuotes = QuotedTextRegex().Replace(text ?? string.Empty, " ");
        var normalized = WhitespaceRegex().Replace(withoutQuotes.Trim().ToLowerInvariant(), " ");
        if (normalized.Length == 0) return new ReferentRequirement(false, "empty");
        if (normalized.Length > 180) return new ReferentRequirement(false, "long_form");
        if (hasSelfContainedMedia && IsSelfContainedMediaReaction(normalized))
        {
            return new ReferentRequirement(false, "self_contained_media");
        }
        if (normalized.StartsWith("it is ", StringComparison.Ordinal)
            || normalized.StartsWith("it's ", StringComparison.Ordinal)
            || normalized is "that rules" or "this rules" or "that's wild" or "thats wild")
        {
            return new ReferentRequirement(false, "self_contained_phrase");
        }

        var reason = normalized switch
        {
            "same" or "same here" or "me too" => "elliptical_agreement",
            _ when DeicticQuestionRegex().IsMatch(normalized) => "deictic_question",
            _ when DeicticCommandRegex().IsMatch(normalized) => "deictic_command",
            _ when BareDeicticRegex().IsMatch(normalized) => "bare_deictic",
            _ => null,
        };
        return reason is null
            ? new ReferentRequirement(false, "self_contained")
            : new ReferentRequirement(true, reason);
    }

    private static bool IsSelfContainedMediaReaction(string text) =>
        text is "that rules" or "this rules" or "look at this" or "check this out";

    [GeneratedRegex("[\"'](?:[^\"']|\\.)*[\"']", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedTextRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("^(?:what|who|where|why|how) (?:is|was|are|were) (?:this|that|it|these|those)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex DeicticQuestionRegex();

    [GeneratedRegex("^(?:look at|check out|did you see|can you believe) (?:this|that|it|these|those)\\b", RegexOptions.CultureInvariant)]
    private static partial Regex DeicticCommandRegex();

    [GeneratedRegex("^(?:this|that|it|these|those)(?:\\?|!|\\.| is crazy| was crazy| happened)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BareDeicticRegex();
}