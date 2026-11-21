/* Shared helpers for the HTML-only Razor Pages. */
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>Base class for every page: loads the shop meta + current
/// session user into ViewData for _Layout, and offers the common
/// login/DB gates. Pages never trust the client for identity — the
/// session row in Postgres is the only source of truth.</summary>
public abstract class AppPageModel : PageModel
{
    public MetaRow? Meta { get; private set; }
    public SessionUser? Me { get; private set; }

    protected async Task LoadCommonAsync()
    {
        if (AppDb.Available) Meta = await AppDb.GetMetaAsync();
        Me = await Auth.CurrentUserAsync(HttpContext);
        ViewData["Meta"] = Meta;
        ViewData["Me"] = Me;
        // Header notification badge (Han 2026-11-21, v34): the logged-in
        // user's active notifications (unread admin messages + unpaid
        // invoice reminders). 0 when logged out or the DB is down.
        ViewData["NotiCount"] = Me is not null && AppDb.Available
            ? await AppDb.CountActiveNotificationsAsync(Me.User.Id) : 0;
    }

    /// <summary>Redirects to /dberror when Postgres is unavailable
    /// (the new pages need it; the legacy /api JSON store still works).</summary>
    protected bool NeedDb(out IActionResult? redirect)
    {
        redirect = null;
        if (AppDb.Available) return false;
        redirect = RedirectToPage("/DbError");
        return true;
    }

    /// <summary>Requires a logged-in user; returns a redirect to /login
    /// when not logged in, else null. Sets Me on success.</summary>
    protected async Task<IActionResult?> NeedLoginAsync()
    {
        await LoadCommonAsync();
        if (Me is null)
        {
            return RedirectToPage("/Login",
                new { returnUrl = HttpContext.Request.Path + HttpContext.Request.QueryString });
        }
        return null;
    }

    /// <summary>Requires a logged-in, APPROVED user (write gate).</summary>
    protected async Task<IActionResult?> NeedWriteAsync()
    {
        var r = await NeedLoginAsync();
        if (r is not null) return r;
        if (!Me!.CanWrite)
        {
            TempData["FlashError"] = "pending";
            return RedirectToPage("/User");
        }
        return null;
    }

    /// <summary>Requires an admin session (Google-verified).</summary>
    protected async Task<IActionResult?> NeedAdminAsync()
    {
        var r = await NeedLoginAsync();
        if (r is not null) return r;
        if (!Me!.IsAdmin) return RedirectToPage("/Index");
        return null;
    }

    protected void Flash(string msg) => TempData["Flash"] = msg;
    protected void FlashError(string msg) => TempData["FlashError"] = msg;
}

/// <summary>Formatting helpers matching the PWA exactly (fmt / fmtDate).</summary>
public static class Paper
{
    static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    public static string Fmt(long n) => n.ToString("N0", EnUs);

    public static string FmtDate(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "";
        var p = iso.Split('-');
        return p.Length == 3 ? p[2] + ". " + p[1] + ". " + p[0] : iso;
    }

    /// <summary>Title case for the thanks-line group name:
    /// "THEINGABAR AUTOMOTIVE GROUP" → "Theingabar Automotive Group".</summary>
    public static string TitleCase(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return EnUs.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }

    /// <summary>Payment name/number + QR source for an invoice, with the
    /// PWA's precedence: the invoice owner's KBZ template, else shop meta,
    /// else the embedded seed QR file.</summary>
    public static (string PayName, string PayNum, string QrSrc) PaymentFor(
        UserRow? owner, MetaRow meta)
    {
        var payName = !string.IsNullOrWhiteSpace(owner?.KbzName) ? owner!.KbzName! : meta.PayName;
        var payNum = !string.IsNullOrWhiteSpace(owner?.KbzNumber) ? owner!.KbzNumber! : meta.PayNum;
        string qrSrc;
        if (owner is not null && owner.HasQr) qrSrc = $"/qr/user/{owner.Id}";
        else if (meta.HasQr) qrSrc = "/qr/meta";
        else qrSrc = "/seed-qr.jpg";
        return (payName, payNum, qrSrc);
    }

