namespace DiscordSky.Bot.Configuration;

public enum MemoryOpportunityGateMode
{
    Off,
    Shadow,
    Live,
}

public enum ShutdownFlushExtractionPolicy
{
    RunAlways,
    RespectGate,
}

public sealed class MemoryExtractionOptions
{
    public const string SectionName = "MemoryExtraction";

    public bool YieldTelemetryEnabled { get; init; } = true;
    public bool EvidenceRequired { get; init; }
    public MemoryOpportunityGateMode OpportunityGateMode { get; init; } = MemoryOpportunityGateMode.Off;
    public double ExplorationRate { get; init; } = 0.05;
    public ShutdownFlushExtractionPolicy ShutdownFlushPolicy { get; init; } = ShutdownFlushExtractionPolicy.RunAlways;
}