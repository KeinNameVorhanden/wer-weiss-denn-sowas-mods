using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WWDS.Protocol;

namespace WWDS.Bridge;

/// <summary>
/// Localhost-only TCP server for game instances. A client must send <see cref="Hello"/> with the shared
/// key first; anything else, or a wrong key, closes the connection. Messages from Twitch are broadcast
/// to every authenticated game; messages from games are raised through <see cref="FromGame"/>.
/// </summary>
public sealed class GameServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly string _key;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<int, Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private int _nextId;

    public GameServer(int port, string key, Action<string> log)
    {
        _port = port;
        _key = key;
        _log = log;
    }

    public event Action<Message, string>? FromGame;

    public event Action<string>? GameConnected;

    public int ConnectedGames => _clients.Values.Count(c => c.Authenticated);

    public int Port => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? _port;

    /// <summary>Status re-sent to every game right after its handshake.</summary>
    public TwitchStatus LastStatus { get; set; } = new() { State = "Disconnected" };

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _ = AcceptLoop(_cts.Token);
    }

    /// <summary>Raised for every broadcast (console mode prints login codes from it).</summary>
    public event Action<Message>? Broadcasted;

    public void Broadcast(Message message)
    {
        Broadcasted?.Invoke(message);
        var bytes = LineFramer.Frame(Codec.Encode(message));
        foreach (var c in _clients.Values)
        {
            if (c.Authenticated) _ = c.SendAsync(bytes);
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log("accept failed: " + ex.Message);
                continue;
            }

            var client = new Client(Interlocked.Increment(ref _nextId), tcp);
            _clients[client.Id] = client;
            _ = Serve(client, ct);
        }
    }

    private async Task Serve(Client client, CancellationToken ct)
    {
        var framer = new LineFramer();
        var buffer = new byte[8192];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!ct.IsCancellationRequested)
            {
                var token = client.Authenticated ? ct : timeout.Token;
                var read = await client.Stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read <= 0) break;
                foreach (var line in framer.Push(buffer, 0, read))
                {
                    var msg = Codec.Decode(line);
                    if (!client.Authenticated)
                    {
                        if (msg is Hello hello && hello.Role == "game" && CryptoEquals(hello.Key, _key))
                        {
                            client.Authenticated = true;
                            client.Game = hello.Game;
                            await client.SendAsync(LineFramer.Frame(Codec.Encode(new Hello { Role = "bridge", Version = BridgeInfo.Version }))).ConfigureAwait(false);
                            await client.SendAsync(LineFramer.Frame(Codec.Encode(LastStatus))).ConfigureAwait(false);
                            _log("game connected: " + hello.Game + " (harness " + hello.Version + ")");
                            GameConnected?.Invoke(hello.Game);
                            continue;
                        }

                        await client.SendAsync(LineFramer.Frame(Codec.Encode(new ErrorMessage { Message = "handshake rejected (wrong key or protocol)" }))).ConfigureAwait(false);
                        _log("rejected a connection: bad handshake");
                        return;
                    }

                    if (msg != null && msg is not UnknownMessage) FromGame?.Invoke(msg, client.Game);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            if (client.Authenticated) _log("game disconnected: " + client.Game);
            client.Dispose();
        }
    }

    private static bool CryptoEquals(string a, string b) =>
        a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        foreach (var c in _clients.Values) c.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private sealed class Client : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly TcpClient _tcp;

        public Client(int id, TcpClient tcp)
        {
            Id = id;
            _tcp = tcp;
            Stream = tcp.GetStream();
        }

        public int Id { get; }

        public NetworkStream Stream { get; }

        public bool Authenticated { get; set; }

        public string Game { get; set; } = string.Empty;

        public async Task SendAsync(byte[] bytes)
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Stream.WriteAsync(bytes).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Broken connection: the reader loop cleans up.
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            try
            {
                _tcp.Close();
            }
            catch
            {
            }
        }
    }
}

public static class BridgeInfo
{
    public const string Version = WWDS.WwdsVersion.Value;
}
