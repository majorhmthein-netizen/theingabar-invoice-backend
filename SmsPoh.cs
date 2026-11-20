/* Theingabar Invoice — SMSPoh Verify API client (OTP-only login).
 *
 * The backend never generates, stores or checks OTP codes itself:
 * SMSPoh's hosted Verify API does that server-side. Two calls:
 *   Request: POST https://v3.smspoh.com/api/otp/request?from=SMSPohTest&to=09...&brand=Theingabar
 *            &pinLength=6&ttl=300&maxInvalidAttempts=5&accessToken=...
 *            -> 201 returns { "requestId": "..." } (the SMS is sent by SMSPoh).
 *   Verify:  POST https://v3.smspoh.com/api/otp/verify?requestId=...&code=...&accessToken=...
 *            -> 201 means the code is correct; 400 means wrong code.
 * Auth is a QUERY parameter: accessToken = base64(APIKey:APISecret) — NOT a
 * Bearer header (the raw SMS API uses Bearer/Basic instead; the Verify API
 * uses the query token). Verified live 2026-11-21: a dummy token returns
 * 401 on both endpoints, so the endpoint shape and auth method are right;
 * the real credentials are only in Render env vars SMSPOH_API_KEY /
 * SMSPOH_API_SECRET, never in source or in chat.
 * Defaults: 6-digit code, 5-minute expiry (ttl 300), 5 wrong tries per
 * code (maxInvalidAttempts 5 — after that the requestId is dead and the
 * user must request a new code). Each request costs one SMS (18 MMK on
 * the FREE tier), so the login page throttles requests per phone+IP.
 * Errors are mapped to short codes, never to raw provider text, and the
 * access token and the code are never logged.
 */
using System.Text;
using System.Text.Json;

public static class SmsPoh
{
    const string BaseUrl = "https://v3.smspoh.com/api/otp";
    const string From = "SMSPohTest";    // SMSPoh test sender (usable before onboarding).
                                         // "SMSPoh" is NOT registered on this account
                                         // (error 10 Invalid Sender ID, 2026-11-21).
                                         // Switch to "Theingabar" once that sender ID is approved.
    const string Brand = "Theingabar";     // shop brand name (<= 11 chars)
    public const int PinLength = 6;
    public const int TtlSeconds = 300;     // 5 minutes
    const int MaxInvalidAttempts = 5;

    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(45) };

    public static bool Configured =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMSPOH_API_KEY")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SMSPOH_API_SECRET"));

    static string? AccessToken()
    {
        var key = Environment.GetEnvironmentVariable("SMSPOH_API_KEY");
        var secret = Environment.GetEnvironmentVariable("SMSPOH_API_SECRET");
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret)) return null;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":" + secret));
    }

    static string Url(string path, params (string Name, string Value)[] query)
    {
        var q = string.Join("&", query.Select(p =>
            Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value)));
        return BaseUrl + path + "?" + q;
    }

    /// <summary>Sends a 6-digit OTP to a Myanmar phone. Returns (ok, requestId, error)
    /// where error is "config" (credentials missing/bad), "bad-request" or "service".</summary>
    public static async Task<(bool Ok, string? RequestId, string? Error)> RequestOtpAsync(string to)
    {
        var token = AccessToken();
        if (token is null)
        {
            Console.WriteLine("[smspoh] OTP requested but SMSPOH_API_KEY/SECRET are not set.");
            return (false, null, "config");
        }
        try
        {
            var url = Url("/request",
                ("from", From), ("to", to), ("brand", Brand),
                ("pinLength", PinLength.ToString()), ("ttl", TtlSeconds.ToString()),
                ("maxInvalidAttempts", MaxInvalidAttempts.ToString()),
                ("accessToken", token));
            using var resp = await Client.PostAsync(url, null);
            if ((int)resp.StatusCode == 201)
            {
                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("requestId", out var id) &&
                    id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString()))
                    return (true, id.GetString(), null);
                Console.WriteLine("[smspoh] request returned 201 without a requestId.");
                return (false, null, "service");
            }
            if ((int)resp.StatusCode == 401)
            {
                Console.WriteLine("[smspoh] request rejected 401 — check SMSPOH_API_KEY/SECRET.");
                return (false, null, "config");
            }
            Console.WriteLine("[smspoh] request failed with HTTP " + (int)resp.StatusCode + ".");
            return (false, null, (int)resp.StatusCode == 400 ? "bad-request" : "service");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine("[smspoh] request failed: " + ex.GetType().Name + ".");
            return (false, null, "service");
        }
    }

    /// <summary>Checks a code against a requestId. Returns (ok, error) where
    /// error is "wrong-code", "config" or "service".</summary>
    public static async Task<(bool Ok, string? Error)> VerifyOtpAsync(string requestId, string code)
    {
        var token = AccessToken();
        if (token is null) return (false, "config");
        try
        {
            var url = Url("/verify",
                ("requestId", requestId), ("code", code), ("accessToken", token));
            using var resp = await Client.PostAsync(url, null);
            if ((int)resp.StatusCode == 201) return (true, null);
            if ((int)resp.StatusCode == 400) return (false, "wrong-code");
            if ((int)resp.StatusCode == 401)
            {
                Console.WriteLine("[smspoh] verify rejected 401 — check SMSPOH_API_KEY/SECRET.");
                return (false, "config");
            }
            Console.WriteLine("[smspoh] verify failed with HTTP " + (int)resp.StatusCode + ".");
            return (false, "service");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine("[smspoh] verify failed: " + ex.GetType().Name + ".");
            return (false, "service");
        }
    }
}
