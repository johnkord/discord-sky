using System.Net;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class LlmAuthCheckHostedServiceTests
{
    private static LlmOptions OptionsWithKey(string? key = "sk-test-key", string? endpoint = null) => new()
    {
        ActiveProvider = "OpenAI",
        Providers = new Dictionary<string, LlmProviderOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] = new LlmProviderOptions
            {
                ApiKey = key ?? string.Empty,
                ChatModel = "gpt-test",
                Endpoint = endpoint ?? string.Empty,
            }
        }
    };

    private static (LlmAuthCheckHostedService service, FakeLifetime lifetime) Build(
        HttpStatusCode responseStatus,
        LlmOptions? opts = null,
        TimeSpan? delay = null)
    {
        var handler = new CannedResponseHandler(responseStatus, delay);
        var factory = new SingleClientFactory(handler);
        var lifetime = new FakeLifetime();
        var service = new LlmAuthCheckHostedService(
            Options.Create(opts ?? OptionsWithKey()),
            factory,
            lifetime,
            NullLogger<LlmAuthCheckHostedService>.Instance);
        return (service, lifetime);
    }

    [Fact]
    public async Task Returns_HTTP_200_Completes_Normally()
    {
        var (service, lifetime) = Build(HttpStatusCode.OK);
        await service.StartAsync(CancellationToken.None);
        Assert.False(lifetime.StopRequested);
    }

    [Fact]
    public async Task Returns_HTTP_401_Throws_And_Requests_Shutdown()
    {
        var (service, lifetime) = Build(HttpStatusCode.Unauthorized);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));
        Assert.Contains("401", ex.Message);
        Assert.True(lifetime.StopRequested, "lifetime.StopApplication() must be called");
    }

    [Fact]
    public async Task Returns_HTTP_500_Does_Not_Crash()
    {
        // We don't want flaky upstream errors to crash-loop the pod.
        var (service, lifetime) = Build(HttpStatusCode.InternalServerError);
        await service.StartAsync(CancellationToken.None);
        Assert.False(lifetime.StopRequested);
    }

    [Fact]
    public async Task NoApiKey_Skips_Silently()
    {
        var (service, lifetime) = Build(HttpStatusCode.Unauthorized, OptionsWithKey(key: ""));
        // No-op: even though the canned handler would 401, we never call it.
        await service.StartAsync(CancellationToken.None);
        Assert.False(lifetime.StopRequested);
    }

    [Fact]
    public async Task CustomEndpoint_IsUsed()
    {
        var handler = new CannedResponseHandler(HttpStatusCode.OK);
        var factory = new SingleClientFactory(handler);
        var service = new LlmAuthCheckHostedService(
            Options.Create(OptionsWithKey(endpoint: "https://api.x.ai/")),
            factory, new FakeLifetime(), NullLogger<LlmAuthCheckHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://api.x.ai/v1/models", handler.LastRequest!.RequestUri!.ToString());
        var auth = handler.LastRequest.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("sk-test-key", auth.Parameter);
    }

    [Fact]
    public async Task Probe_Times_Out_Does_Not_Crash()
    {
        // Use a handler that delays longer than the 10s timeout. We don't want to wait 10s in
        // test, so we set a short timeout window by triggering the linked token externally.
        var handler = new CannedResponseHandler(HttpStatusCode.OK, delay: TimeSpan.FromSeconds(60));
        var factory = new SingleClientFactory(handler);
        var lifetime = new FakeLifetime();
        var service = new LlmAuthCheckHostedService(
            Options.Create(OptionsWithKey()),
            factory, lifetime, NullLogger<LlmAuthCheckHostedService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await service.StartAsync(cts.Token);
        Assert.False(lifetime.StopRequested);
    }

    private sealed class CannedResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly TimeSpan _delay;
        public HttpRequestMessage? LastRequest { get; private set; }

        public CannedResponseHandler(HttpStatusCode status, TimeSpan? delay = null)
        {
            _status = status;
            _delay = delay ?? TimeSpan.Zero;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(_status) { Content = new StringContent("{}") };
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class FakeLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopRequested = true;
    }
}
