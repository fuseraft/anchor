using System.Net;
using System.Net.Sockets;
using Anchor.Core;

namespace Anchor.Tests;

public sealed class HttpTests
{
    [Fact]
    public async Task Clients_ShareOneConnection_AndDisposingOneKeepsItOpen()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/";
        var accepted = 0;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var socket = await server.AcceptTcpClientAsync();
                Interlocked.Increment(ref accepted);
                _ = Task.Run(() => ServeAsync(socket));
            }
        });

        // Like /model, sub-agents and MCP reconnects: a new client each time, the old one disposed.
        for (var i = 0; i < 5; i++)
        {
            using var http = Http.Client(TimeSpan.FromSeconds(10));
            Assert.Equal("ok", await http.GetStringAsync(url));
        }

        Assert.Equal(1, accepted);
    }

    // Answers every request on the connection with "ok", keeping it open.
    static async Task ServeAsync(TcpClient socket)
    {
        using var _ = socket;
        var stream = socket.GetStream();
        var buffer = new byte[4096];
        var text = "";
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
                return;
            text += System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            while (text.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var end and >= 0)
            {
                text = text[(end + 4)..];
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray());
            }
        }
    }
}
