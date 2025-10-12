using System.Text.Json;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Integrations.OpenAI;

public static class OpenAiResponseParser
{
    public static string ExtractPrimaryText(OpenAiResponse response)
    {
        if (response.Output is null)
        {
            return string.Empty;
        }

        foreach (var item in response.Output)
        {
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
}
