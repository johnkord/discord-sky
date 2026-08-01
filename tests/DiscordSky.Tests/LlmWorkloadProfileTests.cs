using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.AI;

namespace DiscordSky.Tests;

public class LlmWorkloadProfileTests
{
    private static LlmProviderOptions OpenAi(Dictionary<string, string>? overrides = null) => new()
    {
        ChatModel = "gpt-5.6-sol",
        AmbientModel = "gpt-5.6-sol",
        UtilityModel = "gpt-5.4-mini",
        ColdOpenModel = "gpt-5.6-sol",
        ColdOpenCriticModel = "gpt-5.6-sol",
        ImageRewriteModel = "gpt-5.6-sol",
        MemoryExtractionModel = "gpt-5.6-luna",
        MemoryConsolidationModel = "gpt-5.6-luna",
        ReasoningEffort = "ExtraHigh",
        AmbientReasoningEffort = "ExtraHigh",
        UtilityReasoningEffort = "none",
        ColdOpenReasoningEffort = "ExtraHigh",
        ColdOpenCriticReasoningEffort = "ExtraHigh",
        ImageRewriteReasoningEffort = "ExtraHigh",
        MemoryExtractionReasoningEffort = "none",
        MemoryConsolidationReasoningEffort = "none",
        IntentModelOverrides = overrides ?? new Dictionary<string, string>(),
    };

    [Theory]
    [InlineData(LlmWorkload.Main, "gpt-5.6-sol", "ExtraHigh")]
    [InlineData(LlmWorkload.Ambient, "gpt-5.6-sol", "ExtraHigh")]
    [InlineData(LlmWorkload.Utility, "gpt-5.4-mini", "none")]
    [InlineData(LlmWorkload.ColdOpen, "gpt-5.6-sol", "ExtraHigh")]
    [InlineData(LlmWorkload.ColdOpenCritic, "gpt-5.6-sol", "ExtraHigh")]
    [InlineData(LlmWorkload.ImageRewrite, "gpt-5.6-sol", "ExtraHigh")]
    [InlineData(LlmWorkload.MemoryExtraction, "gpt-5.6-luna", "none")]
    [InlineData(LlmWorkload.MemoryConsolidation, "gpt-5.6-luna", "none")]
    public void GetProfile_RoutesOpenAiWorkloads(LlmWorkload workload, string model, string effort)
    {
        var profile = OpenAi().GetProfile(workload, "Robotnik");
        Assert.Equal(model, profile.Model);
        Assert.Equal(effort, profile.ReasoningEffort);
    }

    [Fact]
    public void GetProfile_PersonaOverrideWinsForMainAndAmbientOnly()
    {
        var provider = OpenAi(new Dictionary<string, string> { ["Robotnik"] = "special-robotnik" });

        Assert.Equal("special-robotnik", provider.GetProfile(LlmWorkload.Main, "Robotnik").Model);
        Assert.Equal("special-robotnik", provider.GetProfile(LlmWorkload.Ambient, "Robotnik").Model);
        Assert.Equal("gpt-5.6-sol", provider.GetProfile(LlmWorkload.ColdOpen, "Robotnik").Model);
    }

    [Fact]
    public void GetProfile_UnconfiguredProviderFallsBackToChatModelWithoutSecondaryReasoning()
    {
        var provider = new LlmProviderOptions
        {
            ChatModel = "grok-reasoning",
            ReasoningEffort = null,
        };

        foreach (var workload in Enum.GetValues<LlmWorkload>())
        {
            var profile = provider.GetProfile(workload, "Robotnik");
            Assert.Equal("grok-reasoning", profile.Model);
            Assert.Null(profile.ReasoningEffort);
        }
    }

    [Theory]
    [InlineData("none", ReasoningEffort.None)]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("ExtraHigh", ReasoningEffort.ExtraHigh)]
    public void ApplyReasoning_ParsesConfiguredEffort(string configured, ReasoningEffort expected)
    {
        var options = new ChatOptions();
        new LlmWorkloadProfile("model", configured).ApplyReasoning(options);
        Assert.NotNull(options.Reasoning);
        Assert.Equal(expected, options.Reasoning!.Effort);
    }

    [Fact]
    public void MaximumReasoning_ExpandsOutputHeadroom()
    {
        var profile = new LlmWorkloadProfile("model", "ExtraHigh");

        Assert.True(profile.HasMaximumReasoning);
        Assert.Equal(16_384, profile.WithReasoningHeadroom(900));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(15, 15)]
    [InlineData(90, 60)]
    public void RequestTimeout_IsClamped(int configuredMinutes, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), LlmChatClientFactory.ResolveTimeout(configuredMinutes));
    }
}
