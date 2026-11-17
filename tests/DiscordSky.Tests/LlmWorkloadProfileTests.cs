using System.Runtime.CompilerServices;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace DiscordSky.Tests;

public class LlmWorkloadProfileTests
{
    [Fact]
    public void ApplicationDefaults_UseAstraForTextAndSeparateOpenAiImageModels()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(ApplicationSettingsPath());
        var options = configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>()!;

        Assert.Equal("OpenAI", options.ActiveProvider);
        var provider = options.GetActiveProvider();
        Assert.True(string.IsNullOrWhiteSpace(provider.Endpoint));
        Assert.True(provider.UseResponsesApi);
        Assert.True(provider.UseHostedToolSearch);
        foreach (var workload in Enum.GetValues<LlmWorkload>())
        {
            var profile = provider.GetProfile(workload, "Robotnik from AOSTH");
            Assert.Equal("gpt-6.2-astra", profile.Model);
            Assert.Equal(workload is LlmWorkload.Utility or LlmWorkload.MemoryExtraction or LlmWorkload.MemoryConsolidation
                ? "low" : "ExtraHigh", profile.ReasoningEffort);
        }
        Assert.Equal(["gpt-6.2-astra"], provider.GetConfiguredModels());
        Assert.Equal("OpenAI", configuration["Image:ProviderName"]);
        Assert.Equal("gpt-image-2.5-flare", configuration["Image:Model"]);
        Assert.Equal("gpt-image-2.5-sunburst", configuration["Image:EditModel"]);
    }

    private static string ApplicationSettingsPath([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "../../src/DiscordSky.Bot/appsettings.json"));

    private static LlmProviderOptions OpenAi(Dictionary<string, string>? overrides = null) => new()
    {
        ChatModel = "gpt-6.2-astra",
        AmbientModel = "gpt-6.2-astra",
        UtilityModel = "gpt-6.2-astra",
        ColdOpenModel = "gpt-6.2-astra",
        ColdOpenCriticModel = "gpt-6.2-astra",
        ImageRewriteModel = "gpt-6.2-astra",
        MemoryExtractionModel = "gpt-6.2-astra",
        MemoryConsolidationModel = "gpt-6.2-astra",
        ReasoningEffort = "ExtraHigh",
        AmbientReasoningEffort = "ExtraHigh",
        UtilityReasoningEffort = "low",
        ColdOpenReasoningEffort = "ExtraHigh",
        ColdOpenCriticReasoningEffort = "ExtraHigh",
        ImageRewriteReasoningEffort = "ExtraHigh",
        MemoryExtractionReasoningEffort = "low",
        MemoryConsolidationReasoningEffort = "low",
        IntentModelOverrides = overrides ?? new Dictionary<string, string>(),
    };

    [Theory]
    [InlineData(LlmWorkload.Main, "gpt-6.2-astra", "ExtraHigh")]
    [InlineData(LlmWorkload.Ambient, "gpt-6.2-astra", "ExtraHigh")]
    [InlineData(LlmWorkload.Utility, "gpt-6.2-astra", "low")]
    [InlineData(LlmWorkload.ColdOpen, "gpt-6.2-astra", "ExtraHigh")]
    [InlineData(LlmWorkload.ColdOpenCritic, "gpt-6.2-astra", "ExtraHigh")]
    [InlineData(LlmWorkload.ImageRewrite, "gpt-6.2-astra", "ExtraHigh")]
    [InlineData(LlmWorkload.MemoryExtraction, "gpt-6.2-astra", "low")]
    [InlineData(LlmWorkload.MemoryConsolidation, "gpt-6.2-astra", "low")]
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
        Assert.Equal("gpt-6.2-astra", provider.GetProfile(LlmWorkload.ColdOpen, "Robotnik").Model);
    }

    [Fact]
    public void DefaultsAndConfiguredModelsUseAstra()
    {
        Assert.Equal("OpenAI", new LlmOptions().ActiveProvider);
        Assert.Equal("gpt-6.2-astra", new LlmProviderOptions().GetProfile(LlmWorkload.Main).Model);
        Assert.Equal(["gpt-6.2-astra"], OpenAi().GetConfiguredModels());
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
