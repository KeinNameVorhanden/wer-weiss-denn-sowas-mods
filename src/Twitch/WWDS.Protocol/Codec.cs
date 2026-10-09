using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using WWDS.ModCore.Serialization;

namespace WWDS.Protocol;

/// <summary>Encodes messages as single-line JSON with a "type" field and decodes them back.</summary>
public static class Codec
{
    private static readonly Dictionary<string, Type> Types = new(StringComparer.Ordinal);

    static Codec()
    {
        foreach (var t in new[]
                 {
                     typeof(Hello), typeof(Ping), typeof(Pong), typeof(TwitchStatus), typeof(ChatMessage), typeof(Redemption),
                     typeof(Subscription), typeof(Cheer), typeof(Raid), typeof(Follow), typeof(PollUpdate), typeof(ChannelUpdate),
                     typeof(SendChat), typeof(CreatePoll), typeof(GameState), typeof(ErrorMessage),
                     typeof(LoginStart), typeof(LoginCode), typeof(LoginResult), typeof(Logout), typeof(BridgeSettings), typeof(Shutdown),
                     typeof(CreatePrediction), typeof(ResolvePrediction), typeof(PredictionUpdate),
                 })
        {
            var instance = (Message)Activator.CreateInstance(t);
            Types[instance.Type] = t;
        }
    }

    public static string Encode(Message message)
    {
        if (message is UnknownMessage) throw new ArgumentException("UnknownMessage cannot be sent");
        var obj = Json.ToValue(message);
        obj["type"] = message.Type;
        return Json.Write(obj);
    }

    /// <summary>
    /// Decodes one line. Returns null for malformed input (bad JSON, missing type, wrong field types)
    /// so a single bad line never breaks the connection.
    /// </summary>
    public static Message? Decode(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        try
        {
            if (!(Json.Parse(line) is Dictionary<string, object?> obj)) return null;
            if (!obj.TryGetValue("type", out var t) || !(t is string type) || type.Length == 0) return null;
            if (!Types.TryGetValue(type, out var clr)) return new UnknownMessage(type);
            return (Message)Json.FromValue(clr, obj)!;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Splits a byte stream into UTF-8 lines. Handles messages split across reads and multi-byte
/// characters split across reads; drops lines longer than <see cref="ProtocolInfo.MaxLineBytes"/>.
/// </summary>
public sealed class LineFramer
{
    private readonly MemoryStream _buffer = new();
    private bool _discarding;

    public int DroppedOversizedLines { get; private set; }

    public IList<string> Push(byte[] data, int offset, int count)
    {
        var lines = new List<string>();
        for (var i = offset; i < offset + count; i++)
        {
            var b = data[i];
            if (b == (byte)'\n')
            {
                if (!_discarding)
                {
                    var bytes = _buffer.ToArray();
                    var length = bytes.Length;
                    if (length > 0 && bytes[length - 1] == (byte)'\r') length--;
                    if (length > 0) lines.Add(Encoding.UTF8.GetString(bytes, 0, length));
                }

                _buffer.SetLength(0);
                _discarding = false;
                continue;
            }

            if (_discarding) continue;
            if (_buffer.Length >= ProtocolInfo.MaxLineBytes)
            {
                _discarding = true;
                DroppedOversizedLines++;
                _buffer.SetLength(0);
                continue;
            }

            _buffer.WriteByte(b);
        }

        return lines;
    }

    public static byte[] Frame(string line) => Encoding.UTF8.GetBytes(line + "\n");
}
