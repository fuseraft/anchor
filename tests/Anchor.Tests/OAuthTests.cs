using System.Net;
using System.Net.Sockets;
using Anchor.Mcp;
using ModelContextProtocol.Authentication;

namespace Anchor.Tests;

public class OAuthTests
{
    static TokenContainer Tokens(string access) => new() { AccessToken = access, TokenType = "Bearer", ObtainedAt = DateTimeOffset.UtcNow };

    [Fact]
    public async Task TokenStore_UsesTheKeychain_AndForgetRemovesIt()
    {
        var keychain = new MemoryKeychain();
        await new TokenStore("docs", "https://docs.example/mcp", keychain).StoreTokensAsync(Tokens("a1"), default);

        Assert.Equal("a1", (await new TokenStore("docs", "https://docs.example/mcp", keychain).GetTokensAsync(default))!.AccessToken);
        Assert.Null(await new TokenStore("docs", "https://other.example/mcp", keychain).GetTokensAsync(default));

        await TokenStore.ForgetAsync("docs", "https://docs.example/mcp", keychain);
        Assert.Empty(keychain.Items);
    }

    [Fact]
    public async Task TokenStore_WithoutAKeychain_KeepsTokensInMemoryOnly()
    {
        var store = new TokenStore("docs", "https://docs.example/mcp", new MemoryKeychain { Broken = true });

        await store.StoreTokensAsync(Tokens("a1"), default);

        Assert.Equal("a1", (await store.GetTokensAsync(default))!.AccessToken);
    }

    static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task BrowserLogin_CatchesTheRedirect()
    {
        var port = FreePort();
        var notices = new List<string>();
        using var http = new HttpClient();
        var login = new BrowserLogin("docs", notices.Add, u => _ = http.GetAsync($"http://localhost:{port}/callback?code=abc&state=xyz"));
        var context = new AuthorizationCallbackContext
        {
            AuthorizationUri = new Uri("https://auth.example/authorize?x=1"),
            RedirectUri = new Uri($"http://localhost:{port}/callback"),
        };

        var result = await login.AuthorizeAsync(context, default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(("abc", "xyz"), (result!.Code, result.State));
        Assert.Contains("https://auth.example/authorize?x=1", Assert.Single(notices));
    }

    [Fact]
    public async Task BrowserLogin_ReportsADeniedSignIn()
    {
        var port = FreePort();
        var notices = new List<string>();
        using var http = new HttpClient();
        var login = new BrowserLogin("docs", notices.Add, u => _ = http.GetAsync($"http://localhost:{port}/callback?error=access_denied"));
        var context = new AuthorizationCallbackContext { AuthorizationUri = new Uri("https://auth.example/a"), RedirectUri = new Uri($"http://localhost:{port}/callback") };

        Assert.Null(await login.AuthorizeAsync(context, default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(notices, n => n.Contains("access_denied"));
    }
}

public class SameOriginRedirectsTests
{
    sealed class Script(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    static HttpResponseMessage Redirect(HttpStatusCode code, string to) => new(code) { Headers = { Location = new Uri(to) } };

    static async Task<Script> Send(string url, HttpMethod method, params HttpResponseMessage[] responses)
    {
        var script = new Script(responses);
        using var client = new HttpClient(new SameOriginRedirects(["X-Api-Key"], script));
        var request = new HttpRequestMessage(method, url) { Content = method == HttpMethod.Post ? new StringContent("{}") : null };
        request.Headers.Add("X-Api-Key", "k");
        request.Headers.Add("Authorization", "Bearer t");
        await client.SendAsync(request);
        return script;
    }

    [Fact]
    public async Task CrossOrigin_DropsCredentials_SameOriginKeepsThem()
    {
        var script = await Send("https://a.example/mcp", HttpMethod.Get,
            Redirect(HttpStatusCode.Found, "https://a.example/mcp/"), Redirect(HttpStatusCode.Found, "https://evil.example/x"),
            Redirect(HttpStatusCode.Found, "https://a.example/back"), new HttpResponseMessage(HttpStatusCode.OK));

        Assert.True(script.Seen[1].Headers.Contains("X-Api-Key"));
        Assert.False(script.Seen[2].Headers.Contains("X-Api-Key"));
        Assert.False(script.Seen[2].Headers.Contains("Authorization"));
        Assert.False(script.Seen[3].Headers.Contains("X-Api-Key"));
    }

    [Fact]
    public async Task HttpsToHttp_IsNotFollowed()
    {
        var script = await Send("https://a.example/mcp", HttpMethod.Get, Redirect(HttpStatusCode.Found, "http://a.example/mcp"));

        Assert.Single(script.Seen);
    }

    [Fact]
    public async Task Status307_ReplaysThePost()
    {
        var script = await Send("https://a.example/mcp", HttpMethod.Post,
            Redirect(HttpStatusCode.TemporaryRedirect, "https://a.example/v2"), new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(HttpMethod.Post, script.Seen[1].Method);
        Assert.Equal("{}", await script.Seen[1].Content!.ReadAsStringAsync());
    }
}
