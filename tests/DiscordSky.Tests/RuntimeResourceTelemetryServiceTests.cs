using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class RuntimeResourceTelemetryServiceTests
{
    [Fact]
    public void RecordSample_EmitsEverySampleButOnlyBandTransitions()
    {
        var telemetry = new InMemoryTelemetrySink();
        var reader = new SequenceMemoryReader(
            new RuntimeMemorySample(70, 100),
            new RuntimeMemorySample(85, 100),
            new RuntimeMemorySample(86, 100),
            new RuntimeMemorySample(95, 100),
            new RuntimeMemorySample(75, 100));
        var service = new RuntimeResourceTelemetryService(
            telemetry,
            Options.Create(new TelemetryOptions()),
            NullLogger<RuntimeResourceTelemetryService>.Instance,
            reader,
            TimeProvider.System);

        for (var index = 0; index < 5; index++)
        {
            service.RecordSample();
        }

        var samples = telemetry.Events.Where(evt => evt.Stage == "sample").ToArray();
        var transitions = telemetry.Events.Where(evt => evt.Stage == "transition").ToArray();
        Assert.Equal(5, samples.Length);
        Assert.Equal(["normal", "warning", "critical", "normal"], transitions.Select(evt => evt.Outcome));
        Assert.Equal("critical", transitions[^1].BaselineOutcome);
        Assert.Equal(0.75, samples[^1].Utilization);
        Assert.Equal(75, samples[^1].MemoryCurrentBytes);
        Assert.Equal(100, samples[^1].MemoryLimitBytes);
    }

    [Fact]
    public void Classify_HandlesMissingLimitAndThresholdEdges()
    {
        Assert.Equal("limit_unavailable", RuntimeResourceTelemetryService.Classify(null));
        Assert.Equal("normal", RuntimeResourceTelemetryService.Classify(0.799));
        Assert.Equal("warning", RuntimeResourceTelemetryService.Classify(0.80));
        Assert.Equal("critical", RuntimeResourceTelemetryService.Classify(0.90));
    }

    private sealed class SequenceMemoryReader(params RuntimeMemorySample[] samples) : IRuntimeMemoryReader
    {
        private int _index;

        public RuntimeMemorySample Read() => samples[_index++];
    }
}