using DiscordSky.Bot.Integrations.LinkUnfurling;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Tests;

public class CompositeUnfurlerTests
{
    private static readonly DateTimeOffset TestTimestamp = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    // ── CanHandle ──────────────────────────────────────────────────────

    [Fact]
    public void CanHandle_DelegatesToChildren()
    {
        var unfurler1 = new FakeUnfurler(url => url.Host == "a.com");
        var unfurler2 = new FakeUnfurler(url => url.Host == "b.com");
        var composite = CreateComposite(unfurler1, unfurler2);

        Assert.True(composite.CanHandle(new Uri("https://a.com/page")));
        Assert.True(composite.CanHandle(new Uri("https://b.com/page")));
        Assert.False(composite.CanHandle(new Uri("https://c.com/page")));
    }

    // ── UnfurlAsync ── basic behavior ──────────────────────────────────

    [Fact]
    public async Task UnfurlAsync_EmptyMessage_ReturnsEmpty()
    {
        var composite = CreateComposite(new FakeUnfurler(_ => true));
        var result = await composite.UnfurlAsync("", TestTimestamp);

        Assert.Empty(result);
    }

    [Fact]
    public async Task UnfurlAsync_NullMessage_ReturnsEmpty()
    {
        var composite = CreateComposite(new FakeUnfurler(_ => true));
        var result = await composite.UnfurlAsync(null!, TestTimestamp);

        Assert.Empty(result);
    }

    // ── UnfurlAsync ── handler chaining ────────────────────────────────

    [Fact]
    public async Task UnfurlAsync_AggregatesFromMultipleHandlers()
    {
        var handler1 = new FakeUnfurler(_ => true, msg =>
        {
            if (msg.Contains("alpha"))
                return new[] { MakeLink("https://alpha.com", "Alpha content") };
            return Array.Empty<UnfurledLink>();
        });

        var handler2 = new FakeUnfurler(_ => true, msg =>
        {
            if (msg.Contains("beta"))
                return new[] { MakeLink("https://beta.com", "Beta content") };
            return Array.Empty<UnfurledLink>();
        });

        var composite = CreateComposite(handler1, handler2);
        var result = await composite.UnfurlAsync("contains alpha and beta links", TestTimestamp);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, l => l.OriginalUrl.Host == "alpha.com");
        Assert.Contains(result, l => l.OriginalUrl.Host == "beta.com");
    }

    [Fact]
    public async Task UnfurlAsync_DeduplicatesAcrossHandlers()
    {
        // Both handlers return a result for the same URL
        var sameLink = MakeLink("https://example.com/page", "Content");
        var handler1 = new FakeUnfurler(_ => true, _ => new[] { sameLink });
        var handler2 = new FakeUnfurler(_ => true, _ => new[] { sameLink });

        var composite = CreateComposite(handler1, handler2);
        var result = await composite.UnfurlAsync("https://example.com/page", TestTimestamp);

        Assert.Single(result);
    }

    [Fact]
    public async Task UnfurlAsync_FirstHandlerPriority()
    {
        // First handler returns a result → second handler's result for same URL is skipped
        var handler1 = new FakeUnfurler(_ => true, _ => new[] { MakeLink("https://example.com", "From handler 1") });
        var handler2 = new FakeUnfurler(_ => true, _ => new[] { MakeLink("https://example.com", "From handler 2") });

        var composite = CreateComposite(handler1, handler2);
        var result = await composite.UnfurlAsync("https://example.com", TestTimestamp);

        Assert.Single(result);
        Assert.Equal("From handler 1", result[0].Text);
    }

    // ── UnfurlAsync ── error handling ──────────────────────────────────

    [Fact]
    public async Task UnfurlAsync_HandlerThrows_ContinuesWithOthers()
    {
        var failingHandler = new FakeUnfurler(_ => true, _ => throw new InvalidOperationException("boom"));
        var workingHandler = new FakeUnfurler(_ => true, _ => new[] { MakeLink("https://ok.com", "OK") });

        var composite = CreateComposite(failingHandler, workingHandler);
        var result = await composite.UnfurlAsync("https://ok.com", TestTimestamp);

        Assert.Single(result);
        Assert.Equal("OK", result[0].Text);
    }

    [Fact]
    public async Task UnfurlAsync_AllHandlersFail_ReturnsEmpty()
    {
        var failingHandler1 = new FakeUnfurler(_ => true, _ => throw new Exception("fail 1"));
        var failingHandler2 = new FakeUnfurler(_ => true, _ => throw new Exception("fail 2"));

        var composite = CreateComposite(failingHandler1, failingHandler2);
        var result = await composite.UnfurlAsync("https://example.com", TestTimestamp);

        Assert.Empty(result);
    }

    // ── UnfurlAsync ── no handlers scenario ───────────────────────────

    [Fact]
    public async Task UnfurlAsync_NoHandlers_ReturnsEmpty()
    {
        var composite = CreateComposite();
        var result = await composite.UnfurlAsync("https://example.com", TestTimestamp);

        Assert.Empty(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static CompositeUnfurler CreateComposite(params ILinkUnfurler[] unfurlers)
    {
        return new CompositeUnfurler(unfurlers, new TestLogger<CompositeUnfurler>());
    }

    private static UnfurledLink MakeLink(string url, string text)
    {
        return new UnfurledLink
        {
            SourceType = "test",
            OriginalUrl = new Uri(url),
            Text = text,
            Author = "test-author",
            Images = Array.Empty<ChannelImage>()
        };
    }

    /// <summary>
    /// Fake unfurler for testing the composite pipeline.
    /// </summary>
    private sealed class FakeUnfurler : ILinkUnfurler
    {
        private readonly Func<Uri, bool> _canHandle;
        private readonly Func<string, IReadOnlyList<UnfurledLink>>? _unfurl;

        public FakeUnfurler(Func<Uri, bool> canHandle, Func<string, IReadOnlyList<UnfurledLink>>? unfurl = null)
        {
            _canHandle = canHandle;
            _unfurl = unfurl;
        }

        public bool CanHandle(Uri url) => _canHandle(url);

        public Task<IReadOnlyList<UnfurledLink>> UnfurlAsync(
            string messageContent,
            DateTimeOffset messageTimestamp,
            CancellationToken cancellationToken = default)
        {
            if (_unfurl == null)
                return Task.FromResult<IReadOnlyList<UnfurledLink>>(Array.Empty<UnfurledLink>());

            return Task.FromResult(_unfurl(messageContent));
        }
    }

    private sealed class TestLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
