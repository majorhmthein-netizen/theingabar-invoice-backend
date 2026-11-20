using Microsoft.AspNetCore.Mvc;

/* PIN login (2026-11-21): OTP is gone — SMSPoh has no working sender
 * (no registered sender ID; the test sender is rejected too), so
 * mechanics log in with their phone + a 4-digit PIN. Every account
 * starts with the default PIN 1111 (hashed in password_hash; a NULL
 * hash also means 1111, for accounts created before PIN login).
 * The PIN can be changed on the User page. Admin accounts cannot use
 * PIN at all — they sign in with Google at /admin. Sessions never
 * expire (see Auth.cs): login lasts until Log out or admin Revoke.
 * Wrong PINs are throttled: 5 per 10 min per phone+IP, then an
 * escalating lockout (1 / 5 / 10 min) with a live countdown on the page.
 */
public class LoginModel : AppPageModel
{
    [BindProperty] public string Phone { get; set; } = "";
    [BindProperty] public string Pin { get; set; } = "";
    [BindProperty] public string? ReturnUrl { get; set; }
    public string? Error { get; private set; }
    /// <summary>Seconds left on the PIN lockout (0 = not locked). The page
    /// shows a live countdown from this value.</summary>
    public int LockoutSeconds { get; private set; }
    public string LockoutText => FormatCountdown(LockoutSeconds);

    /// <summary>Countdown text: "1:00" for a minute or more, "45s" below.</summary>
    public static string FormatCountdown(int seconds) =>
        seconds >= 60 ? $"{seconds / 60}:{(seconds % 60):D2}" : $"{seconds}s";

    IActionResult SafeRedirect(string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("//"))
            return Redirect(returnUrl);
        return RedirectToPage("/User");
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl, string? phone)
    {
        await LoadCommonAsync();
        if (Me is not null) return RedirectToPage("/User");
        if (!string.IsNullOrEmpty(phone) && Auth.ValidatePhone(phone) is null)
            Phone = Auth.NormalizePhone(phone);
        ReturnUrl = returnUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCommonAsync();
        if (NeedDb(out var redirect)) return redirect!;
        var err = Auth.ValidatePhone(Phone) ?? Auth.ValidatePin(Pin);
        if (err is not null) { Error = err; return Page(); }
        var phone = Auth.NormalizePhone(Phone);
        var pin = Pin.Trim();
        var ip = Auth.ClientIp(HttpContext);
        var (allowed, retry) = await Auth.CheckPinAttemptLimitAsync(phone, ip);
        if (!allowed)
        {
            LockoutSeconds = retry;
            return Page();
        }
        await Auth.RecordPinAttemptAsync(phone, ip);
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
        if (!Auth.VerifyPin(pin, user.PasswordHash))
        {
            Error = "Wrong PIN — try again.";
            return Page();
        }
        // A good PIN forgives past lockouts (next lock starts at 1 min).
        await Auth.ResetPinLockCountAsync(phone, ip);
        await Auth.SignInAsync(HttpContext, user.Id, isAdmin: false);
        if (user.Status == "pending")
            TempData["Flash"] = "pending";
        return SafeRedirect(ReturnUrl);
    }
}
