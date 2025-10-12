using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Integrations.OpenAI;

public sealed class OpenAiClient : IOpenAiClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAIOptions _options;
    private readonly ILogger<OpenAiClient> _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public OpenAiClient(HttpClient httpClient, IOptions<OpenAIOptions> options, ILogger<OpenAiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        _httpClient.BaseAddress = new Uri(_options.Endpoint);
        if (!_httpClient.DefaultRequestHeaders.Contains("Authorization") && !string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public async Task<OpenAiResponse> CreateResponseAsync(OpenAiResponseRequest request, CancellationToken cancellationToken)
    {
        return await SendAsync<OpenAiResponseRequest, OpenAiResponse>("v1/responses", request, cancellationToken) ?? new OpenAiResponse();
    }

    public async Task<OpenAiModerationResponse?> CreateModerationAsync(OpenAiModerationRequest request, CancellationToken cancellationToken)
    {
        return await SendAsync<OpenAiModerationRequest, OpenAiModerationResponse>("v1/moderations", request, cancellationToken);
    }

    private async Task<TResponse?> SendAsync<TRequest, TResponse>(string path, TRequest payload, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _options.RetryCount; attempt++)
        {
            try
            {
                var payloadJson = JsonSerializer.Serialize(payload, SerializerOptions);
                _logger.LogInformation("Dispatching OpenAI request to {Path}: {Payload}", path, payloadJson);

                using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(path, content, cancellationToken);
                var raw = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenAI request to {Path} failed with {Status}: {Body}", path, response.StatusCode, raw);
                    continue;
                }

                return JsonSerializer.Deserialize<TResponse>(raw, SerializerOptions);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("OpenAI request to {Path} timed out on attempt {Attempt}", path, attempt + 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenAI request to {Path} failed on attempt {Attempt}", path, attempt + 1);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
        }

        return default;
    }
}
