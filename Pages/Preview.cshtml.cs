using Microsoft.AspNetCore.Mvc;

public class PreviewModel : AppPageModel
{
    private readonly IInvoiceStore _store;
    public PreviewModel(IInvoiceStore store) => _store = store;

    public InvoicePaperModel PaperModel { get; private set; } = null!;
    public string Date { get; private set; } = "";
    public string Customer { get; private set; } = "";
    public List<InvoiceItem> Items { get; private set; } = new();
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
        RowCount = int.TryParse(f["RowCount"].FirstOrDefault(), out var rc)
            ? Math.Clamp(rc, 1, 20) : 5;
        EditId = long.TryParse(f["EditId"].FirstOrDefault(), out var eid) ? eid : null;
        Error = Paper.ValidateDraft(Date, Customer, Items);
        if (Error is not null)
        {
            // Preserve the draft across the redirect in TempData (cookie,
            // chunked) so the user does not lose what they typed.
            TempData["Draft"] = System.Text.Json.JsonSerializer.Serialize(
                new DraftData(Date, Customer, Items, RowCount, EditId));
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
        PaperModel = new InvoicePaperModel(Meta!, payName, payNum, qrSrc,
            Customer, Date, no, Items, Items.Sum(i => i.Amount));
        return Page();
    }
}