    public static InvoicePaperModel PaperFor(InvoiceRow row, UserRow? owner, MetaRow meta)
    {
        var (payName, payNum, qrSrc) = PaymentFor(owner, meta);
        // Front-end cache key for the invoice QR (Han 2026-11-21: all
        // user uploads cached): the owner's QR, the shop default QR, or
        // "" for the static seed QR (already cached by the service worker).
        var qrCacheKey = owner is not null && owner.HasQr ? $"tgb-qr-{owner.Id}"
            : meta.HasQr ? "tgb-qr-meta" : "";
        return new InvoicePaperModel(meta, payName, payNum, qrSrc,
            row.Customer, row.Date, row.No ?? "", row.Items, row.Total, row.Deposit, row.PaperSize, qrCacheKey);
    }

    /// <summary>Parses a posted item list (desc0/amount0 … desc19/amount19).
    /// Rows with an empty description AND empty amount are dropped; a row
    /// with a description but no amount counts as 0 (same as the PWA).</summary>
    public static List<InvoiceItem> ParseItems(IFormCollection form)
    {
        var items = new List<InvoiceItem>();
        for (var i = 0; i < 20; i++)
        {
            var desc = (form[$"desc{i}"].FirstOrDefault() ?? "").Trim();
            var amtRaw = (form[$"amount{i}"].FirstOrDefault() ?? "").Trim().Replace(",", "");
            var hasAmount = long.TryParse(amtRaw, out var amount);
            if (desc.Length == 0 && !hasAmount) continue;
            if (!hasAmount) amount = 0;
            items.Add(new InvoiceItem(desc, amount));
        }
        return items;
    }

    /// <summary>Items-table size tier (Han 2026-11-21): 0 = up to 10 items
    /// (the default/max size — few items never grow past it), 1 = 11–15
    /// items, 2 = 16–20 items (smallest, so a full 20-item invoice still
    /// fits on one page). Shared by the HTML paper (_InvoicePaper) and
    /// the PDF (PdfService) so both shrink identically.</summary>
    public static int ItemTier(int count) => count >= 16 ? 2 : count >= 11 ? 1 : 0;

    /// <summary>One-page fit tier (Han 2026-11-21 "1 ရွက်ထဲ"): the fixed
    /// blocks (logo, payment box + QR, signatures, thanks, paddings) shrink
    /// as the item count grows, so the whole invoice still fits on one A4
    /// page. 0 = full size (≤4 items); 1 = 5–7; 2 = 8–10; 3 = 11–15;
    /// 4 = 16–20. Separate from ItemTier (description font) so few-item
    /// invoices keep the full-size layout.</summary>
    public static int CompactTier(int count) =>
        count >= 16 ? 4 : count >= 11 ? 3 : count >= 8 ? 2 : count >= 5 ? 1 : 0;

    public static string CompactClass(int count) =>
        CompactTier(count) switch { 1 => " inv-c1", 2 => " inv-c2", 3 => " inv-c3", 4 => " inv-c4", _ => "" };

    /// <summary>Validates a parsed invoice (same rules as the /api server
    /// validation). Returns an error string or null. Deposit (စရံငွေ, Han
    /// 2026-11-21) must be >= 0 and no more than the items total; the paper
    /// shows Total - Deposit as the final total only when Deposit > 0.</summary>
    public static string? ValidateDraft(string? date, string? customer, List<InvoiceItem> items, long deposit = 0)
    {
        if (string.IsNullOrWhiteSpace(date)) return "date is required";
        if (!DateOnly.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            return "date must look like YYYY-MM-DD";
        if (string.IsNullOrWhiteSpace(customer)) return "customer is required";
        if (items.Count == 0) return "items must be a non-empty array";
        if (items.Count > 20) return "at most 20 items";
        foreach (var it in items)
        {
            if (it.Amount < 0) return "each item amount must be an integer >= 0";
        }
        if (deposit < 0) return "deposit must be an integer >= 0";
        if (deposit > items.Sum(i => i.Amount)) return "deposit cannot be more than the total";
        return null;
    }
}

/// <summary>Model for the frozen invoice-paper partial.</summary>
public sealed record InvoicePaperModel(
    MetaRow Meta, string PayName, string PayNum, string QrSrc,
    string Customer, string Date, string No, List<InvoiceItem> Items, long Total, long Deposit = 0,
    string PaperSize = "A4", string QrCacheKey = "")
{
    /// <summary>Amount still to pay: Total - Deposit. When Deposit is 0
    /// this equals Total, and the paper shows only Total (Han 2026-11-21:
    /// a zero deposit is not printed).</summary>
    public long FinalTotal => Total - Deposit;
    /// <summary>True when this invoice prints on half-A4 (A4 တ၀က်စာ)
    /// instead of a full A4 page (Han 2026-11-21).</summary>
    public bool IsHalf => PaperSize == "Half";
}
