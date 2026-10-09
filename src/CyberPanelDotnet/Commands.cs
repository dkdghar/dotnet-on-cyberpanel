using System.Text.RegularExpressions;
using static CyberPanelDotnet.Sys;

namespace CyberPanelDotnet;

internal static partial class Commands
{
    private const string VhostsRoot = "/usr/local/lsws/conf/vhosts";

    private static string AppDir(string domain) => $"/home/{domain}/public_html/NetCoreApp";
    private static string IncludesDir(string domain) => $"{VhostsRoot}/{domain}/includes";
    private static string ServiceName(string domain) => $"dotnet-{domain}.service";

    // Make a safe handler name from domain (dots/dashes -> underscores)
    private static string HandlerName(string domain) =>
        "dotnet_backend_" + domain.Replace('.', '_').Replace('-', '_');

    // ---------------- Helpers ----------------

    // Domains are interpolated into paths we rm -rf and into OLS/systemd configs, so be strict.
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex DomainRegex();

    // DLL name/path: no whitespace or quotes (it goes unquoted into ExecStart).
    [GeneratedRegex(@"^[A-Za-z0-9._/+-]+$")]
    private static partial Regex DllRegex();

    // Hub paths become `context <path> {` lines; reject anything that could break the config.
    [GeneratedRegex(@"^/[A-Za-z0-9._~/-]*[A-Za-z0-9._~-]$")]
    private static partial Regex HubPathRegex();

    private static string RequireDomain(string domain)
    {
        if (domain == "") throw new CliException("domain required");
        if (!DomainRegex().IsMatch(domain)) throw new CliException($"Invalid domain: {domain}");
        return domain;
    }

    private static string NormalizeHubPath(string raw)
    {
        if (raw.StartsWith('-'))
            throw new CliException($"Unknown arg: {raw} (hub paths are positional, e.g. 'on /hub')");
        var hp = raw.StartsWith('/') ? raw : "/" + raw;
        if (!HubPathRegex().IsMatch(hp) || hp.Contains("//"))
            throw new CliException($"Invalid hub path: {raw}");
        return hp;
    }

    private static string FindVhost(string domain)
    {
        var a = $"{VhostsRoot}/{domain}/vhost.conf";
        var b = $"{VhostsRoot}/{domain}/vhconf.conf";
        if (File.Exists(a)) return a;
        if (File.Exists(b)) return b;
        throw new CliException($"vhost not found for {domain}. Create the website first in CyberPanel.");
    }

    private static void BackupFile(string path) => ExecOrFail("cp", "-a", path, $"{path}.bak.{UnixTime()}");

    private static int PickPort(string appDir)
    {
        var pf = Path.Combine(appDir, ".dotnet-port");
        if (File.Exists(pf) && int.TryParse(File.ReadAllText(pf).Trim(), out var existing))
            return existing;

        var port = PickFreePort() ?? throw new CliException("Failed to pick a free port");
        File.WriteAllText(pf, $"{port}\n");
        return port;
    }

    private static string EnsureIncludes(string domain)
    {
        var d = IncludesDir(domain);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void EnsureLineInFile(string line, string file)
    {
        if (!File.ReadAllText(file).Contains(line))
            AppendLine(file, line);
    }

    private static void SetAutoIndexOff(string vhost)
    {
        if (!File.ReadLines(vhost).Any(l => AutoIndexRegex().IsMatch(l)))
            AppendLine(vhost, "autoIndex 0");
    }

    // Like `echo line >> file`: make sure we start on a fresh line.
    private static void AppendLine(string file, string line)
    {
        var text = File.ReadAllText(file);
        var prefix = text.Length > 0 && !text.EndsWith('\n') ? "\n" : "";
        File.AppendAllText(file, $"{prefix}{line}\n");
    }

    private static void EnsureParentPerms(string domain)
    {
        Chmod($"/home/{domain}", Mode755);
        Chmod($"/home/{domain}/public_html", Mode755);
    }

    private static void EnsurePerms(string app)
    {
        ChmodTree(app, Mode755, Mode644);
        var uploads = Path.Combine(app, "wwwroot", "uploads");
        Directory.CreateDirectory(uploads);
        ExecQuiet("chown", "-R", "www-data:www-data", uploads);
        ChmodTree(uploads, Mode770, Mode770);
    }

    private static void WriteDotnetConf(string dest, int port, bool ws, string handler, IReadOnlyList<string> hubs) =>
        File.WriteAllText(dest, Templates.DotnetModeConf(port, ws, handler, hubs));

    private static void LinkMode(string dir, string target)
    {
        var link = Path.Combine(dir, "app-mode.conf");
        SymlinkForce(link, Path.Combine(dir, target));
        ExecQuiet("chown", "-h", "lsadm:lsadm", link);
    }

    private static void RestartOls() => ExecOrFail("systemctl", "restart", "lsws");

    private static void StatusService(string domain) =>
        Exec("systemctl", "--no-pager", "--lines=20", "status", $"dotnet-{domain}");

    private static (string Value, string[] Remaining) Positional(string[] args) =>
        (args.Length > 0 ? args[0] : "", args.Length > 0 ? args[1..] : []);

    // ---------------- Commands ----------------

    public static void Enable(string[] args)
    {
        RequireRoot();
        var dotnet = NeedBin("dotnet");
        var (domain, rest) = Positional(args);
        var dll = "";
        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--dll": dll = i + 1 < rest.Length ? rest[++i] : ""; break;
                default: throw new CliException($"Unknown arg: {rest[i]}");
            }
        }
        RequireDomain(domain);
        if (dll == "") throw new CliException("--dll <MainDll> is required");
        if (!DllRegex().IsMatch(dll) || dll.Split('/').Contains(".."))
            throw new CliException($"Invalid --dll value: {dll}");

