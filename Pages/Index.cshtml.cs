using System.Globalization;
using Microsoft.AspNetCore.Mvc;

/// <summary>One earlier month's closing (လချုပ်) for the Home screen.</summary>
public sealed record MonthSummary(string Ym, string Label, long Total, int Count);

public class IndexModel : AppPageModel
{
    private readonly IInvoiceStore _store;
    public IndexModel(IInvoiceStore store) => _store = store;

    public List<UserRow> Users { get; private set; } = new();
    public List<MonthSummary> Months { get; private set; } = new();
    public long MonthTotal { get; private set; }
    public string MonthYm { get; private set; } = "";

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
        var (mf, mt) = AppDb.ThisMonth();
        MonthYm = mf.ToString("yyyy-MM");
        // Income totals are mechanics-only too, so the totals match the
        // invoices actually shown (same inclusive month range as
        // PeriodTotalAsync, computed over the already-loaded list).
        MonthTotal = TotalIn(all, mf, mt, adminIds);
        // Previous months (လချုပ်, Han 2026-11-21): every earlier month
        // that has a mechanics invoice, most recent first. The current
        // month is reached from the This month income line, not here.
        var byMonth = new Dictionary<string, (long Total, int Count)>();
        foreach (var r in all)
        {
            if (r.UserId is not null && adminIds.Contains(r.UserId)) continue;
            if (!DateOnly.TryParseExact(r.Date, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            var ym = d.ToString("yyyy-MM");
            if (ym == MonthYm) continue;
            if (!byMonth.TryGetValue(ym, out var cur)) cur = (0, 0);
            byMonth[ym] = (cur.Total + r.Total, cur.Count + 1);
        }
        Months = byMonth.Keys.OrderByDescending(k => k).Select(ym =>
        {
            var (y, m) = (int.Parse(ym[..4]), int.Parse(ym[5..7]));
            var label = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m)
                + " " + y;
            return new MonthSummary(ym, label, byMonth[ym].Total, byMonth[ym].Count);
        }).ToList();
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
