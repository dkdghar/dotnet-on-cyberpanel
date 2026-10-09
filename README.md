<p align="center">
  <img src="assets/Logo.png" alt="CyberPanel.NET Logo" width="320">
</p>

# 🚀 CyberPanel .NET — One-Command ASP.NET Core Hosting on CyberPanel/OpenLiteSpeed

**Host ASP.NET Core apps on CyberPanel/OpenLiteSpeed in a single command.**  
No manual vHost editing, no reverse-proxy headache.  
Works with **.NET 6/7/8/9**, **SignalR**, **WebSockets**, and **multiple sites per server**.

This tool gives you an IIS-like workflow on Linux + CyberPanel:
- One-command install (CLI + ASP.NET Core 9 runtime)
- One-command enable
- One-command deploy
- PHP ↔ .NET toggle
- SignalR + WebSockets support (opt-in)
- Secure defaults
- Automatic Kestrel systemd service

---

## 📋 Requirements

- CyberPanel with **OpenLiteSpeed** (Ubuntu 20.04/22.04/24.04, AlmaLinux/Rocky 8/9)
- Root access (`sudo`)
- The website already created in CyberPanel (the tool edits its existing vHost)
- `curl` (installer), `rsync` (only for `deploy --from`)

---

## 🔧 Installation

```bash
curl -fsSL https://raw.githubusercontent.com/dkdghar/dotnet-on-cyberpanel/main/install.sh | sudo bash
```

