using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace DiscordSky.Bot.Configuration;

/// <summary>Builds the provider-bound chat client used by production and evaluation paths.</summary>
public static class LlmChatClientFactory
{
    public static IChatClient Create(LlmProviderOptions provider, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var timeout = ResolveTimeout(provider.RequestTimeoutMinutes);
        var clientOptions = new OpenAIClientOptions { NetworkTimeout = timeout };
        if (!string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            clientOptions.Endpoint = new Uri(provider.Endpoint);
        }
        var openAiClient = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential(provider.ApiKey),
            clientOptions);

        var client = provider.UseResponsesApi
            ? openAiClient.GetResponsesClient().AsIChatClient(model)
            : openAiClient.GetChatClient(model).AsIChatClient();
        return new TimeoutChatClient(client, timeout);
    }

    internal static TimeSpan ResolveTimeout(int minutes) =>
        TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60));
}

internal sealed class TimeoutChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly TimeSpan _timeout;

    public TimeoutChatClient(IChatClient inner, TimeSpan timeout)
    {
        _inner = inner;
        _timeout = timeout;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            return await _inner.GetResponseAsync(messages, options, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (
            !cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"LLM request exceeded the {_timeout.TotalMinutes:F0}-minute deadline.", ex);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        await using var enumerator = _inner
            .GetStreamingResponseAsync(messages, options, timeoutCts.Token)
            .GetAsyncEnumerator(timeoutCts.Token);

        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException ex) when (
                !cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"LLM request exceeded the {_timeout.TotalMinutes:F0}-minute deadline.", ex);
            }

            if (!hasNext) break;
            yield return enumerator.Current;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(TimeoutChatClient)
            ? this
            : _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}