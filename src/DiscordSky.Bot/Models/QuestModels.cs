namespace DiscordSky.Bot.Models;

public enum QuestRewardKind
{
    CustomBadge,
    SoundboardUnlock,
    Roast,
    LoreDrop
}

public sealed record MischiefQuest(string Title, IReadOnlyList<string> Steps, QuestRewardKind RewardKind, string RewardDescription);
