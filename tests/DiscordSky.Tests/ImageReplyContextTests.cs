using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Integrations.Images;

namespace DiscordSky.Tests;

public sealed class ImageReplyContextTests
{
    // --- FormatReferencedContext (DiscordBotService): the pure bounding/formatting of the referent ---

    [Fact]
    public void FormatReferencedContext_formats_author_and_trimmed_content()
    {
        Assert.Equal("alice: the rats are back", DiscordBotService.FormatReferencedContext("alice", "  the rats are back  "));
    }

    [Fact]
    public void FormatReferencedContext_is_null_for_empty_or_whitespace_content()
    {
        Assert.Null(DiscordBotService.FormatReferencedContext("alice", "   "));
        Assert.Null(DiscordBotService.FormatReferencedContext("alice", null));
    }

    [Fact]
    public void FormatReferencedContext_falls_back_to_someone_when_author_missing()
    {
        Assert.Equal("someone: hi", DiscordBotService.FormatReferencedContext(null, "hi"));
    }

    [Fact]
    public void FormatReferencedContext_bounds_long_content()
    {
        var result = DiscordBotService.FormatReferencedContext("a", new string('x', 800));
        Assert.NotNull(result);
        Assert.Equal("a: ".Length + 500, result!.Length); // "a: " + 500 chars
    }

    // --- BuildUserMessage (ImageRewriter): reply-context threading and untrusted data-marking ---

    [Fact]
    public void BuildUserMessage_includes_replied_to_referent_as_untrusted_when_present()
    {
        var msg = ImageRewriter.BuildUserMessage("curlyquote", "draw this then bitch", "aaron: the rats are winning");

        Assert.Contains("aaron: the rats are winning", msg); // the referent is present
        Assert.Contains("REPLIED_TO", msg);                  // it is inside the delimited block
        Assert.Contains("untrusted", msg);                   // and marked as untrusted data, not instructions
        Assert.Contains("draw this then bitch", msg);        // the actual request is still there
    }

    [Fact]
    public void BuildUserMessage_omits_replied_to_block_when_no_referent()
    {
        var msg = ImageRewriter.BuildUserMessage("curlyquote", "a golden statue of my face", null);

        Assert.DoesNotContain("REPLIED_TO", msg);
        Assert.Contains("a golden statue of my face", msg);
    }
}
