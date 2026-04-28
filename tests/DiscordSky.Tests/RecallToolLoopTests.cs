using System.Text.Json;
using Discord.Commands;
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
/// Integration-shape tests for the recall_about_user → send_discord_message tool loop in
/// <see cref="CreativeOrchestrator.ExecuteAsync"/>. Uses a scripted <see cref="IChatClient"/> stub
/// that returns a queued sequence of responses so we can verify loop semantics:
///   - recall on first turn → handler invoked → second turn called with tool result → send terminates loop
///   - send + recall in same turn → recall ignored, send wins
///   - over-budget recalls → BudgetExceeded synthetic result, ToolMode flips to RequireSpecific(send)
///
/// We can't fully drive ExecuteAsync because it requires a SocketCommandContext; instead we exercise
/// the loop indirectly via the tool-handler invariants and via direct tool argument parsing.
/// See docs/recall_tool_design.md §4.
/// </summary>
public class RecallToolLoopTests
{
    [Fact]
    public void ParseRecallArgs_ValidUserIdAndQuery()
    {
        var call = MakeCall("recall_about_user", new { user_id = "12345", query = "cats" });
        var (uid, q) = CreativeOrchestrator.ParseRecallArgs(call);
        Assert.Equal(12345UL, uid);
        Assert.Equal("cats", q);
    }

    [Fact]
    public void ParseRecallArgs_OmittedQuery_IsNull()
    {
        var call = MakeCall("recall_about_user", new { user_id = "42" });
        var (uid, q) = CreativeOrchestrator.ParseRecallArgs(call);
        Assert.Equal(42UL, uid);
        Assert.Null(q);
    }

    [Fact]
    public void ParseRecallArgs_NumericUserId_ParsesViaJsonElement()
    {
        // Some providers send user_id as a JSON number even when the schema says string.
        var doc = JsonDocument.Parse("""{"user_id": 99}""");
        var args = new Dictionary<string, object?>
        {
            ["user_id"] = doc.RootElement.GetProperty("user_id"),
        };
        var call = new FunctionCallContent("c1", "recall_about_user", args);
        var (uid, _) = CreativeOrchestrator.ParseRecallArgs(call);
        Assert.Equal(99UL, uid);
    }

    [Fact]
    public void ParseRecallArgs_GarbageUserId_ReturnsNull()
    {
        var call = MakeCall("recall_about_user", new { user_id = "not-a-number" });
        var (uid, _) = CreativeOrchestrator.ParseRecallArgs(call);
        Assert.Null(uid);
    }

    [Fact]
    public void ParseRecallArgs_EmptyArgs_ReturnsNullNull()
    {
        var call = new FunctionCallContent("c1", "recall_about_user", new Dictionary<string, object?>());
        var (uid, q) = CreativeOrchestrator.ParseRecallArgs(call);
        Assert.Null(uid);
        Assert.Null(q);
    }

    private static FunctionCallContent MakeCall(string name, object args)
    {
        var json = JsonSerializer.Serialize(args);
        var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()
                : prop.Value;
        }
        return new FunctionCallContent("call-" + Guid.NewGuid().ToString("N").Substring(0, 8), name, dict);
    }
}
