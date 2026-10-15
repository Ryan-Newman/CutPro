using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CutPro;

/// <summary>A platform connection: the user's own developer-app credentials, OAuth tokens and what the platform told us
/// (account name, Page / Instagram account / board ids), plus publishing preferences (auto-upload, field defaults).</summary>
public sealed class Account
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? Expires { get; set; }
    public string? UserName { get; set; }
    public Dictionary<string, string> Extra { get; set; } = new();
    public bool AutoUpload { get; set; }
    public Dictionary<string, string> Defaults { get; set; } = new();
    public bool Connected => !string.IsNullOrEmpty(AccessToken);
}

/// <summary>Connections for every platform, stored in %APPDATA%\CutPro\accounts.dat encrypted with Windows DPAPI (only this
/// Windows user can read it). OAuth 2.0 authorization-code flow with PKCE in the user's browser; the redirect comes back to
/// a one-shot listener on 127.0.0.1:53682 / localhost.</summary>
public static class Accounts
{
    public const int Port = 53682;
    /// <summary>Opens the sign-in page (tests replace it with a scripted "browser").</summary>
    public static Action<string> OpenBrowser = url => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    public static string Redirect(PlatformDef p) => $"http://{(p.Auth?.AuthorizeUrl.Contains("facebook.com") == true ? "localhost" : "127.0.0.1")}:{Port}/callback";
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CutPro", "accounts.dat");
    static Dictionary<string, Account>? all;
    /// <summary>Tests: a folder for the accounts file instead of %APPDATA%.</summary>
    public static string? StoreOverride;
    static string Store => StoreOverride != null ? Path.Combine(StoreOverride, "accounts.dat") : FilePath;

