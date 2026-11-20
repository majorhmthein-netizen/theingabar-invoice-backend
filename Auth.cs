/* Theingabar Invoice — HTML-only version: authentication + sessions.
 *
 * Security model (PIN login, 2026-11-21 — OTP REMOVED, see below):
 * - Mechanics log in with their phone + a 4-digit PIN. Every account
 *   starts with the default PIN 1111 (Han 2026-11-21: "all user default
 *   passcode pin 1111"). The PIN is stored hashed (PBKDF2-SHA256) in
 *   users.password_hash; a NULL hash also means 1111, for accounts
 *   created before PIN login. The PIN can be changed on the User page
 *   (changing it logs out other devices). The Admin signs in with
 *   Google only — PIN login refuses admin accounts.
 * - OTP is GONE (Han 2026-11-21: "Otp မသုံးတော့ဘူး"): SMSPoh has no
 *   working sender — no registered sender ID, and the test sender
 *   "SMSPohTest" is rejected with the same error 10 Invalid Sender ID.
 *   SmsPoh.cs stays in the repo, parked, in case a sender ID is ever
 *   approved; nothing calls it. Old password_hash values from the
 *   password era are dead data, never verified as passwords.
 * - Sessions are opaque ids in Postgres; the browser cookie holds only
 *   the id. NO expiry: a login lasts until the user taps Log out, or the
 *   Admin revokes/suspends/rejects the account. The cookie has no
 *   Expires/Max-Age (it lives as long as the browser keeps it) and the
 *   server row expires only after 100 years.
 * - The __Host- cookie prefix is used only over HTTPS (browsers reject a
 *   __Host- cookie without Secure); over plain HTTP (local dev) the
 *   plain name is used instead.
 * - Rate limits are DB-backed (AppDb.rate_limits): PIN attempts 5 per
 *   10 min per phone+IP (then an escalating lockout: 1st 1 min, 2nd
 *   5 min, 3rd+ 10 min) + 10 per hour per IP; admin 3 failures →
 *   5-min lockout.
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

    /* ---------------- PIN ---------------- */

    /// <summary>Default PIN for every account (Han 2026-11-21).
    /// A NULL stored hash also means this PIN.</summary>
    public const string DefaultPin = "1111";

    /// <summary>PBKDF2-SHA256 hash of a PIN, stored as
    /// "pbkdf2-sha256:iterations:salt:hash" (base64 salt+hash).</summary>
    public static string HashPin(string pin)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            pin, salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return "pbkdf2-sha256:100000:" + Convert.ToBase64String(salt) + ":" +
            Convert.ToBase64String(hash);
    }

    /// <summary>Verifies a PIN against its stored hash. A NULL/empty hash
    /// means no PIN was ever set, so the default PIN 1111 works.</summary>
    public static bool VerifyPin(string pin, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return pin == DefaultPin;
        var parts = storedHash.Split(':');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" ||
            !int.TryParse(parts[1], out var iters)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            pin, salt, iters, System.Security.Cryptography.HashAlgorithmName.SHA256,
            expected.Length);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string? ValidatePin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return "PIN is required";
        var p = pin.Trim();
        if (p.Length != 4 || !p.All(char.IsDigit)) return "PIN must be 4 digits";
        return null;
    }

    /* ---------------- rate limiting ---------------- */

    /// <summary>PIN login attempts: 5 per 10 min per phone+IP, plus 10 per hour
    /// per IP. Lockouts escalate: 1st 1 min, 2nd 5 min, 3rd+ 10 min
    /// (see Db.CheckEscalatingThrottleAsync). Returns (allowed, retryAfterSeconds).</summary>
    public static async Task<(bool Allowed, int RetryAfter)> CheckPinAttemptLimitAsync(string phone, string ip)
    {
        var r = await AppDb.CheckEscalatingThrottleAsync("pin-attempt:" + phone + ":" + ip, 5, 600);
        if (!r.Allowed) return r;
        return await AppDb.CheckEscalatingThrottleAsync("pin-attempt-ip:" + ip, 10, 3600);
    }

    public static async Task RecordPinAttemptAsync(string phone, string ip)
    {
        await AppDb.RecordAttemptAsync("pin-attempt:" + phone + ":" + ip, 600);
        await AppDb.RecordAttemptAsync("pin-attempt-ip:" + ip, 3600);
    }

    /// <summary>A successful PIN login forgives past lockouts: the next
    /// lockout starts again at 1 minute.</summary>
    public static async Task ResetPinLockCountAsync(string phone, string ip)
    {
        await AppDb.ResetRateLimitAsync("pin-attempt:" + phone + ":" + ip + ":locks");
        await AppDb.ResetRateLimitAsync("pin-attempt-ip:" + ip + ":locks");
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
