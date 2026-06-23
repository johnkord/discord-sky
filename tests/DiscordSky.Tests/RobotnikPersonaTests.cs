using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;

namespace DiscordSky.Tests;

/// <summary>
/// Tests for the default Robotnik character module and the per-turn flavor that implements the
/// fun_assessment_2026-06-21 personality pass (3.1 anti-repetition, 3.2 length roulette,
/// 3.3 anti-helpful, 3.6 improv moves, 3.8 ammunition reframe, 3.9 politics dodge).
/// </summary>
public class RobotnikPersonaTests
{
    private sealed class SequenceRandomProvider : IRandomProvider
    {
        private readonly double[] _values;
        private int _i;
        public SequenceRandomProvider(params double[] values) => _values = values.Length == 0 ? [0.0] : values;
        public double NextDouble()
        {
            var v = _values[Math.Min(_i, _values.Length - 1)];
            _i++;
            return v;
        }
    }

    [Theory]
    [InlineData("Robotnik from AOSTH", true)]
    [InlineData("robotnik", true)]
    [InlineData("Weird Al", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Matches_DetectsRobotnik(string? persona, bool expected)
        => Assert.Equal(expected, RobotnikPersona.Matches(persona));

    [Fact]
    public void SystemCore_EncodesHardRules()
    {
        var core = RobotnikPersona.SystemCore;
        Assert.Contains("not here to help", core, StringComparison.OrdinalIgnoreCase);     // 3.3
        Assert.Contains("no real-world politics", core, StringComparison.OrdinalIgnoreCase); // 3.9
        Assert.Contains("twice in a row", core, StringComparison.OrdinalIgnoreCase);       // 3.1 anti-repetition
        Assert.Contains("eggs", core, StringComparison.OrdinalIgnoreCase);                 // bible voice
    }

    [Fact]
    public void RollTurnFlavor_LowRoll_PicksPunchyLine()
    {
        var rng = new SequenceRandomProvider(0.0, 0.0, 0.0);
        var flavor = RobotnikPersona.RollTurnFlavor(rng, CreativeInvocationKind.Ambient);
        Assert.Contains("one punchy line", flavor.LengthDirective);
        Assert.Contains("single punchy line", flavor.EndReminder);
    }

    [Fact]
    public void RollTurnFlavor_HighRoll_AllowsRant()
    {
        var rng = new SequenceRandomProvider(0.99, 0.0, 0.0);
        var flavor = RobotnikPersona.RollTurnFlavor(rng, CreativeInvocationKind.Ambient);
        Assert.Contains("let it rip", flavor.LengthDirective);
    }

    [Fact]
    public void RollTurnFlavor_AlwaysDealsMoveAndPalette()
    {
        var rng = new SequenceRandomProvider(0.5, 0.3, 0.7);
        var flavor = RobotnikPersona.RollTurnFlavor(rng, CreativeInvocationKind.Ambient);
        Assert.Contains("COMEDIC MOVE THIS TURN", flavor.MoveDirective);
        Assert.Contains("Optional inspiration only", flavor.PaletteDirective);
        Assert.False(string.IsNullOrWhiteSpace(flavor.EndReminder));
    }

    [Fact]
    public void PersonaTurnFlavor_None_IsAllEmpty()
    {
        var none = PersonaTurnFlavor.None;
        Assert.Equal(string.Empty, none.LengthDirective);
        Assert.Equal(string.Empty, none.MoveDirective);
        Assert.Equal(string.Empty, none.PaletteDirective);
        Assert.Equal(string.Empty, none.EndReminder);
    }

    [Fact]
    public void ExtractionPrompt_FramesImportanceAsComedicAmmunition()
    {
        var conversation = new List<BufferedMessage> { new(100, "Alice", "test", DateTimeOffset.UtcNow) };
        var participants = new Dictionary<ulong, (string DisplayName, IReadOnlyList<UserMemory> Memories)>
        {
            [100] = ("Alice", new List<UserMemory>())
        };

        var prompt = CreativeOrchestrator.BuildConversationExtractionPrompt(conversation, participants);

        Assert.Contains("COMEDIC AMMUNITION", prompt);                  // 3.8
        Assert.Contains("ammunition for a chaotic villain", prompt);    // 3.7 framing
        Assert.Contains("running bits", prompt);                        // 3.7 running-bit bias
    }
}
