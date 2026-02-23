using System.Text.Json;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.AI;

namespace DiscordSky.Tests;

public class ConversationWindowExtractionTests
{
    private static ChatResponse BuildResponseWithToolCalls(params FunctionCallContent[] calls)
    {
        var message = new ChatMessage(ChatRole.Assistant, (IList<AIContent>)calls.Cast<AIContent>().ToList());
        return new ChatResponse(message);
    }

    // ── ParseMultiUserMemoryOperations ──────────────────────────────────

    [Fact]
    public void ParseMultiUserMemoryOperations_SaveAction_ParsesWithUserId()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "123456789",
                ["action"] = "save",
                ["content"] = "Likes pizza",
                ["context"] = "mentioned food preferences"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Single(ops);
        Assert.Equal(123456789UL, ops[0].UserId);
        Assert.Equal(MemoryAction.Save, ops[0].Action);
        Assert.Equal("Likes pizza", ops[0].Content);
        Assert.Equal("mentioned food preferences", ops[0].Context);
        Assert.Null(ops[0].MemoryIndex);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_UpdateAction_ParsesWithIndex()
    {
        var fc = new FunctionCallContent("call-2", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "987654321",
                ["action"] = "update",
                ["memory_index"] = "2",
                ["content"] = "Actually lives in Seattle",
                ["context"] = "correction"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Single(ops);
        Assert.Equal(987654321UL, ops[0].UserId);
        Assert.Equal(MemoryAction.Update, ops[0].Action);
        Assert.Equal(2, ops[0].MemoryIndex);
        Assert.Equal("Actually lives in Seattle", ops[0].Content);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_ForgetAction_ParsesWithIndex()
    {
        var fc = new FunctionCallContent("call-3", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "111222333",
                ["action"] = "forget",
                ["memory_index"] = "0"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Single(ops);
        Assert.Equal(111222333UL, ops[0].UserId);
        Assert.Equal(MemoryAction.Forget, ops[0].Action);
        Assert.Equal(0, ops[0].MemoryIndex);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_MultipleUsersMultipleOps()
    {
        var fc1 = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "100",
                ["action"] = "save",
                ["content"] = "Likes cats",
                ["context"] = "pets"
            });

        var fc2 = new FunctionCallContent("call-2", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "200",
                ["action"] = "save",
                ["content"] = "Works as a teacher",
                ["context"] = "job"
            });

        var fc3 = new FunctionCallContent("call-3", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "100",
                ["action"] = "update",
                ["memory_index"] = "1",
                ["content"] = "Has two cats named Whiskers and Shadow",
                ["context"] = "more detail"
            });

        var response = BuildResponseWithToolCalls(fc1, fc2, fc3);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Equal(3, ops.Count);
        Assert.Equal(2, ops.Count(o => o.UserId == 100));
        Assert.Single(ops.Where(o => o.UserId == 200));
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_MissingUserId_SkipsOperation()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["action"] = "save",
                ["content"] = "Likes pizza"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Empty(ops);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_InvalidUserId_SkipsOperation()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "not-a-number",
                ["action"] = "save",
                ["content"] = "Likes pizza"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Empty(ops);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_SaveWithoutContent_SkipsOperation()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "100",
                ["action"] = "save"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Empty(ops);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_UpdateWithoutIndex_SkipsOperation()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "100",
                ["action"] = "update",
                ["content"] = "Updated fact"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Empty(ops);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_ForgetWithoutIndex_SkipsOperation()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "100",
                ["action"] = "forget"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Empty(ops);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_NoToolCalls_ReturnsEmpty()
    {
        var message = new ChatMessage(ChatRole.Assistant, "Nothing noteworthy in this conversation.");
        var response = new ChatResponse(message);

        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Empty(ops);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_NullContext_DefaultsToEmpty()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "100",
                ["action"] = "save",
                ["content"] = "Has a dog"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Single(ops);
        Assert.Equal(string.Empty, ops[0].Context);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_JsonElementUserId_Parses()
    {
        // Simulate the model returning user_id as a JsonElement (numeric)
        var json = JsonDocument.Parse("{\"val\": 123456789}");
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = json.RootElement.GetProperty("val"),
                ["action"] = "save",
                ["content"] = "Likes hiking"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Single(ops);
        Assert.Equal(123456789UL, ops[0].UserId);
    }

    // ── BuildConversationExtractionPrompt ────────────────────────────────

    [Fact]
    public void BuildConversationExtractionPrompt_SingleUser_IncludesParticipantSection()
    {
        var conversation = new List<BufferedMessage>
        {
            new(100, "Alice", "I just got a new cat!", DateTimeOffset.UtcNow),
            new(100, "Alice", "Her name is Luna", DateTimeOffset.UtcNow.AddSeconds(30))
        };

        var participants = new Dictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)>
        {
            [100] = ("Alice", new List<UserMemory>())
        };

        var prompt = CreativeOrchestrator.BuildConversationExtractionPrompt(conversation, participants);

        Assert.Contains("Alice (ID:100)", prompt);
        Assert.Contains("No existing memories", prompt);
        Assert.Contains("conversation", prompt.ToLowerInvariant());
    }

    [Fact]
    public void BuildConversationExtractionPrompt_MultipleUsers_IncludesAllParticipants()
    {
        var conversation = new List<BufferedMessage>
        {
            new(100, "Alice", "Hey Bob, how's the project?", DateTimeOffset.UtcNow),
            new(200, "Bob", "Going well! Almost done.", DateTimeOffset.UtcNow.AddSeconds(10)),
            new(300, "Charlie", "Nice work team!", DateTimeOffset.UtcNow.AddSeconds(20))
        };

        var participants = new Dictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)>
        {
            [100] = ("Alice", new List<UserMemory>()),
            [200] = ("Bob", new List<UserMemory> { new("Works on Project X", "work", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0) }),
            [300] = ("Charlie", new List<UserMemory>())
        };

        var prompt = CreativeOrchestrator.BuildConversationExtractionPrompt(conversation, participants);

        Assert.Contains("Alice (ID:100)", prompt);
        Assert.Contains("Bob (ID:200)", prompt);
        Assert.Contains("Charlie (ID:300)", prompt);
        Assert.Contains("[0] Works on Project X", prompt);
        Assert.Contains("Existing memories:", prompt);
    }

