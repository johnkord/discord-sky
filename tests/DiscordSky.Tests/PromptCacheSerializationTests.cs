#pragma warning disable OPENAI001
#pragma warning disable SCME0001

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace DiscordSky.Tests;

public sealed class PromptCacheSerializationTests
{
    [Theory]
    [InlineData("gpt-5.6")]
    [InlineData("gpt-6-sol")]
    [InlineData("gpt-6-astra")]
    [InlineData("gpt-6.2-astra")]
    public async Task ResponsesAdapter_DropsUnsupportedRequestCacheControls(string model)
    {
        var port = ReserveLoopbackPort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capturedRequest = CaptureRequestAsync(listener);
        var openAi = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential("not-a-real-key"),
            new OpenAIClientOptions { Endpoint = new Uri(prefix) });
        var chatClient = openAi.GetResponsesClient().AsIChatClient(model);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["prompt_cache_key"] = "world-autonomy:test:v1",
                ["prompt_cache_options"] = new Dictionary<string, object>
                {
                    ["mode"] = "explicit",
                    ["ttl"] = "30m",
                },
            },
        };

        try
        {
            await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Harmless cache serialization probe.")],
                options, timeout.Token);
        }
            catch (System.ClientModel.ClientResultException exception) when (exception.Status == 400)
        {
            // The loopback listener deliberately returns an API error after capturing the outbound request.
        }

        using var document = JsonDocument.Parse(await capturedRequest.WaitAsync(timeout.Token));
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("prompt_cache_key", out _));
        Assert.False(root.TryGetProperty("prompt_cache_options", out _));
    }

    [Theory]
    [InlineData("gpt-5.6", WorldAutonomyPromptCacheMode.Explicit)]
    [InlineData("gpt-6-sol", WorldAutonomyPromptCacheMode.Explicit)]
    [InlineData("gpt-6-astra", WorldAutonomyPromptCacheMode.Explicit)]
    [InlineData("gpt-6-astra", WorldAutonomyPromptCacheMode.Hybrid)]
    [InlineData("gpt-6.2-astra", WorldAutonomyPromptCacheMode.Explicit)]
    [InlineData("gpt-6.2-astra", WorldAutonomyPromptCacheMode.Hybrid)]
    public async Task ResponsesAdapter_RawRepresentationForwardsAllCacheControls(string model, WorldAutonomyPromptCacheMode mode)
    {
        var port = ReserveLoopbackPort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capturedRequest = CaptureRequestAsync(listener);
        var openAi = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential("not-a-real-key"),
            new OpenAIClientOptions { Endpoint = new Uri(prefix) });
        var chatClient = openAi.GetResponsesClient().AsIChatClient(model);
        var context = new WorldAutonomyRunContext(
            "run-cache-probe",
            4001,
            "discord_message",
            "1001",
            "episode-1",
            "trace-1",
            model,
            new string('a', 64),
            new string('b', 64),
            ["01900000-0000-7000-8000-000000000001"],
            PersonaDirective: "Mood: gloating.",
            SourceChannelId: 6001,
            SourceChannelName: "general",
            SourceAuthorId: 8001,
            SourceAuthorDisplayName: "member");
        var options = new ChatOptions();
        var cacheKey = WorldAutonomyPromptCache.Configure(options, context, terminalDeliveryEnabled: true, mode: mode);

        try
        {
            await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Dynamic episode suffix.")],
                options, timeout.Token);
        }
            catch (System.ClientModel.ClientResultException exception) when (exception.Status == 400)
        {
            // The loopback listener deliberately returns an API error after capturing the outbound request.
        }

        using var document = JsonDocument.Parse(await capturedRequest.WaitAsync(timeout.Token));
        var root = document.RootElement;
        Assert.Equal(cacheKey, root.GetProperty("prompt_cache_key").GetString());
        Assert.Equal(64, root.GetProperty("prompt_cache_key").GetString()!.Length);
        var cacheOptions = root.GetProperty("prompt_cache_options");
        Assert.Equal(mode == WorldAutonomyPromptCacheMode.Hybrid ? "implicit" : "explicit", cacheOptions.GetProperty("mode").GetString());
        Assert.Equal("30m", cacheOptions.GetProperty("ttl").GetString());
        var content = root.GetProperty("input")[0].GetProperty("content");
        Assert.Equal(
            "explicit",
            content[0].GetProperty("prompt_cache_breakpoint").GetProperty("mode").GetString());
        Assert.False(
            root.GetProperty("input")[1].GetProperty("content")[0]
                .TryGetProperty("prompt_cache_breakpoint", out _));
        Assert.Equal("developer", root.GetProperty("input")[0].GetProperty("role").GetString());
        Assert.Equal("developer", root.GetProperty("input")[1].GetProperty("role").GetString());
        Assert.Equal("user", root.GetProperty("input")[2].GetProperty("role").GetString());
        if (mode == WorldAutonomyPromptCacheMode.Hybrid)
        {
            var stableText = root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.True(stableText.Length > 6000);
            Assert.DoesNotContain("Mood: gloating.", stableText);
            Assert.DoesNotContain("01900000-0000-7000-8000-000000000001", stableText);
            var combined = root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString() +
                root.GetProperty("input")[1].GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal(WorldAutonomyPrompt.BuildInstructions(context, true), combined);
        }
    }

    [Theory]
    [InlineData(WorldAutonomyPromptCacheMode.Hybrid)]
    [InlineData(WorldAutonomyPromptCacheMode.Explicit)]
    public void CacheKey_IsBoundedStableAndSeparatesInstructionIdentity(WorldAutonomyPromptCacheMode mode)
    {
        var context = new WorldAutonomyRunContext("run", 4001, "message", "5001", null, null,
            "gpt-6.2-astra-" + new string('x', 96), new string('a', 64), new string('b', 64), [],
            SourceChannelId: 6001);
        string Key(WorldAutonomyRunContext candidate, bool terminal = true, bool hosted = true) =>
            WorldAutonomyPromptCache.Configure(new ChatOptions(), candidate, terminal, hosted, mode);

        var key = Key(context);

        Assert.Matches("^[a-f0-9]{64}$", key);
        Assert.Equal(key, Key(context));
        Assert.Equal(key, Key(context with
        {
            RunId = "another-run",
            SourceMessageId = "5002",
            PersonaDirective = "Mood: gloating.",
        }));
        Assert.NotEqual(key, Key(context with { Model = "gpt-6.2-astra" }));
        Assert.NotEqual(key, Key(context with { ManifestDigest = new string('b', 63) + "c" }));
        Assert.NotEqual(key, Key(context, terminal: false));
        Assert.NotEqual(key, Key(context, hosted: false));
    }

    [Theory]
    [InlineData("gpt-6-astra", WorldAutonomyPromptCacheMode.Hybrid)]
    [InlineData("gpt-6-astra", WorldAutonomyPromptCacheMode.Explicit)]
    [InlineData("gpt-6.2-astra", WorldAutonomyPromptCacheMode.Hybrid)]
    [InlineData("gpt-6.2-astra", WorldAutonomyPromptCacheMode.Explicit)]
    public async Task CachedContinuation_DoesNotResendDeveloperInstructions(string model, WorldAutonomyPromptCacheMode mode)
    {
        var prefix = $"http://127.0.0.1:{ReserveLoopbackPort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = CaptureRequestAsync(listener);
        var openAi = new OpenAIClient(new System.ClientModel.ApiKeyCredential("not-a-real-key"), new OpenAIClientOptions { Endpoint = new Uri(prefix) });
        using var client = WorldAutonomyPromptCache.Wrap(openAi.GetResponsesClient().AsIChatClient(model));
        var context = new WorldAutonomyRunContext("run", 4001, "message", "5001", null, null,
            model, new string('a', 64), new string('b', 64), [], SourceChannelId: 6001);
        var options = new ChatOptions();
        WorldAutonomyPromptCache.Configure(options, context, true, mode: mode);
        options.ConversationId = "resp-prior";
        try
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "continue")], options, timeout.Token);
        }
        catch (System.ClientModel.ClientResultException exception) when (exception.Status == 400) { }
        using var document = JsonDocument.Parse(await capture.WaitAsync(timeout.Token));
        Assert.Equal("resp-prior", document.RootElement.GetProperty("previous_response_id").GetString());
        var input = Assert.Single(document.RootElement.GetProperty("input").EnumerateArray());
        Assert.Equal("user", input.GetProperty("role").GetString());
        var initial = Assert.IsType<CreateResponseOptions>(options.RawRepresentationFactory!(client));
        Assert.Equal(2, initial.InputItems.Count);
    }

    [Theory]
    [InlineData("gpt-6-astra")]
    [InlineData("gpt-6.2-astra")]
    public void HybridCohorts_AreStableAcrossRunsAndDisabledForOtherProviders(string model)
    {
        var configuration = WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        { PromptCacheMode = WorldAutonomyPromptCacheMode.Hybrid, PromptCacheCanaryPercent = 50 });
        var context = new WorldAutonomyRunContext("run", 4001, "message", "5001", null, null,
            model, "profile", "manifest", []);
        var first = WorldAutonomyPromptCache.SelectMode(configuration, context, "OpenAI");
        Assert.Equal(first, WorldAutonomyPromptCache.SelectMode(configuration, context with { RunId = "another-run" }, "OpenAI"));
        Assert.Equal(WorldAutonomyPromptCacheMode.Off, WorldAutonomyPromptCache.SelectMode(configuration, context, "xAI"));
        Assert.Equal(WorldAutonomyPromptCacheMode.Off, WorldAutonomyPromptCache.SelectMode(configuration, context with { Model = "grok-4.7" }, "OpenAI"));
        var groups = Enumerable.Range(1, 200).Select(index => WorldAutonomyPromptCache.SelectMode(configuration,
            context with { SourceMessageId = index.ToString() }, "OpenAI")).ToHashSet();
        Assert.Contains(WorldAutonomyPromptCacheMode.Off, groups);
        Assert.Contains(WorldAutonomyPromptCacheMode.Hybrid, groups);
        Assert.Throws<InvalidOperationException>(() => WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions { PromptCacheCanaryPercent = 101 }));
    }

    private static async Task<string> CaptureRequestAsync(HttpListener listener)
    {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        context.Response.ContentType = "application/json";
        var error = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"captured\",\"type\":\"invalid_request_error\"}}");
        await context.Response.OutputStream.WriteAsync(error);
        context.Response.Close();
        return body;
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}