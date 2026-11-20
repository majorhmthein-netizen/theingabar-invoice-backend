/* Signed download links for the invoice PDF/PNG exports. */
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

/// <summary>Creates and validates the signed token that lets anyone with
/// the invoice page link download its PDF/PNG without signing in. The
/// token is the invoice id protected with the app's Data Protection key
/// ring (the same keys antiforgery uses), so it survives restarts but
/// cannot be forged. The invoice page itself is public, so the token
/// only stops drive-by export abuse - it is not a secret.</summary>
public static class InvoiceDownloadToken
{
    private static IDataProtector? _protector;

    public static void Init(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("InvoiceDownload");

    public static string Create(long id)
    {
        if (_protector is null) return "";
        var payload = Encoding.UTF8.GetBytes("invoice-download:" + id.ToString(CultureInfo.InvariantCulture));
        return WebEncoders.Base64UrlEncode(_protector.Protect(payload));
    }

    public static bool IsValid(string? token, long id)
    {
        if (string.IsNullOrEmpty(token) || _protector is null) return false;
        try
        {
            var plain = Encoding.UTF8.GetString(_protector.Unprotect(WebEncoders.Base64UrlDecode(token)));
            return plain == "invoice-download:" + id.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }
}
