using System.Diagnostics;
using WWDS.Bridge;
using WWDS.Protocol;

// WWDS.Bridge: companion app between Twitch and the in-game harness.
//   WWDS.Bridge                              console mode (Twitch if logged in, plus mock commands)
//   WWDS.Bridge --mock [--script <file>]     no Twitch, mock audience only (optionally scripted)
//   WWDS.Bridge --background --parent <pid>  started by the game: no console, log file, exits with the game
//   WWDS.Bridge login | logout | status      account management from a terminal
var paths = new BridgePaths(Environment.GetEnvironmentVariable("WWDS_BRIDGE_HOME"));
var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "run";
var background = args.Contains("--background");
var logWriter = background ? new StreamWriter(Path.Combine(paths.Root, "bridge.log"), append: false) { AutoFlush = true } : null;

void Log(string message)
{
    var line = DateTime.Now.ToString("HH:mm:ss") + " " + LogText.Redact(message);
    if (logWriter != null) logWriter.WriteLine(line);
    else Console.WriteLine(line);
}

var config0 = BridgeConfig.Load(paths);
await using var host = new BridgeHost(paths, config0.MockOnly || args.Contains("--mock"), Log);

switch (command)
{
    case "login":
        await host.LoginAsync();
        return 0;
    case "logout":
        await host.LogoutAsync();
        return 0;
    case "status":
        var s = host.Settings();
        Console.WriteLine("Config:   " + paths.ConfigFile);
        Console.WriteLine("ClientId: " + (s.ClientIdBuiltIn ? "built into this release" : s.HasClientId ? "set" : "MISSING"));
        Console.WriteLine("Port:     " + host.Config.Port + ", DryRun: " + host.Config.DryRun);
        Console.WriteLine("Login:    " + (s.Login.Length > 0 ? s.Login : "not logged in"));
        return 0;
}

var stop = new TaskCompletionSource();
host.ShutdownRequested += () => stop.TrySetResult();
await host.StartAsync();

var scriptIndex = Array.IndexOf(args, "--script");
if (scriptIndex >= 0 && scriptIndex + 1 < args.Length) _ = RunScript(args[scriptIndex + 1]);

if (background)
{
    var parentIndex = Array.IndexOf(args, "--parent");
    if (parentIndex >= 0 && parentIndex + 1 < args.Length && int.TryParse(args[parentIndex + 1], out var pid)) _ = WatchParent(pid);
    await stop.Task;
    Log("bridge stopping");
    return 0;
}

Console.WriteLine("Type 'help' for commands.");
var stdin = Task.Run(ConsoleLoop);
await Task.WhenAny(stdin, stop.Task);
return 0;

async Task ConsoleLoop()
{
    while (true)
    {
        var line = Console.ReadLine();
        if (line == null)
        {
            // Unattended (stdin closed): keep serving until stopped.
            if (scriptIndex >= 0) await Task.Delay(Timeout.Infinite);
            return;
        }

        line = line.Trim();
        if (line.Length == 0) continue;
        var lower = line.ToLowerInvariant();
        if (lower is "quit" or "exit") return;
        switch (lower)
        {
            case "help":
                Console.WriteLine("status | state | login | logout | say <text> | quit\n" + MockAudience.Help);
                continue;
            case "status":
                var t = host.Twitch;
                Console.WriteLine("Twitch: " + (t?.Status.State ?? host.Server.LastStatus.State) + " " + (t?.Status.Detail ?? host.Server.LastStatus.Detail) + " | games connected: " + host.Server.ConnectedGames + " | dry run: " + host.Config.DryRun);
                continue;
            case "state":
                var st = host.LastState;
                Console.WriteLine(st == null ? "no game state yet" : st.Game + " " + st.Phase + " | " + st.TeamA + " " + st.ScoreA + " : " + st.ScoreB + " " + st.TeamB + "\n  " + st.Question + "\n  " + string.Join("\n  ", st.Answers) + (st.Interaction.Length > 0 ? "\n  " + st.Interaction + "\n    " + string.Join("\n    ", st.Tally) : string.Empty));
                continue;
            case "login":
                host.Server.Broadcast(new TwitchStatus { State = "Authenticating", Detail = "login started from the console" });
                _ = PrintLoginCode();
                continue;
            case "logout":
                await host.LogoutAsync();
                continue;
        }

        if (lower.StartsWith("say "))
        {
            if (host.Twitch != null) await host.Twitch.SendChatAsync(line[4..]);
            else Log("not connected to Twitch");
            continue;
        }

        var messages = host.Mock.Run(line);
        if (messages == null)
        {
            Console.WriteLine("unknown command, type 'help'");
            continue;
        }

        foreach (var m in messages) host.Server.Broadcast(m);
        Log("mock: sent " + messages.Count + " message(s) to " + host.Server.ConnectedGames + " game(s)");
    }
}

async Task PrintLoginCode()
{
    void OnMessage(Message m)
    {
        if (m is LoginCode c) Console.WriteLine("Open " + c.VerificationUri + " (log in as the BROADCASTER) and enter code: " + c.UserCode);
        if (m is LoginResult r) Console.WriteLine(r.Ok ? "Logged in as " + r.Login : "Login failed: " + r.Error);
    }

    host.Server.Broadcasted += OnMessage;
    await host.LoginAsync();
    host.Server.Broadcasted -= OnMessage;
}

async Task WatchParent(int pid)
{
    try
    {
        using var parent = Process.GetProcessById(pid);
        await parent.WaitForExitAsync();
    }
    catch (ArgumentException)
    {
        // Parent already gone.
    }

    Log("game process exited");
    stop.TrySetResult();
}

// Mock script: one mock command per line, plus "wait <seconds>", "mark" (loop target), "loop" (jump back
// to the last mark, or the top) and "#" comments.
async Task RunScript(string path)
{
    var lines = File.ReadAllLines(path);
    Log("running mock script " + path + " (" + lines.Length + " lines)");
    var mark = -1;
    for (var i = 0; i < lines.Length; i++)
    {
        var l = lines[i].Trim();
        if (l.Length == 0 || l.StartsWith('#')) continue;
        if (l.Equals("mark", StringComparison.OrdinalIgnoreCase))
        {
            mark = i;
            continue;
        }

        if (l.StartsWith("wait ", StringComparison.OrdinalIgnoreCase) && double.TryParse(l[5..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs))
        {
            await Task.Delay(TimeSpan.FromSeconds(secs));
            continue;
        }

        if (l.Equals("loop", StringComparison.OrdinalIgnoreCase))
        {
            i = mark;
            continue;
        }

        var msgs = host.Mock.Run(l);
        if (msgs == null)
        {
            Log("script: unknown line '" + l + "'");
            continue;
        }

        foreach (var m in msgs) host.Server.Broadcast(m);
    }
}
