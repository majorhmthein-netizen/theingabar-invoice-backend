/* Theingabar Invoice — backend (Render Web Service), C# / ASP.NET Core 8.
 *
 * Two parts in one service:
 *  (1) Legacy minimal API (faithful port of the Node server): same
 *      behaviour, same validation messages, same JSON shapes, same
 *      Postgres schema and startup migrations, same JSON-file fallback
 *      store. Serves the old PWA from ./public/ under /legacy/ during
 *      the HTML-only transition. Kept working until cutover.
 *  (2) HTML-only frontend (Razor Pages): server-rendered pages, no
 *      custom JS in served HTML except the cache-only service worker
 *      registration (one tiny file, no app logic). Sessions are opaque
 *      ids in Postgres; mechanics log in with phone + 4-digit PIN
 *      (default 1111; no password, no OTP); admin signs in via
 *      server-side Google OAuth (env GOOGLE_CLIENT_ID /
 *      GOOGLE_CLIENT_SECRET). No secrets in served HTML. See Db.cs,
 *      Auth.cs, SmsPoh.cs and HTML-ONLY-PLAN.md.
 *
 * Env: PORT (Render sets it; default 10000), DATABASE_URL,
 * INVOICE_API_KEY (legacy /api only), NODE_ENV (compat, unused),
 * GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET (admin sign-in; optional —
 * admin sign-in is unavailable without them, everything else works),
 * SMSPOH_API_KEY, SMSPOH_API_SECRET (OTP login — PARKED 2026-11-21,
 * no working SMS sender; kept in case a sender ID is ever approved).
 */
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using NpgsqlTypes;

const int MaxItems = 20;
const long MaxBodyBytes = 1024 * 1024;

Dictionary<string, string> MimeMap = new(StringComparer.OrdinalIgnoreCase)
{
    [".html"] = "text/html; charset=utf-8",
    [".js"] = "text/javascript; charset=utf-8",
    [".webmanifest"] = "application/manifest+json; charset=utf-8",
    [".json"] = "application/json; charset=utf-8",
    [".png"] = "image/png",
    [".jpg"] = "image/jpeg",
    [".jpeg"] = "image/jpeg",
    [".svg"] = "image/svg+xml",
    [".css"] = "text/css; charset=utf-8",
    [".ico"] = "image/x-icon",
};

var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) && p > 0 ? p : 10000;
var publicDir = ResolvePublicDir();
var dataFile = Path.Combine(AppContext.BaseDirectory, "data", "invoices.json");
var apiKey = Environment.GetEnvironmentVariable("INVOICE_API_KEY") ?? "";
_ = Environment.GetEnvironmentVariable("NODE_ENV"); // compat: read, unused (same as Node)
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "";

static string ResolvePublicDir()
{
    var fromBase = Path.Combine(AppContext.BaseDirectory, "public");
    if (Directory.Exists(fromBase)) return fromBase;
    var fromCwd = Path.Combine(Directory.GetCurrentDirectory(), "public");
    if (Directory.Exists(fromCwd)) return fromCwd;
    return fromBase; // fall back: requests will 404, same as a missing public/ in Node
}

/* ------------------------------------------------------------------ */
/* Startup: pick the store (Postgres, or JSON file fallback)            */
/* ------------------------------------------------------------------ */

IInvoiceStore store;
string storeName;
var pg = string.IsNullOrEmpty(databaseUrl) ? null : await PostgresStore.TryCreateAsync(databaseUrl);
if (pg is not null)
{
    store = pg;
    storeName = "postgres";
    Console.WriteLine("[backend] store: postgres (table invoices ready)");
}
else
{
    store = new JsonStore(dataFile);
    storeName = "json";
    if (string.IsNullOrEmpty(databaseUrl))
        Console.WriteLine("[backend] store: local JSON file ./data/invoices.json (no DATABASE_URL)");
}
if (string.IsNullOrEmpty(apiKey))
    Console.WriteLine("[backend] WARNING: INVOICE_API_KEY is not set — API is open (dev mode). Set it on Render.");
else
    Console.WriteLine("[backend] API key auth enabled for /api/* (except /api/health)");

// HTML-only tables (users, meta, sessions, invoice_no_seq, rate_limits)
// + seeds. Only when Postgres is available; without it the new pages
// report "database unavailable" and the legacy /api JSON store still works.
if (pg is not null) await AppDb.InitAsync(pg.DataSource);

