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
        await ResetPinToDefaultAsync(user);
        Flash("PIN reset to 1111 for " + user.Username + ".");
        return RedirectToPage("/Admin/Users");
    }

    /// <summary>Reset a mechanic's PIN to 1111 straight from their row
    /// (Han 2026-11-21: the admin panel showed no reset button until a
    /// Forgot PIN request existed — now the admin can reset any PIN
    /// directly, no request needed). Same effect as ResetPin: PIN to
    /// 1111, login lockout cleared, sessions revoked.</summary>
    public async Task<IActionResult> OnPostResetPinDirectAsync()
    {
        var g = await GateAsync(); if (g is not null) return g;
        var user = await AppDb.FindUserByIdAsync(UserId);
        if (user is null) { Flash("That user no longer exists."); return RedirectToPage("/Admin/Users"); }
        if (user.IsAdmin) { Flash("Admin accounts sign in with Google, not a PIN."); return RedirectToPage("/Admin/Users"); }
        await ResetPinToDefaultAsync(user);
        Flash("PIN reset to 1111 for " + user.Username + ".");
        return RedirectToPage("/Admin/Users");
    }

    static async Task ResetPinToDefaultAsync(UserRow user)
    {
        await AppDb.SetUserPinHashAsync(user.Id, Auth.HashPin(Auth.DefaultPin));
        await AppDb.ResetRateLimitsByPrefixAsync("pin-attempt:" + user.Phone);
        await AppDb.RevokeUserSessionsAsync(user.Id);
    }

    /// <summary>Delete a user (Han 2026-11-21: "Test ကိုဖျက်ပစ်" + "ဖျက်လို့ရတဲ့
    /// button လည်းထည့်"). Guards: never an admin account, never your own
    /// account, and never a user who has invoices (their invoices would be
    /// orphaned — invoices reference the user's LocalId with no FK).</summary>
    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var g = await GateAsync(); if (g is not null) return g;
        var user = await AppDb.FindUserByIdAsync(UserId);
        if (user is null) { Flash("That user no longer exists."); return RedirectToPage("/Admin/Users"); }
        if (user.IsAdmin) { Flash("Admin accounts cannot be deleted."); return RedirectToPage("/Admin/Users"); }
        if (user.Id == Me!.User.Id) { Flash("You cannot delete your own account."); return RedirectToPage("/Admin/Users"); }
        if (await AppDb.HasInvoicesAsync(user.LocalId))
        { Flash("Cannot delete " + user.Username + " — they have invoices."); return RedirectToPage("/Admin/Users"); }
        await AppDb.DeleteUserAsync(user.Id);
        Flash("Deleted " + user.Username + ".");
        return RedirectToPage("/Admin/Users");
    }
}
