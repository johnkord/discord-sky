using System;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.OpenAI;
using DiscordSky.Bot.Models;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public class OpenAiServiceTests
{
    private static readonly OpenAIOptions DefaultOptions = new();
    private static readonly ChaosSettings DefaultChaos = new();

    [Fact]
    public async Task BitStarter_ParsesStructuredResponse()
    {
        var fakeClient = new FakeOpenAiClient
        {
            Response = new OpenAiResponse
            {
                Output =
                [
                    new OpenAiResponseOutputItem
                    {
                        Content =
                        [
                            new OpenAiResponseOutputContent
                            {
                                Type = "output_text",
                                Text = "{\"title\":\"Operation Spark\",\"script_lines\":[\"Line one\"],\"mentions\":[\"@Spark\"]}"
                            }
                        ]
                    }
                ]
            }
        };

        var service = new BitStarterService(fakeClient, Options.Create(DefaultOptions), NullLogger<BitStarterService>.Instance);
        var result = await service.GenerateAsync(new BitStarterRequest("Spark", ["Spark"]), DefaultChaos);

        Assert.Equal("Operation Spark", result.Title);
        Assert.Single(result.ScriptLines);
        Assert.Contains("@Spark", result.MentionTags);
    }

    [Fact]
    public async Task HeckleCycle_UsesModelDelayWhenProvided()
    {
        var fakeClient = new FakeOpenAiClient
        {
            Response = new OpenAiResponse
            {
                Output =
                [
                    new OpenAiResponseOutputItem
                    {
                        Content =
                        [
                            new OpenAiResponseOutputContent
                            {
                                Type = "output_text",
                                Text = "{\"reminder\":\"Ping\",\"celebration\":\"Yay\",\"nudge_minutes\":45}"
                            }
                        ]
                    }
                ]
            }
        };

        var service = new HeckleCycleService(fakeClient, Options.Create(DefaultOptions), NullLogger<HeckleCycleService>.Instance);
        var trigger = new HeckleTrigger("User", "Build a thing", DateTimeOffset.UtcNow, false);
        var chaos = new ChaosSettings
        {
            QuietHoursStart = new TimeOnly(23, 0),
            QuietHoursEnd = new TimeOnly(23, 0)
        };

        var result = await service.BuildResponseAsync(trigger, chaos);

        Assert.Equal("Ping", result.Reminder);
        Assert.Equal("Yay", result.FollowUpCelebration);
        Assert.Equal(trigger.Timestamp.AddMinutes(45), result.NextNudgeAt);
    }

    [Fact]
    public async Task MischiefQuest_ParsesRewardKind()
    {
        var fakeClient = new FakeOpenAiClient
        {
            Response = new OpenAiResponse
            {
                Output =
                [
                    new OpenAiResponseOutputItem
                    {
                        Content =
                        [
                            new OpenAiResponseOutputContent
                            {
                                Type = "output_text",
                                Text = "{\"title\":\"Quest\",\"steps\":[\"One\",\"Two\",\"Three\"],\"reward_kind\":\"SoundboardUnlock\",\"reward_description\":\"Bleep bloop\"}"
                            }
                        ]
                    }
                ]
            }
        };

        var service = new MischiefQuestService(fakeClient, Options.Create(DefaultOptions), NullLogger<MischiefQuestService>.Instance);
        var result = await service.DrawQuestAsync(DefaultChaos);

        Assert.Equal("Quest", result.Title);
        Assert.Equal(QuestRewardKind.SoundboardUnlock, result.RewardKind);
        Assert.Contains("Three", result.Steps);
        Assert.Equal("Bleep bloop", result.RewardDescription);
    }

    private sealed class FakeOpenAiClient : IOpenAiClient
    {
    public OpenAiResponse Response { get; set; } = new();

        public Task<OpenAiResponse> CreateResponseAsync(OpenAiResponseRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Response);
        }

        public Task<OpenAiModerationResponse?> CreateModerationAsync(OpenAiModerationRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult<OpenAiModerationResponse?>(null);
        }
    }
}
