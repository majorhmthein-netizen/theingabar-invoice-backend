/* Theingabar Invoice — HTML-only version: authentication + sessions.
 *
 * Security model (OTP-only, 2026-11-21):
 * - Mechanics log in with their phone + a 6-digit OTP sent by SMSPoh's
 *   hosted Verify API (see SmsPoh.cs). NO password anywhere: signup takes
 *   username + phone only, login takes phone + code. Old password_hash
 *   values are dead data, never verified. The Admin signs in with Google
 *   only — OTP login refuses admin accounts, so a stolen phone's SMS
 *   inbox can never become an admin login.
 * - Sessions are opaque ids in Postgres; the browser cookie holds only
 *   the id. NO expiry: a login lasts until the user taps Log out, or the
 *   Admin revokes/suspends/rejects the account (Han 2026-11-21: every
 *   re-login costs one SMS, so frequent logins waste money). The cookie
 *   has no Expires/Max-Age (it lives as long as the browser keeps it)
 *   and the server row expires only after 100 years.
 * - The __Host- cookie prefix is used only over HTTPS (browsers reject a
 *   __Host- cookie without Secure); over plain HTTP (local dev) the
 *   plain name is used instead.
 * - Rate limits are DB-backed (AppDb.rate_limits): OTP requests 3 per
 *   10 min per phone+IP (each request costs one SMS) + 10 per hour per IP;
 *   admin 3 failures → 5-min lockout.
 * - Google sign-in is admin-only; the allowlist is the two addresses.
 */
public static class Auth
{
    public static readonly string[] AdminEmails =
        { "hanmintheinn@gmail.com", "majorhmthein@gmail.com" };

    public static string CookieName(HttpRequest req) =>
        req.IsHttps ? "__Host-tgb-session" : "tgb-session";

    /// <summary>Resolves the current logged-in user from the session cookie.
    /// Returns null when there is no valid session (not logged in, expired,
    /// revoked, or the user is no longer approved — a suspended user's
    /// session dies on the next request).</summary>
    public static async Task<SessionUser?> CurrentUserAsync(HttpContext ctx)
    {
        if (!AppDb.Available) return null;
        if (!ctx.Request.Cookies.TryGetValue(CookieName(ctx.Request), out var sid)) return null;
        var s = await AppDb.FindSessionAsync(sid);
        if (s is null) return null;
        // A suspended/rejected user loses access immediately (status is
        // read fresh from the users table on every request). A pending
        // user keeps a read-only session until the Admin approves.
        if (s.Status is "rejected" or "suspended") return null;
        var user = await AppDb.FindUserByIdAsync(s.UserId);
        if (user is null) return null;
        return new SessionUser(s, user);
    }

    public static async Task SignInAsync(HttpContext ctx, int userId, bool isAdmin)
    {
        var ip = ClientIp(ctx);
        var ua = ctx.Request.Headers.UserAgent.ToString();
        var sid = await AppDb.CreateSessionAsync(userId, isAdmin, ip, ua.Length > 512 ? ua[..512] : ua);
        ctx.Response.Cookies.Append(CookieName(ctx.Request), sid, new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            // No Expires/MaxAge: the cookie lives as long as the browser
            // keeps it; the server session has no expiry either (see
            // CreateSessionAsync). Login ends only via Log out / Revoke.
        });
    }

    public static async Task SignOutAsync(HttpContext ctx)
    {
        if (ctx.Request.Cookies.TryGetValue(CookieName(ctx.Request), out var sid))
            await AppDb.RevokeSessionAsync(sid);
        ctx.Response.Cookies.Delete(CookieName(ctx.Request), new CookieOptions
        {
            Path = "/",
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
        });
    }

    public static string ClientIp(HttpContext ctx)
    {
        // Render sets X-Forwarded-For; take the first (client) entry.
        var fwd = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fwd)) return fwd.Split(',')[0].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /* ---------------- rate limiting ---------------- */

    /// <summary>OTP requests: 3 per 10 min per phone+IP (each request costs
    /// one SMS), plus 10 per hour per IP. Returns (allowed, retryAfterSeconds).</summary>
    public static async Task<(bool Allowed, int RetryAfter)> CheckOtpRequestLimitAsync(string phone, string ip)
    {
        var (okPhone, retryPhone) = await AppDb.CheckRateLimitAsync(
            "otp-req:" + phone + ":" + ip, 3, 600, 600);
        if (!okPhone) return (false, retryPhone);
        return await AppDb.CheckRateLimitAsync("otp-req-ip:" + ip, 10, 3600, 3600);
    }

    public static async Task RecordOtpRequestAsync(string phone, string ip)
    {
        await AppDb.RecordAttemptAsync("otp-req:" + phone + ":" + ip, 600);
        await AppDb.RecordAttemptAsync("otp-req-ip:" + ip, 3600);
    }

    /// <summary>Admin Google sign-in failures: 3 failures → 5-min lockout.</summary>
    public static async Task<(bool Allowed, int RetryAfter)> CheckAdminLimitAsync(string ip) =>
        await AppDb.CheckRateLimitAsync("admin:" + ip, 3, 300, 300);

    public static async Task RecordAdminFailureAsync(string ip) =>
        await AppDb.RecordAttemptAsync("admin:" + ip, 300);

    /* ---------------- validation helpers ---------------- */

    public static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "username is required";
        var u = username.Trim();
        if (u.Length < 3 || u.Length > 20) return "username must be 3–20 characters";
        foreach (var c in u)
            if (!(char.IsLetterOrDigit(c) || c == ' ')) return "username: letters, numbers and spaces only";
        return null;
    }

    public static string? ValidatePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "phone is required";
        var p = phone.Trim().Replace(" ", "").Replace("-", "");
        if (!p.StartsWith("09") || p.Length < 9 || p.Length > 11 || !p.All(char.IsDigit))
            return "phone must start with 09 and be 9–11 digits";
        return null;
    }

    public static string NormalizePhone(string phone) =>
        phone.Trim().Replace(" ", "").Replace("-", "");

    public static bool IsAdminEmail(string? email) =>
        email is not null && AdminEmails.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>The logged-in user for one request (session + user row).</summary>
public sealed record SessionUser(SessionRow Session, UserRow User)
{
    public bool IsAdmin => Session.IsAdmin && User.IsAdmin;
    public bool CanWrite => User.Status == "approved";
}
