/* Theingabar Invoice — server-side PDF export (QuestPDF 2024.12, Community).
 *
 * Re-implements the FROZEN invoice paper (extract/invoice.css) as a native
 * A4 PDF: same order, same text, same sizes (px → pt at 96 dpi = ×0.75),
 * same colours, same fonts (TTF files in wwwroot/fonts/ttf — Skia cannot
 * load the woff2 files the web pages use). Myanmar shaping is handled by
 * Skia/HarfBuzz inside QuestPDF. Layout drift vs the frozen design is a
 * known risk (plan §9 #4): verify the seed PDF against the current
 * PNG/PDF before cutover.
 *
 * UNIT TRAP — QuestPDF TextStyle.LetterSpacing takes a FACTOR proportional
 * to the font size (i.e. em), NOT points. The frozen CSS letter-spacing
 * values are already in em, so pass them through unchanged (e.g. .55em →
 * LetterSpacing(0.55f)). Passing a point value (7.8f) is read as 7.8em
 * and explodes the text across lines (seen 2026-11-20: INVOICE stacked
 * vertically, group line wrapped twice).
 */
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

public static class PdfService
{
    static bool _fontsRegistered;
    static readonly object _fontGate = new();

    static string ResolveFont(string name)
    {
        foreach (var baseDir in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "fonts", "ttf"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "fonts", "ttf"),
        })
        {
            var p = Path.Combine(baseDir, name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("PDF font missing: " + name);
    }

    static void EnsureFonts()
    {
        lock (_fontGate)
        {
            if (_fontsRegistered) return;
            FontManager.RegisterFont(File.OpenRead(ResolveFont("NotoSansMyanmar-Regular.ttf")));
            FontManager.RegisterFont(File.OpenRead(ResolveFont("NotoSansMyanmar-Bold.ttf")));
            FontManager.RegisterFont(File.OpenRead(ResolveFont("NotoSansMyanmar-Light.ttf")));
            FontManager.RegisterFont(File.OpenRead(ResolveFont("NotoSerifMyanmar-Bold.ttf")));
            FontManager.RegisterFont(File.OpenRead(ResolveFont("NotoSerifMyanmar-Black.ttf")));
            FontManager.RegisterFont(File.OpenRead(ResolveFont("Archivo.ttf")));
            FontManager.RegisterFont(File.OpenRead(ResolveFont("U-Moe-Handwritten.ttf")));
            _fontsRegistered = true;
        }
    }

    static TextStyle Myanmar(float size, int weight, string color) =>
        TextStyle.Default.FontFamily("Noto Sans Myanmar").FontSize(size).FontColor(color)
            .Weight(weight switch { >= 800 => FontWeight.Black, >= 700 => FontWeight.Bold,
                >= 600 => FontWeight.SemiBold, 300 => FontWeight.Light, _ => FontWeight.Normal });

    // The Light TTF's typographic family (name ID 16) is "Noto Sans
    // Myanmar" with usWeightClass 300, so Skia registers it as the Light
    // weight of that family — Weight(Light) selects it. Its legacy family
    // name "Noto Sans Myanmar Light" does NOT match (verified 2026-11-21:
    // FontFamily("Noto Sans Myanmar Light") silently fell back to Regular).

    static TextStyle Latin(float size, int weight, string color) =>
        TextStyle.Default.FontFamily("Archivo").FontSize(size).FontColor(color)
            .Weight(weight switch { >= 800 => FontWeight.ExtraBold, >= 700 => FontWeight.Bold,
                >= 600 => FontWeight.SemiBold, _ => FontWeight.Normal });

    static TextStyle Serif(float size, int weight, string color) =>
        TextStyle.Default.FontFamily("Noto Serif Myanmar").FontSize(size).FontColor(color)
            .Weight(weight >= 900 ? FontWeight.Black : FontWeight.Bold);

    // Shop-name title only: the user's handwritten font (family name as the
    // font declares it: "Z01-Umoe002"). It ships in weight 400 only, so no
    // Weight() call — QuestPDF/Skia uses the single face as-is.
    static TextStyle ShopTitle(float size, string color) =>
        TextStyle.Default.FontFamily("Z01-Umoe002").FontSize(size).FontColor(color);

    public static async Task<byte[]> RenderAsync(InvoiceRow row, UserRow? viewer)
    {
        EnsureFonts();
        var meta = await AppDb.GetMetaAsync();
        UserRow? owner = null;
        if (row.UserId is not null)
            owner = await AppDb.FindUserByLocalIdAsync(row.UserId);
        var (payName, payNum, _) = Paper.PaymentFor(owner, meta);
        byte[]? qr = null;
        if (owner is not null && owner.HasQr)
            qr = (await AppDb.GetUserQrAsync(owner.Id)).Data;
        qr ??= (await AppDb.GetMetaQrAsync()).Data;
        if (qr is null)
        {
            var seedPath = ResolveFont("..") is string d
                ? Path.Combine(Path.GetDirectoryName(d)!, "..", "seed-qr.jpg")
                : "";
            var full = Path.GetFullPath(seedPath);
            if (File.Exists(full)) qr = await File.ReadAllBytesAsync(full);
        }

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(42);
                page.MarginTop(30);
                page.MarginBottom(28);
                page.DefaultTextStyle(TextStyle.Default.FontFamily("Noto Sans Myanmar"));
                page.Content().Column(col =>
                {
                    // --- head (centred, double rule below) ---
                    col.Item().AlignCenter().Column(h =>
                    {
                        h.Item().AlignCenter().Text(meta.Shop).Style(ShopTitle(33, "#0b5394"));
                        h.Item().AlignCenter().Text("INVOICE").Style(Latin(14.25f, 800, "#0b5394").LetterSpacing(0.55f));
                        h.Item().AlignCenter().Text(meta.Group).Style(Latin(7.9f, 700, "#D4AF37").LetterSpacing(0.28f));
                        h.Item().PaddingTop(7).AlignCenter().Text(meta.Services)
                            .Style(Myanmar(10.1f, 700, "#24344a")).LineHeight(1.75f);
                        h.Item().PaddingTop(4).Column(a =>
                        {
                            a.Item().AlignCenter().Text(t =>
                            {
                                t.Span("Address – ").Style(Myanmar(9.75f, 800, "#24344a"));
                                t.Span(meta.Address).Style(Myanmar(9.75f, 600, "#24344a"));
                            });
                            a.Item().AlignCenter().Text(t =>
                            {
                                t.Span("Contact – ").Style(Myanmar(9.75f, 800, "#24344a"));
                                t.Span(meta.PhonesDisplay).Style(Myanmar(9.75f, 600, "#24344a"));
                            });
                        });
                    });
                    col.Item().PaddingTop(10).LineHorizontal(2).LineColor("#0b5394");
                    col.Item().PaddingTop(2).LineHorizontal(1).LineColor("#0b5394");

                    // --- meta row ---
                    col.Item().PaddingTop(12).Row(r =>
                    {
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().Text("CUSTOMER INFO").Style(Latin(9, 700, "#5b6a7a").LetterSpacing(0.06f));
                            c.Item().Text(string.IsNullOrWhiteSpace(row.Customer) ? "—" : row.Customer)
                                .Style(Myanmar(10.9f, 700, "#14202e"));
                        });
                        r.RelativeItem().AlignRight().Column(c =>
                        {
                            c.Item().Text("DATE").Style(Latin(9, 700, "#5b6a7a").LetterSpacing(0.06f));
                            c.Item().Text(Paper.FmtDate(row.Date)).Style(Myanmar(10.9f, 700, "#14202e"));
                        });
                        r.RelativeItem().AlignRight().Column(c =>
                        {
                            c.Item().Text("NO.").Style(Latin(9, 700, "#5b6a7a").LetterSpacing(0.06f));
                            c.Item().Text(row.No ?? "").Style(Myanmar(10.9f, 700, "#14202e"));
                        });
                    });

                    // --- items table ---
                    col.Item().PaddingTop(8).Table(t =>
                    {
                        t.ColumnsDefinition(cd =>
                        {
                            cd.ConstantColumn(28);
                            cd.RelativeColumn();
                            cd.ConstantColumn(110);
                        });
                        t.Header(h =>
                        {
                            h.Cell().BorderTop(2).BorderBottom(1.5f).BorderColor("#14202e")
                                .PaddingVertical(6).Text("No.").Style(Latin(9.4f, 700, "#14202e").LetterSpacing(0.08f));
                            h.Cell().BorderTop(2).BorderBottom(1.5f).BorderColor("#14202e")
                                .PaddingVertical(6).Text("DESCRIPTION").Style(Latin(9.4f, 700, "#14202e").LetterSpacing(0.08f));
                            h.Cell().BorderTop(2).BorderBottom(1.5f).BorderColor("#14202e")
                                .PaddingVertical(6).AlignRight().Text("AMOUNT (MMK)").Style(Latin(9.4f, 700, "#14202e").LetterSpacing(0.08f));
                        });
                        for (var i = 0; i < row.Items.Count; i++)
                        {
                            var it = row.Items[i];
                            t.Cell().BorderBottom(1).BorderColor("#dfe5ec").PaddingVertical(6)
                                .Text((i + 1).ToString()).Style(Myanmar(10.9f, 400, "#7b8a99"));
                            t.Cell().BorderBottom(1).BorderColor("#dfe5ec").PaddingVertical(6)
                                .Text(string.IsNullOrWhiteSpace(it.Desc) ? "—" : it.Desc)
                                .Style(Myanmar(10.9f, 300, "#000000")).LineHeight(1.55f);
                            t.Cell().BorderBottom(1).BorderColor("#dfe5ec").PaddingVertical(6)
                                .AlignRight().Text(Paper.Fmt(it.Amount)).Style(Myanmar(10.9f, 700, "#000000"));
                        }
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(8)
                            .Text("");
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(8)
                            .Text("Total (MMK)").Style(Myanmar(12.4f, 800, "#14202e"));
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(8)
                            .AlignRight().Text(Paper.Fmt(row.Total)).Style(Myanmar(12.4f, 800, "#0b5394"));
                    });

                    // --- payment + QR ---
                    col.Item().PaddingTop(12).BorderTop(1).BorderColor("#dfe5ec").Row(r =>
                    {
                        r.RelativeItem().PaddingTop(10).Column(c =>
                        {
                            c.Item().Text("PAYMENT").Style(Latin(8.6f, 700, "#5b6a7a").LetterSpacing(0.1f));
                            c.Item().PaddingTop(3).Text("KBZ Pay").Style(Myanmar(12, 800, "#14202e"));
                            c.Item().Text("No. " + payNum).Style(Myanmar(11.25f, 700, "#14202e"));
                            c.Item().Text(t =>
                            {
                                t.Span("Name. ").Style(Myanmar(11.25f, 400, "#14202e"));
                                t.Span(payName).Style(Myanmar(11.25f, 700, "#14202e"));
                            });
                        });
                        if (qr is not null)
                            r.ConstantItem(88).PaddingTop(10).Image(qr).FitArea();
                    });

                    // --- thanks ---
                    col.Item().PaddingTop(12).AlignCenter()
                        .Text(meta.Thanks).Style(Latin(10.1f, 700, "#40536a").LetterSpacing(0.02f));
                });
            });
        });
        return doc.GeneratePdf();
    }
}
