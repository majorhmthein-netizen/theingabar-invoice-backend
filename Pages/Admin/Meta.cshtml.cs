using Microsoft.AspNetCore.Mvc;

public class AdminMetaModel : AppPageModel
{
    [BindProperty] public string Shop { get; set; } = "";
    [BindProperty] public string Group { get; set; } = "";
    [BindProperty] public string Services { get; set; } = "";
    [BindProperty] public string Address { get; set; } = "";
    [BindProperty] public string Phone1 { get; set; } = "";
    [BindProperty] public string Phone2 { get; set; } = "";
    [BindProperty] public string Phone3 { get; set; } = "";
    [BindProperty] public string Phone4 { get; set; } = "";
    [BindProperty] public string Phone5 { get; set; } = "";
    [BindProperty] public string PayName { get; set; } = "";
    [BindProperty] public string PayNum { get; set; } = "";
    [BindProperty] public string Thanks { get; set; } = "";
    public bool HasQr { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await NeedAdminAsync() is { } redirect) return redirect;
        var m = await AppDb.GetMetaAsync();
        Shop = m.Shop; Group = m.Group; Services = m.Services; Address = m.Address;
        Phone1 = m.Phone1; Phone2 = m.Phone2;
        Phone3 = m.Phone3 ?? ""; Phone4 = m.Phone4 ?? ""; Phone5 = m.Phone5 ?? "";
        PayName = m.PayName; PayNum = m.PayNum;
        Thanks = m.Thanks; HasQr = m.HasQr;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? qrFile)
    {
        if (await NeedAdminAsync() is { } redirect) return redirect;
        byte[]? qr = null;
        string? qrType = null;
        if (qrFile is not null && qrFile.Length > 0)
        {
            if (qrFile.Length > 2 * 1024 * 1024)
            {
                FlashError("QR image must be at most 2 MB.");
                return RedirectToPage("/Admin/Meta");
            }
            qrType = qrFile.ContentType.ToLowerInvariant();
            if (qrType is not ("image/jpeg" or "image/png" or "image/webp"))
            {
                FlashError("QR image must be JPG, PNG or WebP.");
                return RedirectToPage("/Admin/Meta");
            }
            using var ms = new MemoryStream();
            await qrFile.CopyToAsync(ms);
            qr = ms.ToArray();
        }
        var m = new MetaRow(Shop.Trim(), Group.Trim(), Services.Trim(), Address.Trim(),
            Phone1.Trim(), Phone2.Trim(), Opt(Phone3), Opt(Phone4), Opt(Phone5),
            PayName.Trim(), PayNum.Trim(), Thanks.Trim(), HasQr);
        await AppDb.UpdateMetaAsync(m, qr, qrType);
        Flash("Shop metadata saved.");
        return RedirectToPage("/Admin/Meta");
    }

    static string? Opt(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
