using System.Globalization;
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
        // Home is the public screen: the admin account and the admin's own
        // invoices never appear here (Han, 2026-11-21). The full picture
        // stays in the admin panel (/admin/log). Mechanics only.
        var adminIds = (await AppDb.ListUsersAsync())
            .Where(u => u.IsAdmin).Select(u => u.LocalId).ToHashSet();
        Users = (await AppDb.ListApprovedUsersAsync())
            .Where(u => !u.IsAdmin).ToList();
        var all = await _store.ListAsync();
        Recent = all.Where(r => r.UserId is null || !adminIds.Contains(r.UserId))
            .Take(10).ToList();
        var (wf, wt) = AppDb.ThisWeek();
        var (mf, mt) = AppDb.ThisMonth();
        WeekFrom = wf.ToString("yyyy-MM-dd");
        WeekTo = wt.ToString("yyyy-MM-dd");
        // Income totals are mechanics-only too, so the totals match the
        // invoices actually shown (same inclusive Mon–Sun / month ranges
        // as PeriodTotalAsync, computed over the already-loaded list).
        WeekTotal = TotalIn(all, wf, wt, adminIds);
        MonthTotal = TotalIn(all, mf, mt, adminIds);
        return Page();
    }

    static long TotalIn(List<InvoiceRow> rows, DateOnly from, DateOnly to,
        HashSet<string> adminIds)
    {
        long sum = 0;
        foreach (var r in rows)
        {
            if (r.UserId is not null && adminIds.Contains(r.UserId)) continue;
            if (!DateOnly.TryParseExact(r.Date, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (d >= from && d <= to) sum += r.Total;
        }
        return sum;
    }
}
