using System.Net;

namespace Anchor.Mcp;

/// <summary>
/// Follows redirects itself so configured credentials never reach another origin. .NET's own redirect handling
/// only strips Authorization, so a custom header like X-Api-Key would otherwise follow a cross-origin redirect.
/// </summary>
public sealed class SameOriginRedirects(IEnumerable<string> headerNames, HttpMessageHandler? inner = null)
    : DelegatingHandler(inner ?? new SocketsHttpHandler { AllowAutoRedirect = false })
{
    const int MaxRedirects = 10;

    readonly HashSet<string> _protected = new(headerNames.Concat(["Authorization", "Proxy-Authorization", "Cookie"]), StringComparer.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var origin = Origin(request.RequestUri!);
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var contentHeaders = request.Content?.Headers.ToList();

        var current = request;
        for (var hop = 0; ; hop++)
        {
            var response = await base.SendAsync(current, ct);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                || response.Headers.Location is not { } location || hop >= MaxRedirects)
                return response;

            var target = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (current.RequestUri!.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp)
                return response;

            // 307/308 replay the method and body; the others become a GET, as browsers do.
            var replay = response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            response.Dispose();
            var next = new HttpRequestMessage(replay || current.Method == HttpMethod.Head ? current.Method : HttpMethod.Get, target) { Version = current.Version };
            foreach (var header in current.Headers)
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (replay && body is not null)
            {
                next.Content = new ByteArrayContent(body);
                foreach (var header in contentHeaders!)
                    next.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (Origin(target) != origin)
                foreach (var name in _protected)
                    next.Headers.Remove(name);
            current = next;
        }
    }

    static (string, string, int) Origin(Uri uri) => (uri.Scheme, uri.IdnHost.ToLowerInvariant(), uri.Port);
}
