namespace DiscordSky.Bot.Orchestration.Impulse;

public enum AmbientActionKind
{
    Silence,
    Text,
    Image,
}

public static class AmbientActionArbiter
{
    public static AmbientActionKind Choose(
        bool useWorthGate,
        WorthVerdict? verdict,
        double textThreshold,
        bool visualEnabled,
        double visualThreshold,
        double visualMinLead)
    {
        if (!useWorthGate || verdict is null) return AmbientActionKind.Text;

        var textEligible = verdict.Worth >= Math.Clamp(textThreshold, 0.0, 1.0);
        var imageEligible = visualEnabled
            && verdict.VisualWorth >= Math.Clamp(visualThreshold, 0.0, 1.0);
        if (imageEligible
            && (!textEligible || verdict.VisualWorth >= verdict.Worth + Math.Max(0.0, visualMinLead)))
        {
            return AmbientActionKind.Image;
        }

        return textEligible ? AmbientActionKind.Text : AmbientActionKind.Silence;
    }

    public static AmbientActionKind FallbackAfterImageVeto(WorthVerdict verdict, double textThreshold) =>
        verdict.Worth >= Math.Clamp(textThreshold, 0.0, 1.0)
            ? AmbientActionKind.Text
            : AmbientActionKind.Silence;
}