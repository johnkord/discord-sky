using System.Text.RegularExpressions;
using DiscordSky.Bot.Memory.Scoring;

namespace DiscordSky.Bot.Orchestration.Impulse;

public sealed record ColdOpenRoomEvidence(
    ulong MessageId,
    ulong? ReferencedMessageId,
    DateTimeOffset Timestamp,
    string Author,
    string RenderedLine,
    IReadOnlyList<string> TopicAnchors,
    IReadOnlyList<string> ResourceIds);

public sealed record ColdOpenEpisodeSnapshot(
    string EpisodeId,
    ulong ChannelId,
    DateTimeOffset FiredAt,
    IReadOnlyList<ulong> SourceMessageIds,
    IReadOnlyList<ulong> ReferencedMessageIds,
    IReadOnlyList<string> ResourceIds,
    IReadOnlyList<string> TopicAnchors,
    string? Hook = null);

public enum ColdOpenNoveltyStage
{
    Off,
    ExactSource,
    ReplyAncestry,
    StableResource,
    MultipleTopicAnchors,
    NoOverlap,
}

public sealed record ColdOpenNoveltyDecision(
    bool Evaluated,
    ColdOpenNoveltyStage Stage,
    bool WouldSuppress,
    bool ShouldSuppress,
    string? MatchingEpisodeId,
    string ReasonCode);

public sealed record ColdOpenSourceValidation(
    string Status,
    int CitedCount,
    int ValidCount,
    int InvalidCount,
    IReadOnlyList<ColdOpenRoomEvidence> SelectedEvidence);

public static class ColdOpenSourceValidator
{
    public static ColdOpenSourceValidation Validate(
        ColdOpenDraft draft,
        IReadOnlyList<ColdOpenRoomEvidence> evidence)
    {
        var cited = (draft.SourceMessageIds ?? Array.Empty<ulong>()).Distinct().ToArray();
        if (cited.Length == 0)
        {
            return new ColdOpenSourceValidation(
                "missing",
                0,
                0,
                0,
                Array.Empty<ColdOpenRoomEvidence>());
        }

        var byId = evidence.ToDictionary(item => item.MessageId);
        var selected = cited.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
        var invalid = cited.Length - selected.Length;
        var status = selected.Length == 0
            ? "invalid"
            : invalid == 0 ? "valid" : "partial";
        return new ColdOpenSourceValidation(status, cited.Length, selected.Length, invalid, selected);
    }
}

public static partial class ColdOpenEvidenceExtractor
{
    private static readonly HashSet<string> NonDiscriminativeAnchors = new(StringComparer.Ordinal)
    {
        "attachment", "deriv", "generat", "image", "media", "summary", "untrust", "visual",
    };

    public static IReadOnlyList<string> ExtractTopicAnchors(params string?[] values) => values
        .SelectMany(value => TokenUtilities.ExtractContentTokens(value))
        .Where(token => !NonDiscriminativeAnchors.Contains(token))
        .Where(token => !token.All(char.IsDigit))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(token => token, StringComparer.Ordinal)
        .Take(12)
        .ToArray();

    public static IReadOnlyList<string> ExtractResourceIds(
        string? text,
        IEnumerable<Uri>? knownUris = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            candidates.AddRange(UrlRegex().Matches(text).Select(match => match.Value));
        }
        if (knownUris is not null) candidates.AddRange(knownUris.Select(uri => uri.ToString()));

        return candidates
            .Select(NormalizeResourceId)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    internal static string? NormalizeResourceId(string value)
    {
        value = value.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }
        var host = uri.IdnHost.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{host}{path}";
    }

    [GeneratedRegex("https?://[^\\s<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}

public static class ColdOpenNoveltyEvaluator
{
    public static ColdOpenNoveltyDecision Evaluate(
        IReadOnlyList<ColdOpenRoomEvidence> candidate,
        IReadOnlyList<ColdOpenEpisodeSnapshot> prior,
        Configuration.ColdOpenEpisodeNoveltyMode mode)
    {
        if (mode == Configuration.ColdOpenEpisodeNoveltyMode.Off)
        {
            return new ColdOpenNoveltyDecision(false, ColdOpenNoveltyStage.Off, false, false, null, "off");
        }

        var sourceIds = candidate.Select(item => item.MessageId).ToHashSet();
        var referencedIds = candidate
            .Where(item => item.ReferencedMessageId.HasValue)
            .Select(item => item.ReferencedMessageId!.Value)
            .ToHashSet();
        var resources = candidate.SelectMany(item => item.ResourceIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchors = candidate.SelectMany(item => item.TopicAnchors).ToHashSet(StringComparer.Ordinal);

        foreach (var episode in prior.OrderByDescending(item => item.FiredAt))
        {
            var priorSources = episode.SourceMessageIds.ToHashSet();
            if (sourceIds.Overlaps(priorSources))
            {
                return Decision(ColdOpenNoveltyStage.ExactSource, episode.EpisodeId, mode);
            }

            var priorReferences = episode.ReferencedMessageIds.ToHashSet();
            if (referencedIds.Overlaps(priorSources)
                || sourceIds.Overlaps(priorReferences)
                || referencedIds.Overlaps(priorReferences))
            {
                return Decision(ColdOpenNoveltyStage.ReplyAncestry, episode.EpisodeId, mode);
            }

            if (resources.Count > 0 && resources.Overlaps(episode.ResourceIds))
            {
                return Decision(ColdOpenNoveltyStage.StableResource, episode.EpisodeId, mode);
            }

            var sharedAnchors = episode.TopicAnchors.Count(anchors.Contains);
            if (sharedAnchors >= 2)
            {
                return Decision(ColdOpenNoveltyStage.MultipleTopicAnchors, episode.EpisodeId, mode);
            }
        }

        return new ColdOpenNoveltyDecision(true, ColdOpenNoveltyStage.NoOverlap, false, false, null, "no_overlap");
    }

    private static ColdOpenNoveltyDecision Decision(
        ColdOpenNoveltyStage stage,
        string episodeId,
        Configuration.ColdOpenEpisodeNoveltyMode mode)
    {
        var exactStage = stage is ColdOpenNoveltyStage.ExactSource
            or ColdOpenNoveltyStage.ReplyAncestry
            or ColdOpenNoveltyStage.StableResource;
        var shouldSuppress = mode switch
        {
            Configuration.ColdOpenEpisodeNoveltyMode.Exact => exactStage,
            Configuration.ColdOpenEpisodeNoveltyMode.Calibrated => true,
            _ => false,
        };
        return new ColdOpenNoveltyDecision(
            true,
            stage,
            WouldSuppress: true,
            ShouldSuppress: shouldSuppress,
            episodeId,
            stage.ToString().ToLowerInvariant());
    }
}