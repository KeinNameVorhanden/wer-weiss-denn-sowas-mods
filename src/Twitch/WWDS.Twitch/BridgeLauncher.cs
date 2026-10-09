using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using WWDS.ModCore.Logging;

namespace WWDS.Twitch;

/// <summary>
/// Starts the bundled WWDS.Bridge invisibly when the game needs it (in-game Twitch login), tied to the
/// game process: the bridge exits when the game exits. Never starts a second instance if the port is
/// already served (e.g. the streamer runs the bridge by hand).
/// </summary>
public sealed class BridgeLauncher
{
    private readonly ModLog _log;
    private Process? _process;

    public BridgeLauncher(ModLog log) => _log = log;

    /// <summary>True if this game process started the running bridge.</summary>
    public bool StartedByUs => _process != null && !HasExited(_process);

    /// <summary>Default location: &lt;plugin folder&gt;/bridge/WWDS.Bridge.exe.</summary>
    public static string DefaultPath()
    {
        var dir = Path.GetDirectoryName(typeof(BridgeLauncher).Assembly.Location) ?? ".";
        return Path.Combine(Path.Combine(dir, "bridge"), "WWDS.Bridge.exe");
    }

    public static bool PortInUse(int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            var ok = result.AsyncWaitHandle.WaitOne(300);
            if (ok) client.EndConnect(result);
            return ok && client.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Starts the bridge unless one already listens on <paramref name="port"/>.</summary>
    public string EnsureRunning(string exePath, int port)
    {
        if (StartedByUs) return "bridge running (started by the game)";
        if (PortInUse(port)) return "bridge already running";
        if (!File.Exists(exePath)) return "bridge not found at " + exePath;
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--background --parent " + Process.GetCurrentProcess().Id,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? ".",
            };
            _process = Process.Start(info);
            _log.Info("Started bridge (pid " + (_process?.Id ?? 0) + ")");
            return "bridge started";
        }
        catch (Exception ex)
        {
            _log.Error("Could not start the bridge", ex);
            return "bridge start failed: " + ex.Message;
        }
    }

    private static bool HasExited(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
