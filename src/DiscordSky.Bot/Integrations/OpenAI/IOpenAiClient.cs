using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Integrations.OpenAI;

public interface IOpenAiClient
{
    Task<OpenAiResponse> CreateResponseAsync(OpenAiResponseRequest request, CancellationToken cancellationToken);

    Task<OpenAiModerationResponse?> CreateModerationAsync(OpenAiModerationRequest request, CancellationToken cancellationToken);
}
