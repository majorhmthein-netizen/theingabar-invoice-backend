using Microsoft.AspNetCore.Mvc;

public class MonthModel : AppPageModel
{
    private readonly IInvoiceStore _store;
    public MonthModel(IInvoiceStore store) => _store = store;

    public string Ym { get; private set; } = "";
    public string Label { get; private set; } = "";
    public List<InvoiceRow> Rows { get; private set; } = new();
    public long Total { get; private set; }

    public async Task<IActionResult> OnGetAsync(string ym)
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        if (NeedDb(out var dbRedirect)) return dbRedirect!;
        await LoadCommonAsync();
        var adminIds = (await AppDb.ListUsersAsync())
            .Where(u => u.IsAdmin).Select(u => u.LocalId).ToHashSet();
        var all = await _store.ListAsync();
        var month = AppDb.MonthRows(all, adminIds, ym);
        if (month is null) return NotFound();
        Ym = ym;
        Label = month.Value.Label;
        Rows = month.Value.Rows;
        Total = month.Value.Total;
        return Page();
    }
}
