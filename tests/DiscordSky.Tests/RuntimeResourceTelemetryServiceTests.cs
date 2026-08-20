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
            new RuntimeMemorySample(
                75,
                100,
                ProcessRssBytes: 40,
                ChildProcessRssBytes: 30,
                ChildProcessCount: 2,
                ManagedHeapBytes: 20,
                GcHeapSizeBytes: 22,
                GcFragmentedBytes: 3,
                GcGen0Count: 8,
                GcGen1Count: 4,
                GcGen2Count: 2,
                ThreadCount: 12));
        var service = new RuntimeResourceTelemetryService(
            telemetry,
            Options.Create(new TelemetryOptions()),
            NullLogger<RuntimeResourceTelemetryService>.Instance,
            reader,
            TimeProvider.System,
            () => new RuntimeApplicationResourceSample(17, 31, 42));

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
        Assert.Equal(40, samples[^1].ProcessRssBytes);
        Assert.Equal(30, samples[^1].ChildProcessRssBytes);
        Assert.Equal(2, samples[^1].ChildProcessCount);
        Assert.Equal(20, samples[^1].ManagedHeapBytes);
        Assert.Equal(22, samples[^1].GcHeapSizeBytes);
        Assert.Equal(3, samples[^1].GcFragmentedBytes);
        Assert.Equal(8, samples[^1].GcGen0Count);
        Assert.Equal(4, samples[^1].GcGen1Count);
        Assert.Equal(2, samples[^1].GcGen2Count);
        Assert.Equal(12, samples[^1].ThreadCount);
        Assert.Equal(17, samples[^1].UserMemoryCacheCount);
        Assert.Equal(31, samples[^1].MediaSemanticCacheCount);
        Assert.Equal(42, samples[^1].SentMessageRegistryCount);
    }

    [Fact]
    public void Classify_HandlesMissingLimitAndThresholdEdges()
    {
        Assert.Equal("limit_unavailable", RuntimeResourceTelemetryService.Classify(null));
        Assert.Equal("normal", RuntimeResourceTelemetryService.Classify(0.799));
        Assert.Equal("warning", RuntimeResourceTelemetryService.Classify(0.80));
        Assert.Equal("critical", RuntimeResourceTelemetryService.Classify(0.90));
    }

    [Fact]
    public void Constructor_ClampsProcSamplingToAtLeastOneMinute()
    {
        var service = new RuntimeResourceTelemetryService(
            new InMemoryTelemetrySink(),
            Options.Create(new TelemetryOptions { ResourceSampleInterval = TimeSpan.FromSeconds(1) }),
            NullLogger<RuntimeResourceTelemetryService>.Instance,
            new SequenceMemoryReader(new RuntimeMemorySample(1, 2)),
            TimeProvider.System);

        Assert.Equal(TimeSpan.FromMinutes(1), service.SampleInterval);
    }

    private sealed class SequenceMemoryReader(params RuntimeMemorySample[] samples) : IRuntimeMemoryReader
    {
        private int _index;

        public RuntimeMemorySample Read() => samples[_index++];
    }
}