using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WWDS.Bridge.Auth;

/// <summary>
/// OAuth Device Code Grant for a public Twitch client (no client secret), token refresh and
/// validation, per https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/.
/// </summary>
public sealed class TwitchAuth
{
    public static readonly string[] Scopes =
    {
        "user:read:chat",
        "user:write:chat",
        "channel:read:redemptions",
        "channel:read:subscriptions",
        "bits:read",
        "moderator:read:followers",
        "channel:manage:polls",
        "channel:manage:predictions",
    };

    private const string DeviceUrl = "https://id.twitch.tv/oauth2/device";
    private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string ValidateUrl = "https://id.twitch.tv/oauth2/validate";

    private readonly HttpClient _http;
    private readonly string _clientId;

    public TwitchAuth(HttpClient http, string clientId)
    {
        _http = http;
        _clientId = clientId;
    }

    public string ClientId => _clientId;

    public sealed record DeviceCode(string Code, string UserCode, string VerificationUri, int ExpiresIn, int Interval);

    public async Task<DeviceCode> StartDeviceFlowAsync(CancellationToken ct)
    {
        using var resp = await _http.PostAsync(DeviceUrl, Form(("client_id", _clientId), ("scopes", string.Join(' ', Scopes))), ct).ConfigureAwait(false);
        var json = await ReadJson(resp, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new AuthException("device flow start failed: " + Message(json));
        return new DeviceCode(
            json.GetProperty("device_code").GetString()!,
            json.GetProperty("user_code").GetString()!,
            json.GetProperty("verification_uri").GetString()!,
            json.GetProperty("expires_in").GetInt32(),
            json.GetProperty("interval").GetInt32());
    }

    /// <summary>Polls until the user authorized in the browser, the code expired, or cancellation.</summary>
    public async Task<TwitchTokens> PollDeviceFlowAsync(DeviceCode code, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(1, code.Interval));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            using var resp = await _http.PostAsync(TokenUrl, Form(
                ("client_id", _clientId),
                ("scopes", string.Join(' ', Scopes)),
                ("device_code", code.Code),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code")), ct).ConfigureAwait(false);
            var json = await ReadJson(resp, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return await CompleteAsync(json, ct).ConfigureAwait(false);

            var message = Message(json);
            if (message.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase)) continue;
            if (message.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
            {
                interval += TimeSpan.FromSeconds(5);
                continue;
            }

            throw new AuthException("authorization failed: " + message);
        }

        throw new AuthException("device code expired before authorization");
    }

    public async Task<TwitchTokens> RefreshAsync(TwitchTokens tokens, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(TokenUrl, Form(
            ("client_id", _clientId),
            ("grant_type", "refresh_token"),
            ("refresh_token", tokens.RefreshToken)), ct).ConfigureAwait(false);
        var json = await ReadJson(resp, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new AuthException("refresh failed (log in again with 'login'): " + Message(json));
        return await CompleteAsync(json, ct).ConfigureAwait(false);
    }

    /// <summary>Returns (login, userId, secondsLeft) or null if the token is invalid.</summary>
    public async Task<(string Login, string UserId, int ExpiresIn, List<string> Scopes)?> ValidateAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ValidateUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return null;
        var json = await ReadJson(resp, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new AuthException("validate failed: " + Message(json));
        var scopes = new List<string>();
        if (json.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in s.EnumerateArray()) scopes.Add(x.GetString() ?? string.Empty);
        }

        return (json.GetProperty("login").GetString() ?? string.Empty, json.GetProperty("user_id").GetString() ?? string.Empty, json.GetProperty("expires_in").GetInt32(), scopes);
    }

    private async Task<TwitchTokens> CompleteAsync(JsonElement json, CancellationToken ct)
    {
        var tokens = new TwitchTokens
        {
            AccessToken = json.GetProperty("access_token").GetString()!,
            RefreshToken = json.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? string.Empty : string.Empty,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600),
        };
        var info = await ValidateAsync(tokens.AccessToken, ct).ConfigureAwait(false) ?? throw new AuthException("new token did not validate");
        tokens.Login = info.Login;
        tokens.UserId = info.UserId;
        tokens.Scopes = info.Scopes;
        return tokens;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] pairs) =>
        new(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static string Message(JsonElement json) =>
        json.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown error" : json.ToString();
}

public sealed class AuthException : Exception
{
    public AuthException(string message)
        : base(message)
    {
    }
}