var googleId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID") ?? "";
var googleSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET") ?? "";
var googleConfigured = !string.IsNullOrEmpty(googleId) && !string.IsNullOrEmpty(googleSecret);
if (!googleConfigured)
    Console.WriteLine("[backend] Google sign-in not configured (GOOGLE_CLIENT_ID/GOOGLE_CLIENT_SECRET unset) — admin Google sign-in unavailable.");

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Data-protection keys (antiforgery tokens) persist across restarts.
var keysDir = Path.Combine(AppContext.BaseDirectory, "data", "protection-keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir));

builder.Services.AddRazorPages()
    .AddCookieTempDataProvider(o =>
    {
        o.Cookie.Name = "tgb-flash";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAntiforgery();
builder.Services.AddHttpClient();

// Trust Render's forwarded headers (see UseForwardedHeaders below).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddSingleton<IInvoiceStore>(store);

if (googleConfigured)
{
    builder.Services.AddAuthentication(o => o.DefaultScheme = "TmpGoogle")
        .AddCookie("TmpGoogle", o =>
        {
            o.Cookie.Name = "tgb-oauth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        })
        .AddGoogle("Google", o =>
        {
            o.ClientId = googleId;
            o.ClientSecret = googleSecret;
            o.CallbackPath = "/auth/google/signin-google";
            o.SignInScheme = "TmpGoogle";
            o.SaveTokens = true;
        });
}

var app = builder.Build();

// Render terminates TLS at its proxy and forwards X-Forwarded-Proto:
// https. Without this the app sees plain http and the Google handler
// builds an http:// redirect_uri, which Google rejects with
// "Error 400: redirect_uri_mismatch" (seen 2026-11-20 on Han's phone).
// Render is the only ingress, so all proxies are trusted. Must run
// first, before auth and before anything that reads Request.Scheme.
app.UseForwardedHeaders();

/* --- Security headers on every response (plan §7.1 #9) --- */
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    // No custom JS in served HTML except the cache-only service-worker
    // registration file (/js/sw-register.js); hence script-src 'self'.
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; " +
        "font-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    if (ctx.Request.IsHttps)
        h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

app.UseStaticFiles(); // wwwroot: css, fonts, js, icons, manifest, sw.js
app.UseRouting();
if (googleConfigured) app.UseAuthentication();

app.MapRazorPages();

/* ------------------------------------------------------------------ */
/* Legacy API (unchanged behaviour — kept until cutover)                */
/* ------------------------------------------------------------------ */

app.MapGet("/api/health", async (HttpResponse res) =>
    await SendJson(res, 200, new { ok = true, store = storeName, time = IsoNow() }));

app.MapGet("/api/invoices", async (HttpRequest req, HttpResponse res) =>
{
    if (!await CheckApiKeyAsync(req, res)) return;
    await SendJson(res, 200, await store.ListAsync());
});

app.MapPost("/api/invoices", async (HttpRequest req, HttpResponse res) =>
{
    if (!await CheckApiKeyAsync(req, res)) return;
    var body = await ReadJsonBody(req);
    if (body is null) { await SendJson(res, 400, new { error = "invalid JSON" }); return; }
    var err = ValidateInvoice(body.Value);
    if (err is not null) { await SendJson(res, 400, new { error = err }); return; }
    InvoiceRow created;
    try { created = await store.CreateAsync(CleanInvoice(body.Value)); }
    catch (DuplicateNoException)
    {
        await SendJson(res, 409, new { error = "invoice number already exists" });
        return;
    }
    await SendJson(res, 201, created);
});

app.MapMethods("/api/invoices/{id:long}", new[] { "PUT", "DELETE" },
    async (long id, HttpRequest req, HttpResponse res) =>
{
    if (!await CheckApiKeyAsync(req, res)) return;
    if (req.Method == "PUT")
    {
        var body = await ReadJsonBody(req);
        if (body is null) { await SendJson(res, 400, new { error = "invalid JSON" }); return; }
        var err = ValidateInvoice(body.Value);
        if (err is not null) { await SendJson(res, 400, new { error = err }); return; }
        InvoiceRow? updated;
        try { updated = await store.UpdateAsync(id, CleanInvoice(body.Value)); }
        catch (DuplicateNoException)
        {
            await SendJson(res, 409, new { error = "invoice number already exists" });
            return;
        }
        if (updated is null) { await SendJson(res, 404, new { error = "invoice not found" }); return; }
        await SendJson(res, 200, updated);
        return;
    }
    if (!await store.DeleteAsync(id)) { await SendJson(res, 404, new { error = "invoice not found" }); return; }
    await SendJson(res, 200, new { ok = true });
});

app.MapGet("/api/export.csv", async (HttpRequest req, HttpResponse res) =>
{
    if (!await CheckApiKeyAsync(req, res)) return;
    var csv = InvoicesToCsv(await store.ListAsync());
    res.StatusCode = 200;
    res.ContentType = "text/csv; charset=utf-8";
    res.Headers.ContentDisposition = "attachment; filename=\"theingabar-invoice-log.csv\"";
    await res.WriteAsync(csv, Shared.Utf8NoBom);
});

app.Map("/api/{**rest}", async (HttpResponse res) =>
    await SendJson(res, 404, new { error = "not found" }));

/* --- Admin-only CSV export for the admin invoice history --- */
app.MapGet("/admin/log/export", async (HttpContext ctx) =>
{
    if (!AppDb.Available)
    {
        ctx.Response.StatusCode = 503;
        return;
    }
    var me = await Auth.CurrentUserAsync(ctx);
    if (me is null) { ctx.Response.Redirect("/login?returnUrl=/admin/log/export"); return; }
    if (!me.IsAdmin) { ctx.Response.StatusCode = 403; return; }
    var list = await store.ListAsync();
    var csv = InvoicesToCsv(list);
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/csv; charset=utf-8";
    ctx.Response.Headers.ContentDisposition = "attachment; filename=\"theingabar-invoice-log.csv\"";
    await ctx.Response.WriteAsync(csv, Shared.Utf8NoBom);
});

/* ------------------------------------------------------------------ */
/* QR images (public — the same QR is printed on the public invoice)    */
/* ------------------------------------------------------------------ */

app.MapGet("/qr/meta", async (HttpResponse res) =>
{
    if (!AppDb.Available) { await SendJson(res, 503, new { error = "database unavailable" }); return; }
    var (data, type) = await AppDb.GetMetaQrAsync();
    if (data is null) { res.StatusCode = 404; return; }
    res.ContentType = type ?? "image/jpeg";
    await res.Body.WriteAsync(data);
});

app.MapGet("/qr/user/{id:int}", async (int id, HttpResponse res) =>
{
    if (!AppDb.Available) { await SendJson(res, 503, new { error = "database unavailable" }); return; }
    var (data, type) = await AppDb.GetUserQrAsync(id);
    if (data is null) { res.StatusCode = 404; return; }
    res.ContentType = type ?? "image/jpeg";
    await res.Body.WriteAsync(data);
});

/* ------------------------------------------------------------------ */
/* Invoice PDF / PNG exports (approved users only)                      */
/* ------------------------------------------------------------------ */

app.MapGet("/invoice/{id:long}.pdf", async (long id, HttpContext ctx) =>
{
    var me = await Auth.CurrentUserAsync(ctx);
    if (me is null || !me.CanWrite) { ctx.Response.StatusCode = 403; return; }
    var row = (await store.ListAsync()).FirstOrDefault(r => r.Id == id);
    if (row is null) { ctx.Response.StatusCode = 404; return; }
    var pdf = await PdfService.RenderAsync(row, me.User);
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "application/pdf";
    ctx.Response.Headers.ContentDisposition =
        $"attachment; filename=\"invoice-{row.No ?? row.Id.ToString()}.pdf\"";
    await ctx.Response.Body.WriteAsync(pdf);
});

app.MapGet("/invoice/{id:long}.png", async (long id, HttpContext ctx) =>
{
    var me = await Auth.CurrentUserAsync(ctx);
    if (me is null || !me.CanWrite) { ctx.Response.StatusCode = 403; return; }
    var row = (await store.ListAsync()).FirstOrDefault(r => r.Id == id);
    if (row is null) { ctx.Response.StatusCode = 404; return; }
    var png = await PngService.RenderAsync(row, me.User);
    if (png is null) { ctx.Response.StatusCode = 503; return; }
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "image/png";
    ctx.Response.Headers.ContentDisposition =
        $"attachment; filename=\"invoice-{row.No ?? row.Id.ToString()}.png\"";
    await ctx.Response.Body.WriteAsync(png);
});

/* ------------------------------------------------------------------ */
/* Admin Google sign-in (server-side OAuth code flow)                   */
/* ------------------------------------------------------------------ */

app.MapGet("/auth/google", async (HttpContext ctx) =>
{
    if (!googleConfigured) { ctx.Response.Redirect("/admin?error=google-not-configured"); return; }
    var (allowed, retry) = await Auth.CheckAdminLimitAsync(Auth.ClientIp(ctx));
    if (!allowed)
    {
        ctx.Response.Redirect($"/admin?error=locked&retry={retry}");
        return;
    }
    await ctx.ChallengeAsync("Google",
        new AuthenticationProperties { RedirectUri = "/auth/google/callback" });
});

app.MapGet("/auth/google/callback", async (HttpContext ctx) =>
{
    if (!googleConfigured) { ctx.Response.Redirect("/admin?error=google-not-configured"); return; }
    var ip = Auth.ClientIp(ctx);
    var result = await ctx.AuthenticateAsync("TmpGoogle");
    if (!result.Succeeded || result.Principal is null)
    {
        await Auth.RecordAdminFailureAsync(ip);
        ctx.Response.Redirect("/admin?error=google-failed");
        return;
    }
    var accessToken = result.Properties?.GetTokenValue("access_token");
    string? email = null;
    bool emailVerified = false;
    string? sub = null;
    if (!string.IsNullOrEmpty(accessToken))
    {
        try
        {
            var http = ctx.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
            var reqMsg = new HttpRequestMessage(HttpMethod.Get,
                "https://openidconnect.googleapis.com/v1/userinfo");
            reqMsg.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var resp = await http.SendAsync(reqMsg);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                if (root.TryGetProperty("email", out var e)) email = e.GetString();
                if (root.TryGetProperty("email_verified", out var v)) emailVerified = v.GetBoolean();
                if (root.TryGetProperty("sub", out var s)) sub = s.GetString();
            }
        }
        catch { /* fall through to deny */ }
    }
    await ctx.SignOutAsync("TmpGoogle");
    if (email is null || !emailVerified || !Auth.IsAdminEmail(email))
    {
        await Auth.RecordAdminFailureAsync(ip);
        ctx.Response.Redirect("/admin?error=not-admin");
        return;
    }
    // Bind the Google identity to the seed Han admin user.
    var admin = await AppDb.FindUserByLocalIdAsync("han");
    if (admin is null || !admin.IsAdmin)
    {
        ctx.Response.Redirect("/admin?error=no-admin-user");
        return;
    }
    if (admin.AuthUid is not null && sub is not null && admin.AuthUid != sub)
    {
        await Auth.RecordAdminFailureAsync(ip);
        ctx.Response.Redirect("/admin?error=not-admin");
        return;
    }
    if (admin.AuthUid is null && sub is not null)
        await AppDb.SetUserAuthUidAsync(admin.Id, sub);
    await AppDb.ResetRateLimitAsync("admin:" + ip);
    await Auth.SignInAsync(ctx, admin.Id, isAdmin: true);
    ctx.Response.Redirect("/admin");
});

/* ------------------------------------------------------------------ */
/* Legacy PWA (old frontend) — served under /legacy/ until cutover      */
/* ------------------------------------------------------------------ */

var legacyContentTypes = new FileExtensionContentTypeProvider();
legacyContentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(publicDir),
    RequestPath = "/legacy",
    ContentTypeProvider = legacyContentTypes,
});
app.MapGet("/legacy", () => Results.Redirect("/legacy/index.html"));

Console.WriteLine($"[backend] listening on port {port} (store: {storeName})");
app.Run();

/* ------------------------------------------------------------------ */
/* Request dispatch (mirrors the Node server's routing, in order)       */
/* ------------------------------------------------------------------ */

async Task<bool> CheckApiKeyAsync(HttpRequest req, HttpResponse res)
{
    if (string.IsNullOrEmpty(apiKey)) return true; // dev mode: no key configured
    if (req.Headers.TryGetValue("X-API-Key", out var v) && v.Count == 1 && string.Equals(v[0], apiKey, StringComparison.Ordinal))
        return true;
    await SendJson(res, 401, new { error = "unauthorized" });
    return false;
}

/* ------------------------------------------------------------------ */
/* HTTP helpers                                                         */
/* ------------------------------------------------------------------ */



static async Task SendJson(HttpResponse res, int status, object obj)
{
    res.StatusCode = status;
    res.ContentType = "application/json; charset=utf-8";
    await res.WriteAsync(JsonSerializer.Serialize(obj, Shared.JsonOpts), Shared.Utf8NoBom);
}

static string IsoNow() => Shared.IsoNow();


/// <summary>Reads the request body (max 1 MB). Returns null when the body is
/// too large or unreadable — the caller reports 400 "invalid JSON", exactly
/// like the Node server's readBody rejection landing in its JSON.parse catch.</summary>
static async Task<JsonElement?> ReadJsonBody(HttpRequest req)
{
    string text;
    try
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        long total = 0;
        int read;
        while ((read = await req.Body.ReadAsync(buf)) > 0)
        {
            total += read;
            if (total > MaxBodyBytes) return null;
            ms.Write(buf, 0, read);
        }
        text = Encoding.UTF8.GetString(ms.ToArray());
    }
    catch
    {
        return null;
    }
    try
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
    catch
    {
        return null;
    }
}

/* ------------------------------------------------------------------ */
/* Validation (error strings identical to the Node server)              */
/* ------------------------------------------------------------------ */

static bool TryGetInteger(JsonElement e, out long v)
{
    v = 0;
    if (e.ValueKind != JsonValueKind.Number) return false;
    if (e.TryGetInt64(out v)) return true;
    // Node's Number.isInteger accepts 1.0 / 1e3; long parsing does not.
    if (e.TryGetDouble(out var d) && d == Math.Truncate(d) && Math.Abs(d) < 9007199254740992d)
    {
        v = (long)d;
        return true;
    }
    return false;
}

static string? ValidateInvoice(JsonElement b)
{
    if (b.ValueKind != JsonValueKind.Object) return "body must be a JSON object";
    if (!b.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(date.GetString()))
        return "date is required";
    if (!b.TryGetProperty("customer", out var customer) || customer.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(customer.GetString()))
        return "customer is required";
    if (!b.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        return "items must be a non-empty array";
    if (items.GetArrayLength() == 0) return "items must be a non-empty array";
    if (items.GetArrayLength() > MaxItems) return "at most " + MaxItems + " items";
    long sum = 0;
    foreach (var it in items.EnumerateArray())
    {
        if (it.ValueKind != JsonValueKind.Object) return "each item must be an object";
        if (!it.TryGetProperty("desc", out var desc) || desc.ValueKind != JsonValueKind.String)
            return "each item needs a desc string";
        if (!it.TryGetProperty("amount", out var amount) || !TryGetInteger(amount, out var a) || a < 0)
            return "each item amount must be an integer >= 0";
        sum += a;
    }
    if (!b.TryGetProperty("total", out var total) || !TryGetInteger(total, out var t) || t < 0)
        return "total must be an integer >= 0";
    if (t != sum) return "total (" + t + ") does not equal sum of items (" + sum + ")";
    if (b.TryGetProperty("uid", out var uid)
        && (uid.ValueKind != JsonValueKind.String || uid.GetString()!.Length > 128))
        return "uid must be a short string";
    if (b.TryGetProperty("no", out var no) && no.ValueKind != JsonValueKind.Null
        && (no.ValueKind != JsonValueKind.String || !Regex.IsMatch(no.GetString()!, @"^\d{8}-\d{3}$")))
        return "no must look like YYYYMMDD-NNN";
    if (b.TryGetProperty("userId", out var userId) && userId.ValueKind != JsonValueKind.Null
        && (userId.ValueKind != JsonValueKind.String || userId.GetString()!.Length > 128))
        return "userId must be a short string";
    if (b.TryGetProperty("createdAt", out var createdAt) && createdAt.ValueKind != JsonValueKind.Null
        && (!TryGetInteger(createdAt, out var ca) || ca < 0))
        return "createdAt must be a non-negative integer (ms)";
    return null;
}

static string? OptStr(JsonElement b, string name) =>
    b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

static long MustLong(JsonElement e) => TryGetInteger(e, out var v) ? v : 0;

static InvoiceInput CleanInvoice(JsonElement b)
{
    var uid = OptStr(b, "uid");
    var no = OptStr(b, "no");
    var userId = OptStr(b, "userId");
    long? createdAt = null;
    if (b.TryGetProperty("createdAt", out var ca) && TryGetInteger(ca, out var v) && v >= 0) createdAt = v;
    return new InvoiceInput(
        Uid: string.IsNullOrEmpty(uid) ? Guid.NewGuid().ToString() : uid,
        No: string.IsNullOrEmpty(no) ? null : no,
        UserId: string.IsNullOrEmpty(userId) ? null : userId,
        CreatedAt: createdAt,
        Date: b.GetProperty("date").GetString()!.Trim(),
        Customer: b.GetProperty("customer").GetString()!.Trim(),
        Items: b.GetProperty("items").EnumerateArray()
            .Select(it => new InvoiceItem(it.GetProperty("desc").GetString()!, MustLong(it.GetProperty("amount"))))
            .ToList(),
        Total: MustLong(b.GetProperty("total")));
}

/* ------------------------------------------------------------------ */
/* CSV export (same columns as the PWA log export)                      */
/* ------------------------------------------------------------------ */

static string FmtDate(string? iso)
{
    if (string.IsNullOrEmpty(iso)) return "";
    var p = iso.Split('-');
    return p.Length == 3 ? p[2] + ". " + p[1] + ". " + p[0] : iso;
}

static string CsvCell(object? v)
{
    var s = v is null ? "" : v.ToString()!;
    return s.IndexOfAny(new[] { '"', ',', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

static string InvoicesToCsv(List<InvoiceRow> list)
{
    var head = new List<string> { "Date", "Customer" };
    for (var i = 1; i <= MaxItems; i++) { head.Add("Item " + i); head.Add("Item " + i + " Amount"); }
    head.Add("Items Sum"); head.Add("Invoice Total"); head.Add("Check"); head.Add("No.");
    var lines = new List<string> { string.Join(",", head) };
    foreach (var e in list)
    {
        var row = new List<object?> { FmtDate(e.Date), e.Customer };
        long sum = 0;
        for (var i = 0; i < MaxItems; i++)
        {
            var it = i < e.Items.Count ? e.Items[i] : null;
            row.Add(it?.Desc ?? (object?)"" );
            row.Add(it is null ? (object?)"" : it.Amount);
            if (it is not null) sum += it.Amount;
        }
        row.Add(sum);
        row.Add(e.Total);
        row.Add(sum == e.Total ? "OK" : "CHECK");
        row.Add(e.No ?? "");
        lines.Add(string.Join(",", row.Select(CsvCell)));
    }
    return "\uFEFF" + string.Join("\r\n", lines) + "\r\n";
}

/* ------------------------------------------------------------------ */
/* Data model                                                           */
/* ------------------------------------------------------------------ */

public sealed record InvoiceItem(
    [property: System.Text.Json.Serialization.JsonPropertyName("desc")] string Desc,
    [property: System.Text.Json.Serialization.JsonPropertyName("amount")] long Amount);

public sealed record InvoiceInput(
    string Uid, string? No, string? UserId, long? CreatedAt,
    string Date, string Customer, List<InvoiceItem> Items, long Total);

/// <summary>One invoice row, serialised in the Node server's exact field
/// order: id, uid, no, userId, createdAt, date, customer, items, total,
/// created_at, updated_at.</summary>
public sealed record InvoiceRow(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] long Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("uid")] string Uid,
    [property: System.Text.Json.Serialization.JsonPropertyName("no")] string? No,
    [property: System.Text.Json.Serialization.JsonPropertyName("userId")] string? UserId,
    [property: System.Text.Json.Serialization.JsonPropertyName("createdAt")] long? CreatedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("date")] string Date,
    [property: System.Text.Json.Serialization.JsonPropertyName("customer")] string Customer,
    [property: System.Text.Json.Serialization.JsonPropertyName("items")] List<InvoiceItem> Items,
    [property: System.Text.Json.Serialization.JsonPropertyName("total")] long Total,
    [property: System.Text.Json.Serialization.JsonPropertyName("created_at")] string CreatedAtStr,
    [property: System.Text.Json.Serialization.JsonPropertyName("updated_at")] string UpdatedAtStr);

public sealed class DuplicateNoException : Exception;

public interface IInvoiceStore
{
    Task<List<InvoiceRow>> ListAsync();
    Task<InvoiceRow> CreateAsync(InvoiceInput inv);
    Task<InvoiceRow?> UpdateAsync(long id, InvoiceInput inv);
    Task<bool> DeleteAsync(long id);
}

/* ------------------------------------------------------------------ */
/* PostgreSQL store (Npgsql)                                            */
/* ------------------------------------------------------------------ */

public sealed class PostgresStore : IInvoiceStore
{
    private readonly NpgsqlDataSource _ds;

    private PostgresStore(NpgsqlDataSource ds) => _ds = ds;

    public NpgsqlDataSource DataSource => _ds;

    private const string SelectCols =
        "id, uid, no, user_id, client_created_at, date, customer, items, total, created_at, updated_at";

    private const string CreateTableSql =
        "CREATE TABLE IF NOT EXISTS invoices (" +
        "id SERIAL PRIMARY KEY, " +
        "uid TEXT UNIQUE NOT NULL, " +
        "no TEXT, " +
        "user_id TEXT, " +
        "client_created_at BIGINT, " +
        "date TEXT NOT NULL, " +
        "customer TEXT NOT NULL, " +
        "items JSONB NOT NULL, " +
        "total INTEGER NOT NULL, " +
        "created_at TIMESTAMPTZ NOT NULL DEFAULT now(), " +
        "updated_at TIMESTAMPTZ NOT NULL DEFAULT now())";

    private const string RenameSeedSql =
        "UPDATE invoices SET no = '20261119-002' " +
        "WHERE no = '20261119-001' AND customer = 'Nissan Safari' " +
        "AND date = '2026-11-19' AND total = 300000 " +
        "AND NOT EXISTS (SELECT 1 FROM invoices WHERE no = '20261119-002')";

    /// <summary>Builds the Npgsql connection string from a postgres:// URL.
    /// SSL: an sslmode= in the URL is honoured (libpq semantics); otherwise
    /// SSL is forced on with certificate verification off — the same as the
    /// Node server's ssl: { rejectUnauthorized: false }.</summary>
    static string BuildConnectionString(string databaseUrl)
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            MaxPoolSize = 5,
        };
        var query = ParseQuery(uri.Query);
        if (query.TryGetValue("sslmode", out var sslmode))
        {
            b.SslMode = sslmode switch
            {
                "disable" => SslMode.Disable,
                "prefer" => SslMode.Prefer,
                "require" => SslMode.Require,
                "verify-ca" => SslMode.VerifyCA,
                "verify-full" => SslMode.VerifyFull,
                _ => SslMode.Require,
            };
            // libpq: require/prefer encrypt without verification;
            // verify-ca/verify-full verify against system CA roots.
        }
        else
        {
            // No sslmode in the URL: force SSL on without verification,
            // the same as the Node server's ssl: { rejectUnauthorized: false }.
            b.SslMode = SslMode.Require;
        }
        return b.ConnectionString;
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2) d[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
        }
        return d;
    }

    /// <summary>Runs the startup migrations. Returns null (caller falls back
    /// to the JSON store) when Postgres cannot be reached or a migration
    /// fails — the seed rename failure alone never takes the store down.</summary>
    public static async Task<PostgresStore?> TryCreateAsync(string databaseUrl)
    {
        NpgsqlDataSource ds;
        try
        {
            ds = new NpgsqlDataSourceBuilder(BuildConnectionString(databaseUrl)).Build();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[backend] postgres init failed, falling back to JSON file: " + ex.Message);
            return null;
        }
        try
        {
            await using var conn = await ds.OpenConnectionAsync();
            await ExecAsync(conn, CreateTableSql);
            // Migrate a DB created before the no/userId/createdAt columns existed.
            await ExecAsync(conn, "ALTER TABLE invoices ADD COLUMN IF NOT EXISTS no TEXT");
            await ExecAsync(conn, "ALTER TABLE invoices ADD COLUMN IF NOT EXISTS user_id TEXT");
            await ExecAsync(conn, "ALTER TABLE invoices ADD COLUMN IF NOT EXISTS client_created_at BIGINT");
            await ExecAsync(conn,
                "CREATE UNIQUE INDEX IF NOT EXISTS invoices_no_unique ON invoices(no) WHERE no IS NOT NULL");
            // One-time data fix (v2, 2026-11-20): the Nissan Safari seed invoice
            // was first stored as 20261119-001 and was renumbered to 20261119-002
            // on the devices; make the server row agree so the server list and
            // CSV export show 002 too. Idempotent and restricted to the seed
            // row only; skipped if a 20261119-002 row already exists, so the
            // unique index above can never be violated by this rename. A
            // failure here is only logged — it must not take the store down.
            try
            {
                var renamed = await ExecAsync(conn, RenameSeedSql);
                if (renamed > 0)
                    Console.WriteLine("[backend] renamed seed invoice 20261119-001 -> 20261119-002");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[backend] seed invoice renumber skipped: " + ex.Message);
            }
            return new PostgresStore(ds);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[backend] postgres init failed, falling back to JSON file: " + ex.Message);
            await ds.DisposeAsync();
            return null;
        }
    }

    static async Task<int> ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync();
    }

    static List<InvoiceItem> ParseItems(string json)
    {
        var arr = JsonNode.Parse(json) as JsonArray ?? new JsonArray();
        var items = new List<InvoiceItem>();
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var desc = o["desc"]?.GetValue<string>() ?? "";
            long amount = 0;
            if (o["amount"] is JsonValue v)
            {
                if (!v.TryGetValue<long>(out amount) && v.TryGetValue<double>(out var d)) amount = (long)d;
            }
            items.Add(new InvoiceItem(desc, amount));
        }
        return items;
    }

    static InvoiceRow ReadRow(NpgsqlDataReader r)
    {
        return new InvoiceRow(
            r.GetInt32(0),
            r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetInt64(4),
            r.GetString(5),
            r.GetString(6),
            ParseItems(r.GetString(7)),
            r.GetInt32(8),
            Shared.FormatTs(r.GetDateTime(9)),
            Shared.FormatTs(r.GetDateTime(10)));
    }

    static string ItemsJson(List<InvoiceItem> items) =>
        JsonSerializer.Serialize(items, Shared.JsonOpts);

    static void AddInvoiceParams(NpgsqlCommand cmd, InvoiceInput inv)
    {
        cmd.Parameters.AddWithValue("@uid", inv.Uid);
        cmd.Parameters.AddWithValue("@no", (object?)inv.No ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@userId", (object?)inv.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", (object?)inv.CreatedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@date", inv.Date);
        cmd.Parameters.AddWithValue("@customer", inv.Customer);
        cmd.Parameters.Add(new NpgsqlParameter("@items", NpgsqlDbType.Jsonb) { Value = ItemsJson(inv.Items) });
        // PG total is INTEGER; an out-of-range total fails here (500), same as Node.
        cmd.Parameters.AddWithValue("@total", checked((int)inv.Total));
    }

    public async Task<List<InvoiceRow>> ListAsync()
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SelectCols} FROM invoices ORDER BY created_at DESC, id DESC", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<InvoiceRow>();
        while (await r.ReadAsync()) list.Add(ReadRow(r));
        return list;
    }

    public async Task<InvoiceRow> CreateAsync(InvoiceInput inv)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO invoices (uid, no, user_id, client_created_at, date, customer, items, total) " +
                "VALUES (@uid,@no,@userId,@createdAt,@date,@customer,@items,@total) " +
                $"RETURNING {SelectCols}", conn);
            AddInvoiceParams(cmd, inv);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            return ReadRow(r);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            if (ex.ConstraintName == "invoices_no_unique") throw new DuplicateNoException();
            // uid already exists (e.g. re-seed after a retry): return the existing row
            await using var conn = await _ds.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                $"SELECT {SelectCols} FROM invoices WHERE uid = @uid", conn);
            cmd.Parameters.AddWithValue("@uid", inv.Uid);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync()) return ReadRow(r);
            throw;
        }
    }

    public async Task<InvoiceRow?> UpdateAsync(long id, InvoiceInput inv)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE invoices SET no=COALESCE(@no, no), user_id=COALESCE(@userId, user_id), " +
                "client_created_at=COALESCE(@createdAt, client_created_at), date=@date, customer=@customer, " +
                "items=@items, total=@total, updated_at=now() WHERE id=@id " +
                $"RETURNING {SelectCols}", conn);
            AddInvoiceParams(cmd, inv);
            cmd.Parameters.AddWithValue("@id", id);
            await using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? ReadRow(r) : null;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == "invoices_no_unique")
        {
            throw new DuplicateNoException();
        }
    }

    public async Task<bool> DeleteAsync(long id)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM invoices WHERE id=@id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }
}

