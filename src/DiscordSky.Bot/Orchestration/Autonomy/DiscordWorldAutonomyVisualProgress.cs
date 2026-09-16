using Discord;
using DiscordSky.Bot.Integrations.Images;

namespace DiscordSky.Bot.Orchestration.Autonomy;

public interface IWorldAutonomyVisualProgress : IAsyncDisposable
{
    Task PreviewAsync(ImagePreview preview, CancellationToken cancellationToken);
    Task<WorldAutonomyDeliveredMessage> CompleteAsync(byte[] bytes, string fileName, string caption, CancellationToken cancellationToken);
}

internal sealed class DiscordWorldAutonomyVisualProgress(IUserMessage message, ulong channelId) : IWorldAutonomyVisualProgress
{
    private bool _completed;

    public Task PreviewAsync(ImagePreview preview, CancellationToken cancellationToken) =>
        UpdateAsync(preview.Bytes, $"preview.{preview.FileExtension}", message.Content, cancellationToken);

    public async Task<WorldAutonomyDeliveredMessage> CompleteAsync(
        byte[] bytes, string fileName, string caption, CancellationToken cancellationToken)
    {
        await UpdateAsync(bytes, fileName, caption, cancellationToken).ConfigureAwait(false);
        _completed = true;
        return new WorldAutonomyDeliveredMessage(message.Id, channelId);
    }

    private async Task UpdateAsync(byte[] bytes, string fileName, string caption, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        await message.ModifyAsync(properties =>
        {
            properties.Content = caption;
            properties.Attachments = new[] { new FileAttachment(stream, fileName) };
            properties.AllowedMentions = AllowedMentions.None;
        }, new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_completed) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await message.DeleteAsync(new RequestOptions { CancelToken = cleanup.Token }).ConfigureAwait(false); }
        catch (Exception) { }
    }
}