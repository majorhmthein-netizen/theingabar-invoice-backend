using Microsoft.AspNetCore.Mvc;

public class AdminUsersModel : AppPageModel
{
    public List<UserRow> Users { get; private set; } = new();

    [BindProperty] public int UserId { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await NeedAdminAsync() is { } redirect) return redirect;
        Users = await AppDb.ListUsersAsync();
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

    public async Task<IActionResult> OnPostResetRateLimitAsync()
    {
        var g = await GateAsync(); if (g is not null) return g;
        var user = await AppDb.FindUserByIdAsync(UserId);
        if (user is not null)
        {
            // All per-account + per-IP counters for this username.
            await AppDb.ResetRateLimitsByPrefixAsync(
                "login:" + user.Username.ToLowerInvariant() + ":");
        }
        Flash("Login lockout reset for this account.");
        return RedirectToPage("/Admin/Users");
    }
}
