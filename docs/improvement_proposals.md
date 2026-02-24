# Discord Sky Bot — Improvement Proposals

> Generated from a full codebase deep dive on 2026-02-23.
> Each proposal is self-contained with enough context for an agent to implement it independently.

---

## Table of Contents

1. [Fix Gateway Blocking](#1-fix-gateway-blocking)
2. [Fix TouchMemoriesAsync Bulk Inflation](#2-fix-touchmemoriesasync-bulk-inflation)
3. [Add HttpClient Timeout for Tweet Unfurling](#3-add-httpclient-timeout-for-tweet-unfurling)
4. [Per-User Locking in Memory Stores](#4-per-user-locking-in-memory-stores)
5. [Context-Aware History Sizing](#5-context-aware-history-sizing)
6. [Per-Channel Rate Limiting](#6-per-channel-rate-limiting)
7. [Pluggable Link Unfurler Interface](#7-pluggable-link-unfurler-interface)
8. [Persona State / Notebook](#8-persona-state--notebook)
9. [Semantic Duplicate Detection via Embeddings](#9-semantic-duplicate-detection-via-embeddings)
10. [Mood System](#10-mood-system)
11. [Multi-Bot Conversations](#11-multi-bot-conversations)
12. [Reaction-Based Engagement Feedback](#12-reaction-based-engagement-feedback)
13. ["Previously On" Summaries](#13-previously-on-summaries)
14. [Memory Cross-Referencing](#14-memory-cross-referencing)
15. [Richer Health Checks](#15-richer-health-checks)

---

## 1. Fix Gateway Blocking

**Priority:** Critical
**Effort:** 5 minutes
**Impact:** Prevents Discord disconnects under load

### Problem

`OnMessageReceivedAsync` in `src/DiscordSky.Bot/Bot/DiscordBotService.cs` (line ~121) directly `await`s `ProcessMessageAsync(rawMessage)` on the Discord gateway thread. Every LLM call, fxtwitter HTTP fetch, and reply chain walk blocks Discord.NET from processing heartbeats and other gateway events. The production logs already show the warning: `A MessageReceived handler is blocking the gateway task.` Under sustained load this causes gateway disconnects and missed messages.

### Current Code

```csharp
private async Task OnMessageReceivedAsync(SocketMessage rawMessage)
{
    try
    {
        await ProcessMessageAsync(rawMessage);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Unhandled exception processing message {MessageId}", rawMessage.Id);
        // ... error notification ...
    }
}
```

### Required Change

Offload to a thread pool task so the gateway thread returns immediately. Discord.NET's recommended pattern:

```csharp
private Task OnMessageReceivedAsync(SocketMessage rawMessage)
{
    _ = Task.Run(async () =>
    {
        try
        {
            await ProcessMessageAsync(rawMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing message {MessageId}", rawMessage.Id);
            try
            {
                if (rawMessage.Channel is not null)
                {
                    await rawMessage.Channel.SendMessageAsync("Something went wrong on my end—try again!");
                }
            }
            catch (Exception innerEx)
            {
                _logger.LogDebug(innerEx, "Failed to send error notification to channel");
            }
        }
    });
    return Task.CompletedTask;
}
```

### File to Edit

- `src/DiscordSky.Bot/Bot/DiscordBotService.cs` — method `OnMessageReceivedAsync` (around line 121)

### Testing

The existing test suite does not directly invoke `OnMessageReceivedAsync` — tests call `ProcessMessageAsync` or `BufferMessageForExtraction` directly. This change should not break any tests. Verify manually by watching logs for the absence of the gateway blocking warning.

---

## 2. Fix TouchMemoriesAsync Bulk Inflation

**Priority:** High
**Effort:** 30 minutes
**Impact:** Memory quality — enables meaningful LRU eviction and consolidation priority

### Problem

In `DiscordBotService.cs`, both `HandlePersonaAsync` (line ~300) and `HandleDirectReplyAsync` (line ~392) call:

```csharp
if (userMemories.Count > 0)
{
    _ = _memoryStore.TouchMemoriesAsync(context.User.Id, _shutdownCts.Token);
}
```

`TouchMemoriesAsync` updates `LastReferencedAt` and increments `ReferenceCount` for **every single memory** the user has. This means all memories always have identical timestamps and uniformly inflated reference counts, which completely defeats:

1. **LRU eviction** — the store picks the memory with the oldest `LastReferencedAt` to evict, but they're all the same.
2. **Consolidation priority** — the consolidation prompt tells the LLM that "memories referenced more frequently are generally more important," but they all have equal counts.

### Approach A: Remove TouchMemoriesAsync entirely (simplest)

Delete the two call sites. Memories would only get their timestamps/counts updated when the LLM explicitly calls `update` during extraction. This is sufficient because:
- `SaveMemoryAsync` already sets `LastReferencedAt = now` on creation
- `UpdateMemoryAsync` already sets `LastReferencedAt = now` on update
- New memories naturally float to the top; stale ones naturally sink

### Approach B: Selective touching via model feedback (better but more work)

Add a `referenced_memories` field to the `send_discord_message` tool schema in `CreativeOrchestrator.cs` (line ~28):

```json
"referenced_memories": {
    "type": "array",
    "items": { "type": "integer", "minimum": 0 },
    "description": "Indices of user memories you actually used in your response.",
    "default": []
}
```

Parse this in `TryParseToolCallArguments`, return the indices, and only touch those specific memories. This requires plumbing the indices from `CreativeResult` back to the handler.

### Files to Edit

- `src/DiscordSky.Bot/Bot/DiscordBotService.cs` — remove or modify the two `TouchMemoriesAsync` call sites (~line 302, ~line 394)
- (Approach B only) `src/DiscordSky.Bot/Orchestration/CreativeOrchestrator.cs` — update tool schema, `TryParseToolCallArguments`, and `CreativeResult`
- (Approach B only) `src/DiscordSky.Bot/Models/Orchestration/CreativeModels.cs` — add `ReferencedMemoryIndices` to `CreativeResult`

### Testing

- Existing tests that verify memory operations in `ContextAggregatorTests.cs` and `CreativeOrchestratorTests.cs` should still pass
- For Approach B: add tests to `CreativeOrchestratorTests.cs` verifying that `referenced_memories` is parsed correctly from tool call arguments, and that missing/empty arrays are handled

---

## 3. Add HttpClient Timeout for Tweet Unfurling

**Priority:** High
**Effort:** 2 minutes
**Impact:** Prevents indefinite hangs when fxtwitter API is unresponsive

### Problem

The `TweetUnfurler` HttpClient is registered in `Program.cs` (line ~43) with `AddHttpClient<TweetUnfurler>()` but no timeout is configured. The default `HttpClient.Timeout` is 100 seconds. If fxtwitter hangs, every message containing a tweet URL will block for up to 100 seconds before failing.

### Required Change

In `src/DiscordSky.Bot/Program.cs`, change:

```csharp
builder.Services.AddHttpClient<TweetUnfurler>();
```

to:

```csharp
builder.Services.AddHttpClient<TweetUnfurler>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("User-Agent", "DiscordSkyBot/1.0");
});
```

The 5-second timeout is generous for a simple JSON API. The `User-Agent` header is currently set per-request in `TweetUnfurler.FetchTweetAsync` — moving it here eliminates per-request header allocation. If moved here, remove the per-request `User-Agent` line from `TweetUnfurler.cs` line ~101.

### Files to Edit

- `src/DiscordSky.Bot/Program.cs` — `AddHttpClient<TweetUnfurler>()` call
- `src/DiscordSky.Bot/Integrations/LinkUnfurling/TweetUnfurler.cs` — optionally remove the per-request `User-Agent` header (line ~101)

### Testing

Existing `TweetUnfurlerTests.cs` (27 tests) should all still pass since they mock the HTTP handler.

---

## 4. Per-User Locking in Memory Stores

**Priority:** Medium
**Effort:** 30 minutes
**Impact:** Concurrency — prevents cross-user blocking

### Problem

Both `FileBackedUserMemoryStore` and `InMemoryUserMemoryStore` use a single `private readonly object _lock = new()` for all users. Any memory operation for User A blocks all operations for User B. With concurrent conversations in multiple channels, this creates unnecessary contention.

### Current Code Pattern (both stores)

```csharp
private readonly object _lock = new();

public async Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct)
{
    var memories = await GetOrLoadAsync(userId, ct);
    lock (_lock)
    {
        // ... mutate memories list ...
    }
}
```

### Required Change

Replace the single lock with per-user `SemaphoreSlim`:

```csharp
private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _userLocks = new();

private SemaphoreSlim GetUserLock(ulong userId) =>
    _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
```

Then change each method to:

```csharp
public async Task SaveMemoryAsync(ulong userId, string content, string context, CancellationToken ct)
{
    var memories = await GetOrLoadAsync(userId, ct);
    var userLock = GetUserLock(userId);
    await userLock.WaitAsync(ct);
    try
    {
        // ... mutate memories list ...
    }
    finally
    {
        userLock.Release();
    }
}
```

Note: `FlushAll()` in `FileBackedUserMemoryStore` reads from `_cache` and serializes. It currently uses `lock (_lock)` around the serialization. With per-user locks, it should acquire each user's lock during serialization. This is fine since flushes are infrequent (every 60s).

### Files to Edit

- `src/DiscordSky.Bot/Memory/FileBackedUserMemoryStore.cs` — all methods using `lock (_lock)`, including `FlushAll()`
- `src/DiscordSky.Bot/Memory/InMemoryUserMemoryStore.cs` — all methods using `lock (_lock)`

### Testing

Existing tests should pass without modification since they're single-threaded. Optionally add a concurrency test that performs parallel saves for different users and verifies no data loss.

---

## 5. Context-Aware History Sizing

**Priority:** Medium
**Effort:** 1 hour
**Impact:** Cost savings on LLM token usage

### Problem

`BuildUserContent` in `CreativeOrchestrator.cs` (line ~341) uses the full `context.ChannelHistory` (up to 20 messages) for every invocation type: commands, ambient replies, and direct replies. This is wasteful:

- **Ambient replies** are drive-by comments — they don't need 20 messages of context
- **Commands with an explicit topic** need some context but not as much as topicless invocations
- **Memory extraction** gathers unfurled links and images during history collection, but the extraction prompt is text-only

### Required Changes

#### A. Tiered history limits

Add to `BotOptions.cs`:

```csharp
public int AmbientHistoryLimit { get; init; } = 5;
public int CommandHistoryLimit { get; init; } = 12;
// Existing HistoryMessageLimit (20) becomes the limit for DirectReply
```

In `ContextAggregator.GatherHistoryAsync`, accept a `maxMessages` parameter or use the invocation kind to select the appropriate limit. Alternatively, pass the invocation kind into `BuildContextAsync` and trim accordingly in the orchestrator.

#### B. Skip memory injection for ambient replies

In `DiscordBotService.HandlePersonaAsync`, skip the `_memoryStore.GetMemoriesAsync` call when `invocationKind == CreativeInvocationKind.Ambient`. The ambient system prompt doesn't reference memories, and the response is short — memory context is wasted tokens.

#### C. Skip images for memory extraction

In `DiscordBotService.BufferMessageForExtraction`, only the text content is buffered (no images). But during history gathering for reply/command processing, images and unfurled links are fetched even though memory extraction doesn't use them. This is already correctly separated — just confirming no change needed here.

### Files to Edit

- `src/DiscordSky.Bot/Configuration/BotOptions.cs` — add new config properties
- `src/DiscordSky.Bot/Orchestration/ContextAggregator.cs` — accept/use tiered limits in `GatherHistoryAsync`
- `src/DiscordSky.Bot/Orchestration/CreativeOrchestrator.cs` — optionally pass invocation kind to context aggregator
- `src/DiscordSky.Bot/Bot/DiscordBotService.cs` — skip memory load for ambient invocations

### Testing

- Update `ContextAggregatorTests.cs` to verify different history sizes per invocation kind
- Update `CreativeOrchestratorTests.cs` if the orchestrator drives the sizing

---

## 6. Per-Channel Rate Limiting

**Priority:** Medium
**Effort:** 1 hour
**Impact:** Fairness — prevents one active channel from starving all others

### Problem

`SafetyFilter.ShouldRateLimit` in `src/DiscordSky.Bot/Orchestration/SafetyFilter.cs` uses a single global `Queue<DateTimeOffset>`. The `MaxPromptsPerHour` limit (default: 20) is shared across all channels and users. One busy channel can exhaust the entire quota.

Additionally, the rate limiter fires even when an ambient reply produces an empty response (which happens when the model decides not to say anything). Empty responses waste rate limit budget.

### Required Changes

#### A. Per-channel rate limiting

Change `SafetyFilter` to accept a channel ID:

```csharp
private readonly ConcurrentDictionary<ulong, Queue<DateTimeOffset>> _channelHistory = new();

public bool ShouldRateLimit(DateTimeOffset timestamp, ulong channelId)
{
    var history = _channelHistory.GetOrAdd(channelId, _ => new Queue<DateTimeOffset>());
    lock (history)
    {
        history.Enqueue(timestamp);
        while (history.Count > 0 && timestamp - history.Peek() > TimeSpan.FromHours(1))
            history.Dequeue();

        return history.Count > _settings.MaxPromptsPerHour;
    }
}
```

Add a separate global cap (e.g., `MaxPromptsPerHourGlobal`) for total cross-channel limiting.

#### B. Don't count ambient misses

In `DiscordBotService.ProcessMessageAsync`, the rate limit check happens before the LLM call. Instead, only count against the rate limit when a non-empty response is actually sent. Move the rate limit recording to after `SendChunkedAsync`.

### Files to Edit

- `src/DiscordSky.Bot/Orchestration/SafetyFilter.cs` — refactor to per-channel
- `src/DiscordSky.Bot/Orchestration/CreativeOrchestrator.cs` — pass channel ID to `ShouldRateLimit`
- `src/DiscordSky.Bot/Models/Orchestration/CreativeModels.cs` — `CreativeRequest` already has `ChannelId`
- `src/DiscordSky.Bot/Configuration/ChaosSettings.cs` — optionally add `MaxPromptsPerHourGlobal`

### Testing

- Existing `SafetyFilter` tests (if any) will need updating for the new signature
- Add test cases verifying that different channels have independent limits
- Add test verifying ambient empty responses don't consume budget

---

## 7. Pluggable Link Unfurler Interface

**Priority:** Medium
**Effort:** 2 hours
**Impact:** Extensibility — enables YouTube, Reddit, GitHub, etc.

### Problem

`TweetUnfurler` is the only link unfurler and it's hardcoded in `ContextAggregator` and `DiscordBotService`. Adding support for YouTube transcript extraction, Reddit thread summaries, or GitHub issue context requires either modifying `TweetUnfurler` or duplicating the pattern.

### Design

#### A. Define an interface

```csharp
// src/DiscordSky.Bot/Integrations/LinkUnfurling/ILinkUnfurler.cs
public interface ILinkUnfurler
{
    /// <summary>
    /// Whether this unfurler can handle the given URL.
    /// </summary>
    bool CanHandle(Uri url);

    /// <summary>
    /// Unfurls a single URL into rich content.
    /// </summary>
    Task<UnfurledLink?> UnfurlAsync(Uri url, DateTimeOffset messageTimestamp, CancellationToken ct);
}
```

#### B. Create a composite unfurler

```csharp
// src/DiscordSky.Bot/Integrations/LinkUnfurling/CompositeUnfurler.cs
public sealed class CompositeUnfurler
{
    private readonly IEnumerable<ILinkUnfurler> _unfurlers;
    private readonly ILogger<CompositeUnfurler> _logger;

    public async Task<IReadOnlyList<UnfurledLink>> UnfurlLinksAsync(
        string messageContent, DateTimeOffset timestamp, CancellationToken ct)
    {
        // Extract all URLs from content
        // For each URL, find the first ILinkUnfurler that CanHandle it
        // Call UnfurlAsync in parallel
        // Return results
    }
}
```

#### C. Refactor TweetUnfurler to implement ILinkUnfurler

The existing `TweetUnfurler` becomes one implementation. Its URL regex moves into `CanHandle()`, and `FetchTweetAsync` becomes the implementation of `UnfurlAsync`.

#### D. Register in DI

```csharp
builder.Services.AddHttpClient<TweetUnfurler>();
builder.Services.AddSingleton<ILinkUnfurler, TweetUnfurler>();
// Future: builder.Services.AddSingleton<ILinkUnfurler, YouTubeUnfurler>();
builder.Services.AddSingleton<CompositeUnfurler>();
```

#### E. Update consumers

Replace `TweetUnfurler` injection in `ContextAggregator` and `DiscordBotService` with `CompositeUnfurler`.

### Files to Edit

- Create `src/DiscordSky.Bot/Integrations/LinkUnfurling/ILinkUnfurler.cs`
- Create `src/DiscordSky.Bot/Integrations/LinkUnfurling/CompositeUnfurler.cs`
- Edit `src/DiscordSky.Bot/Integrations/LinkUnfurling/TweetUnfurler.cs` — implement `ILinkUnfurler`
- Edit `src/DiscordSky.Bot/Orchestration/ContextAggregator.cs` — inject `CompositeUnfurler` instead of `TweetUnfurler`
- Edit `src/DiscordSky.Bot/Bot/DiscordBotService.cs` — inject `CompositeUnfurler` instead of `TweetUnfurler`
- Edit `src/DiscordSky.Bot/Program.cs` — update DI registrations
- Edit `tests/DiscordSky.Tests/TweetUnfurlerTests.cs` — update as needed

### Future Unfurlers to Consider

- **YouTube:** Use `youtube-transcript-api` or `yt-dlp --dump-json` for video metadata + auto-generated transcript
- **Reddit:** Use old.reddit.com JSON API (`https://old.reddit.com/{path}.json`) for thread title + top comments
- **GitHub:** Use GitHub REST API for issue/PR title, body, and status
- **Bluesky:** Similar to tweet unfurling, use AT Protocol or a proxy API

---

## 8. Persona State / Notebook

**Priority:** High
**Effort:** 3 hours
**Impact:** Personality continuity across conversations

### Problem

The bot has no memory of what it has said or done in character. The persona cache (`_personaCache` in `DiscordBotService.cs`) only maps message IDs to persona names for reply chain lookups. It's in-memory with a 24-hour TTL and lost on restart.

The bot can see its own messages in channel history, but the system prompt just says "you are roleplaying as X" with zero awareness of character arc, running bits, or past comedy. Each invocation is essentially a fresh start.

### Design

#### A. Persona state model

```csharp
public sealed record PersonaState
{
    public string Persona { get; init; } = string.Empty;
    public ulong ChannelId { get; init; }
    public List<string> RunningBits { get; init; } = new();  // Things the persona has claimed or recurring jokes
    public string? CharacterArc { get; init; }               // Brief description of personality trajectory
    public DateTimeOffset LastUpdated { get; init; }
}
```

#### B. Storage

Use the same `FileBackedUserMemoryStore` pattern — one JSON file per `{channelId}_{persona}.json` in a `data/persona_state` directory. Or piggyback on the existing memory store with a synthetic user ID.

#### C. Post-response update

After the bot sends a response, make a cheap LLM call with:
- The system prompt: "You are a note-taking assistant. Given this persona's response in a Discord channel, update the persona's notebook."
- The current PersonaState (or empty if first time)
- The bot's response text

The LLM returns an updated PersonaState JSON. Write it to disk.

#### D. Prompt injection

In `BuildSystemInstructions` in `CreativeOrchestrator.cs`, if a `PersonaState` exists for this channel+persona, append:

```
=== YOUR NOTEBOOK (things you've said/done before in this channel) ===
Running bits: {comma-separated list}
Character arc: {arc description}
============================================
Draw on these for callbacks, escalation, or dramatic reversals.
```

#### E. Configuration

Add to `BotOptions.cs`:

```csharp
public bool EnablePersonaState { get; init; } = false;  // Off by default
public string PersonaStateModel { get; init; } = "gpt-4.1-mini";  // Use a cheap model
public string PersonaStateDataPath { get; init; } = "data/persona_state";
public int MaxRunningBits { get; init; } = 10;
```

### Files to Create/Edit

- Create `src/DiscordSky.Bot/Models/Orchestration/PersonaState.cs`
- Create `src/DiscordSky.Bot/Orchestration/PersonaStateManager.cs` — load/save/update logic
- Edit `src/DiscordSky.Bot/Orchestration/CreativeOrchestrator.cs` — inject state into `BuildSystemInstructions`, trigger post-response update
- Edit `src/DiscordSky.Bot/Configuration/BotOptions.cs` — add config properties
- Edit `src/DiscordSky.Bot/Program.cs` — register `PersonaStateManager`
- Create `tests/DiscordSky.Tests/PersonaStateTests.cs`

### Cost Considerations

The post-response LLM call should use a cheap/fast model (gpt-4.1-mini) with a low token limit (~200). The notebook is small (a few running bits + one-sentence arc). At $0.40/M input tokens, this adds ~$0.0001 per response.

---

## 9. Semantic Duplicate Detection via Embeddings

**Priority:** Medium
**Effort:** 2 hours
**Impact:** Memory quality — catches semantic duplicates that Jaccard misses

### Problem

`IsDuplicateMemory` in `DiscordBotService.cs` (line ~853) uses Jaccard word-similarity with a 0.7 threshold. This catches near-exact textual duplicates but misses semantic ones:

- `"Works as a software engineer"` vs `"Is a developer at Google"` → Jaccard ≈ 0.1 (not caught)
- `"Has a cat named Whiskers"` vs `"Owns a cat called Whiskers"` → Jaccard ≈ 0.5 (not caught)

### Approach Options

#### A. Embedding-based cosine similarity (recommended)

Use OpenAI's `text-embedding-3-small` model (cheap: $0.02/M tokens, 1536 dimensions) to embed each memory when it's created. Store the embedding vector alongside the memory. Before saving a new memory, embed the candidate and compute cosine similarity against all existing memory embeddings.

**Changes to `UserMemory` model:**

```csharp
public sealed record UserMemory(
    string Content,
    string Context,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastReferencedAt,
    int ReferenceCount,
    float[]? Embedding = null  // nullable for backward compat with existing JSON files
);
```

**New service: `MemoryEmbedder`**

```csharp
public sealed class MemoryEmbedder
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct);
    public static double CosineSimilarity(float[] a, float[] b);
}
```

Register via `Microsoft.Extensions.AI`:

```csharp
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var options = sp.GetRequiredService<IOptions<OpenAIOptions>>().Value;
    return new OpenAIClient(options.ApiKey)
        .GetEmbeddingClient("text-embedding-3-small")
        .AsIEmbeddingGenerator();
});
```

**Modify `IsDuplicateMemory`:**

Keep Jaccard as a fast first-pass filter (free, no API call). If Jaccard doesn't catch it, fall back to embedding similarity with a 0.85 cosine threshold.

#### B. N-gram overlap (cheaper, no API call)

Use character-level trigram or word bigram overlap instead of word unigram Jaccard. This catches more near-paraphrases without any API cost. Less accurate than embeddings but free.

### Files to Edit

- `src/DiscordSky.Bot/Models/Orchestration/CreativeModels.cs` — add `Embedding` to `UserMemory`
- Create `src/DiscordSky.Bot/Integrations/Embeddings/MemoryEmbedder.cs`
- `src/DiscordSky.Bot/Bot/DiscordBotService.cs` — modify `IsDuplicateMemory`, embed new memories before saving
- `src/DiscordSky.Bot/Memory/FileBackedUserMemoryStore.cs` — existing JSON files without embeddings should deserialize with `Embedding = null`
- `src/DiscordSky.Bot/Program.cs` — register embedding client and `MemoryEmbedder`
- `src/DiscordSky.Bot/Configuration/OpenAIOptions.cs` — add `EmbeddingModel` config property

### Backward Compatibility

Existing memory JSON files lack the `Embedding` field. The JSON deserializer will default it to `null`. Old memories without embeddings should be skipped during cosine comparison (fall back to Jaccard-only for those entries). Optionally, add a startup migration that embeds all existing memories.

---

## 10. Mood System

**Priority:** Fun / Experimental
**Effort:** 3 hours
**Impact:** Emergent personality — bot behavior varies with channel energy

### Design

Track a per-channel "mood" float (0.0 = subdued, 1.0 = chaotic) that drifts based on heuristics derived from recent messages — no extra LLM calls needed.

#### Mood signals (cheap to compute)

| Signal | Effect on Mood |
|--------|---------------|
| High message frequency (>10/5 min) | +0.1 |
| Multiple users active | +0.05 per user beyond 2 |
| Emoji-heavy messages (>3 emoji per msg) | +0.05 |
| Short messages (<20 chars average) | +0.05 (rapid-fire banter) |
| Long silence (>30 min gap) | Decay toward 0.5 |
| User says "chill" / "calm down" | -0.2 |
| All caps messages | +0.05 |

#### Mood → behavior mapping

| Mood Range | System Prompt Modifier |
|-----------|----------------------|
| 0.0–0.3 | "You're feeling subdued and contemplative. Keep responses measured and thoughtful." |
| 0.3–0.6 | (neutral, no modifier) |
| 0.6–0.8 | "You're feeling energized and chaotic. Be more unpredictable and commit to bits harder." |
| 0.8–1.0 | "You're absolutely unhinged right now. Maximum chaos. Break the fourth wall. Escalate everything." |

#### Implementation

```csharp
// src/DiscordSky.Bot/Orchestration/MoodTracker.cs
public sealed class MoodTracker
{
    private readonly ConcurrentDictionary<ulong, ChannelMood> _moods = new();

    public double GetMood(ulong channelId);
    public void ObserveMessage(ulong channelId, string content, int activeUserCount);
    public string GetMoodPromptModifier(double mood);
}
```

Inject `MoodTracker` into `CreativeOrchestrator`. Call `ObserveMessage` from `DiscordBotService.ProcessMessageAsync` (cheap, no I/O). Append the mood modifier to `BuildSystemInstructions`.

### Files to Create/Edit

- Create `src/DiscordSky.Bot/Orchestration/MoodTracker.cs`
- Edit `src/DiscordSky.Bot/Orchestration/CreativeOrchestrator.cs` — inject `MoodTracker`, append mood modifier
- Edit `src/DiscordSky.Bot/Bot/DiscordBotService.cs` — call `ObserveMessage` in `ProcessMessageAsync`
- Edit `src/DiscordSky.Bot/Program.cs` — register `MoodTracker`
- Edit `src/DiscordSky.Bot/Configuration/BotOptions.cs` — add `EnableMoodSystem` flag
- Create `tests/DiscordSky.Tests/MoodTrackerTests.cs`

---

## 11. Multi-Bot Conversations

**Priority:** Fun / Low
**Effort:** 2 hours
**Impact:** Entertainment — bots riffing off each other

### Problem

`ProcessMessageAsync` returns immediately if `message.Author.IsBot`. This means if there are multiple bot instances (or other bots) in a channel, they never react to each other.

### Design

Add a config flag `EnableBotInteraction` (default: false) and a list `InteractableBotIds`. When enabled, instead of returning early for bot messages, check if the bot author is in the allowed list.

Gate behind a very low probability (e.g., 5%) to prevent infinite conversation loops. Add a cooldown: if the bot already replied to a bot message in the last N minutes, skip.

### Safeguards

- **Loop prevention:** Never respond to your own messages (already handled). Add a 5-minute cooldown per channel for bot-to-bot interactions.
- **Cost cap:** Bot-to-bot responses use the ambient token budget (small).
- **Kill switch:** The existing `MaxPromptsPerHour` rate limiter naturally caps runaway loops.

### Files to Edit

- `src/DiscordSky.Bot/Configuration/BotOptions.cs` — add `EnableBotInteraction`, `InteractableBotIds`, `BotInteractionChance`, `BotInteractionCooldown`
- `src/DiscordSky.Bot/Bot/DiscordBotService.cs` — modify the `message.Author.IsBot` check in `ProcessMessageAsync`

---

## 12. Reaction-Based Engagement Feedback

**Priority:** Fun / Medium
**Effort:** 3 hours
**Impact:** Lightweight preference learning

### Design

Subscribe to `ReactionAdded` events on the Discord client. When users react to bot messages with positive emoji (👍, 😂, ❤️, 🔥), record the response style and context. When they react negatively (👎, 😬), record that too.

Store a simple tally per channel: `{positive: N, negative: N, positiveExamples: [...]}`. Feed a summary into the system prompt:

```
Users in this channel tend to react positively when you: {brief summary from positiveExamples}.
```

### Implementation Notes

- Requires `GatewayIntents.GuildMessageReactions` intent — must be added to the `DiscordSocketConfig` in `Program.cs`
- Only track reactions on messages where `_personaCache` has an entry (i.e., messages the bot sent)
- Summarize examples periodically using a cheap LLM call (or just keep raw counts)

### Files to Edit

- `src/DiscordSky.Bot/Program.cs` — add `GatewayIntents.GuildMessageReactions`
- `src/DiscordSky.Bot/Bot/DiscordBotService.cs` — subscribe to `_client.ReactionAdded`, implement handler
- Create `src/DiscordSky.Bot/Orchestration/EngagementTracker.cs` — tally management
- `src/DiscordSky.Bot/Orchestration/CreativeOrchestrator.cs` — inject engagement summary into prompt

---

## 13. "Previously On" Summaries

**Priority:** Fun / Low
**Effort:** 1 hour
**Impact:** Continuity flavor text

### Design

When the bot speaks in a channel after a long absence (>2 hours since `BotLastSpokeAt`), prefix the system prompt with awareness of the gap:

```
You haven't said anything in this channel for {duration}. If it feels natural, you can make a callback to what was happening last time — but don't force it.
```

This is already partially possible with the existing `BotLastSpokeAt` field in `ChannelContext`. The system prompt just needs to use it.

### Required Changes

In `CreativeOrchestrator.BuildSystemInstructions`, check `request.Channel?.BotLastSpokeAt`:

```csharp
if (request.Channel?.BotLastSpokeAt is { } lastSpoke)
{
    var gap = request.Timestamp - lastSpoke;
    if (gap.TotalHours >= 2)
    {
        builder.Append($" You haven't spoken in this channel for about {FormatDuration(gap)}.");
        builder.Append(" If it feels natural, make a self-aware callback to your return. Don't force it.");
    }
}
```

### Files to Edit

- `src/DiscordSky.Bot/Orchestration/CreativeOrchestrator.cs` — `BuildSystemInstructions` method

### Testing

Add a test case to `CreativeOrchestratorTests.cs` that verifies the "previously on" text appears when `BotLastSpokeAt` is >2 hours ago.

---

## 14. Memory Cross-Referencing

**Priority:** Medium
**Effort:** 2 hours
**Impact:** Shared world model — bot knows relationships between users

### Problem

When responding to User A, the bot only loads User A's memories. If User B's memories contain `"B is Alice's brother"` or `"B and A both love hiking"`, the bot has no access to this context.

### Design

#### Lightweight approach: shared-mention indexing

When saving a memory that mentions another user's display name, tag it with a `MentionedUserIds` list. When loading memories for User A, also load memories from other users that mention User A.

**Changes to `UserMemory`:**

```csharp
public sealed record UserMemory(
    string Content,
    string Context,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastReferencedAt,
    int ReferenceCount,
    float[]? Embedding = null,
    List<ulong>? MentionedUserIds = null
);
```

**Query pattern:**

```csharp
// When building context for User A:
var directMemories = await _memoryStore.GetMemoriesAsync(userA);
var crossRefMemories = await _memoryStore.GetMemoriesMentioningAsync(userA);
// Inject crossRefMemories under a separate header: "What others have said about {user}:"
```

This requires adding `GetMemoriesMentioningAsync` to `IUserMemoryStore` and scanning all loaded files (or maintaining a reverse index).

### Files to Edit

- `src/DiscordSky.Bot/Models/Orchestration/CreativeModels.cs` — add `MentionedUserIds`
- `src/DiscordSky.Bot/Memory/IUserMemoryStore.cs` — add `GetMemoriesMentioningAsync`
- `src/DiscordSky.Bot/Memory/FileBackedUserMemoryStore.cs` — implement cross-ref query
- `src/DiscordSky.Bot/Memory/InMemoryUserMemoryStore.cs` — implement cross-ref query
- `src/DiscordSky.Bot/Bot/DiscordBotService.cs` — load cross-ref memories alongside direct memories
- `src/DiscordSky.Bot/Orchestration/CreativeOrchestrator.cs` — render cross-ref memories in prompt

### Performance Note

Scanning all user files on every request is expensive. Options:
1. Maintain an in-memory reverse index (`ConcurrentDictionary<ulong, HashSet<ulong>>` mapping mentioned-user → owner-users)
2. Only cross-reference within the current conversation's participants (already known from `participantMemories`)
3. Cache cross-references with a short TTL

---

## 15. Richer Health Checks

**Priority:** Medium
**Effort:** 1 hour
**Impact:** Operational visibility — catch issues before they cascade

### Problem

The health endpoint in `Program.cs` only checks `Discord ConnectionState`. It doesn't verify:
- OpenAI API reachability
- Memory store filesystem writability
- Recent message processing success rate
- Gateway latency

### Design

```csharp
app.MapGet("/healthz", async (
    DiscordSocketClient client,
    IChatClient chatClient,
    IUserMemoryStore memoryStore) =>
{
    var checks = new Dictionary<string, object>();

    // Discord connection
    checks["discord"] = client.ConnectionState == ConnectionState.Connected ? "ok" : client.ConnectionState.ToString();
    checks["discord_latency_ms"] = client.Latency;

    // Memory store filesystem
    try
    {
        var testPath = Path.Combine("data/user_memories", ".healthcheck");
        await File.WriteAllTextAsync(testPath, "ok");
        File.Delete(testPath);
        checks["memory_store"] = "ok";
    }
    catch (Exception ex)
    {
        checks["memory_store"] = $"error: {ex.Message}";
    }

    var allOk = checks.Values.All(v => v.ToString() == "ok" || v is int);
    var status = allOk ? "healthy" : "degraded";

    return allOk
        ? Results.Ok(new { status, checks })
        : Results.Json(new { status, checks }, statusCode: 503);
});
```

Optionally add a `/healthz/deep` endpoint that does a lightweight OpenAI API call (e.g., list models) to verify connectivity. Keep the shallow `/healthz` fast for K8s probes.

### Files to Edit

- `src/DiscordSky.Bot/Program.cs` — replace existing health endpoint

### K8s Impact

The liveness and readiness probes in `k8s/discord-sky/deployment.yaml` already point to `/healthz`. A richer response is backward-compatible — K8s only cares about the HTTP status code.

---

## Architecture Reference

### Key Files Map

| File | Lines | Purpose |
|------|-------|---------|
| `Program.cs` | ~60 | DI composition root, health endpoint |
| `Bot/DiscordBotService.cs` | ~922 | Message routing, memory lifecycle, conversation buffering |
| `Configuration/BotOptions.cs` | ~115 | All bot config with defaults |
| `Configuration/ChaosSettings.cs` | ~25 | Rate limits, ban words, ambient chance |
| `Configuration/OpenAIOptions.cs` | ~17 | API key, model, token limits |
| `Orchestration/CreativeOrchestrator.cs` | ~873 | LLM interaction, prompt building, tool parsing, memory extraction |
| `Orchestration/ContextAggregator.cs` | ~431 | History gathering, reply chains, image collection, link unfurling |
| `Orchestration/SafetyFilter.cs` | ~80 | Rate limiting, ban word scrubbing |
| `Memory/IUserMemoryStore.cs` | ~26 | Memory store interface |
| `Memory/FileBackedUserMemoryStore.cs` | ~290 | Production store with JSON persistence |
| `Memory/InMemoryUserMemoryStore.cs` | ~135 | Test/MVP store |
| `Integrations/LinkUnfurling/TweetUnfurler.cs` | ~223 | fxtwitter API client |
| `Models/Orchestration/CreativeModels.cs` | ~130 | All domain records and enums |

### Message Flow

```
Discord Gateway
  → OnMessageReceivedAsync
    → ProcessMessageAsync
      ├─ Bot check, ban word check, channel allow-list
      ├─ BufferMessageForExtraction (passive, all messages)
      ├─ Direct reply? → HandleDirectReplyAsync
      │   ├─ GatherReplyChainAsync
      │   ├─ Load user memories
      │   ├─ Unfurl links in topic
      │   └─ CreativeOrchestrator.ExecuteAsync
      ├─ Has command prefix? → HandlePersonaAsync
      │   ├─ Parse persona + topic
      │   ├─ Load user memories
      │   ├─ Unfurl links in topic
      │   └─ CreativeOrchestrator.ExecuteAsync
      └─ Ambient roll? → HandlePersonaAsync (same path)

CreativeOrchestrator.ExecuteAsync
  ├─ SafetyFilter.ShouldRateLimit
  ├─ ContextAggregator.BuildContextAsync
  │   ├─ GatherHistoryAsync (channel messages)
  │   └─ UnfurlLinksInMessagesAsync
  ├─ BuildSystemInstructions (persona, reply chain, thread, images, memory)
  ├─ BuildUserContent (metadata, memories, history, images, unfurled links)
  ├─ IChatClient.GetResponseAsync (with retry)
  └─ Parse send_discord_message tool call → CreativeResult

Memory Extraction (passive, decoupled from response path)
  BufferMessageForExtraction
    → debounce timer / hard caps
    → ProcessConversationWindowAsync
      ├─ Load participant memories
      ├─ CreativeOrchestrator.ExtractMemoriesFromConversationAsync
      ├─ Parse update_user_memory tool calls
      ├─ Apply operations (save/update/forget with dedup)
      └─ TryConsolidateUserMemoriesAsync (if at cap)
```

### Test Files

| Test File | Count | Covers |
|-----------|-------|--------|
| `AmbientReplyTests.cs` | — | Ambient reply triggering |
| `ContextAggregatorTests.cs` | — | History gathering, image collection, reply chains |
| `CreativeOrchestratorTests.cs` | — | Prompt building, tool parsing, model routing |
| `OpenAiResponseParserTests.cs` | — | Response parsing (legacy, may be stubs) |
| `TweetUnfurlerTests.cs` | 27 | URL detection, API parsing, edge cases |
| `UnitTest1.cs` | — | Possibly placeholder |
| **Total** | **148** | |

### Dependencies

- **Discord.NET** 3.14.1 — `Discord.WebSocket`, `Discord.Commands`
- **Microsoft.Extensions.AI** 10.3.0 — `IChatClient`, `ChatOptions`, `UriContent`, `FunctionCallContent`
- **OpenAI** (via M.E.AI adapter) — `OpenAIClient.GetChatClient().AsIChatClient()`
- **xUnit** — test framework
- **NSubstitute** — mocking (used in tests)

### Config Hierarchy

```
appsettings.json          ← base defaults (checked into git)
appsettings.Development.json  ← dev overrides (gitignored, has real tokens)
Environment variables     ← K8s configmap + secrets (highest priority)
```

K8s injects config via `envFrom` on both the secret and configmap. Environment variable names use `__` as section separator (e.g., `Bot__EnableUserMemory`).
