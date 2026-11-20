using Microsoft.AspNetCore.Mvc;

/* OTP-only login (2026-11-21): no username, no password.
 * Step 1: the mechanic enters the phone on file -> the backend asks
 * SMSPoh's Verify API to text a 6-digit code (5 min, 5 wrong tries).
 * Step 2: the mechanic enters the code -> SMSPoh verifies it -> sign-in.
 * The pending OTP (phone + SMSPoh requestId + request time) lives in
 * TempData (encrypted cookie), read with Peek so it survives the
 * redirect after step 1 and the verify POST. Admin accounts cannot use
 * OTP at all — they sign in with Google at /admin, so a stolen phone's
 * SMS inbox can never become an admin login. Sessions never expire
 * (see Auth.cs): login lasts until Log out or admin Revoke.
 */
public class LoginModel : AppPageModel
{
    [BindProperty] public string Phone { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string? ReturnUrl { get; set; }
    public string? Error { get; private set; }
    public bool Step2 { get; private set; }

    const string OtpPhoneKey = "OtpPhone";
    const string OtpRequestIdKey = "OtpRequestId";
    const string OtpRequestedAtKey = "OtpRequestedAt";

    (string Phone, string RequestId, DateTime RequestedAt)? PendingOtp()
    {
        var phone = TempData.Peek(OtpPhoneKey) as string;
        var requestId = TempData.Peek(OtpRequestIdKey) as string;
        var at = TempData.Peek(OtpRequestedAtKey) as string;
        if (string.IsNullOrEmpty(phone) || string.IsNullOrEmpty(requestId) ||
            string.IsNullOrEmpty(at) || !long.TryParse(at, out var ticks))
            return null;
        return (phone, requestId, new DateTime(ticks, DateTimeKind.Utc));
    }

    void ClearPendingOtp()
    {
        TempData.Remove(OtpPhoneKey);
        TempData.Remove(OtpRequestIdKey);
        TempData.Remove(OtpRequestedAtKey);
    }

    IActionResult SafeRedirect(string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("//"))
            return Redirect(returnUrl);
        return RedirectToPage("/User");
    }

    static string RetryText(int retry) =>
        retry >= 60 ? $"{(retry + 59) / 60} minute(s)" : $"{retry} seconds";

    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        await LoadCommonAsync();
        if (Me is not null) return RedirectToPage("/User");
        var pending = PendingOtp();
        Step2 = pending is not null;
        if (Step2) Phone = pending!.Value.Phone;
        ReturnUrl = returnUrl;
        return Page();
    }

    // Step 1: request the OTP.
    public async Task<IActionResult> OnPostRequestAsync()
    {
        await LoadCommonAsync();
        if (NeedDb(out var redirect)) return redirect!;
        var err = Auth.ValidatePhone(Phone);
        if (err is not null) { Error = err; return Page(); }
        var phone = Auth.NormalizePhone(Phone);
        var ip = Auth.ClientIp(HttpContext);
        var (allowed, retry) = await Auth.CheckOtpRequestLimitAsync(phone, ip);
        if (!allowed)
        {
            Error = $"Too many codes — try again in {RetryText(retry)}.";
            return Page();
        }
        await Auth.RecordOtpRequestAsync(phone, ip);
        var user = await AppDb.FindUserByPhoneAsync(phone);
        if (user is null)
        {
            Error = "No account for this phone — sign up or ask the Admin.";
            return Page();
        }
        if (user.IsAdmin)
        {
            Error = "Admins sign in with Google at /admin.";
            return Page();
        }
        if (user.Status is "rejected" or "suspended")
        {
            Error = "This account is " + user.Status + ". Ask the Admin.";
            return Page();
        }
        var (ok, requestId, smsErr) = await SmsPoh.RequestOtpAsync(phone);
        if (!ok || requestId is null)
        {
            Error = smsErr == "config"
                ? "OTP is not set up yet — ask the Admin."
                : "Could not send the code — try again.";
            return Page();
        }
        TempData[OtpPhoneKey] = phone;
        TempData[OtpRequestIdKey] = requestId;
        TempData[OtpRequestedAtKey] = DateTime.UtcNow.Ticks.ToString();
        // Redirect (PRG): a refresh must not re-send the code.
        TempData["Flash"] = "Code sent to " + phone + " — valid 5 minutes.";
        return RedirectToPage("/Login", new { returnUrl = ReturnUrl });
    }

    // Step 2: verify the code.
    public async Task<IActionResult> OnPostVerifyAsync()
    {
        await LoadCommonAsync();
        if (NeedDb(out var redirect)) return redirect!;
        var pending = PendingOtp();
        if (pending is null)
        {
            Error = "Request a code first.";
            return Page();
        }
        Step2 = true;
        Phone = pending.Value.Phone;
        if (DateTime.UtcNow - pending.Value.RequestedAt > TimeSpan.FromSeconds(SmsPoh.TtlSeconds))
        {
            ClearPendingOtp();
            Step2 = false;
            Error = "Code expired — request a new one.";
            return Page();
        }
        var code = (Code ?? "").Trim();
        if (code.Length != SmsPoh.PinLength || !code.All(char.IsDigit))
        {
            Error = "Enter the 6-digit code.";
            return Page();
        }
        var (ok, smsErr) = await SmsPoh.VerifyOtpAsync(pending.Value.RequestId, code);
        if (!ok)
        {
            Error = smsErr == "wrong-code"
                ? "Wrong code — try again."
                : smsErr == "config"
                    ? "OTP is not set up yet — ask the Admin."
                    : "Could not check the code — try again.";
            return Page();
        }
        ClearPendingOtp();
        var user = await AppDb.FindUserByPhoneAsync(pending.Value.Phone);
        if (user is null || user.IsAdmin)
        {
            Error = "Account not found — ask the Admin.";
            Step2 = false;
            return Page();
        }
        if (user.Status is "rejected" or "suspended")
        {
            Error = "This account is " + user.Status + ". Ask the Admin.";
            Step2 = false;
            return Page();
        }
        await Auth.SignInAsync(HttpContext, user.Id, isAdmin: false);
        if (user.Status == "pending")
            TempData["Flash"] = "Waiting for Admin approval — read-only until approved.";
        return SafeRedirect(ReturnUrl);
    }
}
