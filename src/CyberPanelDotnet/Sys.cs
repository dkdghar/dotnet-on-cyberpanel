using System.Diagnostics;
using System.Net.NetworkInformation;

namespace CyberPanelDotnet;

// Thin wrappers over the OS: processes, permissions, ports, logging.
internal static class Sys
{
    public static void Info(string msg) => Console.WriteLine($"[i] {msg}");
    public static void Ok(string msg) => Console.WriteLine($"[✓] {msg}");

    public static void RequireRoot()
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
            throw new CliException("Run as root (sudo).");
    }

    /// Resolves a binary on PATH (`command -v`). Throws if missing.
    public static string NeedBin(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists)
            ?? throw new CliException($"Missing dependency: {name}");
    }

    /// Runs a command, inheriting stdout/stderr. Returns the exit code.
    public static int Exec(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 127;
        }
    }

    /// Runs a command and throws if it fails (bash `set -e` semantics).
    public static void ExecOrFail(string file, params string[] args)
    {
        var code = Exec(file, args);
        if (code != 0) throw new CliException($"Command failed ({code}): {file} {string.Join(' ', args)}");
    }

    /// Runs a command silently, ignoring failure (bash `>/dev/null 2>&1 || true`).
    public static void ExecQuiet(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public static void Chmod(string path, UnixFileMode mode)
    {
        try { File.SetUnixFileMode(path, mode); } catch (Exception) { }
    }

    public const UnixFileMode Mode755 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public const UnixFileMode Mode644 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public const UnixFileMode Mode770 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;

    /// Recursively applies dirMode/fileMode under root (including root), not following symlinks.
    public static void ChmodTree(string root, UnixFileMode dirMode, UnixFileMode fileMode)
    {
        if (!Directory.Exists(root)) return;
        Chmod(root, dirMode);
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts))
            Chmod(entry.FullName, entry is DirectoryInfo ? dirMode : fileMode);
    }

    /// Replaces (or creates) a symlink at linkPath pointing to target (`ln -sfn`).
    public static void SymlinkForce(string linkPath, string target)
    {
        if (File.Exists(linkPath) || new FileInfo(linkPath).LinkTarget != null)
            File.Delete(linkPath);
        File.CreateSymbolicLink(linkPath, target);
    }

    /// Picks a random free TCP port in 50000-50999 (200 attempts), like `shuf | ss -ltn`.
    public static int? PickFreePort()
    {
        var listening = IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Select(ep => ep.Port)
            .ToHashSet();
        var candidates = Enumerable.Range(50000, 1000).ToArray();
        Random.Shared.Shuffle(candidates);
        foreach (var p in candidates.Take(200))
            if (!listening.Contains(p)) return p;
        return null;
    }

    public static long UnixTime() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
