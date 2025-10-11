using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models;

namespace DiscordSky.Bot.Services;

public sealed class MischiefQuestService
{
    private static readonly (string Title, string[] Steps, QuestRewardKind Reward, string Description)[] QuestDeck =
    [
        (
            "Glitter Parcel Heist",
            new[]
            {
                "Find a message with 🍿 reactions and add a new lore twist.",
                "DM a mod with a fake cliffhanger ending.",
                "Drop a celebratory gif in the quest channel."
            },
            QuestRewardKind.CustomBadge,
            "Badge unlocked: Chaos Courier"
        ),
        (
            "Emote Remix Rally",
            new[]
            {
                "Collect three emotes and describe their secret meeting.",
                "Create a meme caption that fuses them into a prophecy.",
                "Record a voice note narrating the aftermath."
            },
            QuestRewardKind.SoundboardUnlock,
            "Soundboard clip: 'We did a thing!'"
        ),
        (
            "Lore Scroll Shuffle",
            new[]
            {
                "Quote an ancient message and add a modern sequel.",
                "Invent a fake historian to validate the lore.",
                "Nominate a co-conspirator with a dramatic @"
            },
            QuestRewardKind.LoreDrop,
            "Lore drop incoming: Secret server history page"
        ),
        (
            "Deadline Derby",
            new[]
            {
                "Announce a task you'll finish tonight.",
                "Share a progress pic or update within 30 minutes.",
                "Celebrate with the 'mission accomplished' gif."
            },
            QuestRewardKind.Roast,
            "Roast delivered by the bot featuring your best quote"
        )
    ];

    private readonly Random _random;

    public MischiefQuestService(Random? random = null)
    {
        _random = random ?? Random.Shared;
    }

    public MischiefQuest DrawQuest(ChaosSettings settings)
    {
        var quest = QuestDeck[_random.Next(QuestDeck.Length)];
        var steps = quest.Steps;

        if (settings.AnnoyanceLevel >= 0.8)
        {
            steps = steps.Concat(new[] { "Bonus chaos: recruit one more goblin to escalate." }).ToArray();
        }

        return new MischiefQuest(quest.Title, steps, quest.Reward, quest.Description);
    }
}
