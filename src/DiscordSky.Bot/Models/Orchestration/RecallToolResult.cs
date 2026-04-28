using System.Text.Json.Serialization;

namespace DiscordSky.Bot.Models.Orchestration;

/// <summary>
/// Result returned by the <c>recall_about_user</c> tool. See docs/recall_tool_design.md §3.1.
/// Always returned as a structured payload to the LLM via a <c>ChatRole.Tool</c> message.
/// </summary>
/// <param name="Notes">Stored notes about the user. Empty list = bot has no admissible notes about this user.</param>
/// <param name="Total">Total admissible notes for this user (may be greater than Notes.Count when truncated).</param>
/// <param name="Truncated">True when there were more admissible notes than the per-call cap.</param>
/// <param name="Note">Free-text guidance for the model. Used to discourage invention when notes is empty,
/// to indicate budget exhaustion, or to flag unknown user_id.</param>
public sealed record RecallToolResult(
    [property: JsonPropertyName("notes")] IReadOnlyList<RecalledNote> Notes,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("note")] string? Note
)
{
    /// <summary>Synthetic result emitted when the per-reply recall budget is exhausted.</summary>
    public static RecallToolResult BudgetExceeded { get; } = new(
        Notes: Array.Empty<RecalledNote>(),
        Total: 0,
        Truncated: false,
        Note: "recall budget exhausted for this reply; do not call recall again, send your message");

    /// <summary>The model passed a user_id that is not a participant in the current conversation.</summary>
    public static RecallToolResult UnknownUser { get; } = new(
        Notes: Array.Empty<RecalledNote>(),
        Total: 0,
        Truncated: false,
        Note: "unknown user_id; only participants of the current conversation can be recalled");

    /// <summary>The user is known, but has no admissible notes stored.</summary>
    public static RecallToolResult NoNotes { get; } = new(
        Notes: Array.Empty<RecalledNote>(),
        Total: 0,
        Truncated: false,
        Note: "no notes stored for this user yet; do not invent");
}

/// <summary>
/// One stored note returned to the LLM via <see cref="RecallToolResult"/>.
/// Mirrors the shape of <see cref="UserMemory"/> minus internal bookkeeping (timestamps, refcount, suppressed flag).
/// </summary>
public sealed record RecalledNote(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("age")] string Age
);
