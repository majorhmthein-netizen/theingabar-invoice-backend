using Microsoft.AspNetCore.Mvc;

public class NotificationsModel : AppPageModel
{
    private readonly IInvoiceStore _store;
    public NotificationsModel(IInvoiceStore store) => _store = store;

    public List<AppDb.NotificationRow> Items { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        // Opening the list marks admin messages as read (they leave the
        // badge); invoice reminders stay until the invoice is Paid.
        await AppDb.MarkAdminNotificationsReadAsync(Me!.User.Id);
        Items = await AppDb.ListNotificationsAsync(Me!.User.Id);
        // The badge was counted before the read above — refresh it.
        ViewData["NotiCount"] = await AppDb.CountActiveNotificationsAsync(Me!.User.Id);
        return Page();
    }

    /// <summary>Marks an invoice Paid from its reminder (Han 2026-11-21:
    /// "Paid ရင် noti ပျောက်"). The invoice then counts into the month total.</summary>
    public async Task<IActionResult> OnPostMarkPaidAsync(int id)
    {
        if (await NeedLoginAsync() is { } redirect) return redirect;
        var n = (await AppDb.ListNotificationsAsync(Me!.User.Id))
            .FirstOrDefault(x => x.Id == id && x.Kind == "invoice");
        if (n is null || n.InvoiceId is null)
        { Flash("That notification is gone."); return RedirectToPage("/Notifications"); }
        var row = (await _store.ListAsync()).FirstOrDefault(r => r.Id == n.InvoiceId.Value);
        if (row is null) { Flash("That invoice is gone."); return RedirectToPage("/Notifications"); }
        if (row.UserId != Me!.User.LocalId && !Me.IsAdmin) return Forbid();
        await AppDb.SetInvoicePaymentStatusAsync(row.Id, "paid");
        await AppDb.ResolveInvoiceNotificationAsync(row.Id);
        Flash($"Invoice {row.No} marked as Paid.");
        return RedirectToPage("/Notifications");
    }
}
