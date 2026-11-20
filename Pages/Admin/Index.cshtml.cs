using Microsoft.AspNetCore.Mvc;

public class AdminIndexModel : AppPageModel
{
    public bool IsAdmin => Me?.IsAdmin ?? false;
    public bool GoogleConfigured { get; private set; }
    public string? Error { get; private set; }
    public List<UserRow> Users { get; private set; } = new();
    public List<UserRow> Pending { get; private set; } = new();

    [BindProperty] public int UserId { get; set; }

    public async Task<IActionResult> OnGetAsync(string? error, int? retry)
    {
        if (NeedDb(out var redirect)) return redirect!;
        await LoadCommonAsync();
        GoogleConfigured = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID"))
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET"));
        Error = error switch
        {
            "google-not-configured" => "Google sign-in is not configured on this server.",
            "google-failed" => "Google sign-in failed — try again.",
            "not-admin" => "That Google account is not an admin.",
            "locked" => $"Too many failed admin sign-ins — try again in {retry ?? 0} seconds.",
            "no-admin-user" => "Admin user record is missing — contact support.",
            _ => null,
        };
        if (IsAdmin)
        {
            Users = await AppDb.ListUsersAsync();
            Pending = Users.Where(u => u.Status == "pending").ToList();
        }
        return Page();
    }

    async Task<IActionResult> RequireAdminAsync()
    {
        if (await NeedAdminAsync() is { } redirect) return redirect;
        return null!;
    }

    public async Task<IActionResult> OnPostApproveAsync()
    {
        var gate = await RequireAdminAsync();
        if (gate is not null) return gate;
        await AppDb.SetUserStatusAsync(UserId, "approved", Me!.User.Username);
        Flash("User approved.");
        return RedirectToPage("/Admin/Index");
    }

    public async Task<IActionResult> OnPostRejectAsync()
    {
        var gate = await RequireAdminAsync();
        if (gate is not null) return gate;
        await AppDb.SetUserStatusAsync(UserId, "rejected", Me!.User.Username);
        await AppDb.RevokeUserSessionsAsync(UserId);
        Flash("User rejected.");
        return RedirectToPage("/Admin/Index");
    }

    public async Task<IActionResult> OnPostSuspendAsync()
    {
        var gate = await RequireAdminAsync();
        if (gate is not null) return gate;
        await AppDb.SetUserStatusAsync(UserId, "suspended", Me!.User.Username);
        await AppDb.RevokeUserSessionsAsync(UserId);
        Flash("User suspended — their sessions are revoked.");
        return RedirectToPage("/Admin/Index");
    }

    public async Task<IActionResult> OnPostUnsuspendAsync()
    {
        var gate = await RequireAdminAsync();
        if (gate is not null) return gate;
        await AppDb.SetUserStatusAsync(UserId, "approved", Me!.User.Username);
        Flash("User re-approved.");
        return RedirectToPage("/Admin/Index");
    }

}
