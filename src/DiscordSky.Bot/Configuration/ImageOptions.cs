namespace DiscordSky.Bot.Configuration;

/// <summary>
/// Configuration for Robotnik's image generation (docs/image_generation_design.md). Bound from the
/// <c>Image:</c> section. Off by default: enabling it spends real money, and the GPT Image models
/// require the OpenAI org to pass API Organization Verification first.
/// </summary>
public sealed class ImageOptions
{
    public const string SectionName = "Image";

    /// <summary>Master switch. When false, the <c>!sky(image)</c> command refuses in character and no API key is read.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Which <c>LLM:Providers</c> block supplies the image API key. Images always go through OpenAI even when the
    /// active chat provider is xAI, so this is resolved independently of <c>LLM:ActiveProvider</c>.
    /// </summary>
    public string ProviderName { get; set; } = "OpenAI";

    /// <summary>GPT Image model. <c>gpt-image-1-mini</c> is cheapest; <c>gpt-image-2</c> is best quality.</summary>
    public string Model { get; set; } = "gpt-image-1-mini";

    /// <summary>Square or portrait/landscape, "WxH". gpt-image supports 1024x1024, 1536x1024, 1024x1536.</summary>
    public string Size { get; set; } = "1024x1024";

    /// <summary>low | medium | high | auto. Defaults low for cost and latency.</summary>
    public string Quality { get; set; } = "low";

    /// <summary>png | jpeg | webp. jpeg is faster and cheaper to ship to Discord.</summary>
    public string OutputFormat { get; set; } = "jpeg";

    /// <summary>auto (stricter, default) | low.</summary>
    public string Moderation { get; set; } = "auto";

    /// <summary>Per-user hourly cap on accepted generations. &lt;= 0 disables the check.</summary>
    public int PerUserPerHour { get; set; } = 2;

    /// <summary>Durable daily cap across all users (counted from the on-disk log so it survives restarts). &lt;= 0 disables.</summary>
    public int GlobalPerDay { get; set; } = 25;

    /// <summary>Max simultaneous generations. A 2-minute operation with no gate is a cost and thread DoS.</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>Hard monthly USD guard summed from the on-disk log. &lt;= 0 disables.</summary>
    public double MonthlyUsdGuard { get; set; } = 20.0;

    /// <summary>When false, a requested <c>high</c> quality is clamped to <c>medium</c>.</summary>
    public bool AllowHighQuality { get; set; } = false;

    /// <summary>
    /// Probability (0..1) that the model-decided <c>generate_image</c> tool is even offered on an ambient
    /// (unprompted) interjection. Keeps spontaneous ambient images a rare surprise. Command and direct-reply
    /// turns always offer the tool regardless of this value.
    /// </summary>
    public double AmbientChance { get; set; } = 0.08;

    /// <summary>Directory for the durable generation log. Should sit on the PVC so the daily cap survives restarts.</summary>
    public string BaseDirectory { get; set; } = Path.Combine("data", "images");

    /// <summary>Days to retain generation-log files. Older files are pruned on startup.</summary>
    public int RetentionDays { get; set; } = 60;
}