    public static Dictionary<string, Account> All
    {
        get
        {
            if (all != null) return all;
            try { all = File.Exists(Store) ? JsonSerializer.Deserialize<Dictionary<string, Account>>(Dpapi.Unprotect(File.ReadAllBytes(Store))) : null; }
            catch { all = null; }
            return all ??= new();
        }
    }
    public static Account Get(string id) { if (!All.TryGetValue(id, out var a)) All[id] = a = new Account(); return a; }
    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
        File.WriteAllBytes(Store, Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(All)));
    }
    public static void Reload() => all = null;
    public static void Disconnect(string id)
    {
        var a = Get(id);
        a.AccessToken = a.RefreshToken = a.UserName = null; a.Expires = null; a.Extra.Clear();
        Save();
    }

    // ---------------- OAuth ----------------
    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Opens the platform's sign-in page and waits (up to 5 minutes) for the redirect. Then stores the tokens and
    /// fetches the account details the uploads need.</summary>
    public static async Task ConnectAsync(PlatformDef p, CancellationToken ct)
    {
        var a = Get(p.Id);
        if (p.Auth is not { } auth) throw new InvalidOperationException($"{p.Name} needs no connection.");
        if (string.IsNullOrWhiteSpace(a.ClientId)) throw new InvalidOperationException($"Enter your {p.Name} app's Client ID first ({p.Setup})");
        string verifier = B64Url(RandomNumberGenerator.GetBytes(48)), state = B64Url(RandomNumberGenerator.GetBytes(16));
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        string challenge = auth.HexChallenge ? Convert.ToHexString(hash).ToLowerInvariant() : B64Url(hash);
        bool meta = auth.AuthorizeUrl.Contains("facebook.com"), pkce = !meta && !auth.AuthorizeUrl.Contains("linkedin.com");
        var q = new List<(string, string)>
        {
            (auth.ClientIdParam, a.ClientId), ("redirect_uri", Redirect(p)), ("response_type", "code"), ("state", state),
            ("scope", auth.Scopes),
        };
        if (pkce) { q.Add(("code_challenge", challenge)); q.Add(("code_challenge_method", "S256")); }
        if (auth.AuthorizeUrl.Contains("google.com")) { q.Add(("access_type", "offline")); q.Add(("prompt", "consent")); }
        string url = auth.AuthorizeUrl + "?" + string.Join("&", q.Select(x => $"{x.Item1}={Uri.EscapeDataString(x.Item2)}"));
        var wait = WaitForCodeAsync(state, ct);
        OpenBrowser(url);
        string code = await wait;
        // code → tokens
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", Redirect(p)),
            new(auth.ClientIdParam, a.ClientId),
        };
        if (!auth.BasicAuthToken && !string.IsNullOrEmpty(a.ClientSecret)) form.Add(new("client_secret", a.ClientSecret));
        if (pkce) form.Add(new("code_verifier", verifier));
        // Meta: GET with the parameters in the query (client_secret is already in the form)
        using var req = new HttpRequestMessage(meta ? HttpMethod.Get : HttpMethod.Post, Http.Url(auth.TokenUrl) + (meta ? "?" + await new FormUrlEncodedContent(form).ReadAsStringAsync(ct) : ""));
        if (!meta) req.Content = new FormUrlEncodedContent(form);
        if (auth.BasicAuthToken && !string.IsNullOrEmpty(a.ClientSecret))
            req.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{a.ClientId}:{a.ClientSecret}")));
        var tok = await Http.SendJsonAsync(req, ct);
        var t = tok["data"] is JsonObject d && p.Id == "tiktok" ? d : tok;      // (TikTok wraps some responses)
        a.AccessToken = (string?)t["access_token"] ?? throw new Exception($"{p.Name} sign-in didn't return a token: {tok.ToJsonString()}");
        a.RefreshToken = (string?)t["refresh_token"] ?? a.RefreshToken;
        a.Expires = t["expires_in"] is JsonNode e && double.TryParse(e.ToString(), out var secs) ? DateTime.UtcNow.AddSeconds(secs - 60) : null;
        if (meta) await MetaLongLivedAsync(p, a, ct);
        await Profile.FetchAsync(p, a, ct);
        Save();
    }

    /// <summary>A valid access token (refreshed when it has expired).</summary>
    public static async Task<string> TokenAsync(PlatformDef p, CancellationToken ct)
    {
        var a = Get(p.Id);
        if (!a.Connected) throw new InvalidOperationException($"Connect {p.Name} first (Publish → Accounts).");
        if (a.Expires is DateTime ex && ex < DateTime.UtcNow && a.RefreshToken != null && p.Auth is { } auth && !auth.AuthorizeUrl.Contains("facebook.com"))
        {
            var form = new List<KeyValuePair<string, string>> { new("grant_type", "refresh_token"), new("refresh_token", a.RefreshToken), new(auth.ClientIdParam, a.ClientId) };
            if (!auth.BasicAuthToken && !string.IsNullOrEmpty(a.ClientSecret)) form.Add(new("client_secret", a.ClientSecret));
            using var req = new HttpRequestMessage(HttpMethod.Post, Http.Url(auth.TokenUrl)) { Content = new FormUrlEncodedContent(form) };
            if (auth.BasicAuthToken && !string.IsNullOrEmpty(a.ClientSecret))
                req.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{a.ClientId}:{a.ClientSecret}")));
            var tok = await Http.SendJsonAsync(req, ct);
            var t = tok["data"] is JsonObject d && p.Id == "tiktok" ? d : tok;
            a.AccessToken = (string?)t["access_token"] ?? a.AccessToken;
            a.RefreshToken = (string?)t["refresh_token"] ?? a.RefreshToken;
            a.Expires = t["expires_in"] is JsonNode e && double.TryParse(e.ToString(), out var secs) ? DateTime.UtcNow.AddSeconds(secs - 60) : null;
            Save();
        }
        return a.AccessToken!;
    }

    /// <summary>Meta: short-lived user token → long-lived (≈60 days).</summary>
    static async Task MetaLongLivedAsync(PlatformDef p, Account a, CancellationToken ct)
    {
        var url = $"{Http.Url("https://graph.facebook.com/v23.0/oauth/access_token")}?grant_type=fb_exchange_token&client_id={Uri.EscapeDataString(a.ClientId)}" +
                  $"&client_secret={Uri.EscapeDataString(a.ClientSecret)}&fb_exchange_token={Uri.EscapeDataString(a.AccessToken!)}";
        try
        {
            var j = await Http.SendJsonAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
            if ((string?)j["access_token"] is { } lt) { a.AccessToken = lt; a.Expires = DateTime.UtcNow.AddDays(55); }
        }
        catch { /* keep the short-lived token */ }
    }

    /// <summary>One-shot loopback HTTP listener for the OAuth redirect (IPv4 + IPv6 loopback; no admin rights, no firewall prompt).</summary>
    static async Task<string> WaitForCodeAsync(string state, CancellationToken ct)
    {
        var l4 = new TcpListener(IPAddress.Loopback, Port); var l6 = new TcpListener(IPAddress.IPv6Loopback, Port);
        l4.Start(); try { l6.Start(); } catch { l6 = null!; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            while (true)
            {
                var accepts = new List<Task<TcpClient>> { l4.AcceptTcpClientAsync(timeout.Token).AsTask() };
                if (l6 != null) accepts.Add(l6.AcceptTcpClientAsync(timeout.Token).AsTask());
                using var client = await await Task.WhenAny(accepts);
                using var stream = client.GetStream();
                var buf = new byte[8192]; int n = await stream.ReadAsync(buf, timeout.Token);
                string line = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];            // GET /callback?code=…&state=… HTTP/1.1
                string path = line.Split(' ').ElementAtOrDefault(1) ?? "";
                var qs = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + path).Query);
                bool ok = qs["code"] is { Length: > 0 } && qs["state"] == state;
                string msg = ok ? "Connected. You can close this tab and go back to CutPro."
                                : $"Sign-in didn't finish: {WebUtility.HtmlEncode(qs["error_description"] ?? qs["error"] ?? "no code returned")}";
                string html = $"<!doctype html><meta charset=utf-8><title>CutPro</title><body style=\"font-family:Segoe UI;background:#1f1f1f;color:#ddd;display:grid;place-items:center;height:90vh\"><h2>{msg}</h2>";
                var resp = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(html)}\r\nConnection: close\r\n\r\n{html}");
                await stream.WriteAsync(resp, timeout.Token);
                if (!path.StartsWith("/callback")) continue;                                  // e.g. /favicon.ico
                if (ok) return qs["code"]!;
                throw new Exception("Sign-in didn't finish: " + (qs["error_description"] ?? qs["error"] ?? "no code returned"));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Sign-in timed out (5 minutes)."); }
        finally { l4.Stop(); l6?.Stop(); }
    }
}

/// <summary>Windows DPAPI (CryptProtectData, current user): encrypts the accounts file without a NuGet package.</summary>
static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    static byte[] Run(byte[] data, bool protect)
    {
        var inB = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        try
        {
            Marshal.Copy(data, 0, inB.Data, data.Length);
            bool ok = protect ? CryptProtectData(ref inB, "CutPro accounts", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var outB)
                              : CryptUnprotectData(ref inB, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outB);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var r = new byte[outB.Size]; Marshal.Copy(outB.Data, r, 0, outB.Size); LocalFree(outB.Data);
            return r;
        }
        finally { Marshal.FreeHGlobal(inB.Data); }
    }
    public static byte[] Protect(byte[] d) => Run(d, true);
    public static byte[] Unprotect(byte[] d) => Run(d, false);
}
