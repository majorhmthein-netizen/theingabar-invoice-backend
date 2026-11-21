using Microsoft.AspNetCore.Mvc;

/// <summary>Admin sends a notification to one user (Han 2026-11-21, v34:
/// "Admin panel ကနေ user တစ်ယောက်ဆီကို noti ပို့လို့ရမယ့် gateway").
/// The message lands in that user's header badge + notification list.</summary>
public class AdminNotifyModel : AppPageModel
{
    public UserRow? Target { get; private set; }
    [BindProperty] public int UserId { get; set; }
    [BindProperty] public string Message { get; set; } = "";

    public async Task<IActionResult> OnGetAsync(int userId)
    {
        if (await NeedAdminAsync() is { } redirect) return redirect;
        Target = await AppDb.FindUserByIdAsync(userId);
        if (Target is null) return NotFound();
        UserId = Target.Id;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await NeedAdminAsync() is { } redirect) return redirect;
        var target = await AppDb.FindUserByIdAsync(UserId);
        if (target is null) { Flash("That user no longer exists."); return RedirectToPage("/Admin/Users"); }
        if (target.IsAdmin) { Flash("Admin accounts do not need notifications."); return RedirectToPage("/Admin/Users"); }
        var msg = Message.Trim();
        if (msg.Length == 0) { Flash("Write a message first."); return RedirectToPage("/Admin/Notify", new { userId = UserId }); }
        await AppDb.CreateNotificationAsync(target.Id, "admin", msg, null, null, null);
        Flash("Notification sent to " + target.Username + ".");
        return RedirectToPage("/Admin/Users");
    }
}
