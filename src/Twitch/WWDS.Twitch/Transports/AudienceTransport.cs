using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using WWDS.ModCore.Events;
using WWDS.Protocol;

namespace WWDS.Twitch.Transports;

/// <summary>
/// Source of audience messages. Implementations run their own background thread and must never throw
/// into the caller. Callbacks arrive on that background thread; the Twitch feature marshals them to
/// the main thread.
/// </summary>
public interface IAudienceTransport : IDisposable
{
    string Name { get; }

    AudienceLinkState State { get; }

    /// <summary>True if this transport can post chat messages (the bridge can, anonymous IRC cannot).</summary>
    bool CanSend { get; }

    event Action<Message>? Received;

    event Action<AudienceLinkState, string>? StateChanged;

    void Start();

    void Send(Message message);
}

/// <summary>Shared reconnect loop: connect, read lines until the socket closes, back off, repeat.</summary>
public abstract class SocketTransport : IAudienceTransport
{
    private static readonly int[] BackoffSeconds = { 1, 2, 5, 10, 20, 30 };

    private readonly object _sendGate = new();
    private Thread? _thread;
    private volatile bool _stopping;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public abstract string Name { get; }

    public AudienceLinkState State { get; private set; } = AudienceLinkState.Disconnected;

    public abstract bool CanSend { get; }

    public event Action<Message>? Received;

    public event Action<AudienceLinkState, string>? StateChanged;

    protected abstract string Host { get; }

