using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

/// <summary>
/// Tests for the names-only availability hint emitted by <see cref="CreativeOrchestrator.BuildUserContent"/>.
/// Replaces the prior MemoryBlockRenderingTests once implicit memory injection was removed in favour of
/// the LLM-invoked recall_about_user tool. See docs/recall_tool_design.md §2.3.
/// </summary>
public class MemoryAvailabilityHintTests
{
    private static CreativeOrchestrator BuildOrchestrator(MemoryRelevanceOptions? relevance = null)
    {
        var botOptions = Options.Create(new BotOptions { EnableUserMemory = true, MaxMemoriesPerUser = 20 });
        var llmOptions = new TestOptionsMonitor<LlmOptions>(new LlmOptions
        {
            ActiveProvider = "OpenAI",
            Providers = new Dictionary<string, LlmProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["OpenAI"] = new LlmProviderOptions { ChatModel = "test" }
            }
        });
        var chaos = new TestOptionsMonitor<ChaosSettings>(new ChaosSettings());
        var contextAgg = new ContextAggregator(
            botOptions, new StubLinkUnfurler2(), NullLogger<ContextAggregator>.Instance);
        var safety = new SafetyFilter(chaos, NullLogger<SafetyFilter>.Instance);
        var relevanceMonitor = new TestOptionsMonitor<MemoryRelevanceOptions>(relevance ?? new MemoryRelevanceOptions());
        var scorer = new LexicalMemoryScorer(relevanceMonitor);
        var memoryStore = new InMemoryUserMemoryStore(botOptions, NullLogger<InMemoryUserMemoryStore>.Instance);
        return new CreativeOrchestrator(
            contextAgg, new StubChatClient(), safety,
            llmOptions, botOptions, scorer, relevanceMonitor, memoryStore,
            NullLogger<CreativeOrchestrator>.Instance);
    }

    private static UserMemory Mem(string content, MemoryKind kind = MemoryKind.Factual) =>
        new(content, "pet chatter", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-2), 0, kind);

    [Fact]
    public void NoMemories_OmitsHintEntirely()
    {
        var orch = BuildOrchestrator();
        var request = new CreativeRequest("Weird Al", "tell me a joke", "alice", 1UL, 2UL, null, DateTimeOffset.UtcNow);
        var content = orch.BuildUserContent(request, Array.Empty<ChannelMessage>(), hasTopic: true);
        var text = string.Concat(content.OfType<TextContent>().Select(t => t.Text));
        Assert.DoesNotContain("notes_available_about", text);
    }

    [Fact]
    public void WithAdmissibleMemories_EmitsNamesOnlyHint()
    {
        var orch = BuildOrchestrator();
        var request = new CreativeRequest(
            "Weird Al", "cats are great", "Alice Jones", 42UL, 2UL, null, DateTimeOffset.UtcNow,
            UserMemories: new[] { Mem("has a cat named whiskers"), Mem("likes vancouver") });
        var content = orch.BuildUserContent(request, Array.Empty<ChannelMessage>(), hasTopic: true);
        var text = string.Concat(content.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains("notes_available_about: Alice Jones (user_id=42).", text);
        Assert.Contains("recall_about_user", text);
    }

    [Fact]
    public void HintDoesNotLeakCounts()
    {
        // Critical privacy property: the hint says *whose* notes are available, never *how many*.
        // See docs/recall_tool_design.md §2.3.
        var orch = BuildOrchestrator();
        var request = new CreativeRequest(
            "Weird Al", "ok", "alice", 1UL, 2UL, null, DateTimeOffset.UtcNow,
            UserMemories: new[] { Mem("a"), Mem("b"), Mem("c"), Mem("d"), Mem("e") });
        var content = orch.BuildUserContent(request, Array.Empty<ChannelMessage>(), hasTopic: true);
        var text = string.Concat(content.OfType<TextContent>().Select(t => t.Text));
        // The hint line itself should not contain the count "5".
        var hintLine = text.Split("notes_available_about:")[1].Split('\n')[0];
        Assert.DoesNotContain("5", hintLine);
        Assert.DoesNotContain("facts", text);
    }

    [Fact]
    public void HintDoesNotEmitNoteContent()
    {
        // Implicit injection is gone. The note text must not appear in the prompt.
        var orch = BuildOrchestrator();
        var request = new CreativeRequest(
            "Weird Al", "ok", "alice", 1UL, 2UL, null, DateTimeOffset.UtcNow,
            UserMemories: new[] { Mem("has a cat named whiskers") });
        var content = orch.BuildUserContent(request, Array.Empty<ChannelMessage>(), hasTopic: true);
        var text = string.Concat(content.OfType<TextContent>().Select(t => t.Text));
        Assert.DoesNotContain("whiskers", text);
        Assert.DoesNotContain("background_notes_about_", text);
    }

    [Fact]
    public void OnlyMetaOrSuppressedMemories_OmitsHint()
    {
        // Meta and Suppressed are inadmissible — hint should not advertise them.
        var orch = BuildOrchestrator();
        var request = new CreativeRequest(
            "Weird Al", "ok", "alice", 1UL, 2UL, null, DateTimeOffset.UtcNow,
            UserMemories: new[]
            {
                Mem("prefers short replies", MemoryKind.Meta),
                Mem("don't bring up coffee", MemoryKind.Suppressed),
            });
        var content = orch.BuildUserContent(request, Array.Empty<ChannelMessage>(), hasTopic: true);
        var text = string.Concat(content.OfType<TextContent>().Select(t => t.Text));
        Assert.DoesNotContain("notes_available_about", text);
    }

    private sealed class StubLinkUnfurler2 : DiscordSky.Bot.Integrations.LinkUnfurling.ILinkUnfurler
    {
        public bool CanHandle(Uri url) => false;
        public Task<IReadOnlyList<UnfurledLink>> UnfurlAsync(string content, DateTimeOffset at, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<UnfurledLink>>(Array.Empty<UnfurledLink>());
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
