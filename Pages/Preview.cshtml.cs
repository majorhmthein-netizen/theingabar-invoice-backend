using Microsoft.AspNetCore.Mvc;

public class PreviewModel : AppPageModel
{
    private readonly IInvoiceStore _store;
    public PreviewModel(IInvoiceStore store) => _store = store;

    public InvoicePaperModel PaperModel { get; private set; } = null!;
    public string Date { get; private set; } = "";
    public string Customer { get; private set; } = "";
    public List<InvoiceItem> Items { get; private set; } = new();
    public long Deposit { get; private set; }
    public string PaperSize { get; private set; } = "A4";
    public int RowCount { get; private set; } = 5;
    public long? EditId { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await NeedWriteAsync() is { } redirect) return redirect;
        var f = Request.Form;
        Date = f["Date"].FirstOrDefault() ?? "";
        Customer = f["Customer"].FirstOrDefault() ?? "";
        Items = Paper.ParseItems(f);
        Deposit = long.TryParse((f["Deposit"].FirstOrDefault() ?? "").Trim().Replace(",", ""),
            out var dep) ? Math.Max(0, dep) : 0;
        PaperSize = f["PaperSize"].FirstOrDefault() == "Half" ? "Half" : "A4";
        RowCount = int.TryParse(f["RowCount"].FirstOrDefault(), out var rc)
            ? Math.Clamp(rc, 1, 20) : 5;
        EditId = long.TryParse(f["EditId"].FirstOrDefault(), out var eid) ? eid : null;
        Error = Paper.ValidateDraft(Date, Customer, Items, Deposit);
        if (Error is not null)
        {
            // Preserve the draft across the redirect in TempData (cookie,
            // chunked) so the user does not lose what they typed.
            TempData["Draft"] = System.Text.Json.JsonSerializer.Serialize(
                new DraftData(Date, Customer, Items, RowCount, EditId, Deposit, PaperSize));
            TempData["FlashError"] = Error;
            return RedirectToPage("/Form");
        }
        // Post-Redirect-Get: keep the draft in TempData and redirect to
        // GET /preview, so the preview page is a GET page. (Returning
        // Page() here made Preview a POST result, and the device back
        // button from the issued invoice landed on it — Chrome "Confirm
        // Form Resubmission" / ERR_CACHE_MISS.)
        TempData["Draft"] = System.Text.Json.JsonSerializer.Serialize(
            new DraftData(Date, Customer, Items, RowCount, EditId, Deposit, PaperSize));
        return RedirectToPage("/Preview");
    }
    /// to /preview, the draft is kept in TempData, and the browser is
    /// redirected to GET /preview. Before this, Preview returned Page()
    /// from the POST, so the device back button from the issued invoice
    /// landed on a POST page and Chrome showed "Confirm Form
    /// Resubmission" / ERR_CACHE_MISS. Now back lands on a GET page.</summary>
    public async Task<IActionResult> OnGetAsync()
    {
        if (await NeedWriteAsync() is { } redirect) return redirect;
        if (TempData["Draft"] is not string json) return RedirectToPage("/Form");
        DraftData? draft;
        try { draft = System.Text.Json.JsonSerializer.Deserialize<DraftData>(json); }
        catch { return RedirectToPage("/Form"); }
        if (draft is null) return RedirectToPage("/Form");
        // Keep the draft so a refresh of the preview still renders, and
        // so Back-to-form can still go back to this preview. Save removes
        // it (FormModel.OnPostSaveAsync) so the next new invoice is fresh.
        TempData.Keep("Draft");
        Date = draft.Date;
        Customer = draft.Customer;
        Items = draft.Items;
        Deposit = draft.Deposit;
        PaperSize = draft.PaperSize;
        RowCount = draft.RowCount;
        EditId = draft.EditId;
        Error = Paper.ValidateDraft(Date, Customer, Items, Deposit);
        if (Error is not null)
        {
            TempData["FlashError"] = Error;
            return RedirectToPage("/Form");
        }
        string no;
        if (EditId is not null)
        {
            var existing = (await _store.ListAsync()).FirstOrDefault(r => r.Id == EditId.Value);
            if (existing is null) return NotFound();
            if (existing.UserId != Me!.User.LocalId && !Me.IsAdmin) return Forbid();
            no = existing.No ?? "";
        }
        else
        {
            no = await AppDb.PeekNextNumberAsync(Date);
        }
        var (payName, payNum, qrSrc) = Paper.PaymentFor(Me!.User, Meta!);
        var qrCacheKey = Me!.User.HasQr ? $"tgb-qr-{Me!.User.Id}"
            : Meta!.HasQr ? "tgb-qr-meta" : "";
        PaperModel = new InvoicePaperModel(Meta!, payName, payNum, qrSrc,
            Customer, Date, no, Items, Items.Sum(i => i.Amount), Deposit, PaperSize, qrCacheKey);
        return Page();
    }
}
