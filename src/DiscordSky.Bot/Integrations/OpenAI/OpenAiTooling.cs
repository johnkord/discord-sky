using System;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Integrations.OpenAI;

public static class OpenAiTooling
{
    public const string SendDiscordMessageToolName = "send_discord_message";

    public static OpenAiTool CreateSendDiscordMessageTool()
    {
        var schema = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                mode = new
                {
                    type = "string",
                    @enum = new[] { "reply", "broadcast" }
                },
                target_message_id = new
                {
                    anyOf = new object[]
                    {
                        new { type = "string", pattern = "^[0-9]{1,20}$" },
                        new { type = "null" }
                    }
                },
                text = new
                {
                    type = "string",
                    minLength = 1
                },
                embeds = new
                {
                    type = "array",
                    items = new { type = "object" },
                    @default = Array.Empty<object>()
                },
                components = new
                {
                    type = "array",
                    items = new { type = "object" },
                    @default = Array.Empty<object>()
                }
            },
            required = new[] { "mode", "text" }
        };

        return new OpenAiTool
        {
            Type = "function",
            Name = SendDiscordMessageToolName,
            Description = "Send a Discord message, optionally replying to one of the provided messages.",
            Parameters = schema
        };
    }
}
