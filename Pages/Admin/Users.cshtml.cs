using Microsoft.AspNetCore.Mvc;

public class AdminUsersModel : AppPageModel
{
    public List<UserRow> Users { get; private set; } = new();
    /// <summary>Pending PIN-reset requests (Forgot PIN, Han 2026-11-21).</summary>
    public List<PinResetRequestRow> PinRequests { get; private set; } = new();

    [BindProperty] public int UserId { get; set; }
    [BindProperty] public int RequestId { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await NeedAdminAsync() is { } redirect) return redirect;
        Users = await AppDb.ListUsersAsync();
        PinRequests = await AppDb.ListPendingPinResetRequestsAsync();
        return Page();
    }

    async Task<IActionResult> GateAsync()
    {
        if (await NeedAdminAsync() is { } redirect) return redirect;
        return null!;
    }

    public async Task<IActionResult> OnPostApproveAsync()
    {
        var g = await GateAsync(); if (g is not null) return g;
        await AppDb.SetUserStatusAsync(UserId, "approved", Me!.User.Username);
        Flash("User approved.");
        return RedirectToPage("/Admin/Users");
    }

    public async Task<IActionResult> OnPostRejectAsync()
    {
        var g = await GateAsync(); if (g is not null) return g;
        await AppDb.SetUserStatusAsync(UserId, "rejected", Me!.User.Username);
        await AppDb.RevokeUserSessionsAsync(UserId);
        Flash("User rejected.");
        return RedirectToPage("/Admin/Users");
    }

    public async Task<IActionResult> OnPostSuspendAsync()
    {
        var g = await GateAsync(); if (g is not null) return g;
        await AppDb.SetUserStatusAsync(UserId, "suspended", Me!.User.Username);
        await AppDb.RevokeUserSessionsAsync(UserId);
        Flash("User suspended — their sessions are revoked.");
        return RedirectToPage("/Admin/Users");
    }

    public async Task<IActionResult> OnPostUnsuspendAsync()
    {
        var g = await GateAsync(); if (g is not null) return g;
        await AppDb.SetUserStatusAsync(UserId, "approved", Me!.User.Username);
        Flash("User re-approved.");
        return RedirectToPage("/Admin/Users");
    }

    public async Task<IActionResult> OnPostRevokeSessionsAsync()
    {
        var g = await GateAsync(); if (g is not null) return g;
        await AppDb.RevokeUserSessionsAsync(UserId);
        Flash("Sessions revoked.");
        return RedirectToPage("/Admin/Users");
    }

    public async Task<IActionResult> OnPostResetRateLimitAsync()
    {
        var g = await GateAsync(); if (g is not null) return g;
        var user = await AppDb.FindUserByIdAsync(UserId);
        if (user is not null)
        {
            // PIN lockout keys are per phone (pin-attempt:{phone}:{ip}) —
            // the old "login:{username}:" prefix matched nothing, so this
            // button never cleared a PIN lockout (fixed 2026-11-21).
            await AppDb.ResetRateLimitsByPrefixAsync("pin-attempt:" + user.Phone);
        }
        Flash("Login lockout reset for this account.");
        return RedirectToPage("/Admin/Users");
    }

    /// <summary>Forgot PIN (Han 2026-11-21): reset a mechanic's PIN to the
    /// default 1111, clear their login lockout, and revoke their sessions
    /// (the old PIN is gone, so old sessions must not persist).</summary>
    public async Task<IActionResult> OnPostResetPinAsync()
    {
        var g = await GateAsync(); if (g is not null) return g;
        var userId = await AppDb.ResolvePinResetRequestAsync(RequestId, Me!.User.Username);
        if (userId is null) { Flash("That reset request is already done."); return RedirectToPage("/Admin/Users"); }
        var user = await AppDb.FindUserByIdAsync(userId.Value);
        if (user is null) { Flash("That user no longer exists."); return RedirectToPage("/Admin/Users"); }
        await AppDb.SetUserPinHashAsync(user.Id, Auth.HashPin(Auth.DefaultPin));
        await AppDb.ResetRateLimitsByPrefixAsync("pin-attempt:" + user.Phone);
        await AppDb.RevokeUserSessionsAsync(user.Id);
        Flash("PIN reset to 1111 for " + user.Username + ".");
        return RedirectToPage("/Admin/Users");
    }
}