/* ------------------------------------------------------------------ */
/* JSON file store (fallback when DATABASE_URL is unset)                 */
/* ------------------------------------------------------------------ */

public sealed class JsonStore : IInvoiceStore
{
    private readonly string _file;
    private readonly object _gate = new();

    public JsonStore(string file) => _file = file;

    sealed record JsonDoc(long NextId, JsonArray Invoices);

    static string? JStr(JsonObject o, string k)
    {
        if (o[k] is not JsonValue v || v.GetValueKind() == JsonValueKind.Null) return null;
        return v.GetValue<string>();
    }

    static long? JLong(JsonObject o, string k)
    {
        if (o[k] is not JsonValue v || v.GetValueKind() == JsonValueKind.Null) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return null;
    }

    static JsonNode? JStrNode(string? s) => s is null ? JsonValue.Create<string?>(null) : JsonValue.Create(s);
    static JsonNode? JLongNode(long? n) => n is null ? JsonValue.Create<long?>(null) : JsonValue.Create(n.Value);

    static JsonArray ItemsArray(List<InvoiceItem> items)
    {
        var arr = new JsonArray();
        foreach (var it in items)
        {
            var o = new JsonObject { ["desc"] = it.Desc, ["amount"] = it.Amount };
            arr.Add(o);
        }
        return arr;
    }

