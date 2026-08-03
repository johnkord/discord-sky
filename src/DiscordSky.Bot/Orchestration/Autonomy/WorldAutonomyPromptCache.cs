using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

#pragma warning disable OPENAI001
#pragma warning disable SCME0001

namespace DiscordSky.Bot.Orchestration.Autonomy;

internal static class WorldAutonomyPromptCache
{
    internal static string Configure(
        ChatOptions options,
        WorldAutonomyRunContext context,
        bool terminalDeliveryEnabled)
    {
        if (!context.Model.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Explicit world-autonomy prompt caching requires a GPT-5.6 model.");
        }

        var stablePrefix = WorldAutonomyPrompt.BuildStableCachePrefix(
            context.SourceChannelId.HasValue,
            terminalDeliveryEnabled);
        var dynamicSuffix = WorldAutonomyPrompt.BuildDynamicCacheSuffix(context);
        var prefixDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stablePrefix)))
            .ToLowerInvariant()[..16];
        var cacheKey = $"world-autonomy:{context.Model}:{context.ManifestDigest}:{prefixDigest}";
        options.Instructions = null;
        options.RawRepresentationFactory = _ => CreateNativeOptions(cacheKey, stablePrefix, dynamicSuffix);
        return cacheKey;
    }

    private static CreateResponseOptions CreateNativeOptions(
        string cacheKey,
        string stablePrefix,
        string dynamicSuffix)
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
            """{"mode":"explicit","ttl":"30m"}"""u8);
        native.InputItems.Add(ResponseItem.CreateDeveloperMessageItem([stablePart]));
        native.InputItems.Add(ResponseItem.CreateDeveloperMessageItem(dynamicSuffix));
        return native;
    }
}