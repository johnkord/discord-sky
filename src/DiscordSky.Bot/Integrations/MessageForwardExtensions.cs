using System.Text;
using Discord;

namespace DiscordSky.Bot.Integrations;

/// <summary>
/// Reads forwarded-message content. Discord message forwarding puts the forwarded payload in message snapshots
/// (<see cref="IMessage.ForwardedMessages"/>), NOT in <c>Content</c>, so code that only reads <c>Content</c>
/// sees a forwarded scam link or shared post as near-empty. These fold the snapshot text back in, so both the
/// persona context and ScamGuard scan what was actually forwarded.
/// </summary>
public static class MessageForwardExtensions
{
    /// <summary>The message's own content plus any forwarded snapshot content, newline-joined.</summary>
    public static string TextWithForwarded(this IMessage? message)
    {
        if (message is null)
        {
            return string.Empty;
        }

        // ForwardedMessages lives on IUserMessage (message snapshots), not the base IMessage.
        var forwarded = message is IUserMessage userMessage
            ? userMessage.ForwardedMessages?.Select(s => s.Message?.Content)
            : null;
        return Combine(message.Content, forwarded);
    }

    /// <summary>
    /// Pure combiner: the base content plus each non-empty forwarded text, newline-joined. Kept separate from
    /// the Discord types so it is unit-testable.
    /// </summary>
    public static string Combine(string? content, IEnumerable<string?>? forwardedTexts)
    {
        var sb = new StringBuilder((content ?? string.Empty).Trim());
        if (forwardedTexts is not null)
        {
            foreach (var t in forwardedTexts)
            {
                if (string.IsNullOrWhiteSpace(t))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(t.Trim());
            }
        }

        return sb.ToString();
    }
}
