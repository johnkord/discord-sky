using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscordSky.Bot.Orchestration.Autonomy;

internal interface IVerifiedDeedReader
{
    Task<VerifiedDeed?> ReadAppealedDeedAsync(ulong guild, ulong channel, ulong messageId, string decree, string petition, DateTimeOffset now, CancellationToken token);
}

internal sealed record VerifiedDeed(string Action, string Subject, string Value, DateTimeOffset At, string RequestId)
{
    private static readonly Regex DiscordMention = new(@"<(@!|@&|@|#)([0-9]{1,20})>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static bool HasNamedSubject(string text) => DiscordMention.IsMatch(text);

    internal static VerifiedDeed? From(WorldAutonomyToolCall call, ulong guild, ulong channel)
    {
        if (call.DispatchStatus != WorldAutonomyDispatchStatuses.Succeeded || call.CompletedAt is not { } at ||
            call.RequestId is null || call.ResultJson is null || call.ErrorMessage is not null ||
            call.ToolName is not ("create_role" or "set_member_role" or "set_member_nickname" or "update_channel")) return null;
        try
        {
            using var outer = JsonDocument.Parse(call.ResultJson);
            if (outer.RootElement.ValueKind != JsonValueKind.Object || True(outer.RootElement, "isError") || True(outer.RootElement, "IsError")) return null;
            using var wrapped = Text(outer.RootElement, "Text") is { } text ? JsonDocument.Parse(text) : null;
            var root = wrapped?.RootElement ?? outer.RootElement;
            if (!(Text(root, "outcome") is "succeeded" or "ok" or "success") || Text(root, "requestId") != call.RequestId ||
                (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) ||
                !Object(root, "guild", out var binding) || !ulong.TryParse(Text(binding, "id"), out var guildId) || guildId != guild ||
                !Object(root, "data", out var data) || Text(data, "requestId") != call.RequestId || Text(data, "toolName") != call.ToolName ||
                Text(data, "status") != "succeeded" || (data.TryGetProperty("errorCode", out var errorCode) && errorCode.ValueKind != JsonValueKind.Null) || !True(data, "verified") ||
                Text(data, "outcome") is not ("verified_desired_state" or "recovered_verified_desired_state" or "reconciled_verified_desired_state") ||
                !Object(data, "before", out var before) || !Object(data, "desired", out var desired) || !Object(data, "observed", out var observed)) return null;
            var member = Text(observed, "userId");
            var objectId = Text(observed, "id");
            var name = Text(observed, "name");
            var nickname = Text(observed, "nickname");
            if (call.ToolName == "create_role" && ValidId(objectId) && ValidLabel(name) && Text(desired, "name") == name && True(desired, "exists") && True(observed, "exists"))
                return new("created_role", objectId!, name!, at, call.RequestId);
            var sameMember = ValidId(member) && Text(before, "userId") == member && Text(desired, "userId") == member;
            if (call.ToolName == "set_member_nickname" && sameMember && Text(data, "resourceId") == member && ValidLabel(nickname) &&
                Text(desired, "nickname") == nickname && Text(before, "nickname") != nickname)
                return new("set_nickname", member!, nickname!, at, call.RequestId);
            if (call.ToolName == "update_channel" && ulong.TryParse(objectId, out var destination) && destination == channel &&
                Text(data, "resourceId") == objectId && Text(before, "id") == objectId && Text(desired, "id") == objectId &&
                ValidLabel(name) && Text(desired, "name") == name && Text(before, "name") is { } oldName && oldName != name)
                return new("renamed_channel", objectId!, name!, at, call.RequestId);
            if (call.ToolName == "set_member_role" && sameMember && Roles(before) is { } prior && Roles(desired) is { } wanted &&
                Roles(observed) is { } after && after.SetEquals(wanted) && after.Count == prior.Count + 1 && prior.IsSubsetOf(after))
                return new("awarded_role", member!, after.Single(role => !prior.Contains(role)), at, call.RequestId);
        }
        catch (JsonException) { }
        return null;
    }

    internal bool IsPubliclyNamedIn(string decree, string petition)
    {
        static HashSet<string> Mentions(string text) => DiscordMention.Matches(text)
            .Select(match => (match.Groups[1].Value == "@!" ? "@" : match.Groups[1].Value) + match.Groups[2].Value)
            .ToHashSet(StringComparer.Ordinal);
        string[] targets = Action switch
        {
            "awarded_role" => ["@" + Subject, "@&" + Value],
            "created_role" => ["@&" + Subject],
            "set_nickname" => ["@" + Subject],
            "renamed_channel" => ["#" + Subject],
            _ => []
        };
        return targets.Length > 0 && (Action == "awarded_role" || ContainsLabel(DiscordMention.Replace(decree, " "), Value)) &&
            Mentions(decree).SetEquals(targets) && Mentions(petition).SetEquals(targets);
    }

    private static bool ContainsLabel(string text, string label)
    {
        for (var index = text.IndexOf(label, StringComparison.OrdinalIgnoreCase); index >= 0;
            index = text.IndexOf(label, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = index + label.Length;
            if ((index == 0 || !WordChar(text[index - 1])) && (end == text.Length || !WordChar(text[end]))) return true;
        }
        return false;
    }

    private static bool WordChar(char value) => char.IsLetterOrDigit(value) || char.GetUnicodeCategory(value) is
        System.Globalization.UnicodeCategory.ConnectorPunctuation or System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark;

    private static HashSet<string>? Roles(JsonElement state)
    {
        if (!state.TryGetProperty("roleIds", out var ids) || ids.ValueKind != JsonValueKind.Array) return null;
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in ids.EnumerateArray())
        {
            if (role.ValueKind != JsonValueKind.String || role.GetString() is not { } id || !ValidId(id) || !roles.Add(id)) return null;
        }
        return roles;
    }

    private static bool True(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool ValidId(string? value) => ulong.TryParse(value, out var id) && id != 0;
    private static bool ValidLabel(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100 && !value.Any(char.IsControl);
    private static string? Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    private static bool Object(JsonElement obj, string key, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object;
    }
}