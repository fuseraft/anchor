using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using ModelContextProtocol.Authentication;

namespace Anchor.Mcp;

/// <summary>Interactive OAuth 2.1 login: opens the browser and catches the redirect on a loopback listener.</summary>
public sealed class BrowserLogin(string server, Action<string> notify, Action<Uri>? openBrowser = null)
{
    public async Task<AuthorizationResult?> AuthorizeAsync(AuthorizationCallbackContext context, CancellationToken ct)
    {
        var prefix = context.RedirectUri.GetLeftPart(UriPartial.Authority) + "/";
        var listener = new HttpListener();
        using var closer = new Closer(listener);
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException e)
        {
            notify($"MCP server '{server}': can't listen for the sign-in callback on {prefix} ({e.Message}). Set oauth.callbackPort to a free port.");
            return null;
        }

        notify($"MCP server '{server}' needs you to sign in. Opening your browser; if it doesn't open, visit:\n  {context.AuthorizationUri}");
        Open(context.AuthorizationUri);

        // GetContextAsync can't be cancelled directly; stopping the listener unblocks it.
        using var registration = ct.Register(() => { try { listener.Stop(); } catch (ObjectDisposedException) { } });
        HttpListenerContext callback;
        try
        {
            callback = await listener.GetContextAsync();
        }
        catch (Exception e) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Sign-in was cancelled.", e, ct);
        }

        var query = HttpUtility.ParseQueryString(callback.Request.Url?.Query ?? "");
        var error = query["error"];
        Respond(callback.Response, error is null ? "Signed in. You can close this window and return to anchor." : $"Sign-in failed: {HttpUtility.HtmlEncode(error)}");
        if (error is not null || query["code"] is not { Length: > 0 } code)
        {
            notify($"MCP server '{server}': sign-in failed ({error ?? "no authorization code"}).");
            return null;
        }
        return new AuthorizationResult { Code = code, State = query["state"], Iss = query["iss"] };
    }

    void Open(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
            return;
        if (openBrowser is not null)
        {
            openBrowser(url);
            return;
        }
        try
        {
            if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", [url.ToString()]))?.Dispose();
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", [url.ToString()]))?.Dispose();
            else
                Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            notify($"Couldn't open a browser ({e.Message}); open the URL above yourself.");
        }
    }

    static void Respond(HttpListenerResponse response, string message)
    {
        var body = Encoding.UTF8.GetBytes($"<html><body><p>{message}</p></body></html>");
        response.ContentType = "text/html";
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
        response.Close();
    }

    // HttpListener can throw "address already in use" while stopping; by then the outcome is decided, so never let teardown replace it.
    sealed class Closer(HttpListener listener) : IDisposable
    {
        public void Dispose()
        {
            try { if (listener.IsListening) listener.Stop(); } catch (Exception) { }
            try { listener.Close(); } catch (Exception) { }
        }
    }
}

/// <summary>Keeps a server's OAuth tokens in the OS keychain, or in memory when there is none. Never in a plaintext file.</summary>
public sealed class TokenStore(string server, string url, IKeychain keychain) : ITokenCache
{
    readonly string _account = Account(server, url);
    TokenContainer? _memory;

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken ct)
    {
        try
        {
            return await keychain.GetAsync(_account) is { } json ? JsonSerializer.Deserialize<TokenContainer>(json) : _memory;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return _memory;
        }
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken ct)
    {
        _memory = tokens;
        try
        {
            await keychain.SetAsync(_account, JsonSerializer.Serialize(tokens));
        }
        catch (InvalidOperationException)
        {
            // No usable keychain: the in-memory copy lasts for this session.
        }
    }

    public static Task ForgetAsync(string server, string url, IKeychain keychain) => keychain.DeleteAsync(Account(server, url));

    // The URL hash keeps two servers with the same name from sharing a token.
    static string Account(string server, string url)
    {
        var name = new string(server.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return $"mcp-oauth-{name}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLowerInvariant()}";
    }
}

