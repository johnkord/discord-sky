using System.Text.RegularExpressions;

namespace DiscordSky.Bot.Memory;

/// <summary>
/// Belt-and-suspenders filter: if extracted memory text looks like a prompt-injection instruction
/// rather than a fact about the user, drop it. Checked both at write time (extraction) and
/// at inject time (prompt rendering).
/// See docs/memory_relevance_design.md §6.5.3.
/// </summary>
public static class InstructionShapePolicy
{
    private static readonly Regex InstructionShape = new(
        @"^\s*(always|never|ignore|disregard|forget\s+everything|you\s+must|from\s+now\s+on|as\s+an\s+ai|system\s*:)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsInstructionShaped(string? content) =>
        !string.IsNullOrWhiteSpace(content) && InstructionShape.IsMatch(content);
}
