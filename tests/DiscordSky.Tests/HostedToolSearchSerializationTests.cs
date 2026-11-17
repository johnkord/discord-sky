#pragma warning disable MEAI001
#pragma warning disable OPENAI001

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.AI;
using OpenAI;

namespace DiscordSky.Tests;

public sealed class HostedToolSearchSerializationTests
{
    [Theory]
    [InlineData("gpt-6-astra", true)]
    [InlineData("gpt-6.2-astra", true)]
    [InlineData("grok-4.7", false)]
    public async Task ResponsesAdapter_SerializesProviderSpecificToolExposure(string model, bool useHostedToolSearch)
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
        AIFunction deferredWrite = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
            (string value) => value,
            name: "update_channel",
            description: "Update a Discord channel."));
        var toolSearch = new HostedToolSearchTool
        {
            DeferredTools = ["update_channel"],
            Namespace = "discord_channels",
            NamespaceDescription = "Discord channel administration tools."
        };

        try
        {
            await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Find a channel tool.")],
                new ChatOptions
                {
                    ToolMode = ChatToolMode.RequireAny,
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
                    Tools = useHostedToolSearch ? [deferredWrite, toolSearch] : [deferredWrite],
                },
                timeout.Token);
        }
            catch (System.ClientModel.ClientResultException exception) when (exception.Status == 400)
        {
            // The local listener deliberately responds with a provider error after capturing the request.
        }

        using var document = JsonDocument.Parse(await capturedRequest.WaitAsync(timeout.Token));
        var serialized = document.RootElement.GetRawText();
        Assert.Equal(model, document.RootElement.GetProperty("model").GetString());
        Assert.Equal("xhigh", document.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("required", document.RootElement.GetProperty("tool_choice").GetString());
        if (useHostedToolSearch)
        {
            Assert.Contains("tool_search", serialized, StringComparison.Ordinal);
            Assert.Contains("defer_loading", serialized, StringComparison.Ordinal);
            Assert.Contains("discord_channels", serialized, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("tool_search", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("defer_loading", serialized, StringComparison.Ordinal);
            var tool = Assert.Single(document.RootElement.GetProperty("tools").EnumerateArray());
            Assert.Equal("function", tool.GetProperty("type").GetString());
            Assert.Equal("update_channel", tool.GetProperty("name").GetString());
        }
        Assert.Contains("update_channel", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void XaiContinuation_InheritsInstructionsWithoutMutatingSharedOptions()
    {
        var options = new ChatOptions
        {
            ModelId = "grok-4.7",
            Instructions = "Robotnik persona and tool contract",
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh },
        };
        Assert.Same(options, XaiResponsesChatClient.PrepareOptions(options));
        options.ConversationId = "resp-test";
        var forwarded = XaiResponsesChatClient.PrepareOptions(options)!;
        Assert.NotSame(options, forwarded);
        Assert.Null(forwarded.Instructions);
        Assert.Equal("resp-test", forwarded.ConversationId);
        Assert.Equal("grok-4.7", forwarded.ModelId);
        Assert.Equal(ReasoningEffort.ExtraHigh, forwarded.Reasoning!.Effort);
        Assert.Equal("Robotnik persona and tool contract", options.Instructions);
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