    static InvoiceRow RowFromJson(JsonObject r)
    {
        var items = new List<InvoiceItem>();
        if (r["items"] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                items.Add(new InvoiceItem(JStr(o, "desc") ?? "", JLong(o, "amount") ?? 0));
            }
        }
        // Node rowToInvoice: createdAt = client_created_at, else Number(createdAt) (NaN -> null).
        long? createdAt = JLong(r, "client_created_at") ?? JLong(r, "createdAt");
        return new InvoiceRow(
            JLong(r, "id") ?? 0,
            JStr(r, "uid") ?? "",
            JStr(r, "no"),
            JStr(r, "user_id") ?? JStr(r, "userId"),
            createdAt,
            JStr(r, "date") ?? "",
            JStr(r, "customer") ?? "",
            items,
            JLong(r, "total") ?? 0,
            JStr(r, "created_at") ?? "",
            JStr(r, "updated_at") ?? "");
    }

    JsonDoc Read()
    {
        string raw;
        try
        {
            raw = File.ReadAllText(_file);
        }
        catch (FileNotFoundException) { return new JsonDoc(1, new JsonArray()); }
        catch (DirectoryNotFoundException) { return new JsonDoc(1, new JsonArray()); }
        catch (Exception ex)
        {
            Console.WriteLine("[backend] could not read " + _file + " " + ex.Message);
            return new JsonDoc(1, new JsonArray());
        }
        try
        {
            var node = JsonNode.Parse(raw) as JsonObject;
            if (node?["invoices"] is JsonArray arr)
                return new JsonDoc(JLong(node, "nextId") ?? 1, arr);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[backend] could not read " + _file + " " + ex.Message);
        }
        return new JsonDoc(1, new JsonArray());
    }

    void Write(JsonDoc doc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        // DeepClone: the array parsed by Read() still belongs to its parsed
        // root object and cannot be re-parented into a new JsonObject.
        var obj = new JsonObject { ["nextId"] = doc.NextId, ["invoices"] = doc.Invoices.DeepClone() };
        var tmp = _file + ".tmp";
        // Same bytes as Node's JSON.stringify(doc, null, 2): indented, raw UTF-8.
        var opts = new JsonSerializerOptions(Shared.JsonOpts) { WriteIndented = true };
        File.WriteAllText(tmp, obj.ToJsonString(opts), Shared.Utf8NoBom);
        File.Move(tmp, _file, overwrite: true); // atomic replace
    }

    public Task<List<InvoiceRow>> ListAsync()
    {
        lock (_gate)
        {
            var doc = Read();
            var rows = doc.Invoices.OfType<JsonObject>().ToList();
            // Node: sort by created_at string desc, then id desc.
            rows.Sort((a, b) =>
                string.CompareOrdinal(JStr(b, "created_at") ?? "", JStr(a, "created_at") ?? "") != 0
                    ? string.CompareOrdinal(JStr(b, "created_at") ?? "", JStr(a, "created_at") ?? "")
                    : (JLong(b, "id") ?? 0).CompareTo(JLong(a, "id") ?? 0));
            return Task.FromResult(rows.Select(RowFromJson).ToList());
        }
    }

    public Task<InvoiceRow> CreateAsync(InvoiceInput inv)
    {
        lock (_gate)
        {
            var doc = Read();
            var rows = doc.Invoices.OfType<JsonObject>().ToList();
            var existing = rows.FirstOrDefault(r => JStr(r, "uid") == inv.Uid);
            if (existing is not null) return Task.FromResult(RowFromJson(existing));
            if (inv.No is not null && rows.Any(r => JStr(r, "no") == inv.No))
                throw new DuplicateNoException();
            var now = Shared.IsoNow();
            var row = new JsonObject
            {
                ["id"] = doc.NextId,
                ["uid"] = inv.Uid,
                ["no"] = JStrNode(inv.No),
                ["user_id"] = JStrNode(inv.UserId),
                ["client_created_at"] = JLongNode(inv.CreatedAt),
                ["date"] = inv.Date,
                ["customer"] = inv.Customer,
                ["items"] = ItemsArray(inv.Items),
                ["total"] = inv.Total,
                ["created_at"] = now,
                ["updated_at"] = now,
            };
            doc.Invoices.Add(row);
            Write(new JsonDoc(doc.NextId + 1, doc.Invoices));
            return Task.FromResult(RowFromJson(row));
        }
    }

    public Task<InvoiceRow?> UpdateAsync(long id, InvoiceInput inv)
    {
        lock (_gate)
        {
            var doc = Read();
            var rows = doc.Invoices.OfType<JsonObject>().ToList();
            var row = rows.FirstOrDefault(r => (JLong(r, "id") ?? -1) == id);
            if (row is null) return Task.FromResult<InvoiceRow?>(null);
            if (inv.No is not null)
            {
                if (rows.Any(r => (JLong(r, "id") ?? -1) != id && JStr(r, "no") == inv.No))
                    throw new DuplicateNoException();
                row["no"] = inv.No;
            }
            if (inv.UserId is not null) row["user_id"] = inv.UserId;
            if (inv.CreatedAt is not null) row["client_created_at"] = inv.CreatedAt.Value;
            row["date"] = inv.Date;
            row["customer"] = inv.Customer;
            row["items"] = ItemsArray(inv.Items);
            row["total"] = inv.Total;
            row["updated_at"] = Shared.IsoNow();
            Write(doc);
            return Task.FromResult<InvoiceRow?>(RowFromJson(row));
        }
    }

    public Task<bool> DeleteAsync(long id)
    {
        lock (_gate)
        {
            var doc = Read();
            var before = doc.Invoices.Count;
            var kept = new JsonArray();
            foreach (var n in doc.Invoices)
            {
                if (n is JsonObject o && (JLong(o, "id") ?? -1) == id) continue;
                kept.Add(n?.DeepClone());
            }
            if (kept.Count == before) return Task.FromResult(false);
            Write(new JsonDoc(doc.NextId, kept));
            return Task.FromResult(true);
        }
    }
}

/// <summary>Helpers shared by the top-level dispatcher and the store classes
/// (top-level local functions are not visible from classes declared below).</summary>
public static class Shared
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // like JSON.stringify: UTF-8 out, not \uXXXX
    };

    public static string IsoNow() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>UTF-8 without a BOM preamble. .NET's Encoding.UTF8 emits one;
    /// Node's JSON responses, CSV and JSON file never have one.</summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string FormatTs(DateTime dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

