using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WWDS.Bridge.Auth;

public sealed class TwitchTokens
{
    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string Login { get; set; } = string.Empty;

    public List<string> Scopes { get; set; } = new();
}

/// <summary>
/// Persists tokens encrypted with Windows DPAPI (current user scope), so a copied file is useless on
/// another account or machine. Tokens are never written in plain text or logged.
/// </summary>
public sealed class TokenStore
{
    private readonly string _path;

    public TokenStore(string path) => _path = path;

    public TwitchTokens? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(_path)));
            return JsonSerializer.Deserialize<TwitchTokens>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(TwitchTokens tokens)
    {
        var bytes = Dpapi.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens)));
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, _path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

/// <summary>Minimal Windows DPAPI (CryptProtectData) binding; avoids an extra package.</summary>
internal static class Dpapi
{
    private const int UiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WWDS.Bridge.tokens.v1");

    public static byte[] Protect(byte[] data) => Run(data, protect: true);

    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var input = Blob.From(data);
        var entropy = Blob.From(Entropy);
        var output = default(Blob);
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            input.Free();
            entropy.Free();
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;

        public static Blob From(byte[] bytes)
        {
            var blob = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        public void Free()
        {
            if (Data != IntPtr.Zero) Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
