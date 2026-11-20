/* Theingabar Invoice — server-side PNG export (PdfiumRaster over the
 * QuestPDF PDF — PNG-ROUTE.md §3a/§5).
 *
 * GET /invoice/{id}/png = generate the same QuestPDF PDF as the PDF
 * export, render page 1 to PNG at 200 DPI (A4 @ 200 DPI = 1654×2339 px).
 * The PNG is by construction identical to the PDF, so the frozen-design
 * check is done once (PDF vs frozen HTML preview) instead of twice.
 * No browser, no Chromium: tens of MB RAM, no /dev/shm or sandbox
 * problems. PDFium calls are serialized with a semaphore (PDFium is
 * not thread-safe).
 */
using PdfiumRaster;

public static class PngService
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Renders page 1 of the invoice PDF to PNG (200 DPI).
    /// Returns null when rendering is unavailable.</summary>
    public static async Task<byte[]?> RenderAsync(InvoiceRow row, UserRow? viewer)
    {
        var pdf = await PdfService.RenderAsync(row, viewer);
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                var options = new PdfImageConversionOptions
                {
                    Render = new PdfPageRenderOptions { Dpi = 200 }
                };
                var bitmap = PdfImageConverter.RenderPage(pdf, 0, options, "");
                using var ms = new MemoryStream();
                PdfImageConverter.SaveBitmap(bitmap, ms, PdfImageOutputFormat.Png);
                return ms.ToArray();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[backend] PNG render failed: " + ex.Message);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
