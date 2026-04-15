#:property Nullable=enable
#:property ImplicitUsings=enable

// IpSync.cs — Minecraft Server IP Sync Tool
//
// PURPOSE:
//   Keeps friends' hosts files up to date when your server's public IP changes.
//   Friends run this in SERVER mode. You run this in CLIENT mode.
//
// REQUIREMENTS:
//   .NET 10 SDK  →  https://dotnet.microsoft.com/download
//
// QUICK START:
//   1. Put this file anywhere and run: dotnet run IpSync.cs
//      (or compile: dotnet publish IpSync.cs -c Release -r linux-x64 --self-contained
//                   dotnet publish IpSync.cs -c Release -r win-x64   --self-contained)
//   2. Friends (Linux only): run with sudo, choose [S]erver, follow prompts — config is saved
//   3. You:     run normally, choose [C]lient, enter token and friends — config is saved
//   4. After setup, just run with no arguments — no input needed
//      To change config or add friends later: dotnet run IpSync.cs --setup
//
// HOW IT WORKS:
//   - Server (friend's machine, Linux only): listens on a port, validates a shared token,
//     reads your public IP from the incoming TCP connection, and writes/updates
//     an entry in /etc/hosts like "1.2.3.4  minecraft-home"
//   - Client (your machine):     POSTs your token to each friend's server.
//     No IP lookup needed — the server sees your IP from the connection itself.
//
// SECURITY:
//   - The shared token prevents strangers from triggering a hosts update
//   - The IP is read server-side from the TCP connection — the client doesn't
//     supply it, so there's nothing to spoof in the payload
//   - Friends should only run the server while they want to sync, not 24/7

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

const string CONFIG_FILE = "ipsync-config.json";
const string HOSTS_HOSTNAME = "minecraft-home";  // hostname friends use in Minecraft to connect
const string HOSTS_FILE = "/etc/hosts";
const int MAX_BODY_BYTES = 4096;              // token payload is <100 bytes; cap to limit abuse

// ── Entry Point ───────────────────────────────────────────────────────────────

var config = LoadOrCreateConfig();

Console.WriteLine("=== IpSync — Minecraft Server IP Sync ===");
Console.WriteLine();

bool forceSetup = args.Contains("--setup");

if (forceSetup || config.Mode == "")
    await ChooseMode(config);
else if (config.Mode == "server")
    await RunServer(config);
else if (config.Mode == "client")
    await RunClient(config);

// ── Server (runs on friends' machines) ───────────────────────────────────────

