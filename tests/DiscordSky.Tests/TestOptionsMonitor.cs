using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

/// <summary>
/// A simple <see cref="IOptionsMonitor{TOptions}"/> stub for unit tests.
/// Returns a fixed value and does not fire change notifications.
/// </summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
