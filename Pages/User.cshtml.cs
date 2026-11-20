using Microsoft.AspNetCore.Mvc;

public class UserModel : AppPageModel
{
    public new UserRow? User { get; private set; }
    public bool CanWrite => Me?.CanWrite ?? false;

    [BindProperty] public string? KbzName { get; set; }
    [BindProperty] public string? KbzNumber { get; set; }
    [BindProperty] public string? NewPassword { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        User = Me!.User;
        KbzName = User.KbzName;
        KbzNumber = User.KbzNumber;
        return Page();
    }

    public async Task<IActionResult> OnPostTemplateAsync(IFormFile? qrFile)
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        User = Me!.User;
        byte[]? qr = null;
        string? qrType = null;
        if (qrFile is not null && qrFile.Length > 0)
        {
            if (qrFile.Length > 2 * 1024 * 1024)
            {
                FlashError("QR image must be at most 2 MB.");
                return RedirectToPage("/User");
            }
            qrType = qrFile.ContentType.ToLowerInvariant();
            if (qrType is not ("image/jpeg" or "image/png" or "image/webp"))
            {
                FlashError("QR image must be JPG, PNG or WebP.");
                return RedirectToPage("/User");
            }
            using var ms = new MemoryStream();
            await qrFile.CopyToAsync(ms);
            qr = ms.ToArray();
        }
        await AppDb.UpdateUserTemplateAsync(User.Id, KbzName?.Trim(), KbzNumber?.Trim(), qr, qrType);
        Flash("KBZ Pay template saved.");
        return RedirectToPage("/User");
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        var err = Auth.ValidatePassword(NewPassword);
        if (err is not null)
        {
            FlashError(err);
            return RedirectToPage("/User");
        }
        await AppDb.SetUserPasswordAsync(Me!.User.Id, Auth.HashPassword(NewPassword!));
        Flash("Password changed.");
        return RedirectToPage("/User");
    }
}
