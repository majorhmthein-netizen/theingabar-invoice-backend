using Microsoft.AspNetCore.Mvc;

/// <summary>The mechanic's own KBZ Pay template: name, number and QR
/// shown on the invoices they issue.</summary>
public class UserMyQrModel : AppPageModel
{
    [BindProperty] public string? KbzName { get; set; }
    [BindProperty] public string? KbzNumber { get; set; }
    public int UserId { get; private set; }
    public bool HasQr { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        var user = Me!.User;
        UserId = user.Id; HasQr = user.HasQr;
        KbzName = user.KbzName; KbzNumber = user.KbzNumber;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? qrFile)
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        var user = Me!.User;
        byte[]? qr = null;
        string? qrType = null;
        if (qrFile is not null && qrFile.Length > 0)
        {
            if (qrFile.Length > 2 * 1024 * 1024)
            {
                FlashError("QR image must be at most 2 MB.");
                return RedirectToPage("/User/MyQr");
            }
            qrType = qrFile.ContentType.ToLowerInvariant();
            if (qrType is not ("image/jpeg" or "image/png" or "image/webp"))
            {
                FlashError("QR image must be JPG, PNG or WebP.");
                return RedirectToPage("/User/MyQr");
            }
            using var ms = new MemoryStream();
            await qrFile.CopyToAsync(ms);
            qr = ms.ToArray();
        }
        await AppDb.UpdateUserTemplateAsync(user.Id, KbzName?.Trim(), KbzNumber?.Trim(), qr, qrType);
        Flash("KBZ Pay template saved.");
        return RedirectToPage("/User");
    }
}
