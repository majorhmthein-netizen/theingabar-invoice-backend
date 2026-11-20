using Microsoft.AspNetCore.Mvc;

public class SignupModel : AppPageModel
{
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Phone { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; private set; }

    public async Task OnGetAsync() => await LoadCommonAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCommonAsync();
        if (NeedDb(out var redirect)) return redirect!;
        var err = Auth.ValidateUsername(Username) ?? Auth.ValidatePhone(Phone) ?? Auth.ValidatePassword(Password);
        if (err is not null) { Error = err; return Page(); }
        if (await AppDb.CountUsersAsync() >= 5)
        {
            Error = "Maximum 5 mechanics — ask the Admin.";
            return Page();
        }
        if (await AppDb.FindUserByUsernameAsync(Username) is not null)
        {
            Error = "That username is already taken.";
            return Page();
        }
        await AppDb.CreateUserAsync(Username, Auth.NormalizePhone(Phone), Auth.HashPassword(Password));
        Flash("Signed up — waiting for Admin approval. You can log in once approved.");
        return RedirectToPage("/Login");
    }
}
