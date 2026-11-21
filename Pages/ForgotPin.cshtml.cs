using Microsoft.AspNetCore.Mvc;

/* Forgot PIN (Han 2026-11-21): a mechanic who forgot their PIN taps
 * "Forgot PIN?" on the login page and sends a reset request with their
 * phone number. The request reaches the admin (Admin/Users), who taps
 * Reset and the PIN becomes 1111 again. Nothing is reset here — only
 * the admin can reset a PIN, so knowing a phone number alone cannot
 * take over an account.
 */
public class ForgotPinModel : AppPageModel
{
    [BindProperty] public string Phone { get; set; } = "";
    public string? Error { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? phone)
    {
        await LoadCommonAsync();
        if (Me is not null) return RedirectToPage("/User");
        if (!string.IsNullOrEmpty(phone) && Auth.ValidatePhone(phone) is null)
            Phone = Auth.NormalizePhone(phone);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCommonAsync();
        if (NeedDb(out var redirect)) return redirect!;
        var err = Auth.ValidatePhone(Phone);
        if (err is not null) { Error = err; return Page(); }
        var phone = Auth.NormalizePhone(Phone);
        var user = await AppDb.FindUserByPhoneAsync(phone);
        if (user is null)
        {
            Error = "No account for this phone — sign up first.";
            return Page();
        }
        if (user.IsAdmin)
        {
            Error = "This account signs in with Google.";
            return Page();
        }
        if (user.Status is "rejected" or "suspended")
        {
            Error = "This account is " + user.Status + ".";
            return Page();
        }
        await AppDb.CreatePinResetRequestAsync(user.Id);
        Message = "Request sent to the admin. Your PIN will be reset to 1111 — try logging in with 1111 after the admin resets it.";
        return Page();
    }
}
