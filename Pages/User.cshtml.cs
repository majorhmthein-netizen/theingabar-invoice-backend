using Microsoft.AspNetCore.Mvc;

public class UserModel : AppPageModel
{
    public new UserRow? User { get; private set; }
    public bool CanWrite => Me?.CanWrite ?? false;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        User = Me!.User;
        return Page();
    }
}