    [Fact]
    public void BuildConversationExtractionPrompt_WithExistingMemories_IncludesIndexedList()
    {
        var conversation = new List<BufferedMessage>
        {
            new(100, "Alice", "I moved to New York!", DateTimeOffset.UtcNow)
        };

        var participants = new Dictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)>
        {
            [100] = ("Alice", new List<UserMemory>
            {
                new("Lives in California", "location", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0),
                new("Likes hiking", "hobbies", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1)
            })
        };

        var prompt = CreativeOrchestrator.BuildConversationExtractionPrompt(conversation, participants);

        Assert.Contains("[0] Lives in California", prompt);
        Assert.Contains("[1] Likes hiking", prompt);
    }

    [Fact]
    public void BuildConversationExtractionPrompt_ContainsMultiUserGuidance()
    {
        var conversation = new List<BufferedMessage>
        {
            new(100, "Alice", "My brother Bob just got promoted!", DateTimeOffset.UtcNow)
        };

        var participants = new Dictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)>
        {
            [100] = ("Alice", new List<UserMemory>())
        };

        var prompt = CreativeOrchestrator.BuildConversationExtractionPrompt(conversation, participants);

        Assert.Contains("user_id", prompt);
        Assert.Contains("Relationships between users", prompt);
        Assert.Contains("ABOUT", prompt); // "attribute the memory to the person the fact is ABOUT"
        Assert.Contains("SAVE things like", prompt);
        Assert.Contains("DO NOT SAVE", prompt);
    }

    [Fact]
    public void BuildConversationExtractionPrompt_MentionsThirdPartyFacts()
    {
        var conversation = new List<BufferedMessage>
        {
            new(100, "Alice", "Did you hear Bob got a dog?", DateTimeOffset.UtcNow)
        };

        var participants = new Dictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)>
        {
            [100] = ("Alice", new List<UserMemory>())
        };

        var prompt = CreativeOrchestrator.BuildConversationExtractionPrompt(conversation, participants);

        // Should mention extracting facts about OTHER users
        Assert.Contains("OTHER user", prompt);
    }

    // ── BufferedMessage and ChannelMessageBuffer ────────────────────────

    [Fact]
    public void BufferedMessage_RecordEquality()
    {
        var now = DateTimeOffset.UtcNow;
        var msg1 = new BufferedMessage(100, "Alice", "Hello!", now);
        var msg2 = new BufferedMessage(100, "Alice", "Hello!", now);

        Assert.Equal(msg1, msg2);
    }

    [Fact]
    public void MultiUserMemoryOperation_RecordEquality()
    {
        var op1 = new MultiUserMemoryOperation(100, MemoryAction.Save, null, "Likes cats", "pets");
        var op2 = new MultiUserMemoryOperation(100, MemoryAction.Save, null, "Likes cats", "pets");

        Assert.Equal(op1, op2);
    }

    [Fact]
    public void ChannelMessageBuffer_InitializesEmpty()
    {
        var buffer = new ChannelMessageBuffer();

        Assert.Empty(buffer.Messages);
        Assert.Null(buffer.DebounceTimer);
    }

    // ── Additional parser edge cases ────────────────────────────────────

    [Fact]
    public void ParseMultiUserMemoryOperations_NullArguments_SkipsOperation()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName);

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Empty(ops);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_MissingAction_SkipsOperation()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "100",
                ["content"] = "orphaned content"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Empty(ops);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_InvalidAction_SkipsOperation()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "100",
                ["action"] = "destroy",
                ["content"] = "should not parse"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Empty(ops);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_CaseInsensitiveAction()
    {
        var fc = new FunctionCallContent("call-1", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "100",
                ["action"] = "SAVE",
                ["content"] = "uppercase action test"
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Single(ops);
        Assert.Equal(MemoryAction.Save, ops[0].Action);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_IgnoresNonMemoryTools()
    {
        var sendMsg = new FunctionCallContent("call-x", CreativeOrchestrator.SendDiscordMessageToolName,
            new Dictionary<string, object?>
            {
                ["mode"] = "broadcast",
                ["text"] = "Hello"
            });
        var saveMem = new FunctionCallContent("call-y", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = "100",
                ["action"] = "save",
                ["content"] = "Fact",
                ["context"] = "ctx"
            });

        var response = BuildResponseWithToolCalls(sendMsg, saveMem);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Single(ops);
        Assert.Equal(MemoryAction.Save, ops[0].Action);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_EmptyResponse_ReturnsEmpty()
    {
        var response = new ChatResponse();
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);
        Assert.Empty(ops);
    }

    [Fact]
    public void ParseMultiUserMemoryOperations_JsonElementAction_Parses()
    {
        // Simulate M.E.AI deserialization returning JsonElement values
        var json = JsonDocument.Parse("""{"action":"save","content":"JSON fact","context":"json ctx","user_id":"100"}""");
        var root = json.RootElement;

        var fc = new FunctionCallContent("call-j", CreativeOrchestrator.UpdateUserMemoryConversationToolName,
            new Dictionary<string, object?>
            {
                ["user_id"] = root.GetProperty("user_id"),
                ["action"] = root.GetProperty("action"),
                ["content"] = root.GetProperty("content"),
                ["context"] = root.GetProperty("context")
            });

        var response = BuildResponseWithToolCalls(fc);
        var ops = CreativeOrchestrator.ParseMultiUserMemoryOperations(response);

        Assert.Single(ops);
        Assert.Equal(100UL, ops[0].UserId);
        Assert.Equal("JSON fact", ops[0].Content);
    }

    // ── Prompt consolidation guidance ───────────────────────────────────

    [Fact]
    public void BuildConversationExtractionPrompt_ContainsConsolidationGuidance()
    {
        var conversation = new List<BufferedMessage>
        {
            new(100, "Alice", "test", DateTimeOffset.UtcNow)
        };

        var participants = new Dictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)>
        {
            [100] = ("Alice", new List<UserMemory>())
        };

        var prompt = CreativeOrchestrator.BuildConversationExtractionPrompt(conversation, participants);

        Assert.Contains("at most 5 facts per user", prompt);
        Assert.Contains("combine them into a single consolidated fact", prompt);
        Assert.Contains("Only use user_id values from the PARTICIPANTS list", prompt);
    }
}
