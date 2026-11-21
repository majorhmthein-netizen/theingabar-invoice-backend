using Microsoft.AspNetCore.Mvc;

public class FormModel : AppPageModel
{
    private readonly IInvoiceStore _store;
    public FormModel(IInvoiceStore store) => _store = store;

    public string Date { get; private set; } = "";
    public string Customer { get; private set; } = "";
    public List<InvoiceItem> Items { get; private set; } = new();
    public long Deposit { get; private set; }
    public string PaperSize { get; private set; } = "A4";
    public int RowCount { get; private set; } = 5;
    public long? EditId { get; private set; }
    public string? Error { get; private set; }

    void LoadDraft()
    {
        var f = Request.Form;
        Date = f["Date"].FirstOrDefault() ?? AppDb.TodayYangon().ToString("yyyy-MM-dd");
        Customer = f["Customer"].FirstOrDefault() ?? "";
        Items = Paper.ParseItems(f);
        Deposit = long.TryParse((f["Deposit"].FirstOrDefault() ?? "").Trim().Replace(",", ""),
            out var dep) ? Math.Max(0, dep) : 0;
        // Paper size (Han 2026-11-21): chosen per invoice, A4 (full page)
        // is the default; only the exact value "Half" means half-A4.
        PaperSize = f["PaperSize"].FirstOrDefault() == "Half" ? "Half" : "A4";
        RowCount = int.TryParse(f["RowCount"].FirstOrDefault(), out var rc)
            ? Math.Clamp(rc, 1, 20) : 5;
        EditId = long.TryParse(f["EditId"].FirstOrDefault(), out var eid) ? eid : null;
    }

    void FillForRender(List<InvoiceItem> items, int rowCount)
    {
        Items = items;
        RowCount = rowCount;
        while (Items.Count < RowCount) Items.Add(new InvoiceItem("", 0));
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        if (await NeedWriteAsync() is { } redirect) return redirect;
        Date = AppDb.TodayYangon().ToString("yyyy-MM-dd");
        if (TempData["Draft"] is string draftJson)
        {
            try
            {
                var draft = System.Text.Json.JsonSerializer.Deserialize<DraftData>(draftJson);
                if (draft is not null)
                {
                    Date = draft.Date;
                    Customer = draft.Customer;
                    Deposit = draft.Deposit;
                    PaperSize = draft.PaperSize;
                    EditId = draft.EditId;
                    FillForRender(draft.Items, draft.RowCount);
                    return Page();
                }
            }
            catch { /* fall through to defaults */ }
        }
        if (id is not null)
        {
            var row = (await _store.ListAsync()).FirstOrDefault(r => r.Id == id.Value);
            if (row is null) return NotFound();
            if (row.UserId != Me!.User.LocalId && !Me.IsAdmin) return Forbid();
            EditId = row.Id;
            Date = row.Date;
            Customer = row.Customer;
            Deposit = row.Deposit;
            PaperSize = row.PaperSize;
            FillForRender(row.Items, Math.Max(5, row.Items.Count));
        }
        else
        {
            FillForRender(new List<InvoiceItem>(), 5);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAddRowAsync()
    {
        if (await NeedWriteAsync() is { } redirect) return redirect;
        LoadDraft();
        FillForRender(Items, Math.Min(20, RowCount + 1));
        return Page();
    }

    /// <summary>Preview's "Back to form": the draft is posted back and
    /// re-rendered, so nothing typed is lost. (Was missing until
    /// 2026-11-21: with no handler the POST rendered the page with no
    /// handler run, so LoadCommonAsync never set Me — the toolbar showed
    /// Log in / Sign up — and LoadDraft never ran, so the form came
    /// back empty.)</summary>
    public async Task<IActionResult> OnPostBackAsync()
    {
        if (await NeedWriteAsync() is { } redirect) return redirect;
        LoadDraft();
        FillForRender(Items, RowCount);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveRowAsync(int row)
    {
        if (await NeedWriteAsync() is { } redirect) return redirect;
        LoadDraft();
        if (row >= 0 && row < Items.Count) Items.RemoveAt(row);
        FillForRender(Items, Math.Max(1, RowCount - 1));
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (await NeedWriteAsync() is { } redirect) return redirect;
        // The invoice is issued now, so drop the preview draft kept in
        // TempData: the next new-invoice form must be fresh, not pre-filled
        // with the just-issued invoice.
        TempData.Remove("Draft");
        LoadDraft();
        Error = Paper.ValidateDraft(Date, Customer, Items, Deposit);
        if (Error is not null)
        {
            FillForRender(Items, RowCount);
            return Page();
        }
        var total = Items.Sum(i => i.Amount);
        if (EditId is not null)
        {
            var existing = (await _store.ListAsync()).FirstOrDefault(r => r.Id == EditId.Value);
            if (existing is null) return NotFound();
            if (existing.UserId != Me!.User.LocalId && !Me.IsAdmin) return Forbid();
            var input = new InvoiceInput(existing.Uid, existing.No, existing.UserId,
                existing.CreatedAt, Date.Trim(), Customer.Trim(), Items, total, Deposit, PaperSize);
            await _store.UpdateAsync(existing.Id, input);
            Flash($"Invoice {existing.No} saved.");
            return RedirectToPage("/Invoice", new { id = existing.Id });
        }
        var created = await AppDb.CreateInvoiceNumberedAsync(new InvoiceInput(
            Guid.NewGuid().ToString(), null, Me!.User.LocalId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Date.Trim(), Customer.Trim(), Items, total, Deposit, PaperSize));
        Flash($"Invoice {created.No} saved.");
        return RedirectToPage("/Invoice", new { id = created.Id });
    }
}

/// <summary>Draft round-tripped between Form and Preview via TempData.</summary>
public sealed record DraftData(string Date, string Customer, List<InvoiceItem> Items, int RowCount, long? EditId, long Deposit = 0, string PaperSize = "A4");
