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

    /// <summary>GPT Image model. Runtime policy requires <c>gpt-image-2</c> or a newer non-mini generation.</summary>
    public string Model { get; set; } = "gpt-image-2";

    public string EditModel { get; set; } = "gpt-image-2.5-sunburst";

    public string Background { get; set; } = "opaque";

    public int? OutputCompression { get; set; }

    public int PartialImages { get; set; }

    public bool EditingEnabled { get; set; } = true;

    public int MaxReferenceImages { get; set; } = 4;

    public int MaxReferenceBytes { get; set; } = 8 * 1024 * 1024;

    public int MaxTotalReferenceBytes { get; set; } = 16 * 1024 * 1024;

    public int RequestTimeoutMinutes { get; set; } = 5;

    /// <summary>Auto or WIDTHxHEIGHT. Dimensions must satisfy the GPT Image pixel and aspect-ratio limits.</summary>
    public string Size { get; set; } = "1024x1024";

    /// <summary>low | medium | high | xhigh | max | auto. xhigh and max require GPT Image 2.5 or newer.</summary>
    public string Quality { get; set; } = "medium";

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

    /// <summary>When false, high, xhigh, max, and auto quality requests are capped at medium.</summary>
    public bool AllowHighQuality { get; set; } = false;

    /// <summary>Whether the ambient impulse judge may select an image instead of prose.</summary>
    public bool AmbientVisualEnabled { get; set; } = false;

    /// <summary>Minimum visual-worth score required before an unsolicited image can win.</summary>
    public double AmbientVisualWorthThreshold { get; set; } = 0.72;

    /// <summary>When prose also qualifies, visual worth must exceed prose worth by at least this amount.</summary>
    public double AmbientVisualMinLead { get; set; } = 0.05;

    /// <summary>Successful unsolicited images allowed per guild per UTC day. Explicit images are separate.</summary>
    public int AmbientVisualMaxPerGuildPerDay { get; set; } = 1;

    /// <summary>Minimum hours between successful unsolicited images in the same guild.</summary>
    public double AmbientVisualCooldownHours { get; set; } = 6;

    /// <summary>Directory for the durable generation log. Should sit on the PVC so the daily cap survives restarts.</summary>
    public string BaseDirectory { get; set; } = Path.Combine("data", "images");

    /// <summary>Days to retain generation-log files. Older files are pruned on startup.</summary>
    public int RetentionDays { get; set; } = 60;
}