This installs the `cyberpanel-dotnet` CLI to `/usr/local/bin` **and the ASP.NET Core 9 runtime**
(using Microsoft's official `dotnet-install.sh`; skipped if the runtime is already present).

Options:
```bash
# CLI only (you manage .NET yourself)
curl -fsSL https://raw.githubusercontent.com/dkdghar/dotnet-on-cyberpanel/main/install.sh | sudo bash -s -- --no-dotnet

# Another runtime version, installed side-by-side (e.g. for .NET 8 apps)
curl -fsSL https://raw.githubusercontent.com/dkdghar/dotnet-on-cyberpanel/main/install.sh | sudo bash -s -- --dotnet-channel 8.0
```

Verify:
```bash
cyberpanel-dotnet --version
dotnet --list-runtimes
```

> Only the **runtime** is installed on the server. Build your app with the SDK on your dev machine (`dotnet publish`).

---

## ⚡ Quick Start

```bash
# 1. On your dev machine
dotnet publish -c Release -o publish

# 2. Upload the publish output to the server
rsync -a --delete publish/ root@SERVER:/home/<domain>/public_html/NetCoreApp/

# 3. Enable .NET for the site (first time only)
sudo cyberpanel-dotnet enable <domain> --dll <MainDll>      # e.g. --dll MyApp.dll

# 4. Later updates: upload new files, then
sudo cyberpanel-dotnet deploy <domain>
```

Your site is now served by Kestrel behind OpenLiteSpeed. Roll back to PHP at any time with
`sudo cyberpanel-dotnet toggle <domain> php`.

---

## 🧰 Commands

```bash
sudo cyberpanel-dotnet enable  <domain> --dll <MainDll>
sudo cyberpanel-dotnet deploy  <domain> [--from <dir>]
sudo cyberpanel-dotnet toggle  <domain> php|dotnet
sudo cyberpanel-dotnet disable <domain> [--purge]
sudo cyberpanel-dotnet signalr <domain> on [hubPaths...]
sudo cyberpanel-dotnet signalr <domain> off
cyberpanel-dotnet --help | -h
cyberpanel-dotnet --version
```

### `enable`
Sets up a site for .NET:
- Picks a free port (50000–50999) and saves it in `NetCoreApp/.dotnet-port` (reused on re-runs)
- Creates and starts `dotnet-<domain>.service` (runs as `www-data`, auto-restarts)
- Writes OpenLiteSpeed includes for **.NET mode** and **PHP mode**, and switches the site to .NET mode
- Backs up the vHost (`vhost.conf.bak.<timestamp>`) before editing it
- `--dll` is relative to `NetCoreApp/` (an absolute path is also accepted)

### `deploy`
Fixes permissions and restarts the service. With `--from <dir>`, it first syncs that directory
into `NetCoreApp/` (`rsync --delete`), **keeping** `wwwroot/uploads/` and `.dotnet-port`.

```bash
sudo cyberpanel-dotnet deploy example.com --from /root/publish
```

> The service is restarted, so expect a few seconds of downtime while the app starts.

### `toggle`
Instantly switches the site between PHP and .NET (for testing or rollback). The .NET service keeps
its configuration in both modes.

### `disable`
Switches the site back to PHP, stops and removes the systemd service.
`--purge` also deletes `/home/<domain>/public_html/NetCoreApp` — **including uploads**.

### `signalr`
WebSocket forwarding is **off by default**. Turn it on only if you use SignalR or other WebSockets.

```bash
# Default hub (/hub)
sudo cyberpanel-dotnet signalr example.com on

# One or more custom hubs (paths are positional — no --path flag)
sudo cyberpanel-dotnet signalr example.com on /hub /ConnectionHub /notifications

# Turn it off again (removes all hub contexts)
sudo cyberpanel-dotnet signalr example.com off
```

When on, it forwards `Upgrade`/`Connection` and `X-Forwarded-*` headers and adds an OpenLiteSpeed
`websocket` context per hub path. If OpenLiteSpeed fails to restart, the previous config is restored
automatically. Hub paths must match the `MapHub` paths in your app.

---

## 📦 Folder Layout

```
/home/<domain>/public_html/
├─ NetCoreApp/
│  ├─ <MainDll>                 # e.g. MyApp.dll
│  ├─ appsettings.json
│  ├─ .dotnet-port              # assigned Kestrel port
│  └─ wwwroot/
│     └─ uploads/               # persists across deploys (writable by the app)
└─ index.php                    # used only in PHP mode

/usr/local/lsws/conf/vhosts/<domain>/includes/
├─ dotnet-mode.conf             # reverse proxy to Kestrel
├─ php-mode.conf                # PHP mode, blocks /NetCoreApp
└─ app-mode.conf -> one of the above (switched by `toggle`)

/etc/systemd/system/dotnet-<domain>.service
```

## 🏗️ How It Works

```
Client ──HTTPS──▶ OpenLiteSpeed (CyberPanel)
                       │  reverse proxy (+ WebSocket upgrade if enabled)
                       ▼
                 127.0.0.1:<port>
                       │
                       ▼
            Kestrel (dotnet-<domain>.service, user www-data)
```

---

## 🔒 Security

- Direct access denied to `.dll`, `.exe`, `.pdb`, `.deps.json`, `.runtimeconfig.json`,
  `appsettings*.json`, `.env`, `.ini`, `.config`, `.sqlite`, `.db`, `.bak`, `.zip`, `.tar.gz`,
  `.ps1`, `.cmd`, `.sh`
- `autoIndex 0` enforced in the vHost
- PHP mode blocks `/NetCoreApp/**`
- Kestrel listens on `127.0.0.1` only and runs as `www-data`
- App files `644`/`755`; only `wwwroot/uploads` is writable (`770`, `www-data`)
- WebSocket forwarding is **opt-in**
- Domain names, `--dll` values and hub paths are validated before being written to any config

---

## 📄 Program.cs Example

Your app should trust the proxy headers so it sees the real scheme and client IP:

```csharp
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
// using YourApp.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();

var app = builder.Build();

// Reverse proxy headers (very early)
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1
};
fwd.KnownProxies.Add(IPAddress.Loopback);
app.UseForwardedHeaders(fwd);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Map your hubs — must match paths enabled via `signalr <domain> on ...`
// app.MapHub<ChatHub>("/hub");
// app.MapHub<ConnectionHub>("/ConnectionHub");

// Health & debug endpoints
app.MapGet("/healthz", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow }));
app.MapGet("/_debug", (HttpContext ctx) =>
    Results.Ok(new
    {
        scheme = ctx.Request.Scheme,
        host = ctx.Request.Host.Value,
        clientIp = ctx.Connection.RemoteIpAddress?.ToString(),
        xff = ctx.Request.Headers["X-Forwarded-For"].ToString(),
        xfp = ctx.Request.Headers["X-Forwarded-Proto"].ToString()
    })
);

app.Run();
```

---

## 🩺 Troubleshooting

```bash
systemctl status dotnet-<domain> --no-pager    # service state
journalctl -u dotnet-<domain> -f               # app logs
dotnet --list-runtimes                         # installed runtimes
```

See [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) for common issues (502 errors, WebSockets, permissions).

---

## 🛠️ Building the C# CLI (optional)

The installer uses the Bash CLI in [`cli/`](cli/). A functionally identical C# port lives in
[`src/CyberPanelDotnet/`](src/CyberPanelDotnet/):

```bash
dotnet publish src/CyberPanelDotnet -c Release -r linux-x64 --self-contained false -o out
sudo install -m 755 out/cyberpanel-dotnet /usr/local/bin/cyberpanel-dotnet
```

---

## 📜 License

[MIT](LICENSE) © 2026 Dinesh