async Task RunServer(Config cfg)
{
    WarnIfNotRoot();

    string url = $"http://+:{cfg.ServerPort}/ipsync/";
    var listener = new HttpListener();
    listener.Prefixes.Add(url);

    try { listener.Start(); }
    catch (HttpListenerException ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Could not start listener on port {cfg.ServerPort}: {ex.Message}");
        Console.WriteLine("Try running with: sudo dotnet run");
        Console.ResetColor();
        return;
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Server listening on port {cfg.ServerPort}");
    Console.WriteLine($"Will update {HOSTS_FILE} with hostname: {HOSTS_HOSTNAME}");
    Console.ResetColor();
    Console.WriteLine("Press Ctrl+C to stop.\n");

    while (true)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); }
        catch { break; }

        var req = ctx.Request;
        var resp = ctx.Response;

        if (req.HttpMethod != "POST" || req.Url?.AbsolutePath != "/ipsync/update")
        {
            resp.StatusCode = 404;
            resp.Close();
            continue;
        }

        // The IP comes from the TCP connection — not from the client payload.
        string callerIp = req.RemoteEndPoint.Address.ToString();

        // Strip IPv6-mapped IPv4 prefix if present (e.g. "::ffff:1.2.3.4" → "1.2.3.4")
        if (callerIp.StartsWith("::ffff:"))
            callerIp = callerIp[7..];

        // Reject oversized bodies before reading — the token payload is under 100 bytes.
        // Check Content-Length first, then enforce a hard cap while actually reading.
        if (req.ContentLength64 is > MAX_BODY_BYTES)
        {
            resp.StatusCode = 413;
            resp.Close();
            continue;
        }

        byte[] bodyBuf = new byte[MAX_BODY_BYTES + 1];
        int totalRead = 0, n;
        while ((n = await req.InputStream.ReadAsync(bodyBuf.AsMemory(totalRead))) > 0)
        {
            totalRead += n;
            if (totalRead > MAX_BODY_BYTES) break;
        }
        if (totalRead > MAX_BODY_BYTES)
        {
            resp.StatusCode = 413;
            resp.Close();
            continue;
        }
        string body = req.ContentEncoding.GetString(bodyBuf, 0, totalRead);

        SyncRequest? payload = null;
        try { payload = JsonSerializer.Deserialize(body, AppJsonContext.Default.SyncRequest); }
        catch (JsonException) { }

        if (payload == null || payload.Token != cfg.SharedToken)
        {
            Console.WriteLine($"[{DateTime.Now:T}] Rejected — bad or missing token from {callerIp}");
            resp.StatusCode = 403;
            resp.Close();
            continue;
        }

        bool ok = UpdateHostsFile(callerIp);
        resp.StatusCode = ok ? 200 : 500;
        resp.Close();

        if (ok)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[{DateTime.Now:T}] Updated: {HOSTS_HOSTNAME} → {callerIp}");
            Console.ResetColor();
        }
    }
}

// ── Client (runs on the server owner's machine) ───────────────────────────────

async Task RunClient(Config cfg)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    string jsonPayload = JsonSerializer.Serialize(
        new SyncRequest { Token = cfg.SharedToken }, AppJsonContext.Default.SyncRequest);

    foreach (var friend in cfg.Friends)
    {
        string endpoint = $"http://{friend.Host}:{friend.Port}/ipsync/update";
        Console.Write($"  → Pinging {friend.Name} ({friend.Host})... ");

        try
        {
            var result = await http.PostAsync(endpoint,
                new StringContent(jsonPayload, Encoding.UTF8, "application/json"));

            if (result.IsSuccessStatusCode)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("OK — their hosts file updated");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Server returned {(int)result.StatusCode} — wrong token, or server not running?");
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed — {ex.Message}");
            Console.WriteLine($"     Is {friend.Name} running IpSync in server mode?");
        }
        
        Console.ResetColor();
    }

    Console.WriteLine();
    Console.WriteLine($"Done. Friends connect to Minecraft using hostname: {HOSTS_HOSTNAME}");
    Console.WriteLine("(Run with --setup to add friends or change config.)");
}

// ── Mode selection / setup wizard ─────────────────────────────────────────────

