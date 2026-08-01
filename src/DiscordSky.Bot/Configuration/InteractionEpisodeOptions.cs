namespace DiscordSky.Bot.Configuration;

public enum InteractionEpisodeMode
{
    Off,
    Shadow,
    Live,
}

public enum EpisodeFailurePolicy
{
    SilenceAmbient,
    UseLegacyPath,
}

public sealed class InteractionEpisodeOptions
{
    public const string SectionName = "InteractionEpisode";

    public InteractionEpisodeMode Mode { get; init; } = InteractionEpisodeMode.Off;
    public double ShadowSampleRate { get; init; } = 0.10;
    public int RecentMessageLimit { get; init; } = 6;
    public int RecentWindowMinutes { get; init; } = 10;
    public bool DeicticAbstentionEnabled { get; init; }
    public double ReferentConfidenceThreshold { get; init; } = 0.70;
    public int ShadowQueueCapacity { get; init; } = 32;
    public EpisodeFailurePolicy OnBuildError { get; init; } = EpisodeFailurePolicy.SilenceAmbient;
}