#pragma warning disable MEAI001
#pragma warning disable OPENAI001

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;

namespace DiscordSky.Tests;

public sealed class HostedToolSearchSerializationTests
{
    [Fact]
    public async Task ResponsesAdapter_SerializesDeferredApprovalRequiredToolsIntoHostedSearchNamespace()
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
        var chatClient = openAi.GetResponsesClient().AsIChatClient("gpt-5.5");
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
                new ChatOptions { Tools = [deferredWrite, toolSearch] });
        }
        catch
        {
            // The local listener deliberately responds with a provider error after capturing the request.
        }

        using var document = JsonDocument.Parse(await capturedRequest);
        var serialized = document.RootElement.GetRawText();
        Assert.Contains("tool_search", serialized, StringComparison.Ordinal);
        Assert.Contains("defer_loading", serialized, StringComparison.Ordinal);
        Assert.Contains("discord_channels", serialized, StringComparison.Ordinal);
        Assert.Contains("update_channel", serialized, StringComparison.Ordinal);
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