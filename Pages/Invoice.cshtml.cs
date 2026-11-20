using Microsoft.AspNetCore.Mvc;

public class InvoiceModel : AppPageModel
{
    private readonly IInvoiceStore _store;
    public InvoiceModel(IInvoiceStore store) => _store = store;

    public InvoiceRow? Row { get; private set; }
    public InvoicePaperModel PaperModel { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (NeedDb(out var redirect)) return redirect!;
        await LoadCommonAsync();
        Row = (await _store.ListAsync()).FirstOrDefault(r => r.Id == id);
        if (Row is null) return NotFound();
        UserRow? owner = null;
        if (Row.UserId is not null)
            owner = await AppDb.FindUserByLocalIdAsync(Row.UserId);
        PaperModel = Paper.PaperFor(Row, owner, Meta!);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        if (await NeedWriteAsync() is { } redirect) return redirect;
        var row = (await _store.ListAsync()).FirstOrDefault(r => r.Id == id);
        if (row is null) return NotFound();
        if (row.UserId != Me!.User.LocalId && !Me.IsAdmin) return Forbid();
        await _store.DeleteAsync(id);
        Flash($"Invoice {row.No} deleted.");
        return RedirectToPage("/Log");
    }
}