public interface IKeychain
{
    Task<string?> GetAsync(string account);

    Task SetAsync(string account, string secret);

    Task DeleteAsync(string account);
}

/// <summary>The OS credential store: macOS Keychain, libsecret (secret-tool) on Linux, Credential Manager on Windows.</summary>
public static class Keychain
{
    const string Service = "anchor-cli";

    public static IKeychain Default() =>
        OperatingSystem.IsWindows() ? new WindowsKeychain()
        : OperatingSystem.IsMacOS() ? new CliKeychain("security",
            a => ["find-generic-password", "-s", Service, "-a", a, "-w"],
            (a, s) => (["add-generic-password", "-U", "-s", Service, "-a", a, "-w", s], null),
            a => ["delete-generic-password", "-s", Service, "-a", a])
        : new CliKeychain("secret-tool",
            a => ["lookup", "service", Service, "account", a],
            (a, s) => (["store", "--label", $"anchor {a}", "service", Service, "account", a], s),
            a => ["clear", "service", Service, "account", a]);

    sealed class CliKeychain(string tool, Func<string, string[]> get, Func<string, string, (string[] Args, string? Stdin)> set, Func<string, string[]> delete) : IKeychain
    {
        public async Task<string?> GetAsync(string account)
        {
            var (exit, output) = await RunAsync(get(account), null);
            return exit == 0 && output.Trim().Length > 0 ? output.Trim() : null;
        }

        public async Task SetAsync(string account, string secret)
        {
            var (args, stdin) = set(account, secret);
            var (exit, output) = await RunAsync(args, stdin);
            if (exit != 0)
                throw new InvalidOperationException($"{tool} could not store the secret: {output.Trim()}");
        }

        public Task DeleteAsync(string account) => RunAsync(delete(account), null);

        async Task<(int Exit, string Output)> RunAsync(string[] args, string? stdin)
        {
            var psi = new ProcessStartInfo(tool, args) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            Process process;
            try
            {
                process = Process.Start(psi)!;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException($"{tool} is not installed.");
            }
            using (process)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                if (stdin is not null)
                    await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return (process.ExitCode, await stdout + await stderr);
            }
        }
    }

    sealed class WindowsKeychain : IKeychain
    {
        const int Generic = 1;

        public Task<string?> GetAsync(string account)
        {
            if (!CredRead(Target(account), Generic, 0, out var ptr))
                return Task.FromResult<string?>(null);
            try
            {
                var cred = Marshal.PtrToStructure<Credential>(ptr);
                var bytes = new byte[cred.BlobSize];
                Marshal.Copy(cred.Blob, bytes, 0, bytes.Length);
                return Task.FromResult<string?>(Encoding.Unicode.GetString(bytes));
            }
            finally
            {
                CredFree(ptr);
            }
        }

        public Task SetAsync(string account, string secret)
        {
            var blob = Encoding.Unicode.GetBytes(secret);
            var blobPtr = Marshal.AllocHGlobal(blob.Length);
            var target = Marshal.StringToHGlobalUni(Target(account));
            try
            {
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                var cred = new Credential { Type = Generic, TargetName = target, BlobSize = (uint)blob.Length, Blob = blobPtr, Persist = 2 };
                if (!CredWrite(ref cred, 0))
                    throw new InvalidOperationException($"CredWrite failed ({Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                Marshal.FreeHGlobal(blobPtr);
                Marshal.FreeHGlobal(target);
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string account)
        {
            CredDelete(Target(account), Generic, 0);
            return Task.CompletedTask;
        }

        static string Target(string account) => $"{Service}/{account}";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Credential
        {
            public uint Flags;
            public int Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public long LastWritten;
            public uint BlobSize;
            public IntPtr Blob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredWrite(ref Credential credential, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        static extern void CredFree(IntPtr credential);
    }
}
