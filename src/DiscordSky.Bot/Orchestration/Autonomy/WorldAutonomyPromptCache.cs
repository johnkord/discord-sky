using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

#pragma warning disable OPENAI001
#pragma warning disable SCME0001

namespace DiscordSky.Bot.Orchestration.Autonomy;

internal static class WorldAutonomyPromptCache
{
    internal static WorldAutonomyPromptCacheMode SelectMode(
        WorldAutonomyConfiguration configuration, WorldAutonomyRunContext context, string providerName)
    {
        if (configuration.PromptCacheMode == WorldAutonomyPromptCacheMode.Off || configuration.PromptCacheCanaryPercent == 0 ||
            !providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) || !SupportsModel(context.Model))
            return WorldAutonomyPromptCacheMode.Off;
        var cohortKey = "world-autonomy-cache-v1:" + (context.SourceMessageId ?? context.RunId);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(cohortKey));
        return BinaryPrimitives.ReadUInt32BigEndian(digest) % 100 < configuration.PromptCacheCanaryPercent
            ? configuration.PromptCacheMode : WorldAutonomyPromptCacheMode.Off;
    }

    private static bool SupportsModel(string model) => model.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("gpt-6-", StringComparison.OrdinalIgnoreCase) || model.StartsWith("gpt-6.", StringComparison.OrdinalIgnoreCase);

    internal static string Configure(
        ChatOptions options,
        WorldAutonomyRunContext context,
        bool terminalDeliveryEnabled,
        bool useHostedToolSearch = true,
        WorldAutonomyPromptCacheMode mode = WorldAutonomyPromptCacheMode.Explicit)
    {
        if (mode is not (WorldAutonomyPromptCacheMode.Explicit or WorldAutonomyPromptCacheMode.Hybrid))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!SupportsModel(context.Model))
        {
            throw new InvalidOperationException("Explicit world-autonomy prompt caching requires GPT-5.6 or a GPT-6 model.");
        }

        string stablePrefix;
        string dynamicSuffix;
        if (mode == WorldAutonomyPromptCacheMode.Hybrid)
        {
            var instructions = options.Instructions ?? WorldAutonomyPrompt.BuildInstructions(context, terminalDeliveryEnabled, useHostedToolSearch);
            stablePrefix = WorldAutonomyPrompt.BuildFullStableInstructions(context.SourceChannelId.HasValue, terminalDeliveryEnabled, useHostedToolSearch);
            if (!instructions.StartsWith(stablePrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Hybrid caching requires the unchanged Robotnik instruction prefix.");
            dynamicSuffix = instructions[stablePrefix.Length..];
        }
        else
        {
            stablePrefix = WorldAutonomyPrompt.BuildStableCachePrefix(context.SourceChannelId.HasValue, terminalDeliveryEnabled, useHostedToolSearch);
            dynamicSuffix = WorldAutonomyPrompt.BuildDynamicCacheSuffix(context);
        }
        var cacheIdentity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Scope = "world-autonomy",
            Version = 2,
            context.Model,
            context.ManifestDigest,
            StableInstructions = stablePrefix,
        });
        var cacheKey = Convert.ToHexString(SHA256.HashData(cacheIdentity)).ToLowerInvariant();
        options.Instructions = null;
        var factory = new NativeOptionsFactory(cacheKey, stablePrefix, dynamicSuffix, mode);
        options.RawRepresentationFactory = factory.Create;
        return cacheKey;
    }

    internal static IChatClient Wrap(IChatClient client) => new CacheChatClient(client);

    internal static ChatOptions? PrepareContinuationOptions(ChatOptions? options)
    {
        if (string.IsNullOrWhiteSpace(options?.ConversationId) || options.RawRepresentationFactory?.Target is not NativeOptionsFactory factory)
            return options;
        var forwarded = options.Clone();
        forwarded.RawRepresentationFactory = factory.CreateContinuation;
        return forwarded;
    }

    private sealed record NativeOptionsFactory(string Key, string Prefix, string Suffix, WorldAutonomyPromptCacheMode Mode)
    {
        public object Create(IChatClient client) => CreateNativeOptions(Key, Prefix, Suffix, Mode, includeInstructions: true);
        public object CreateContinuation(IChatClient client) => CreateNativeOptions(Key, Prefix, Suffix, Mode, includeInstructions: false);
    }

    private sealed class CacheChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(messages, PrepareContinuationOptions(options), cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetStreamingResponseAsync(messages, PrepareContinuationOptions(options), cancellationToken);
    }

    private static CreateResponseOptions CreateNativeOptions(
        string cacheKey,
        string stablePrefix,
        string dynamicSuffix,
        WorldAutonomyPromptCacheMode mode,
        bool includeInstructions)
    {
        var stablePart = ResponseContentPart.CreateInputTextPart(stablePrefix);
        stablePart.Patch.Set(
            "$.prompt_cache_breakpoint"u8,
            """{"mode":"explicit"}"""u8);
        var native = new CreateResponseOptions();
        native.Patch.Set(
            "$.prompt_cache_key"u8,
            JsonSerializer.SerializeToUtf8Bytes(cacheKey).AsSpan());
        native.Patch.Set(
            "$.prompt_cache_options"u8,
            mode == WorldAutonomyPromptCacheMode.Hybrid
                ? """{"mode":"implicit","ttl":"30m"}"""u8
                : """{"mode":"explicit","ttl":"30m"}"""u8);
        if (includeInstructions)
        {
            native.InputItems.Add(ResponseItem.CreateDeveloperMessageItem([stablePart]));
            native.InputItems.Add(ResponseItem.CreateDeveloperMessageItem(dynamicSuffix));
        }
        return native;
    }
}