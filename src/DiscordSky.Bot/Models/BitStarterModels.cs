namespace DiscordSky.Bot.Models;

public sealed record BitStarterRequest(string Topic, IReadOnlyList<string> Participants, double ChaosMultiplier = 1.0);

public sealed record BitStarterResponse(string Title, IReadOnlyList<string> ScriptLines, IReadOnlyList<string> MentionTags);