async Task ChooseMode(Config cfg)
{
    if (cfg.Mode != "")
        Console.WriteLine($"(Current mode: {cfg.Mode} — reconfiguring)\n");

    Console.WriteLine("Are you:");
    Console.WriteLine("  [S] A FRIEND receiving IP updates        (needs sudo for /etc/hosts)");
    Console.WriteLine("  [C] The SERVER OWNER pushing your IP out (run normally)");
    Console.Write("\nChoice: ");
    var choice = Console.ReadLine()?.Trim().ToUpperInvariant();

    if (choice == "S")
    {
        Console.Write("Port to listen on [default: 8765]: ");
        var portInput = Console.ReadLine()?.Trim();
        cfg.ServerPort = int.TryParse(portInput, out int p) ? p : 8765;

        cfg.SharedToken = PromptToken("Shared token (ask the server owner): ");
        cfg.Mode = "server";
        SaveConfig(cfg);
        Console.WriteLine("\nConfig saved. Starting server...\n");
        await RunServer(cfg);
    }
    else if (choice == "C")
    {
        cfg.SharedToken = PromptToken("Shared token (make one up, give it to each friend): ");
        cfg.Mode = "client";
        AddFriend(cfg);

        Console.Write("Add another friend? [y/N] ");
        while (string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            AddFriend(cfg);
            Console.Write("Add another friend? [y/N] ");
        }

        SaveConfig(cfg);
        Console.WriteLine("\nConfig saved. Running client...\n");
        await RunClient(cfg);
    }
    else
    {
        Console.WriteLine("No valid choice. Exiting.");
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

string PromptToken(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        string token = Console.ReadLine()?.Trim() ?? "";
        if (token.Length > 0) return token;
        Console.WriteLine("Token cannot be empty.");
    }
}

void AddFriend(Config cfg)
{
    Console.Write("Friend's name (for display): ");
    string name = Console.ReadLine()?.Trim() ?? "Friend";

    string host;
    while (true)
    {
        Console.Write("Friend's IP or hostname (where their server is reachable from the internet): ");
        host = Console.ReadLine()?.Trim() ?? "";
        if (host.Length > 0) break;
        Console.WriteLine("Host cannot be empty.");
    }

    Console.Write("Friend's server port [default: 8765]: ");
    var portInput = Console.ReadLine()?.Trim();
    int port = int.TryParse(portInput, out int p) ? p : 8765;

    cfg.Friends.Add(new FriendEntry { Name = name, Host = host, Port = port });
}

bool UpdateHostsFile(string newIp)
{
    string tmp = HOSTS_FILE + ".ipsync-tmp";
    try
    {
        var lines = File.ReadAllLines(HOSTS_FILE).ToList();

        // Remove any existing IpSync-managed entry for our hostname
        lines = [.. lines.Where(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#')) return true;
            var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length < 2 ||
                   !parts[1].Equals(HOSTS_HOSTNAME, StringComparison.OrdinalIgnoreCase);
        })];

        lines.Add($"{newIp,-20} {HOSTS_HOSTNAME}   # managed by IpSync — {DateTime.Now:yyyy-MM-dd HH:mm}");

        // Write to a temp file first, then rename atomically — keeps /etc/hosts intact if the write fails
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, HOSTS_FILE, overwrite: true);
        return true;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed to write {HOSTS_FILE}: {ex.Message}");
        Console.WriteLine("Run with: sudo dotnet run");
        Console.ResetColor();
        try { File.Delete(tmp); } catch { }
        return false;
    }
}

void WarnIfNotRoot()
{
    if (Environment.GetEnvironmentVariable("USER") != "root" &&
        Environment.GetEnvironmentVariable("SUDO_USER") == null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"WARNING: Not running as root. Writing to {HOSTS_FILE} will fail.");
        Console.WriteLine("Restart with: sudo dotnet run");
        Console.ResetColor();
        Console.WriteLine();
    }
}

Config LoadOrCreateConfig()
{
    if (File.Exists(CONFIG_FILE))
    {
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(CONFIG_FILE), AppJsonContext.Default.Config) ?? new Config();
        }
        catch (JsonException) { }
    }
    return new Config();
}

void SaveConfig(Config cfg)
{
    try
    {
        File.WriteAllText(CONFIG_FILE,
            JsonSerializer.Serialize(cfg, AppJsonContext.Default.Config));
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Warning: could not save config — {ex.Message}");
        Console.ResetColor();
    }
}

// ── Data models ───────────────────────────────────────────────────────────────

class Config
{
    public string Mode { get; set; } = "";
    public string SharedToken { get; set; } = "";
    public int ServerPort { get; set; } = 8765;
    public List<FriendEntry> Friends { get; set; } = [];
}

class FriendEntry
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8765;
}

// Token only — the server derives the IP from the TCP connection, never trusts the client to supply it
class SyncRequest
{
    public string Token { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(SyncRequest))]
internal partial class AppJsonContext : JsonSerializerContext { }
