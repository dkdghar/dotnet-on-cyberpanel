using System.Runtime.Versioning;
using System.Text;
using CyberPanelDotnet;

[assembly: SupportedOSPlatform("linux")]

Console.OutputEncoding = Encoding.UTF8;

try
{
    return Cli.Run(args);
}
catch (CliException ex)
{
    Console.Error.WriteLine($"[X] {ex.Message}");
    return 1;
}

namespace CyberPanelDotnet
{
    internal sealed class CliException(string message) : Exception(message);

    internal static class Cli
    {
        public const string Version = "1.0.9";

        public const string Usage = $"""
            cyberpanel-dotnet v{Version}

            Usage:
              cyberpanel-dotnet enable <domain> --dll <MainDll>
              cyberpanel-dotnet deploy <domain> [--from <dir>]
              cyberpanel-dotnet toggle <domain> php|dotnet
              cyberpanel-dotnet disable <domain> [--purge]
              cyberpanel-dotnet signalr <domain> on [hubPaths...]
              cyberpanel-dotnet signalr <domain> off
              cyberpanel-dotnet --help | -h
              cyberpanel-dotnet --version

            Notes:
            - App path: /home/<domain>/public_html/NetCoreApp
            - Auto-port stored in: /home/<domain>/public_html/NetCoreApp/.dotnet-port
            - SignalR/WebSockets is OFF by default. Enable per site:
                sudo cyberpanel-dotnet signalr <domain> on
            - You can enable multiple hubs:
                sudo cyberpanel-dotnet signalr <domain> on /hub /ConnectionHub /notifications
            - When SignalR is ON, we:
                * forward Upgrade/Connection + X-Forwarded-* headers
                * add explicit OpenLiteSpeed websocket contexts for given hub paths
            """;

        public static int Run(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine(Usage);
                return 1;
            }

            var rest = args[1..];
            switch (args[0])
            {
                case "--help":
                case "-h":
                    Console.WriteLine(Usage);
                    return 0;
                case "--version":
                    Console.WriteLine(Version);
                    return 0;
                case "enable":
                    if (rest.Length < 1) throw new CliException("enable <domain> --dll <MainDll>");
                    Commands.Enable(rest);
                    return 0;
                case "deploy":
                    if (rest.Length < 1) throw new CliException("deploy <domain> [--from <dir>]");
                    Commands.Deploy(rest);
                    return 0;
                case "toggle":
                    if (rest.Length < 2) throw new CliException("toggle <domain> php|dotnet");
                    Commands.Toggle(rest);
                    return 0;
                case "disable":
                    if (rest.Length < 1) throw new CliException("disable <domain> [--purge]");
                    Commands.Disable(rest);
                    return 0;
                case "signalr":
                    if (rest.Length < 2) throw new CliException("signalr <domain> on|off [hubPaths...]");
                    return Commands.SignalR(rest);
                default:
                    Console.WriteLine(Usage);
                    throw new CliException($"Unknown command: {args[0]}");
            }
        }
    }
}