    protected abstract int Port { get; }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "WWDS " + Name };
        _thread.Start();
    }

    public void Send(Message message)
    {
        if (!CanSend) return;
        WriteLine(Codec.Encode(message));
    }

    protected void WriteLine(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_sendGate)
        {
            var s = _stream;
            if (s == null) return;
            try
            {
                s.Write(bytes, 0, bytes.Length);
            }
            catch (IOException)
            {
                // Reader loop notices the broken connection and reconnects.
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    protected void Emit(Message message)
    {
        try
        {
            Received?.Invoke(message);
        }
        catch
        {
            // Consumer errors must not kill the transport thread.
        }
    }

    protected void SetState(AudienceLinkState state, string detail)
    {
        State = state;
        try
        {
            StateChanged?.Invoke(state, detail);
        }
        catch
        {
        }
    }

    /// <summary>Called right after the TCP connection is open (send handshake).</summary>
    protected abstract void OnConnected();

    /// <summary>Handles one received line.</summary>
    protected abstract void OnLine(string line);

    private void Run()
    {
        var attempt = 0;
        while (!_stopping)
        {
            try
            {
                SetState(AudienceLinkState.Connecting, Host + ":" + Port);
                using var client = new TcpClient();
                client.Connect(Host, Port);
                client.NoDelay = true;
                lock (_sendGate)
                {
                    _client = client;
                    _stream = client.GetStream();
                }

                attempt = 0;
                OnConnected();
                ReadLoop(_stream!);
            }
            catch (Exception ex) when (ex is SocketException || ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                if (!_stopping) SetState(AudienceLinkState.Disconnected, ex.Message);
            }
            catch (Exception ex)
            {
                if (!_stopping) SetState(AudienceLinkState.Error, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                lock (_sendGate)
                {
                    _stream = null;
                    _client = null;
                }
            }

            if (_stopping) break;
            var wait = BackoffSeconds[Math.Min(attempt++, BackoffSeconds.Length - 1)];
            for (var i = 0; i < wait * 10 && !_stopping; i++) Thread.Sleep(100);
        }

        SetState(AudienceLinkState.Disconnected, "stopped");
    }

    private void ReadLoop(NetworkStream stream)
    {
        var framer = new LineFramer();
        var buffer = new byte[8192];
        while (!_stopping)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) throw new IOException("connection closed by peer");
            foreach (var line in framer.Push(buffer, 0, read)) OnLine(line);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        lock (_sendGate)
        {
            try
            {
                _client?.Close();
            }
            catch
            {
            }
        }

        _thread = null;
    }
}

/// <summary>Connects to the companion bridge (WWDS.Bridge) on localhost.</summary>
public sealed class BridgeClientTransport : SocketTransport
{
    private readonly int _port;
    private readonly Func<Hello> _hello;

    /// <param name="hello">Built on every connect, so a key file created later (bridge started by the game) is picked up.</param>
    public BridgeClientTransport(int port, Func<Hello> hello)
    {
        _port = port;
        _hello = hello;
    }

    public override string Name => "bridge";

    public override bool CanSend => State == AudienceLinkState.Connected;

    protected override string Host => "127.0.0.1";

    protected override int Port => _port;

    protected override void OnConnected() => WriteLine(Codec.Encode(_hello()));

    protected override void OnLine(string line)
    {
        var msg = Codec.Decode(line);
        switch (msg)
        {
            case null:
            case UnknownMessage:
                return;
            case Hello:
                SetState(AudienceLinkState.Connected, "bridge handshake ok");
                return;
            case ErrorMessage err:
                SetState(AudienceLinkState.Error, err.Message);
                return;
            case Ping ping:
                WriteLine(Codec.Encode(new Pong { Seq = ping.Seq }));
                return;
            default:
                Emit(msg);
                return;
        }
    }

    /// <summary>Shared secret written by the bridge, so other local programs cannot inject chat.</summary>
    public static string ReadKey(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string DefaultKeyPath() =>
        Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WWDS"), "bridge.key");
}

/// <summary>
/// Read-only, anonymous Twitch chat over plain IRC (port 6667, unencrypted). Used when the bridge is
/// not running. Delivers chat only (no redemptions, subs, raids, polls) and cannot send messages.
/// </summary>
public sealed class IrcFallbackTransport : SocketTransport
{
    private readonly string _channel;
    private readonly string _nick;

    public IrcFallbackTransport(string channel)
    {
        _channel = channel.Trim().TrimStart('#').ToLowerInvariant();
        _nick = "justinfan" + new Random().Next(10000, 99999);
    }

    public override string Name => "irc";

    public override bool CanSend => false;

    protected override string Host => "irc.chat.twitch.tv";

    protected override int Port => 6667;

    protected override void OnConnected()
    {
        WriteLine("CAP REQ :twitch.tv/tags twitch.tv/commands");
        WriteLine("NICK " + _nick);
        WriteLine("JOIN #" + _channel);
    }

    protected override void OnLine(string line)
    {
        var irc = Irc.IrcParser.Parse(line);
        if (irc == null) return;
        switch (irc.Command)
        {
            case "PING":
                WriteLine("PONG :" + irc.Trailing);
                break;
            case "RECONNECT":
                throw new IOException("server requested reconnect");
            case "366":
                SetState(AudienceLinkState.Connected, "joined #" + _channel + " (read-only)");
                break;
            case "PRIVMSG":
                var chat = Irc.IrcParser.ToChat(irc);
                if (chat != null) Emit(chat);
                break;
        }
    }
}

/// <summary>In-process transport for tests and the in-game audience simulator.</summary>
public sealed class LoopbackTransport : IAudienceTransport
{
    public List<Message> Sent { get; } = new();

    public string Name => "mock";

    public AudienceLinkState State { get; private set; } = AudienceLinkState.Disconnected;

    public bool CanSend => true;

    public event Action<Message>? Received;

    public event Action<AudienceLinkState, string>? StateChanged;

    public void Start()
    {
        State = AudienceLinkState.Connected;
        StateChanged?.Invoke(State, "mock audience");
    }

    public void Send(Message message) => Sent.Add(message);

    public void Inject(Message message) => Received?.Invoke(message);

    public void Dispose()
    {
        State = AudienceLinkState.Disconnected;
    }
}
