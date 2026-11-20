using Microsoft.AspNetCore.Mvc;

/// <summary>Change the login PIN. Other devices logged in as this user
/// are logged out; the current session stays.</summary>
public class UserChangePinModel : AppPageModel
{
    [BindProperty] public string? CurrentPin { get; set; }
    [BindProperty] public string? NewPin { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        var user = Me!.User;
        var err = Auth.ValidatePin(CurrentPin) ?? Auth.ValidatePin(NewPin);
        if (err is not null)
        {
            FlashError(err);
            return RedirectToPage("/User/ChangePin");
        }
        if (!Auth.VerifyPin(CurrentPin!.Trim(), user.PasswordHash))
        {
            FlashError("Wrong current PIN.");
            return RedirectToPage("/User/ChangePin");
        }
        await AppDb.SetUserPinHashAsync(user.Id, Auth.HashPin(NewPin!.Trim()));
        await AppDb.RevokeOtherSessionsAsync(user.Id, Me.Session.Id);
        Flash("PIN changed.");
        return RedirectToPage("/User");
    }
}
