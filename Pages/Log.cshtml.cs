using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;

public class LogModel : AppPageModel
{
    private readonly IInvoiceStore _store;
    public LogModel(IInvoiceStore store) => _store = store;

    public List<UserRow> Users { get; private set; } = new();
    public UserRow? ViewUser { get; private set; }
    public List<LogSection> Sections { get; private set; } = new();

    public bool CanEdit(InvoiceRow inv) =>
        Me is not null && Me.CanWrite &&
        (Me.IsAdmin || inv.UserId == Me.User.LocalId);

    public async Task<IActionResult> OnGetAsync(string? user)
    {
        if (NeedDb(out var redirect)) return redirect!;
        await LoadCommonAsync();
        Users = await AppDb.ListUsersAsync();
        string? filterLocalId = null;
        if (!string.IsNullOrEmpty(user))
        {
            ViewUser = Users.FirstOrDefault(u => u.LocalId == user);
            if (ViewUser is null) return NotFound();
            filterLocalId = ViewUser.LocalId;
        }
        var all = await _store.ListAsync();
        if (filterLocalId is not null)
            all = all.Where(r => r.UserId == filterLocalId).ToList();
        var (wf, wt) = AppDb.ThisWeek();
        var (mf, mt) = AppDb.ThisMonth();
        string wfIso = wf.ToString("yyyy-MM-dd"), wtIso = wt.ToString("yyyy-MM-dd");
        string mfIso = mf.ToString("yyyy-MM-dd"), mtIso = mt.ToString("yyyy-MM-dd");
        var weekRows = all.Where(r => string.CompareOrdinal(r.Date, wfIso) >= 0
                                   && string.CompareOrdinal(r.Date, wtIso) <= 0).ToList();
        var monthRows = all.Where(r => string.CompareOrdinal(r.Date, mfIso) >= 0
                                    && string.CompareOrdinal(r.Date, mtIso) <= 0).ToList();
        var earlierRows = all.Where(r => string.CompareOrdinal(r.Date, mfIso) < 0).ToList();
        Sections = new List<LogSection>
        {
            new($"This week ({Paper.FmtDate(wfIso)} – {Paper.FmtDate(wtIso)})",
                weekRows, weekRows.Sum(r => r.Total)),
            new($"This month ({Paper.FmtDate(mfIso)} – {Paper.FmtDate(mtIso)})",
                monthRows, monthRows.Sum(r => r.Total)),
            new("Earlier", earlierRows, earlierRows.Sum(r => r.Total)),
        };
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? csvFile)
    {
        if (await NeedWriteAsync() is { } redirect) return redirect;
        if (csvFile is null || csvFile.Length == 0)
        {
            FlashError("Choose a CSV file to import.");
            return RedirectToPage("/Log");
        }
        if (csvFile.Length > 2 * 1024 * 1024)
        {
            FlashError("CSV file must be at most 2 MB.");
            return RedirectToPage("/Log");
        }
        string text;
        using (var reader = new StreamReader(csvFile.OpenReadStream(), Encoding.UTF8))
            text = await reader.ReadToEndAsync();
        var rows = ParseCsv(text);
        var imported = 0;
        var errors = new List<string>();
        foreach (var (lineNo, fields) in rows)
        {
            var err = await TryImportRowAsync(fields, lineNo, errors);
            if (err) imported++;
        }
        Flash($"Imported {imported} invoice(s)." +
            (errors.Count > 0 ? " Errors: " + string.Join(" ", errors.Take(5)) : ""));
        return RedirectToPage("/Log");
    }

    async Task<bool> TryImportRowAsync(string[] f, int lineNo, List<string> errors)
    {
        // Export columns: Date, Customer, Item 1, Item 1 Amount, … (40),
        // Items Sum, Invoice Total, Check, No.
        if (f.Length < 4)
        {
            errors.Add($"line {lineNo}: too few columns.");
            return false;
        }
        var date = f[0].Trim();
        var customer = f[1].Trim();
        var items = new List<InvoiceItem>();
        for (var i = 0; i < 20; i++)
        {
            var di = 2 + i * 2;
            if (di + 1 >= f.Length) break;
            var desc = f[di].Trim();
            var amtRaw = f[di + 1].Trim().Replace(",", "");
            var hasAmount = long.TryParse(amtRaw, out var amount);
            if (desc.Length == 0 && !hasAmount) continue;
            items.Add(new InvoiceItem(desc, hasAmount ? amount : 0));
        }
        long total = -1;
        var totalIdx = 2 + 40 + 1; // after Items Sum
        if (totalIdx < f.Length) long.TryParse(f[totalIdx].Trim().Replace(",", ""), out total);
        if (total < 0) total = items.Sum(i => i.Amount);
        var err = Paper.ValidateDraft(date, customer, items);
        if (err is not null) { errors.Add($"line {lineNo}: {err}"); return false; }
        if (total != items.Sum(i => i.Amount))
        {
            errors.Add($"line {lineNo}: total does not equal sum of items.");
            return false;
        }
        try
        {
            await AppDb.CreateInvoiceNumberedAsync(new InvoiceInput(
                Guid.NewGuid().ToString(), null, Me!.User.LocalId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                date, customer, items, total));
            return true;
        }
        catch (Exception ex)
        {
            errors.Add($"line {lineNo}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Minimal CSV parser (quoted fields, doubled quotes, CRLF).
    /// Returns (lineNo, fields) for data rows; skips the header row.</summary>
    static List<(int, string[])> ParseCsv(string text)
    {
        var rows = new List<(int, string[])>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var lineNo = 1;
        var fieldStart = true;
        void EndField() { fields.Add(sb.ToString()); sb.Clear(); fieldStart = true; }
        void EndRow()
        {
            EndField();
            if (fields.Count > 1 || fields[0].Length > 0) rows.Add((lineNo, fields.ToArray()));
            fields.Clear();
            lineNo++;
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"' && fieldStart && sb.Length == 0) { inQuotes = true; fieldStart = false; }
            else if (c == ',') EndField();
            else if (c == '\r') { /* skip */ }
            else if (c == '\n') EndRow();
            else { sb.Append(c); fieldStart = false; }
        }
        if (sb.Length > 0 || fields.Count > 0) EndRow();
        // Drop the header row (first row starting with "Date").
        if (rows.Count > 0 && rows[0].Item2.Length > 0 &&
            rows[0].Item2[0].Trim().TrimStart('\uFEFF') == "Date")
            rows.RemoveAt(0);
        return rows;
    }
}

public sealed record LogSection(string Title, List<InvoiceRow> Rows, long Total);
