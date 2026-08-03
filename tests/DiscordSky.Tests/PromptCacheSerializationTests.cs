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
    [Fact]
    public async Task ResponsesAdapter_DropsUnsupportedGpt56RequestCacheControls()
    {
        var port = ReserveLoopbackPort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var capturedRequest = CaptureRequestAsync(listener);
        var openAi = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential("not-a-real-key"),
            new OpenAIClientOptions { Endpoint = new Uri(prefix) });
        var chatClient = openAi.GetResponsesClient().AsIChatClient("gpt-5.6");
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
                options);
        }
        catch
        {
            // The loopback listener deliberately returns an API error after capturing the outbound request.
        }

        using var document = JsonDocument.Parse(await capturedRequest);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("prompt_cache_key", out _));
        Assert.False(root.TryGetProperty("prompt_cache_options", out _));
    }

    [Fact]
    public async Task ResponsesAdapter_RawRepresentationForwardsAllGpt56CacheControls()
    {
        var port = ReserveLoopbackPort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var capturedRequest = CaptureRequestAsync(listener);
        var openAi = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential("not-a-real-key"),
            new OpenAIClientOptions { Endpoint = new Uri(prefix) });
        var chatClient = openAi.GetResponsesClient().AsIChatClient("gpt-5.6");
        var context = new WorldAutonomyRunContext(
            "run-cache-probe",
            4001,
            "discord_message",
            "1001",
            "episode-1",
            "trace-1",
            "gpt-5.6",
            "profile-digest",
            "manifest-digest",
            ["01900000-0000-7000-8000-000000000001"],
            PersonaDirective: "Mood: gloating.",
            SourceChannelId: 6001,
            SourceChannelName: "general",
            SourceAuthorId: 8001,
            SourceAuthorDisplayName: "member");
        var options = new ChatOptions();
        var cacheKey = WorldAutonomyPromptCache.Configure(options, context, terminalDeliveryEnabled: true);

        try
        {
            await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Dynamic episode suffix.")],
                options);
        }
        catch
        {
            // The loopback listener deliberately returns an API error after capturing the outbound request.
        }

        using var document = JsonDocument.Parse(await capturedRequest);
        var root = document.RootElement;
        Assert.Equal(cacheKey, root.GetProperty("prompt_cache_key").GetString());
        var cacheOptions = root.GetProperty("prompt_cache_options");
        Assert.Equal("explicit", cacheOptions.GetProperty("mode").GetString());
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