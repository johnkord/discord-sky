using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Bot;

/// <summary>
/// Verifies the active LLM provider's API key at startup by issuing a single, harmless
/// <c>GET /v1/models</c> request. If the key is rejected (HTTP 401), the host fails to start
/// and the pod crash-loops — surfacing the auth failure to operators instead of letting it
/// hide behind a healthy <c>/healthz</c> while every reply silently fails.
///
/// Background: we have had three OpenAI 401 incidents in Apr–May 2026 (12→5-day pattern) where
/// the bot looked healthy in <c>kubectl get pods</c> but the orchestrator's circuit breaker
/// was constantly open. <c>/healthz</c> only checks the Discord gateway, not the LLM.
/// See docs/recall_feature_review_2026-05-26.md §7.2.
///
/// Why <c>GET /v1/models</c> rather than a chat completion:
/// list-models is auth-checked but does NOT consume the chat completion code path. One open
/// hypothesis for the shrinking <c>sk-proj-</c> revocation cycle is that auto-revocation
/// correlates with failed-write count, in which case boot-time POSTs would make the original
/// problem worse. <c>GET /v1/models</c> exercises the same auth surface without that risk.
/// </summary>
public sealed class LlmAuthCheckHostedService : IHostedService
{
    private readonly IOptions<LlmOptions> _llmOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<LlmAuthCheckHostedService> _logger;

    public LlmAuthCheckHostedService(
        IOptions<LlmOptions> llmOptions,
        IHttpClientFactory httpClientFactory,
        IHostApplicationLifetime lifetime,
        ILogger<LlmAuthCheckHostedService> logger)
    {
        _llmOptions = llmOptions;
        _httpClientFactory = httpClientFactory;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var provider = _llmOptions.Value.GetActiveProvider();
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            // Program.cs already fails fast on missing key during IChatClient construction;
            // we just skip the check rather than duplicate the error path.
            _logger.LogWarning("LLM auth self-test: no API key configured; skipping.");
            return;
        }

        var baseUri = string.IsNullOrWhiteSpace(provider.Endpoint)
            ? "https://api.openai.com"
            : provider.Endpoint.TrimEnd('/');
        var url = $"{baseUri}/v1/models";

        try
        {
            using var http = _httpClientFactory.CreateClient(nameof(LlmAuthCheckHostedService));
            http.Timeout = TimeSpan.FromSeconds(10);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);

            if ((int)resp.StatusCode == 401)
            {
                // The exact failure mode we're guarding against. Crash the pod loudly.
                _logger.LogCritical(
                    "LLM auth self-test FAILED: HTTP 401 from {Url}. The configured API key for provider '{Provider}' is rejected. " +
                    "Crashing pod to surface the failure (see docs/recall_feature_review_2026-05-26.md §7.2). " +
                    "Rotate the key and redeploy.",
                    url, _llmOptions.Value.ActiveProvider);
                _lifetime.StopApplication();
                throw new InvalidOperationException(
                    $"LLM auth self-test failed: provider '{_llmOptions.Value.ActiveProvider}' returned HTTP 401 from {url}.");
            }

            if (!resp.IsSuccessStatusCode)
            {
                // Other non-2xx (rate limit, transient 5xx, captive-portal HTML, etc.): don't
                // crash the pod — we don't want a flaky cold-start cascade. Just record it.
                _logger.LogWarning(
                    "LLM auth self-test: provider '{Provider}' returned HTTP {Status} from {Url}. Not crashing; this is not a 401.",
                    _llmOptions.Value.ActiveProvider, (int)resp.StatusCode, url);
                return;
            }

            _logger.LogInformation(
                "LLM auth self-test OK: provider '{Provider}' authenticated at {Url} (HTTP {Status}).",
                _llmOptions.Value.ActiveProvider, url, (int)resp.StatusCode);
        }
        catch (InvalidOperationException)
        {
            // 401 path above. Rethrow to fail StartAsync.
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("LLM auth self-test: request to {Url} timed out after 10s; continuing startup.", url);
        }
        catch (Exception ex)
        {
            // Network errors etc.: don't gate startup on transient failure.
            _logger.LogWarning(ex, "LLM auth self-test: probe failed (non-auth error). Continuing startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
