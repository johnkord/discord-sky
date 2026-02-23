namespace DiscordSky.Bot.Configuration;

public sealed class BotOptions
{
    public const string SectionName = "Bot";

    public string Token { get; init; } = string.Empty;
    public string Status { get; init; } = "SnooPING AS usual";
    public List<string> AllowedChannelNames { get; init; } = new();
    public string CommandPrefix { get; init; } = "!sky";
    public int HistoryMessageLimit { get; init; } = 20;
    public string DefaultPersona { get; init; } = "Weird Al";
    public bool AllowImageContext { get; init; } = true;
    public int HistoryImageLimit { get; init; } = 3;
    public List<string> ImageHostAllowList { get; init; } = new()
    {
        "cdn.discordapp.com",
        "media.discordapp.net"
    };

    /// <summary>
    /// Maximum depth of reply chain to fetch when someone replies to the bot.
    /// </summary>
    public int ReplyChainDepth { get; init; } = 40;

    /// <summary>
    /// Whether to include this bot's own messages in channel history.
    /// Enables callbacks and running gags.
    /// </summary>
    public bool IncludeOwnMessagesInHistory { get; init; } = true;

    /// <summary>
    /// Whether to unfurl links (e.g. X/Twitter tweets) and include their content as context.
    /// </summary>
    public bool EnableLinkUnfurling { get; init; } = true;

    /// <summary>
    /// Whether per-user memory is enabled.
    /// </summary>
    public bool EnableUserMemory { get; init; } = true;

    /// <summary>
    /// Maximum number of memories to store per user.
    /// </summary>
    public int MaxMemoriesPerUser { get; init; } = 20;

    /// <summary>
    /// Model to use for memory extraction (should be cheap/fast).
    /// </summary>
    public string MemoryExtractionModel { get; init; } = "gpt-5.2";

    /// <summary>
    /// Probability (0.0–1.0) of running memory extraction on a given invocation.
    /// Reduces cost by only extracting memories on a fraction of interactions.
    /// </summary>
    public double MemoryExtractionRate { get; init; } = 1.0;

    /// <summary>
    /// Directory path for persisted user memory files.
    /// Each user gets a JSON file named {userId}.json.
    /// In K8s, this should point to a PersistentVolume mount.
    /// </summary>
    public string MemoryDataPath { get; init; } = "data/user_memories";

    /// <summary>
    /// How long to wait after the last message in a channel before processing the
    /// accumulated conversation window for memory extraction.
    /// </summary>
    public TimeSpan ConversationWindowTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Maximum number of messages to buffer per channel before forcing extraction.
    /// </summary>
    public int MaxWindowMessages { get; init; } = 30;

    /// <summary>
    /// Maximum wall-clock duration for a conversation window before forcing extraction.
    /// </summary>
    public TimeSpan MaxWindowDuration { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Hard cap on memory operations produced by a single conversation-window extraction.
    /// </summary>
    public int MaxMemoriesPerExtraction { get; init; } = 15;

    /// <summary>
    /// Whether to use LLM-based memory consolidation when a user nears their memory cap.
    /// When disabled, falls back to simple LRU eviction.
    /// </summary>
    public bool EnableMemoryConsolidation { get; init; } = true;

    /// <summary>
    /// Target percentage of MaxMemoriesPerUser to consolidate down to (0.0–1.0).
    /// After consolidation, the user should have at most this fraction of their cap.
    /// For example, 0.75 with MaxMemoriesPerUser=20 consolidates down to 15 memories.
    /// </summary>
    public double ConsolidationTargetPercent { get; init; } = 0.75;

    public bool IsChannelAllowed(string? channelName)
    {
        if (AllowedChannelNames.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(channelName))
        {
            return false;
        }

        return AllowedChannelNames.Any(allowed => string.Equals(allowed, channelName, StringComparison.OrdinalIgnoreCase));
    }
}
