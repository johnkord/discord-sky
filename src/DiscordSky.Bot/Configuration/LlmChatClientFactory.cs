using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace DiscordSky.Bot.Configuration;

/// <summary>Builds the provider-bound chat client used by production and evaluation paths.</summary>
public static class LlmChatClientFactory
{
    public static IChatClient Create(LlmProviderOptions provider, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var openAiClient = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? new OpenAIClient(provider.ApiKey)
            : new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(provider.ApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(provider.Endpoint) });

        return provider.UseResponsesApi
            ? openAiClient.GetResponsesClient(model).AsIChatClient()
            : openAiClient.GetChatClient(model).AsIChatClient();
    }
}