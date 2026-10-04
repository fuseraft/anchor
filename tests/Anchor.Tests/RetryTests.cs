using System.Net;
using System.Net.Http.Headers;
using Anchor.Providers;

namespace Anchor.Tests;

public class RetryTests
{
    /// <summary>Answers each request with the next scripted response, or throws the scripted exception.</summary>
    sealed class Script(params Func<HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        readonly Queue<Func<HttpResponseMessage>> _steps = new(steps);

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return _steps.Dequeue()();
        }
    }

    static Func<HttpResponseMessage> Status(int code, Action<HttpResponseMessage>? configure = null) => () =>
    {
        var response = new HttpResponseMessage((HttpStatusCode)code);
        configure?.Invoke(response);
        return response;
    };

    static (HttpClient Client, List<TimeSpan> Waits, List<string> Notices) Build(Script script, int maxRetries = RetryHandler.DefaultRetries)
    {
        List<TimeSpan> waits = [];
        List<string> notices = [];
        var handler = new RetryHandler(notices.Add, maxRetries, (d, _) => { waits.Add(d); return Task.CompletedTask; }, script);
        return (new HttpClient(handler), waits, notices);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(408)]
    public async Task RetriesTransientStatuses_AndResendsTheBody(int code)
    {
        var script = new Script(Status(code), Status(200));
        var (client, waits, notices) = Build(script);

        using var response = await client.PostAsync("http://model.test/v1/messages", new StringContent("{\"hi\":1}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["{\"hi\":1}", "{\"hi\":1}"], script.Bodies);
        Assert.Single(waits);
        Assert.Contains(code.ToString(), Assert.Single(notices));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(413)]
    public async Task DoesNotRetryClientErrors(int code)
    {
        var (client, waits, _) = Build(new Script(Status(code)));

        using var response = await client.PostAsync("http://model.test/", new StringContent("x"));

        Assert.Equal(code, (int)response.StatusCode);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task ShouldRetryHeader_Overrides()
    {
        var (client, waits, _) = Build(new Script(
            Status(400, r => r.Headers.Add("x-should-retry", "true")),
            Status(503, r => r.Headers.Add("x-should-retry", "false"))));

        using var response = await client.PostAsync("http://model.test/", new StringContent("x"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(waits);
    }

    [Fact]
    public async Task HonorsRetryAfter_CappedAtAMinute()
    {
        var (client, waits, _) = Build(new Script(
            Status(429, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7))),
            Status(429, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1))),
            Status(200)));

        using var response = await client.PostAsync("http://model.test/", new StringContent("x"));

        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(60)], waits);
    }

    [Fact]
    public async Task GivesUpAfterMaxRetries_WithTheLastResponse()
    {
        var (client, waits, _) = Build(new Script(Status(529), Status(529), Status(529)), maxRetries: 2);

        using var response = await client.PostAsync("http://model.test/", new StringContent("x"));

        Assert.Equal(529, (int)response.StatusCode);
        Assert.Equal(2, waits.Count);
        Assert.True(waits[1] > waits[0], "backoff grows");
    }

    [Fact]
    public async Task RetriesDroppedConnections_ThenRethrows()
    {
        Func<HttpResponseMessage> drop = () => throw new HttpRequestException("Connection reset");
        var (client, waits, _) = Build(new Script(drop, drop), maxRetries: 1);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("http://model.test/", new StringContent("x")));
        Assert.Single(waits);
    }

    [Fact]
    public async Task CancellingDuringAWait_Stops()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RetryHandler(null, 3, (d, ct) => { cts.Cancel(); return Task.Delay(d, ct); }, new Script(Status(529), Status(200)));
        var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostAsync("http://model.test/", new StringContent("x"), cts.Token));
    }
}
