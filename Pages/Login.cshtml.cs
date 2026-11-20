using Microsoft.AspNetCore.Mvc;

public class LoginModel : AppPageModel
{
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        await LoadCommonAsync();
        if (Me is not null) return RedirectToPage("/User");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        await LoadCommonAsync();
        if (NeedDb(out var redirect)) return redirect!;
        var ip = Auth.ClientIp(HttpContext);
        var (allowed, retry) = await Auth.CheckLoginLimitAsync(Username, ip);
        if (!allowed)
        {
            Error = $"Too many attempts — try again in {retry} seconds.";
            return Page();
        }
        var user = await AppDb.FindUserByUsernameAsync(Username);
        if (user is null || !Auth.VerifyPassword(user.PasswordHash, Password))
        {
            await Auth.RecordLoginFailureAsync(Username, ip);
            Error = "Invalid username or password.";
            return Page();
        }
        if (user.Status is "rejected" or "suspended")
        {
            Error = "This account is " + user.Status + ". Ask the Admin.";
            return Page();
        }
        await Auth.ResetLoginLimitAsync(Username, ip);
        await Auth.SignInAsync(HttpContext, user.Id, isAdmin: user.IsAdmin);
        if (user.Status == "pending")
            TempData["Flash"] = "Waiting for Admin approval — read-only until approved.";
        if (!string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("//"))
            return Redirect(returnUrl);
        return RedirectToPage("/User");
    }
}
