using Microsoft.AspNetCore.Mvc;

public class LogoutModel : AppPageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await LoadCommonAsync();
        if (AppDb.Available) await Auth.SignOutAsync(HttpContext);
        return RedirectToPage("/Index");
    }
}