        var app = AppDir(domain);
        var dllPath = dll.StartsWith('/') ? dll : $"{app}/{dll}";
        Directory.CreateDirectory(Path.Combine(app, "wwwroot", "uploads"));
        if (!File.Exists(dllPath))
            Info($"Warning: {dllPath} does not exist yet; upload your publish output before the service can start.");

        var vhost = FindVhost(domain);
        BackupFile(vhost);

        var port = PickPort(app);
        Info($"Using port {port}");

        var handler = HandlerName(domain);
        Info($"Using handler {handler}");

        EnsureParentPerms(domain);
        EnsurePerms(app);

        File.WriteAllText($"/etc/systemd/system/{ServiceName(domain)}", Templates.SystemdUnit(domain, app, dotnet, dllPath, port));

        ExecOrFail("systemctl", "daemon-reload");
        ExecQuiet("systemctl", "enable", ServiceName(domain));
        ExecOrFail("systemctl", "restart", ServiceName(domain));

        var inc = EnsureIncludes(domain);
        WriteDotnetConf(Path.Combine(inc, "dotnet-mode.conf"), port, ws: false, handler, []); // default: WS OFF
        File.WriteAllText(Path.Combine(inc, "php-mode.conf"), Templates.PhpModeConf);
        SetAutoIndexOff(vhost);
        EnsureLineInFile("# cyberpanel-dotnet include (do not remove)", vhost);
        EnsureLineInFile($"include {inc}/app-mode.conf", vhost);
        LinkMode(inc, "dotnet-mode.conf");

