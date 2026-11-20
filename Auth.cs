/* Theingabar Invoice — HTML-only version: authentication + sessions.
 *
 * Security model (plan §7):
 * - Login = username + password (min 6 chars), hashed server-side with
 *   ASP.NET Core PasswordHasher (PBKDF2). The old client pinDigest hashes
 *   are never reused and cannot be migrated (one-way, weak 32-bit).
 * - Sessions are opaque ids in Postgres; the browser cookie holds only
 *   the id. Q7: NO persistent login — the cookie has no Expires/Max-Age
 *   (session cookie, dies on browser close); the server row also expires
 *   after 12 h as a backstop. Log in again on every open.
 * - The __Host- cookie prefix is used only over HTTPS (browsers reject a
 *   __Host- cookie without Secure); over plain HTTP (local dev) the
 *   plain name is used instead.
 * - Rate limits are DB-backed (AppDb.rate_limits): login 3/min per
 *   account+IP, admin 3 failures → 5-min lockout.
 * - Google sign-in is admin-only; the allowlist is the two addresses.
 */
using Microsoft.AspNetCore.Identity;

public static class Auth
{
    public static readonly string[] AdminEmails =
        { "hanmintheinn@gmail.com", "majorhmthein@gmail.com" };

    static readonly PasswordHasher<string> Hasher = new();

    public static string CookieName(HttpRequest req) =>
        req.IsHttps ? "__Host-tgb-session" : "tgb-session";

    public static string HashPassword(string password) =>
        Hasher.HashPassword("user", password);

    public static bool VerifyPassword(string? hash, string password)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        return Hasher.VerifyHashedPassword("user", hash, password)
            == PasswordVerificationResult.Success;
    }

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
            // No Expires/MaxAge: session cookie — dies on browser close (Q7).
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

    public static async Task<(bool Allowed, int RetryAfter)> CheckLoginLimitAsync(string username, string ip)
    {
        var (okAcct, retryAcct) = await AppDb.CheckRateLimitAsync(
            "login:" + username.Trim().ToLowerInvariant() + ":" + ip, 3, 60, 60);
        if (!okAcct) return (false, retryAcct);
        return await AppDb.CheckRateLimitAsync("login-ip:" + ip, 20, 60, 60);
    }

    public static async Task RecordLoginFailureAsync(string username, string ip)
    {
        await AppDb.RecordAttemptAsync("login:" + username.Trim().ToLowerInvariant() + ":" + ip, 60);
        await AppDb.RecordAttemptAsync("login-ip:" + ip, 60);
    }

    public static async Task ResetLoginLimitAsync(string username, string ip)
    {
        await AppDb.ResetRateLimitAsync("login:" + username.Trim().ToLowerInvariant() + ":" + ip);
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

    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return "password is required";
        if (password.Length < 6) return "password must be at least 6 characters";
        if (password.Length > 128) return "password must be at most 128 characters";
        return null;
    }

    public static bool IsAdminEmail(string? email) =>
        email is not null && AdminEmails.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>The logged-in user for one request (session + user row).</summary>
public sealed record SessionUser(SessionRow Session, UserRow User)
{
    public bool IsAdmin => Session.IsAdmin && User.IsAdmin;
    public bool CanWrite => User.Status == "approved";
}
