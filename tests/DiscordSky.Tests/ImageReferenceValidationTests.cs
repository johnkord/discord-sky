using DiscordSky.Bot.Integrations.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace DiscordSky.Tests;

public sealed class ImageReferenceValidationTests
{
    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/42/100/source.png?ex=123", true)]
    [InlineData("https://media.discordapp.net/attachments/42/100/source.webp", true)]
    [InlineData("https://cdn.discordapp.com/attachments/43/100/source.png", false)]
    [InlineData("https://cdn.discordapp.com.evil.test/attachments/42/100/source.png", false)]
    [InlineData("https://user@cdn.discordapp.com/attachments/42/100/source.png", false)]
    [InlineData("https://127.0.0.1/attachments/42/100/source.png", false)]
    [InlineData("http://cdn.discordapp.com/attachments/42/100/source.png", false)]
    [InlineData("https://cdn.discordapp.com:444/attachments/42/100/source.png", false)]
    [InlineData("file:///attachments/42/100/source.png", false)]
    public void References_AreBoundToDiscordChannelAttachments(string url, bool allowed) =>
        Assert.Equal(allowed, DiscordImageReferenceResolver.IsSafeAttachmentUrl(url, 42));

    [Fact]
    public void Metadata_UsesActualImageEncoding()
    {
        var source = ImageReferenceValidation.Create(Png(16, 16), 123, false);
        Assert.Equal("image/png", source.MediaType);
        Assert.Equal((ulong)123, source.MessageId);
        Assert.Throws<ArgumentException>(() => ImageReferenceValidation.Create([1, 2, 3], 123, false));
    }

    [Fact]
    public void Mask_RequiresAlphaAndMatchingSourceDimensions()
    {
        var source = ImageReferenceValidation.Create(Png(16, 16), 123, false);
        var mask = ImageReferenceValidation.Create(Png(16, 16), 124, true);
        ImageReferenceValidation.ValidateMask(new ImageReferenceSet([source], mask));
        Assert.Throws<ArgumentException>(() => ImageReferenceValidation.Create(Png(16, 16, PngColorType.Rgb), 124, true));
        var wrongSize = ImageReferenceValidation.Create(Png(32, 16), 124, true);
        Assert.Throws<ArgumentException>(() => ImageReferenceValidation.ValidateMask(new ImageReferenceSet([source], wrongSize)));
        Assert.Throws<ArgumentException>(() => ImageReferenceValidation.ValidateMask(new ImageReferenceSet([], mask)));
    }

    private static byte[] Png(int width, int height, PngColorType colorType = PngColorType.RgbWithAlpha)
    {
        using var image = new Image<Rgba32>(width, height);
        using var buffer = new MemoryStream();
        image.Save(buffer, new PngEncoder { ColorType = colorType });
        return buffer.ToArray();
    }
}