using System.Text.Json;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Integrations.OpenAI;

public static class OpenAiResponseParser
{
    public sealed record SendDiscordMessageCall(string Mode, string Text, ulong? TargetMessageId, string RawArguments);

    public static string ExtractPrimaryText(OpenAiResponse response)
    {
        if (response.Output is null)
        {
            return string.Empty;
        }

        foreach (var item in response.Output)
        {
            if (item is null)
            {
                continue;
            }
            if (item?.Content is null)
            {
                continue;
            }

            foreach (var content in item.Content)
            {
                if (string.IsNullOrWhiteSpace(content?.Text))
                {
                    continue;
                }

                return content.Text!;
            }
        }

        return string.Empty;
    }

    public static bool TryParseSendDiscordMessageCall(OpenAiResponse response, out SendDiscordMessageCall? payload)
    {
        payload = null;

        if (response.Output is null)
        {
            return false;
        }

        foreach (var item in response.Output)
        {
            if (item is null)
            {
                continue;
            }
            if (item?.Content is null)
            {
                if (IsMatchingFunctionCall(item))
                {
                    var rawArgs = item!.Arguments!;
                    if (TryParseArguments(rawArgs, out var directMode, out var directText, out var directTarget))
                    {
                        payload = new SendDiscordMessageCall(directMode, directText, directTarget, rawArgs);
                        return true;
                    }
                }

                continue;
            }

            foreach (var content in item.Content)
            {
                if (content is null)
                {
                    continue;
                }

                if (!string.Equals(content.Type, "tool_call", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(content.Name, "send_discord_message", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(content.Arguments))
                {
                    continue;
                }

                if (TryParseArguments(content.Arguments, out var mode, out var text, out var targetId))
                {
                    payload = new SendDiscordMessageCall(mode, text, targetId, content.Arguments);
                    return true;
                }
            }
        }

        return false;
    }

    public static bool TryGetJsonDocument(OpenAiResponse response, out JsonDocument? document)
    {
        document = null;
        var text = ExtractPrimaryText(response);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseArguments(string arguments, out string mode, out string text, out ulong? targetMessageId)
    {
        mode = "broadcast";
        text = string.Empty;
        targetMessageId = null;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("mode", out var modeElement) || modeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var candidateMode = modeElement.GetString()?.Trim().ToLowerInvariant();
            if (candidateMode is not ("reply" or "broadcast"))
            {
                return false;
            }

            if (!root.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var candidateText = textElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(candidateText))
            {
                return false;
            }

            ulong? parsedTarget = null;
            if (root.TryGetProperty("target_message_id", out var targetElement))
            {
                switch (targetElement.ValueKind)
                {
                    case JsonValueKind.String when ulong.TryParse(targetElement.GetString(), out var fromString):
                        parsedTarget = fromString;
                        break;
                    case JsonValueKind.Number when targetElement.TryGetUInt64(out var fromNumber):
                        parsedTarget = fromNumber;
                        break;
                    case JsonValueKind.Null:
                        parsedTarget = null;
                        break;
                }
            }

            mode = candidateMode;
            text = candidateText;
            targetMessageId = parsedTarget;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsMatchingFunctionCall(OpenAiResponseOutputItem? item)
    {
        if (item is null)
        {
            return false;
        }

        return string.Equals(item.Type, "function_call", StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Name, OpenAiTooling.SendDiscordMessageToolName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.Arguments);
    }
}
