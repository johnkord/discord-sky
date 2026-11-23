using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public sealed class VerifiedDeedTrialOptions
{
    public int Percent { get; init; }
    public DateTimeOffset? UntilUtc { get; init; }
}

internal interface IVerifiedDeedReader
{
    Task<VerifiedDeed?> ReadDeedAsync(ulong guild, ulong channel, string query, DateTimeOffset now, CancellationToken token);
}

internal sealed record VerifiedDeed(string Action, string Subject, string Value, DateTimeOffset At, string RequestId)
{
    private static readonly Regex RoleCue = Cue("roles?|titles?|honou?rific|promotion");
    private static readonly Regex NickCue = Cue("nicknames?|nameplates?|renamed?|names?");
    private static readonly Regex ChannelCue = Cue("channels?|sidebar|renamed?");
    private static Regex Cue(string pattern) => new($@"\b(?:{pattern})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool HasCue(string query) => RoleCue.IsMatch(query) || NickCue.IsMatch(query) || ChannelCue.IsMatch(query);
    internal static bool InCohort(string trigger, int percent) => BinaryPrimitives.ReadUInt32BigEndian(
        SHA256.HashData(Encoding.UTF8.GetBytes("sky-deed-v1:" + trigger))) % 100 < percent;

    internal static VerifiedDeed? From(WorldAutonomyToolCall call, ulong guild, ulong channel, string query)
    {
        if (call.DispatchStatus != WorldAutonomyDispatchStatuses.Succeeded || call.CompletedAt is not { } at ||
            call.RequestId is null || call.ResultJson is null || call.ToolName switch
            {
                "create_role" or "set_member_role" => !RoleCue.IsMatch(query),
                "set_member_nickname" => !NickCue.IsMatch(query),
                "update_channel" => !ChannelCue.IsMatch(query),
                _ => true,
            }) return null;
        try
        {
            using var outer = JsonDocument.Parse(call.ResultJson);
            using var wrapped = Text(outer.RootElement, "Text") is { } text ? JsonDocument.Parse(text) : null;
            var root = wrapped?.RootElement ?? outer.RootElement;
            if (!(Text(root, "outcome") is "succeeded" or "ok" or "success") || Text(root, "requestId") != call.RequestId ||
                !Object(root, "guild", out var binding) || !ulong.TryParse(Text(binding, "id"), out var guildId) || guildId != guild ||
                !Object(root, "data", out var data) || Text(data, "requestId") != call.RequestId || Text(data, "toolName") != call.ToolName ||
                Text(data, "status") != "succeeded" || !data.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True ||
                !Object(data, "observed", out var observed)) return null;
            var member = Text(observed, "userId");
            var objectId = Text(observed, "id");
            var name = Text(observed, "name");
            var nickname = Text(observed, "nickname");
            bool hasBefore = Object(data, "before", out var before);
            if (call.ToolName == "create_role" && ValidId(objectId) && ValidLabel(name))
                return new("created_role", objectId!, name!, at, call.RequestId);
            if (call.ToolName == "set_member_nickname" && ValidId(member) && ValidLabel(nickname))
                return new("set_nickname", member!, nickname!, at, call.RequestId);
            if (call.ToolName == "update_channel" && ulong.TryParse(objectId, out var destination) && destination == channel &&
                ValidLabel(name) && hasBefore && Text(before, "name") is { } oldName && oldName != name)
                return new("renamed_channel", objectId!, name!, at, call.RequestId);
            if (call.ToolName == "set_member_role" && ValidId(member) && hasBefore &&
                before.TryGetProperty("roleIds", out var oldRoles) && oldRoles.ValueKind == JsonValueKind.Array &&
                observed.TryGetProperty("roleIds", out var currentRoles) && currentRoles.ValueKind == JsonValueKind.Array)
            {
                var prior = oldRoles.EnumerateArray().Select(role => role.ToString()).ToHashSet(StringComparer.Ordinal);
                var added = currentRoles.EnumerateArray().Select(role => role.ToString()).Where(role => !prior.Contains(role)).Take(2).ToArray();
                if (added.Length == 1 && ValidId(added[0])) return new("awarded_role", member!, added[0], at, call.RequestId);
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static bool ValidId(string? value) => ulong.TryParse(value, out var id) && id != 0;
    private static bool ValidLabel(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100 && !value.Any(char.IsControl);
    private static string? Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    private static bool Object(JsonElement obj, string key, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object;
    }
}