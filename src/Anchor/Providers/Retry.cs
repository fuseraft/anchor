using System.Net;

namespace Anchor.Providers;

/// <summary>Retries a model request the provider says to try again: rate limits, overload, server errors and dropped connections.</summary>
/// <remarks>A streamed response's status arrives before its first token, so a retry never repeats output the user saw.
/// An error that interrupts a stream after it started is not retried here.</remarks>
public sealed class RetryHandler(Action<string>? onRetry = null, int maxRetries = RetryHandler.DefaultRetries,
    Func<TimeSpan, CancellationToken, Task>? delay = null, HttpMessageHandler? inner = null)
    : DelegatingHandler(inner ?? new SocketsHttpHandler())
{
    public const int DefaultRetries = 6;

    static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // The body is sent again on a retry, so it has to be readable more than once.
        if (request.Content is not null)
            await request.Content.LoadIntoBufferAsync(ct);

        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            string reason;
            try
            {
                response = await base.SendAsync(request, ct);
                if (attempt > maxRetries || !ShouldRetry(response))
                    return response;
                reason = $"{(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
            }
            catch (HttpRequestException e) when (attempt <= maxRetries && !ct.IsCancellationRequested)
            {
                reason = e.Message;
            }

            var wait = Wait(attempt, response);
            response?.Dispose();
            onRetry?.Invoke($"The model provider answered {reason}; retrying in {wait.TotalSeconds:0}s ({attempt}/{maxRetries}).");
            await _delay(wait, ct);
        }
    }

    /// <summary>Anthropic's <c>x-should-retry</c> header decides when present; otherwise the status does.</summary>
    internal static bool ShouldRetry(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-should-retry", out var values) && bool.TryParse(values.FirstOrDefault(), out var should))
            return should;
        return response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests
            || (int)response.StatusCode >= 500;
    }

    /// <summary><c>Retry-After</c> when the provider sends it, else 1s doubling with jitter; at most a minute.</summary>
    internal static TimeSpan Wait(int attempt, HttpResponseMessage? response)
    {
        var after = response?.Headers.RetryAfter switch
        {
            { Delta: { } d } => d,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => (TimeSpan?)null,
        };
        if (after is { } a && a >= TimeSpan.Zero)
            return a < MaxDelay ? a : MaxDelay;
        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1) * (0.75 + Random.Shared.NextDouble() / 2));
        return backoff < MaxDelay ? backoff : MaxDelay;
    }
}
