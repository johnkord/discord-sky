namespace DiscordSky.Bot.Orchestration.Empire;

/// <summary>
/// Pure mood math. Mood is two axes in [-1, 1] (valence, arousal) that decay toward a baseline with inertia
/// and quantize into a small readable label. The model never sets the label; it is always derived here, so a
/// hand-edited or model-influenced state can never inject a bogus mood word.
/// </summary>
public static class EmpireMood
{
    public const string Gloating = "gloating";
    public const string Smug = "smug";
    public const string Seething = "seething";
    public const string Sulking = "sulking";
    public const string Scheming = "scheming";

    private const double Threshold = 0.33;

    public static double Clamp(double v) => Math.Clamp(v, -1.0, 1.0);

    /// <summary>Quantizes the two axes into one of the five labels (thresholds at +/-0.33).</summary>
    public static string DeriveLabel(double valence, double arousal)
    {
        var v = Clamp(valence);
        var a = Clamp(arousal);
        if (v >= Threshold && a >= Threshold) return Gloating;
        if (v >= Threshold && a <= -Threshold) return Smug;
        if (v <= -Threshold && a >= Threshold) return Seething;
        if (v <= -Threshold && a <= -Threshold) return Sulking;
        return Scheming;
    }

    /// <summary>Builds a Mood with clamped axes and the correct derived label.</summary>
    public static Mood Make(double valence, double arousal)
    {
        var v = Clamp(valence);
        var a = Clamp(arousal);
        return new Mood(v, a, DeriveLabel(v, a));
    }

    /// <summary>One step of decay toward baseline: next = baseline + (current - baseline) * retain.</summary>
    public static Mood Decay(Mood m, double baselineValence, double baselineArousal, double retain)
    {
        var r = Math.Clamp(retain, 0.0, 1.0);
        var v = baselineValence + (m.Valence - baselineValence) * r;
        var a = baselineArousal + (m.Arousal - baselineArousal) * r;
        return Make(v, a);
    }

    /// <summary>Applies a clamped mood delta (appraisal).</summary>
    public static Mood Nudge(Mood m, double dValence, double dArousal) => Make(m.Valence + dValence, m.Arousal + dArousal);
}
