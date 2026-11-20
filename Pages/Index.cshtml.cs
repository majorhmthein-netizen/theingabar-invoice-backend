using Microsoft.AspNetCore.Mvc;

public class IndexModel : AppPageModel
{
    private readonly IInvoiceStore _store;
    public IndexModel(IInvoiceStore store) => _store = store;

    public List<UserRow> Users { get; private set; } = new();
    public List<InvoiceRow> Recent { get; private set; } = new();
    public long WeekTotal { get; private set; }
    public long MonthTotal { get; private set; }
    public string WeekFrom { get; private set; } = "";
    public string WeekTo { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync()
    {
        if (NeedDb(out var redirect)) return redirect!;
        await LoadCommonAsync();
        Users = await AppDb.ListApprovedUsersAsync();
        var all = await _store.ListAsync();
        Recent = all.Take(10).ToList();
        var (wf, wt) = AppDb.ThisWeek();
        var (mf, mt) = AppDb.ThisMonth();
        WeekFrom = wf.ToString("yyyy-MM-dd");
        WeekTo = wt.ToString("yyyy-MM-dd");
        WeekTotal = await AppDb.PeriodTotalAsync(wf, wt, null);
        MonthTotal = await AppDb.PeriodTotalAsync(mf, mt, null);
        return Page();
    }
}
