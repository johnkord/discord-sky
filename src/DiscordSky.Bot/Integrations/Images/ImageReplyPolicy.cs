namespace DiscordSky.Bot.Integrations.Images;

internal static class ImageReplyPolicy
{
    internal const string TextArtRefusal =
        "Drawings must use create_visual with an OpenAI image model. You may decline in plain text, " +
        "but ASCII, text art, and code-block drawings are not substitutes.";

    internal static bool IsTextArtSubstitute(VisualRequestIntent intent, string text)
    {
        if (intent == VisualRequestIntent.None) return false;
        if (text.Contains("```", StringComparison.Ordinal) || text.Contains("~~~", StringComparison.Ordinal)) return true;

        var drawingLines = 0;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var visible = 0;
            var drawing = 0;
            foreach (var character in line)
            {
                if (char.IsWhiteSpace(character)) continue;
                visible++;
                if ("/\\|_()[]{}<>^~=+*.-".Contains(character) || character is >= '\u2500' and <= '\u259f') drawing++;
            }
            if (drawing >= 3 && drawing * 3 >= visible * 2 && ++drawingLines >= 3) return true;
        }
        return false;
    }
}