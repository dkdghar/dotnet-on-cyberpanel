using System.Text;

namespace CyberPanelDotnet;

// Canonical OpenLiteSpeed / systemd templates. Output matches the Bash CLI byte-for-byte.
internal static class Templates
{
    public static string DotnetModeConf(int port, bool ws, string handler, IReadOnlyList<string> hubPaths)
    {
        var wsHeaders = "";
        var wsCtx = new StringBuilder();

        if (ws)
        {
            wsHeaders =
                "  requestHeader set \"Upgrade\" \"%{ENV:HTTP_UPGRADE}\"\n" +
                "  requestHeader set \"Connection\" \"%{ENV:HTTP_CONNECTION}\"\n" +
                "  requestHeader set \"X-Forwarded-Proto\" \"https\"\n" +
                "  requestHeader set \"X-Forwarded-Host\"  \"%{HTTP_HOST}\"\n" +
                "  requestHeader set \"X-Forwarded-For\"   \"%{CLIENTIP}\"";

            // default hub path if none given
            IReadOnlyList<string> hubs = hubPaths.Count == 0 ? ["/hub"] : hubPaths;
            foreach (var raw in hubs)
            {
                var hp = raw.StartsWith('/') ? raw : "/" + raw;
                wsCtx.Append($"# WebSocket hub context for {hp}\n");
                wsCtx.Append($"context {hp} {{\n");
                wsCtx.Append("  type                    websocket\n");
                wsCtx.Append($"  address                 127.0.0.1:{port}\n");
                wsCtx.Append("}\n");
            }
        }

        return $$"""
            extprocessor {{handler}} {
              type                    proxy
              # IMPORTANT: use scheme for stable proxy routing
              address                 http://127.0.0.1:{{port}}
              maxConns                200
              initTimeout             60
              retryTimeout            0
              respBuffer              0
            }

            {{wsCtx}}
            context / {
              type                    proxy
              handler                 {{handler}}

              enableRewrite           1
              rewriteCond             %{HTTP:Upgrade} =websocket
              rewriteRule             .* - [E=HTTP_UPGRADE:%{HTTP:Upgrade},E=HTTP_CONNECTION:%{HTTP:Connection}]

            {{wsHeaders}}
              rewrite  {
                rewriteCond %{REQUEST_URI} \.(dll|exe|pdb|deps\.json|runtimeconfig\.json|ps1|cmd|sh)$
                rewriteRule .* - [F,L]
                rewriteCond %{REQUEST_URI} (appsettings\.json|appsettings\..*\.json|\.env|\.ini|\.config)$
                rewriteRule .* - [F,L]
                rewriteCond %{REQUEST_URI} \.(sqlite|db|bak|zip|tar\.gz)$
                rewriteRule .* - [F,L]
              }
            }

            """.ReplaceLineEndings("\n");
    }

    public static readonly string PhpModeConf = """
        index  {
          useServer              0
          indexFiles             index.php, index.html, index.htm
        }
        rewrite  {
          rewriteCond %{REQUEST_URI} ^/NetCoreApp($|/.*)
          rewriteRule .* - [F,L]
        }

        """.ReplaceLineEndings("\n");

    public static string SystemdUnit(string domain, string app, string dotnet, string dllPath, int port) => $"""
        [Unit]
        Description=.NET app for {domain} (public_html/NetCoreApp)
        After=network.target

        [Service]
        WorkingDirectory={app}
        ExecStart={dotnet} {dllPath}
        Restart=always
        RestartSec=2
        User=www-data
        Group=www-data
        Environment=ASPNETCORE_URLS=http://127.0.0.1:{port}
        Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

        [Install]
        WantedBy=multi-user.target

        """.ReplaceLineEndings("\n");
}
