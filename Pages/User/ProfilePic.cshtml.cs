using Microsoft.AspNetCore.Mvc;

/// <summary>The user's own profile picture: shown next to their name in
/// the toolbar and on the profile page. Square-cropped on the phone
/// (cropper.js) before upload; stored as bytes on the users row.</summary>
public class UserProfilePicModel : AppPageModel
{
    public int UserId { get; private set; }
    public bool HasAvatar { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        var user = Me!.User;
        UserId = user.Id; HasAvatar = user.HasAvatar;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(IFormFile? avatarFile)
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        var user = Me!.User;
        if (avatarFile is null || avatarFile.Length == 0)
        {
            FlashError("Choose a photo first.");
            return RedirectToPage("/User/ProfilePic");
        }
        if (avatarFile.Length > 2 * 1024 * 1024)
        {
            FlashError("Photo must be at most 2 MB.");
            return RedirectToPage("/User/ProfilePic");
        }
        var type = avatarFile.ContentType.ToLowerInvariant();
        if (type is not ("image/jpeg" or "image/png" or "image/webp"))
        {
            FlashError("Photo must be JPG, PNG or WebP.");
            return RedirectToPage("/User/ProfilePic");
        }
        using var ms = new MemoryStream();
        await avatarFile.CopyToAsync(ms);
        await AppDb.UpdateUserAvatarAsync(user.Id, ms.ToArray(), type);
        Flash("Profile picture saved.");
        return RedirectToPage("/User/ProfilePic");
    }
}
