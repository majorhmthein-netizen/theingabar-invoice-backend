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
            TempData["FlashError"] = "Waiting for Admin approval — read-only until approved.";
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
        return new InvoicePaperModel(meta, payName, payNum, qrSrc,
            row.Customer, row.Date, row.No ?? "", row.Items, row.Total);
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

    /// <summary>Validates a parsed invoice (same rules as the /api server
    /// validation). Returns an error string or null.</summary>
    public static string? ValidateDraft(string? date, string? customer, List<InvoiceItem> items)
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
        return null;
    }
}

/// <summary>Model for the frozen invoice-paper partial.</summary>
public sealed record InvoicePaperModel(
    MetaRow Meta, string PayName, string PayNum, string QrSrc,
    string Customer, string Date, string No, List<InvoiceItem> Items, long Total);
