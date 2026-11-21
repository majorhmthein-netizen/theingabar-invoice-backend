/* Theingabar Invoice — HTML-only version: database layer for the new tables.
 *
 * Tables (all created idempotently at startup, alongside the legacy
 * `invoices` table owned by PostgresStore):
 *   users            mechanics (max 5), approval status, KBZ template, QR
 *   meta             single-row shop template (id = 1)
 *   sessions         opaque server-side sessions (cookie holds only the id)
 *   invoice_no_seq   global invoice-number sequence (plain running
 *                      number from 60212; race-free numbering)
 *   rate_limits      DB-backed rate-limit / lockout counters
 *
 * All SQL is parameterized. Dates are stored as TEXT 'YYYY-MM-DD' (as the
 * legacy table does); week/month arithmetic is done in C# in Asia/Yangon
 * time and passed to SQL as ISO date strings, so comparisons stay correct.
 */
using System.Globalization;
using Npgsql;

public static class AppDb
{
    public static NpgsqlDataSource? DataSource { get; private set; }
    public static bool Available => DataSource is not null;

    static readonly TimeZoneInfo Yangon =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Yangon");

    public static DateOnly TodayYangon() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Yangon));

    /// <summary>Monday of the current week (Asia/Yangon). Week = Mon–Sun.</summary>
    public static (DateOnly From, DateOnly To) ThisWeek()
    {
        var today = TodayYangon();
        var dow = (int)today.DayOfWeek; // Sunday = 0
        var monday = today.AddDays(-(dow == 0 ? 6 : dow - 1));
        return (monday, monday.AddDays(6));
    }

    public static (DateOnly From, DateOnly To) ThisMonth()
    {
        var today = TodayYangon();
        return (new DateOnly(today.Year, today.Month, 1),
                new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)));
    }

    /// <summary>One month's log (လချုပ်, Han 2026-11-21): the mechanics
    /// invoices whose Date falls in the given "yyyy-MM" month, most recent
    /// first, plus the month total and the "July 2026" label. Returns null
    /// when ym is not a valid year-month. Admin accounts' invoices are
    /// excluded, like Home (the admin's full picture is /admin/log).</summary>
    public static (List<InvoiceRow> Rows, long Total, string Label)? MonthRows(
        List<InvoiceRow> all, HashSet<string> adminIds, string ym)
    {
        if (ym.Length != 7 || ym[4] != '-' ||
            !int.TryParse(ym[..4], out var y) || !int.TryParse(ym[5..7], out var m) ||
            m < 1 || m > 12) return null;
        var label = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m) + " " + y;
        var rows = new List<InvoiceRow>();
        long total = 0;
        foreach (var r in all)
        {
            if (r.UserId is not null && adminIds.Contains(r.UserId)) continue;
            if (!DateOnly.TryParseExact(r.Date, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (d.Year != y || d.Month != m) continue;
            rows.Add(r);
            total += r.Total;
        }
        rows.Sort((a, b) => string.Compare(b.Date, a.Date, StringComparison.Ordinal) is var c && c != 0
            ? c : b.Id.CompareTo(a.Id));
        return (rows, total, label);
    }

    static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static async Task InitAsync(NpgsqlDataSource ds)
    {
        DataSource = ds;
        await using var conn = await ds.OpenConnectionAsync();
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS users (
                id SERIAL PRIMARY KEY,
                local_id TEXT UNIQUE NOT NULL,
                username TEXT NOT NULL,
                phone TEXT NOT NULL,
                password_hash TEXT,
                kbz_name TEXT,
                kbz_number TEXT,
                qr BYTEA,
                qr_content_type TEXT,
                avatar BYTEA,
                avatar_content_type TEXT,
                status TEXT NOT NULL DEFAULT 'pending',
                is_admin BOOLEAN NOT NULL DEFAULT FALSE,
                auth_uid TEXT,
                approved_by TEXT,
                approved_at TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """);
        await ExecAsync(conn, "CREATE UNIQUE INDEX IF NOT EXISTS users_username_lower_unique ON users (lower(username))");
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS meta (
                id INTEGER PRIMARY KEY,
                shop TEXT NOT NULL,
                group_name TEXT NOT NULL,
                services TEXT NOT NULL,
                address TEXT NOT NULL,
                phone1 TEXT NOT NULL,
                phone2 TEXT NOT NULL,
                phone3 TEXT,
                phone4 TEXT,
                phone5 TEXT,
                pay_name TEXT NOT NULL,
                pay_num TEXT NOT NULL,
                qr BYTEA,
                qr_content_type TEXT,
                thanks TEXT NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """);
        // Contact 3/4/5 (Han 2026-11-21): added after the meta table already
        // existed, so the columns are added idempotently for live DBs.
        await ExecAsync(conn, "ALTER TABLE meta ADD COLUMN IF NOT EXISTS phone3 TEXT");
        await ExecAsync(conn, "ALTER TABLE meta ADD COLUMN IF NOT EXISTS phone4 TEXT");
        await ExecAsync(conn, "ALTER TABLE meta ADD COLUMN IF NOT EXISTS phone5 TEXT");
        // Profile picture (Han 2026-11-21): avatar bytes per user, added
        // after the users table already existed — idempotent for live DBs.
        await ExecAsync(conn, "ALTER TABLE users ADD COLUMN IF NOT EXISTS avatar BYTEA");
        await ExecAsync(conn, "ALTER TABLE users ADD COLUMN IF NOT EXISTS avatar_content_type TEXT");
        // Template thanks (Han 2026-11-21): the approved template's closing
        // line is "Thank you for choosing our service." — update the stored
        // value only when it still equals the old default (i.e. never
        // customised in Admin/Meta); a custom closing line is left alone.
        await ExecAsync(conn,
            "UPDATE meta SET thanks = 'Thank you for choosing our service.' " +
            "WHERE id = 1 AND thanks = 'Thank you for choosing Theingabar Automotive Group.'");
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                is_admin BOOLEAN NOT NULL DEFAULT FALSE,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                expires_at TIMESTAMPTZ NOT NULL,
                revoked BOOLEAN NOT NULL DEFAULT FALSE,
                ip TEXT,
                user_agent TEXT
            )
            """);
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS invoice_no_seq (
                date_key TEXT PRIMARY KEY,
                seq INTEGER NOT NULL
            )
            """);
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS rate_limits (
                key TEXT PRIMARY KEY,
                attempts INTEGER NOT NULL DEFAULT 0,
                window_start TIMESTAMPTZ NOT NULL DEFAULT now(),
                locked_until TIMESTAMPTZ
            )
            """);
        // PIN reset requests (Han 2026-11-21, "Forgot PIN"): a mechanic who
        // forgot their PIN taps Forgot PIN on the login page; the request
        // reaches the admin, who taps Reset and the PIN becomes 1111 again.
        // One pending request per user — a repeat tap while one is pending
        // does not create a duplicate.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS pin_reset_requests (
                id SERIAL PRIMARY KEY,
                user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                status TEXT NOT NULL DEFAULT 'pending',
                created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                resolved_at TIMESTAMPTZ,
                resolved_by TEXT
            )
            """);
        await SeedAsync(conn);
        Console.WriteLine("[backend] html-only tables ready (users, meta, sessions, invoice_no_seq, rate_limits, pin_reset_requests)");
    }

    static async Task<int> ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Idempotent seeds: shop meta (PWA defaults), the Han Min Thein
    /// seed user (approved admin, no password — admin signs in via Google),
    /// and the Nissan Safari seed invoice when the invoices table is empty
    /// (number 20261119-002, sequence 20261119 → 2).</summary>
    static async Task SeedAsync(NpgsqlConnection conn)
    {
        // --- meta (id = 1) ---
        byte[]? seedQr = null;
        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "seed-qr.jpg"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "seed-qr.jpg"),
        })
        {
            if (File.Exists(candidate)) { seedQr = await File.ReadAllBytesAsync(candidate); break; }
        }
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO meta (id, shop, group_name, services, address, phone1, phone2,
                              pay_name, pay_num, qr, qr_content_type, thanks)
            VALUES (1, @shop, @group, @services, @address, @phone1, @phone2,
                    @payName, @payNum, @qr, @qrType, @thanks)
            ON CONFLICT (id) DO NOTHING
            """, conn))
        {
            cmd.Parameters.AddWithValue("@shop", "သိန်းကမ္ဘာ");
            cmd.Parameters.AddWithValue("@group", "THEINGABAR AUTOMOTIVE GROUP");
            cmd.Parameters.AddWithValue("@services", "ကားကြီး၊ ကားလတ်၊ မော်ဒယ်မြင့် အိမ်စီးကား အမျိုးမျိုး - အင်ဂျင်၊ AT/MT ဂီယာဘောက်၊ ဝါယာရိန်း, Air-Con, ဘရိတ်၊ အောက်ပိုင်း ပြုပြင်ရေးနှင့် OBD Diagnosis");
            cmd.Parameters.AddWithValue("@address", "ပင်မှီလမ်း၊ ဇောတိ၊ အောင်ပန်းမြို့");
            cmd.Parameters.AddWithValue("@phone1", "09428188129");
            cmd.Parameters.AddWithValue("@phone2", "09428332233");
            cmd.Parameters.AddWithValue("@payName", "Han Min Thein");
            cmd.Parameters.AddWithValue("@payNum", "09428332233");
            cmd.Parameters.AddWithValue("@qr", (object?)seedQr ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@qrType", seedQr is null ? (object)DBNull.Value : "image/jpeg");
            cmd.Parameters.AddWithValue("@thanks", "Thank you for choosing our service.");
            await cmd.ExecuteNonQueryAsync();
        }

        // --- seed user: Han Min Thein (approved admin) ---
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO users (local_id, username, phone, password_hash, kbz_name, kbz_number,
                               qr, qr_content_type, status, is_admin, approved_by, approved_at)
            VALUES ('han', 'Han Min Thein', '09428332233', NULL, 'Han Min Thein', '09428332233',
                    @qr, @qrType, 'approved', TRUE, 'seed', '2026-11-19')
            ON CONFLICT (local_id) DO NOTHING
            """, conn))
        {
            cmd.Parameters.AddWithValue("@qr", (object?)seedQr ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@qrType", seedQr is null ? (object)DBNull.Value : "image/jpeg");
            await cmd.ExecuteNonQueryAsync();
        }

        // --- seed invoice, only when the invoices table is empty ---
        await using (var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM invoices", conn))
        {
            var count = (long)(await countCmd.ExecuteScalarAsync())!;
            if (count == 0)
            {
                var itemsJson = "[{\"desc\":\"Blind spot mirror + ကားခ\",\"amount\":120000}," +
                    "{\"desc\":\"Fuel line cleaning & leakage check\",\"amount\":50000}," +
                    "{\"desc\":\"Power & Starter Wiring (နေရာရောက်)\",\"amount\":130000}]";
                await using var ins = new NpgsqlCommand(
                    """
                    INSERT INTO invoices (uid, no, user_id, client_created_at, date, customer, items, total)
                    VALUES ('seed-nissan-safari-20261119', '20261119-002', 'han',
                            1795046400000, '2026-11-19', 'Nissan Safari', @items::jsonb, 300000)
                    ON CONFLICT (uid) DO NOTHING
                    """, conn);
                ins.Parameters.AddWithValue("@items", itemsJson);
                await ins.ExecuteNonQueryAsync();
                await using var seq = new NpgsqlCommand(
                    "INSERT INTO invoice_no_seq (date_key, seq) VALUES ('20261119', 2) " +
                    "ON CONFLICT (date_key) DO NOTHING", conn);
                await seq.ExecuteNonQueryAsync();
                Console.WriteLine("[backend] seeded Nissan Safari invoice 20261119-002");
            }
        }

        // Global invoice-number sequence (Han 2026-11-21: numbers start at
        // 60212). The row holds the LAST used number, so the next invoice
        // is 60212. Idempotent — never overwrites the live value.
        await using (var gseq = new NpgsqlCommand(
            "INSERT INTO invoice_no_seq (date_key, seq) VALUES ('global', 60211) " +
            "ON CONFLICT (date_key) DO NOTHING", conn))
        {
            await gseq.ExecuteNonQueryAsync();
        }
    }

    /* ---------------- users ---------------- */

    public static async Task<UserRow?> FindUserByIdAsync(int id)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, local_id, username, phone, password_hash, kbz_name, kbz_number, " +
            "status, is_admin, auth_uid, approved_by, approved_at, created_at, updated_at, " +
            "(qr IS NOT NULL), (avatar IS NOT NULL) FROM users WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadUser(r) : null;
    }

    public static async Task<UserRow?> FindUserByUsernameAsync(string username)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, local_id, username, phone, password_hash, kbz_name, kbz_number, " +
            "status, is_admin, auth_uid, approved_by, approved_at, created_at, updated_at, " +
            "(qr IS NOT NULL), (avatar IS NOT NULL) FROM users WHERE lower(username) = lower(@u)", conn);
        cmd.Parameters.AddWithValue("@u", username.Trim());
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadUser(r) : null;
    }

    public static async Task<UserRow?> FindUserByPhoneAsync(string phone)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, local_id, username, phone, password_hash, kbz_name, kbz_number, " +
            "status, is_admin, auth_uid, approved_by, approved_at, created_at, updated_at, " +
            "(qr IS NOT NULL), (avatar IS NOT NULL) FROM users WHERE phone = @p", conn);
        cmd.Parameters.AddWithValue("@p", phone.Trim());
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadUser(r) : null;
    }

    public static async Task<UserRow?> FindUserByLocalIdAsync(string localId)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, local_id, username, phone, password_hash, kbz_name, kbz_number, " +
            "status, is_admin, auth_uid, approved_by, approved_at, created_at, updated_at, " +
            "(qr IS NOT NULL), (avatar IS NOT NULL) FROM users WHERE local_id = @l", conn);
        cmd.Parameters.AddWithValue("@l", localId);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ReadUser(r) : null;
    }

    public static async Task<List<UserRow>> ListUsersAsync()
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, local_id, username, phone, password_hash, kbz_name, kbz_number, " +
            "status, is_admin, auth_uid, approved_by, approved_at, created_at, updated_at, " +
            "(qr IS NOT NULL), (avatar IS NOT NULL) FROM users ORDER BY created_at ASC, id ASC", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<UserRow>();
        while (await r.ReadAsync()) list.Add(ReadUser(r));
        return list;
    }

    /// <summary>Approved users only, for public lists (Home Mechanics, Log filter).
    /// Pending/rejected/suspended users never appear there — only on Admin pages.</summary>
    public static async Task<List<UserRow>> ListApprovedUsersAsync()
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, local_id, username, phone, password_hash, kbz_name, kbz_number, " +
            "status, is_admin, auth_uid, approved_by, approved_at, created_at, updated_at, " +
            "(qr IS NOT NULL), (avatar IS NOT NULL) FROM users WHERE status = 'approved' ORDER BY created_at ASC, id ASC", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<UserRow>();
        while (await r.ReadAsync()) list.Add(ReadUser(r));
        return list;
    }

    public static async Task<int> CountUsersAsync()
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM users", conn);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    static UserRow ReadUser(NpgsqlDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.GetString(7), r.GetBoolean(8),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? (DateTime?)null : r.GetDateTime(11),
        r.GetDateTime(12), r.GetDateTime(13), r.GetBoolean(14), r.GetBoolean(15));

    // PIN login (2026-11-21): every new account starts with the default
    // PIN 1111, stored hashed in password_hash (the column is reused —
    // old password hashes in it are dead data, never verified).
    public static async Task<int> CreateUserAsync(string username, string phone)
    {
        var localId = "u" + Guid.NewGuid().ToString("N")[..12];
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO users (local_id, username, phone, password_hash, kbz_name, kbz_number, status) " +
            "VALUES (@l, @u, @p, @h, @u, @p2, 'pending') RETURNING id", conn);
        cmd.Parameters.AddWithValue("@l", localId);
        cmd.Parameters.AddWithValue("@u", username.Trim());
        cmd.Parameters.AddWithValue("@p", phone.Trim());
        cmd.Parameters.AddWithValue("@h", Auth.HashPin(Auth.DefaultPin));
        cmd.Parameters.AddWithValue("@p2", phone.Trim());
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Stores a new (hashed) PIN for a user.</summary>
    public static async Task SetUserPinHashAsync(int id, string hash)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE users SET password_hash = @h, updated_at = now() WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@h", hash);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /* ---------------- PIN reset requests (Forgot PIN, Han 2026-11-21) ---------------- */

    /// <summary>Creates a pending PIN-reset request for a user — unless one
    /// is already pending, in which case nothing is created (a repeat tap
    /// does not spam the admin). Returns true when a new request was made.</summary>
    public static async Task<bool> CreatePinResetRequestAsync(int userId)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO pin_reset_requests (user_id) " +
            "SELECT @u WHERE NOT EXISTS (SELECT 1 FROM pin_reset_requests " +
            "WHERE user_id = @u AND status = 'pending')", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    /// <summary>Pending PIN-reset requests, oldest first, with the
    /// requester's username + phone.</summary>
    public static async Task<List<PinResetRequestRow>> ListPendingPinResetRequestsAsync()
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT r.id, r.user_id, u.username, u.phone, r.created_at " +
            "FROM pin_reset_requests r JOIN users u ON u.id = r.user_id " +
            "WHERE r.status = 'pending' ORDER BY r.created_at", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<PinResetRequestRow>();
        while (await r.ReadAsync())
            list.Add(new PinResetRequestRow(
                r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
                r.GetDateTime(4)));
        return list;
    }

    /// <summary>Marks a PIN-reset request done (the admin tapped Reset).
    /// Returns the user id, or null when the request is not pending.</summary>
    public static async Task<int?> ResolvePinResetRequestAsync(int requestId, string resolvedBy)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE pin_reset_requests SET status = 'done', resolved_at = now(), " +
            "resolved_by = @b WHERE id = @id AND status = 'pending' " +
            "RETURNING user_id", conn);
        cmd.Parameters.AddWithValue("@b", resolvedBy);
        cmd.Parameters.AddWithValue("@id", requestId);
        var v = await cmd.ExecuteScalarAsync();
        return v is null ? null : (int)v;
    }

    public static async Task SetUserStatusAsync(int id, string status, string? approvedBy)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE users SET status = @s, approved_by = @b, " +
            "approved_at = CASE WHEN @s = 'approved' THEN now() ELSE approved_at END, " +
            "updated_at = now() WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@s", status);
        cmd.Parameters.AddWithValue("@b", (object?)approvedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task UpdateUserTemplateAsync(int id, string? kbzName, string? kbzNumber,
        byte[]? qr, string? qrType)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE users SET kbz_name = @n, kbz_number = @num, " +
            "qr = CASE WHEN @hasQr THEN @qr ELSE qr END, " +
            "qr_content_type = CASE WHEN @hasQr THEN @qt ELSE qr_content_type END, " +
            "updated_at = now() WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@n", (object?)kbzName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@num", (object?)kbzNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hasQr", qr is not null);
        cmd.Parameters.AddWithValue("@qr", (object?)qr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qt", (object?)qrType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task SetUserAuthUidAsync(int id, string authUid)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE users SET auth_uid = @a, updated_at = now() WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@a", authUid);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<(byte[]? Data, string? ContentType)> GetUserQrAsync(int id)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT qr, qr_content_type FROM users WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync() || r.IsDBNull(0)) return (null, null);
        return ((byte[])r.GetValue(0), r.IsDBNull(1) ? "image/jpeg" : r.GetString(1));
    }

    /// <summary>Stores a user's profile picture (replaces any previous one).</summary>
    public static async Task UpdateUserAvatarAsync(int id, byte[] avatar, string avatarType)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE users SET avatar = @a, avatar_content_type = @t, " +
            "updated_at = now() WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@a", avatar);
        cmd.Parameters.AddWithValue("@t", avatarType);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<(byte[]? Data, string? ContentType)> GetUserAvatarAsync(int id)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT avatar, avatar_content_type FROM users WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync() || r.IsDBNull(0)) return (null, null);
        return ((byte[])r.GetValue(0), r.IsDBNull(1) ? "image/jpeg" : r.GetString(1));
    }

    /* ---------------- meta ---------------- */

    /// <summary>Built-in shop defaults (same values SeedAsync writes).
    /// Used when the meta row is missing so pages keep rendering.</summary>
    public static readonly MetaRow DefaultMeta = new(
        "သိန်းကမ္ဘာ", "THEINGABAR AUTOMOTIVE GROUP",
        "ကားကြီး၊ ကားလတ်၊ မော်ဒယ်မြင့် အိမ်စီးကား အမျိုးမျိုး - အင်ဂျင်၊ AT/MT ဂီယာဘောက်၊ ဝါယာရိန်း, Air-Con, ဘရိတ်၊ အောက်ပိုင်း ပြုပြင်ရေးနှင့် OBD Diagnosis",
        "ပင်မှီလမ်း၊ ဇောတိ၊ အောင်ပန်းမြို့",
        "09428188129", "09428332233", null, null, null, "Han Min Thein", "09428332233",
        "Thank you for choosing our service.", false);

    public static async Task<MetaRow> GetMetaAsync()
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT shop, group_name, services, address, phone1, phone2, phone3, phone4, phone5, " +
            "pay_name, pay_num, thanks, (qr IS NOT NULL) FROM meta WHERE id = 1", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
        {
            // No meta row (e.g. DB was reset after startup): fall back to
            // the built-in shop defaults instead of 500ing every page.
            // The row is re-created by SeedAsync on the next startup.
            return DefaultMeta;
        }
        return new MetaRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.IsDBNull(8) ? null : r.GetString(8),
            r.GetString(9), r.GetString(10), r.GetString(11), r.GetBoolean(12));
    }

    public static async Task UpdateMetaAsync(MetaRow m, byte[]? qr, string? qrType)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE meta SET shop=@shop, group_name=@group, services=@services, address=@addr, " +
            "phone1=@p1, phone2=@p2, phone3=@p3, phone4=@p4, phone5=@p5, pay_name=@pn, pay_num=@pnum, " +
            "qr = CASE WHEN @hasQr THEN @qr ELSE qr END, " +
            "qr_content_type = CASE WHEN @hasQr THEN @qt ELSE qr_content_type END, " +
            "thanks=@thanks, updated_at=now() WHERE id=1", conn);
        cmd.Parameters.AddWithValue("@shop", m.Shop);
        cmd.Parameters.AddWithValue("@group", m.Group);
        cmd.Parameters.AddWithValue("@services", m.Services);
        cmd.Parameters.AddWithValue("@addr", m.Address);
        cmd.Parameters.AddWithValue("@p1", m.Phone1);
        cmd.Parameters.AddWithValue("@p2", m.Phone2);
        cmd.Parameters.AddWithValue("@p3", (object?)m.Phone3 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p4", (object?)m.Phone4 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p5", (object?)m.Phone5 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pn", m.PayName);
        cmd.Parameters.AddWithValue("@pnum", m.PayNum);
        cmd.Parameters.AddWithValue("@hasQr", qr is not null);
        cmd.Parameters.AddWithValue("@qr", (object?)qr ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qt", (object?)qrType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@thanks", m.Thanks);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<(byte[]? Data, string? ContentType)> GetMetaQrAsync()
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT qr, qr_content_type FROM meta WHERE id = 1", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync() || r.IsDBNull(0)) return (null, null);
        return ((byte[])r.GetValue(0), r.IsDBNull(1) ? "image/jpeg" : r.GetString(1));
    }

    /* ---------------- sessions ---------------- */

    // NO session expiry (Han 2026-11-21: every re-login costs one SMS, so a
    // login must last until the user taps Log out, or the Admin revokes it).
    // expires_at is kept far in the future so the FindSessionAsync check
    // still works; the row is ended only by explicit Log out, Revoke,
    // Suspend or Reject.
    public static async Task<string> CreateSessionAsync(int userId, bool isAdmin, string? ip, string? userAgent)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO sessions (id, user_id, is_admin, expires_at, ip, user_agent) " +
            "VALUES (@id, @u, @a, now() + interval '100 years', @ip, @ua)", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@a", isAdmin);
        cmd.Parameters.AddWithValue("@ip", (object?)ip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ua", (object?)userAgent ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public static async Task<SessionRow?> FindSessionAsync(string id)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT s.id, s.user_id, s.is_admin, s.created_at, s.expires_at, s.revoked, " +
            "u.username, u.status FROM sessions s JOIN users u ON u.id = s.user_id " +
            "WHERE s.id = @id AND s.revoked = FALSE AND s.expires_at > now()", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new SessionRow(r.GetString(0), r.GetInt32(1), r.GetBoolean(2), r.GetDateTime(3),
            r.GetDateTime(4), r.GetBoolean(5), r.GetString(6), r.GetString(7));
    }

    public static async Task RevokeSessionAsync(string id)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("UPDATE sessions SET revoked = TRUE WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task RevokeUserSessionsAsync(int userId)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE sessions SET revoked = TRUE WHERE user_id = @u", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Revokes all of a user's sessions except one (e.g. after a
    /// PIN change: other devices are logged out, the current session stays).</summary>
    public static async Task RevokeOtherSessionsAsync(int userId, string exceptSessionId)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE sessions SET revoked = TRUE WHERE user_id = @u AND id <> @s", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@s", exceptSessionId);
        await cmd.ExecuteNonQueryAsync();
    }

    /* ---------------- rate limiting ---------------- */

    /// <summary>DB-backed rate limit. Returns (allowed, retryAfterSeconds).
    /// A failed attempt is recorded by the caller via <see cref="RecordAttemptAsync"/>.</summary>
    public static async Task<(bool Allowed, int RetryAfter)> CheckRateLimitAsync(
        string key, int maxAttempts, int windowSeconds, int lockoutSeconds)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT attempts, window_start, locked_until FROM rate_limits WHERE key = @k", conn);
        cmd.Parameters.AddWithValue("@k", key);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (true, 0);
        var attempts = r.GetInt32(0);
        var windowStart = r.GetDateTime(1);
        var lockedUntil = r.IsDBNull(2) ? (DateTime?)null : r.GetDateTime(2);
        var now = DateTime.UtcNow;
        if (lockedUntil is not null && lockedUntil.Value > now)
            return (false, (int)Math.Ceiling((lockedUntil.Value - now).TotalSeconds));
        if ((now - windowStart).TotalSeconds > windowSeconds) return (true, 0);
        if (attempts >= maxAttempts)
        {
            var lockUntil = now.AddSeconds(lockoutSeconds);
            await r.CloseAsync();
            await using var up = new NpgsqlCommand(
                "UPDATE rate_limits SET locked_until = @l WHERE key = @k", conn);
            up.Parameters.AddWithValue("@l", lockUntil);
            up.Parameters.AddWithValue("@k", key);
            await up.ExecuteNonQueryAsync();
            return (false, lockoutSeconds);
        }
        return (true, 0);
    }

    public static async Task RecordAttemptAsync(string key, int windowSeconds)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO rate_limits (key, attempts, window_start) VALUES (@k, 1, now())
            ON CONFLICT (key) DO UPDATE SET
                attempts = CASE WHEN now() - rate_limits.window_start > (@w || ' seconds')::interval
                                THEN 1 ELSE rate_limits.attempts + 1 END,
                window_start = CASE WHEN now() - rate_limits.window_start > (@w || ' seconds')::interval
                                THEN now() ELSE rate_limits.window_start END,
                locked_until = NULL
            """, conn);
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@w", windowSeconds);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task ResetRateLimitAsync(string key)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM rate_limits WHERE key = @k", conn);
        cmd.Parameters.AddWithValue("@k", key);
        await cmd.ExecuteNonQueryAsync();
    }

    /* Escalating throttle (Han 2026-11-21): the first lockout is 1 minute,
     * the second 5 minutes, the third and later 10 minutes — never longer.
     * The lock count lives in a companion key ("{key}:locks"); a successful
     * login resets it, so normal use always starts again at 1 minute.
     * Returns (allowed, retryAfterSeconds). */

    static async Task<int> GetAttemptsAsync(NpgsqlConnection conn, string key)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT attempts FROM rate_limits WHERE key = @k", conn);
        cmd.Parameters.AddWithValue("@k", key);
        var v = await cmd.ExecuteScalarAsync();
        return v is null ? 0 : (int)v;
    }

    static async Task BumpLockCountAsync(NpgsqlConnection conn, string key)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO rate_limits (key, attempts, window_start) VALUES (@k, 1, now()) " +
            "ON CONFLICT (key) DO UPDATE SET attempts = rate_limits.attempts + 1", conn);
        cmd.Parameters.AddWithValue("@k", key);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<(bool Allowed, int RetryAfter)> CheckEscalatingThrottleAsync(
        string key, int maxAttempts, int windowSeconds)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        int? attempts = null;
        DateTime? windowStart = null, lockedUntil = null;
        await using (var cmd = new NpgsqlCommand(
            "SELECT attempts, window_start, locked_until FROM rate_limits WHERE key = @k", conn))
        {
            cmd.Parameters.AddWithValue("@k", key);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                attempts = r.GetInt32(0);
                windowStart = r.GetDateTime(1);
                lockedUntil = r.IsDBNull(2) ? (DateTime?)null : r.GetDateTime(2);
            }
        }
        var now = DateTime.UtcNow;
        if (lockedUntil is not null && lockedUntil.Value > now)
            return (false, (int)Math.Ceiling((lockedUntil.Value - now).TotalSeconds));
        if (attempts is not null && windowStart is not null &&
            (now - windowStart.Value).TotalSeconds <= windowSeconds &&
            attempts.Value >= maxAttempts)
        {
            var locks = await GetAttemptsAsync(conn, key + ":locks");
            var duration = locks <= 0 ? 60 : locks == 1 ? 300 : 600;
            var lockUntil = now.AddSeconds(duration);
            await using var up = new NpgsqlCommand(
                "UPDATE rate_limits SET locked_until = @l WHERE key = @k", conn);
            up.Parameters.AddWithValue("@l", lockUntil);
            up.Parameters.AddWithValue("@k", key);
            await up.ExecuteNonQueryAsync();
            await BumpLockCountAsync(conn, key + ":locks");
            return (false, duration);
        }
        return (true, 0);
    }

    /// <summary>Deletes all rate-limit keys with the given prefix
    /// (admin reset: all per-IP counters for one account).</summary>
    public static async Task ResetRateLimitsByPrefixAsync(string prefix)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM rate_limits WHERE key LIKE @p", conn);
        cmd.Parameters.AddWithValue("@p", prefix + "%");
        await cmd.ExecuteNonQueryAsync();
    }

    /* ---------------- invoice numbering + totals ---------------- */

    /// <summary>Assigns the next global invoice number (plain running
    /// number, starting at 60212 — Han 2026-11-21) inside a transaction
    /// and inserts the invoice, returning the stored row. Retries once
    /// on a unique-index conflict. The old per-date rows stay in
    /// invoice_no_seq, unused; the seed invoice keeps 20261119-002.</summary>
    public static async Task<InvoiceRow> CreateInvoiceNumberedAsync(InvoiceInput inv)
    {
        if (DataSource is null) throw new InvalidOperationException("Postgres required for numbering");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var conn = await DataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                await using var seqCmd = new NpgsqlCommand(
                    "INSERT INTO invoice_no_seq (date_key, seq) VALUES ('global', 60212) " +
                    "ON CONFLICT (date_key) DO UPDATE SET seq = invoice_no_seq.seq + 1 RETURNING seq",
                    conn, tx);
                var seq = (int)(await seqCmd.ExecuteScalarAsync())!;
                var no = $"{seq}";
                var withNo = inv with { No = no };
                // Insert via the store's Postgres path inside the same transaction
                // by using a raw insert here (same columns as PostgresStore).
                var itemsJson = System.Text.Json.JsonSerializer.Serialize(withNo.Items, Shared.JsonOpts);
                await using var ins = new NpgsqlCommand(
                    "INSERT INTO invoices (uid, no, user_id, client_created_at, date, customer, items, total, deposit, paper_size) " +
                    "VALUES (@uid,@no,@userId,@createdAt,@date,@customer,@items::jsonb,@total,@deposit,@paperSize) " +
                    "RETURNING id, uid, no, user_id, client_created_at, date, customer, items, total, deposit, paper_size, created_at, updated_at",
                    conn, tx);
                ins.Parameters.AddWithValue("@uid", withNo.Uid);
                ins.Parameters.AddWithValue("@no", no);
                ins.Parameters.AddWithValue("@userId", (object?)withNo.UserId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@createdAt", (object?)withNo.CreatedAt ?? DBNull.Value);
                ins.Parameters.AddWithValue("@date", withNo.Date);
                ins.Parameters.AddWithValue("@customer", withNo.Customer);
                ins.Parameters.AddWithValue("@items", itemsJson);
                ins.Parameters.AddWithValue("@total", checked((int)withNo.Total));
                ins.Parameters.AddWithValue("@deposit", checked((int)withNo.Deposit));
                ins.Parameters.AddWithValue("@paperSize", withNo.PaperSize == "Half" ? "Half" : "A4");
                await using var r = await ins.ExecuteReaderAsync();
                await r.ReadAsync();
                var row = ReadInvoiceRow(r);
                await r.CloseAsync();
                await tx.CommitAsync();
                return row;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                await tx.RollbackAsync();
                if (attempt == 1) throw new DuplicateNoException();
                // Retry once with a fresh sequence number.
            }
        }
        throw new DuplicateNoException();
    }

    /// <summary>Next number a NEW invoice would get (for the Preview page).
    /// Numbering is date-independent now (plain running number from 60212),
    /// so the date argument is ignored. Does not reserve the number.</summary>
    public static async Task<string> PeekNextNumberAsync(string dateIso)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COALESCE(MAX(seq), 60211) FROM invoice_no_seq WHERE date_key = 'global'", conn);
        var seq = (int)(await cmd.ExecuteScalarAsync())!;
        // Also consider numbers already stored without a seq row (legacy data).
        await using var maxCmd = new NpgsqlCommand(
            "SELECT COALESCE(MAX(CAST(no AS INTEGER)), 0) FROM invoices WHERE no ~ '^[0-9]+$'", conn);
        var maxNo = (int)(await maxCmd.ExecuteScalarAsync())!;
        return $"{Math.Max(seq, maxNo) + 1}";
    }

    static InvoiceRow ReadInvoiceRow(NpgsqlDataReader r)
    {
        var items = new List<InvoiceItem>();
        var arr = System.Text.Json.Nodes.JsonNode.Parse(r.GetString(7)) as System.Text.Json.Nodes.JsonArray
            ?? new System.Text.Json.Nodes.JsonArray();
        foreach (var n in arr)
        {
            if (n is not System.Text.Json.Nodes.JsonObject o) continue;
            long amount = 0;
            if (o["amount"] is System.Text.Json.Nodes.JsonValue v)
            {
                if (!v.TryGetValue<long>(out amount) && v.TryGetValue<double>(out var d)) amount = (long)d;
            }
            items.Add(new InvoiceItem(o["desc"]?.GetValue<string>() ?? "", amount));
        }
        return new InvoiceRow(
            r.GetInt32(0), r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetInt64(4),
            r.GetString(5), r.GetString(6), items, r.GetInt32(8),
            r.IsDBNull(9) ? 0 : r.GetInt32(9),
            r.IsDBNull(10) ? "A4" : r.GetString(10),
            Shared.FormatTs(r.GetDateTime(11)), Shared.FormatTs(r.GetDateTime(12)));
    }

    public static async Task<long> PeriodTotalAsync(DateOnly from, DateOnly to, string? userLocalId)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        var sql = "SELECT COALESCE(SUM(total), 0) FROM invoices WHERE date >= @f AND date <= @t";
        if (userLocalId is not null) sql += " AND user_id = @u";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@f", Iso(from));
        cmd.Parameters.AddWithValue("@t", Iso(to));
        if (userLocalId is not null) cmd.Parameters.AddWithValue("@u", userLocalId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

public sealed record UserRow(
    int Id, string LocalId, string Username, string Phone, string? PasswordHash,
    string? KbzName, string? KbzNumber, string Status, bool IsAdmin,
    string? AuthUid, string? ApprovedBy, DateTime? ApprovedAt,
    DateTime CreatedAt, DateTime UpdatedAt, bool HasQr, bool HasAvatar);

public sealed record MetaRow(
    string Shop, string Group, string Services, string Address,
    string Phone1, string Phone2, string? Phone3, string? Phone4, string? Phone5,
    string PayName, string PayNum, string Thanks, bool HasQr)
{
    /// <summary>All set contact numbers, joined with ", " (empty ones skipped).</summary>
    public string PhonesDisplay => string.Join(", ",
        new[] { Phone1, Phone2, Phone3, Phone4, Phone5 }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed record SessionRow(
    string Id, int UserId, bool IsAdmin, DateTime CreatedAt, DateTime ExpiresAt,
    bool Revoked, string Username, string Status);

/// <summary>A mechanic's PIN-reset request (Forgot PIN, Han 2026-11-21):
/// the requester (username + phone) and when it was made. Only pending
/// requests are listed for the admin.</summary>
public sealed record PinResetRequestRow(
    int Id, int UserId, string Username, string Phone, DateTime CreatedAt);
