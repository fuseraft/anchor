namespace Anchor.Core;

/// <summary>
/// The process's connection pools. Each <see cref="HttpClient"/> made here shares one, so making a client for each model,
/// MCP server, setup or update check never leaves a pool of sockets behind. Disposing such a client never closes it.
/// </summary>
public static class Http
{
    // Connections are renewed now and then, so DNS changes are seen.
    static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>The pool that follows redirects itself.</summary>
    public static readonly SocketsHttpHandler Pool = new() { PooledConnectionLifetime = Lifetime };

    /// <summary>The pool that leaves redirects to the handler in front of it, such as MCP's same-origin redirects.</summary>
    public static readonly SocketsHttpHandler NoRedirects = new() { PooledConnectionLifetime = Lifetime, AllowAutoRedirect = false };

    /// <summary>A client on <paramref name="handler"/>, or on <see cref="Pool"/>, whose pool outlives it.</summary>
    public static HttpClient Client(TimeSpan timeout, HttpMessageHandler? handler = null) =>
        new(handler ?? Pool, disposeHandler: false) { Timeout = timeout };
}