        RestartOls();
        Ok($"Enabled .NET for {domain} (port {port}). App dir: {app}");
        StatusService(domain);
    }

    public static void Deploy(string[] args)
    {
        RequireRoot();
        var (domain, rest) = Positional(args);
        var from = "";
        for (var i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--from": from = i + 1 < rest.Length ? rest[++i] : ""; break;
                default: throw new CliException($"Unknown arg: {rest[i]}");
            }
        }
        RequireDomain(domain);
        var app = AppDir(domain);
        if (!Directory.Exists(app)) throw new CliException($"App dir not found: {app}");
        if (from != "")
        {
            if (!Directory.Exists(from)) throw new CliException($"Source dir not found: {from}");
            NeedBin("rsync");
            Info($"Deploying from {from} to {app} (keeping uploads)");
            // Exclusions are also protected from --delete: keep uploads and the assigned port.
            ExecOrFail("rsync", "-a", "--delete",
                "--exclude", "/wwwroot/uploads/", "--exclude", "/.dotnet-port",
                from.TrimEnd('/') + "/", app + "/");
        }

        EnsureParentPerms(domain);
        EnsurePerms(app);

        ExecOrFail("systemctl", "restart", ServiceName(domain));
        Ok($"Deployed and restarted dotnet-{domain}");
    }

    public static void Toggle(string[] args)
    {
        RequireRoot();
        var domain = args[0];
        var mode = args[1];
        if (domain == "" || mode == "") throw new CliException("Usage: toggle <domain> php|dotnet");
        RequireDomain(domain);
        if (mode is not ("php" or "dotnet")) throw new CliException($"Unknown mode: {mode}");
        var inc = IncludesDir(domain);
        if (!Directory.Exists(inc)) throw new CliException($"Includes dir not found: {inc}");
        switch (mode)
        {
            case "php":
                LinkMode(inc, "php-mode.conf");
                RestartOls();
                Ok($"Switched {domain} to PHP mode");
                break;
            case "dotnet":
                LinkMode(inc, "dotnet-mode.conf");
                RestartOls();
                ExecOrFail("systemctl", "restart", ServiceName(domain));
                Ok($"Switched {domain} to .NET mode");
                break;
            default:
                throw new CliException($"Unknown mode: {mode}");
        }
    }

    public static void Disable(string[] args)
    {
        RequireRoot();
        var (domain, rest) = Positional(args);
        var purge = false;
        foreach (var a in rest)
        {
            switch (a)
            {
                case "--purge": purge = true; break;
                default: throw new CliException($"Unknown arg: {a}");
            }
        }
        RequireDomain(domain);
        var inc = IncludesDir(domain);
        if (Directory.Exists(inc))
        {
            try { LinkMode(inc, "php-mode.conf"); } catch (IOException) { }
            Exec("systemctl", "restart", "lsws");
        }
        ExecQuiet("systemctl", "stop", ServiceName(domain));
        ExecQuiet("systemctl", "disable", ServiceName(domain));
        File.Delete($"/etc/systemd/system/{ServiceName(domain)}");
        Exec("systemctl", "daemon-reload");
        if (purge && Directory.Exists(AppDir(domain))) Directory.Delete(AppDir(domain), recursive: true);
        Ok($"Disabled .NET for {domain} (purge={(purge ? "yes" : "no")})");
    }

    // --------------- SignalR/WebSocket toggle (multi-hub) ---------------

    // Supports:
    //   address 127.0.0.1:50150
    //   address http://127.0.0.1:50150
    [GeneratedRegex(@"address\s+(?:http://)?127\.0\.0\.1:(\d+)")]
    private static partial Regex AddressPortRegex();

    [GeneratedRegex(@"^\s*autoIndex\s+")]
    private static partial Regex AutoIndexRegex();

    private static int? PortFromInclude(string conf)
    {
        var m = AddressPortRegex().Match(File.ReadAllText(conf));
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static int? PortFromApp(string domain)
    {
        var pf = Path.Combine(AppDir(domain), ".dotnet-port");
        if (!File.Exists(pf)) return null;
        var first = File.ReadAllText(pf).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, out var p) ? p : null;
    }

    public static int SignalR(string[] args)
    {
        RequireRoot();
        var domain = args[0];
        var state = args[1];
        if (domain == "" || state == "") throw new CliException("Usage: signalr <domain> on|off [hubPaths...]");
        RequireDomain(domain);
        if (state is not ("on" or "off")) throw new CliException($"Unknown state: {state} (use on|off)");

        var hubPaths = args[2..].Select(NormalizeHubPath).Distinct().ToArray();

        var conf = Path.Combine(IncludesDir(domain), "dotnet-mode.conf");
        if (!File.Exists(conf)) throw new CliException($"dotnet-mode.conf not found for {domain} (run 'enable' first).");

        var handler = HandlerName(domain);

        var port = PortFromInclude(conf) ?? PortFromApp(domain)
            ?? throw new CliException($"Unable to determine backend port for {domain}.");

        var backup = $"{conf}.bak.{UnixTime()}";
        ExecOrFail("cp", "-a", conf, backup);

        var tmp = $"{conf}.tmp.{Environment.ProcessId}";
        switch (state)
        {
            case "on": WriteDotnetConf(tmp, port, ws: true, handler, hubPaths); break;
            case "off": WriteDotnetConf(tmp, port, ws: false, handler, []); break;
            default: throw new CliException($"Unknown state: {state} (use on|off)");
        }

        File.Move(tmp, conf, overwrite: true);

        if (Exec("systemctl", "restart", "lsws") != 0)
        {
            Console.Error.WriteLine("[X] OpenLiteSpeed restart failed. Restoring previous config...");
            try { File.Move(backup, conf, overwrite: true); } catch (IOException) { }
            Exec("systemctl", "restart", "lsws");
            return 1;
        }

        Ok($"SignalR/WebSocket headers {state} for {domain} (port {port})");
        if (state == "on")
            Info($"Hubs enabled: {(hubPaths.Length > 0 ? string.Join(' ', hubPaths) : "/hub")}");
        return 0;
    }
}
