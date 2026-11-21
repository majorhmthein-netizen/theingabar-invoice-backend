/* Theingabar Invoice — server-side PDF export (QuestPDF 2024.12, Community).
 *
 * Mirrors the APPROVED Safari template layout (Han 2026-11-21): two-column
 * letterhead, boxed customer panel, navy items-table header, boxed payment
 * panel, two signature lines, thanks + group line. Title U Moe + both
 * group lines gold (Han-ordered, vs the template's serif title and grey
 * group line). Same text, same sizes (px → pt at 96 dpi = ×0.75), same
 * colours, same fonts (TTF files in wwwroot/fonts/ttf — Skia cannot load
 * the woff2 files the web pages use). Myanmar shaping is handled by
 * Skia/HarfBuzz inside QuestPDF.
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
                    // --- letterhead: top rule + brand row (template layout) ---
                    col.Item().LineHorizontal(3).LineColor("#14202e");
                    col.Item().PaddingTop(12).Row(r =>
                    {
                        r.RelativeItem().Column(h =>
                        {
                            h.Item().Text(meta.Shop).Style(ShopTitle(33, "#0b5394"));
                            h.Item().PaddingTop(4).Text(meta.Group)
                                .Style(Latin(9, 700, "#D4AF37").LetterSpacing(0.18f));
                        });
                        r.ConstantItem(135).Column(h =>
                        {
                            h.Item().AlignRight().Text("INVOICE")
                                .Style(Latin(21, 800, "#14202e").LetterSpacing(0.22f));
                            h.Item().PaddingTop(5).AlignRight().Text(t =>
                            {
                                t.Span("id: ").Style(Myanmar(10, 600, "#5b6a7a"));
                                t.Span(row.No ?? "").Style(Myanmar(10, 600, "#24344a"));
                            });
                        });
                    });
                    col.Item().PaddingTop(10).Text(meta.Services)
                        .Style(Myanmar(10.1f, 700, "#14202e")).LineHeight(1.9f);
                    col.Item().PaddingTop(8).BorderTop(1).BorderColor("#dfe5ec").Row(r =>
                    {
                        r.RelativeItem().PaddingTop(8).Text(t =>
                        {
                            t.Span("Address: ").Style(Myanmar(9.75f, 800, "#0b5394"));
                            t.Span(meta.Address).Style(Myanmar(9.75f, 600, "#14202e"));
                        });
                        r.RelativeItem().PaddingTop(8).AlignRight().Text(t =>
                        {
                            t.Span("Contact: ").Style(Myanmar(9.75f, 800, "#0b5394"));
                            t.Span(meta.PhonesDisplay).Style(Myanmar(9.75f, 600, "#14202e"));
                        });
                    });

                    // --- customer box ---
                    col.Item().PaddingTop(14).Border(1).BorderColor("#dfe5ec").Padding(14).Row(r =>
                    {
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().Text("CUSTOMER / VEHICLE").Style(Latin(9, 700, "#5b6a7a").LetterSpacing(0.14f));
                            c.Item().PaddingTop(3).Text(string.IsNullOrWhiteSpace(row.Customer) ? "—" : row.Customer)
                                .Style(Myanmar(14.25f, 700, "#14202e"));
                        });
                        r.RelativeItem().AlignRight().Column(c =>
                        {
                            c.Item().Text("DATE").Style(Latin(9, 700, "#5b6a7a").LetterSpacing(0.14f));
                            c.Item().PaddingTop(3).Text(Paper.FmtDate(row.Date)).Style(Myanmar(14.25f, 700, "#14202e"));
                            c.Item().PaddingTop(3).Text("Currency: MMK").Style(Myanmar(10.5f, 400, "#5b6a7a"));
                        });
                    });

                    // --- items table (navy header) ---
                    col.Item().PaddingTop(14).Table(t =>
                    {
                        t.ColumnsDefinition(cd =>
                        {
                            cd.ConstantColumn(38);
                            cd.RelativeColumn();
                            cd.ConstantColumn(110);
                        });
                        t.Header(h =>
                        {
                            h.Cell().Background("#14202e").PaddingVertical(7).PaddingHorizontal(8)
                                .Text("No.").Style(Latin(9.4f, 700, "#ffffff").LetterSpacing(0.06f));
                            h.Cell().Background("#14202e").PaddingVertical(7).PaddingHorizontal(8)
                                .Text("DESCRIPTION").Style(Latin(9.4f, 700, "#ffffff").LetterSpacing(0.06f));
                            h.Cell().Background("#14202e").PaddingVertical(7).PaddingHorizontal(8)
                                .AlignRight().Text("AMOUNT (MMK)").Style(Latin(9.4f, 700, "#ffffff").LetterSpacing(0.06f));
                        });
                        for (var i = 0; i < row.Items.Count; i++)
                        {
                            var it = row.Items[i];
                            t.Cell().BorderBottom(1).BorderColor("#dfe5ec").PaddingVertical(6).PaddingHorizontal(8)
                                .Text((i + 1).ToString()).Style(Myanmar(10.9f, 400, "#7b8a99"));
                            t.Cell().BorderBottom(1).BorderColor("#dfe5ec").PaddingVertical(6).PaddingHorizontal(8)
                                .Text(string.IsNullOrWhiteSpace(it.Desc) ? "—" : it.Desc)
                                .Style(Myanmar(10.9f, 300, "#000000")).LineHeight(1.55f);
                            t.Cell().BorderBottom(1).BorderColor("#dfe5ec").PaddingVertical(6).PaddingHorizontal(8)
                                .AlignRight().Text(Paper.Fmt(it.Amount)).Style(Myanmar(10.9f, 700, "#000000"));
                        }
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(8).PaddingHorizontal(8)
                            .Text("");
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(8).PaddingHorizontal(8)
                            .Text("Total (MMK)").Style(Myanmar(12.4f, 800, "#14202e"));
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(8).PaddingHorizontal(8)
                            .AlignRight().Text(Paper.Fmt(row.Total)).Style(Myanmar(12.4f, 800, "#0b5394"));
                    });

                    // --- payment box + QR ---
                    col.Item().PaddingTop(14).Border(1).BorderColor("#dfe5ec").Padding(14).Row(r =>
                    {
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().Text("PAYMENT").Style(Latin(8.6f, 700, "#5b6a7a").LetterSpacing(0.1f));
                            c.Item().PaddingTop(5).Text(t =>
                            {
                                t.Span("KBZ Pay").Style(Myanmar(11.25f, 800, "#0b5394"));
                                t.Span(" — No. ").Style(Myanmar(11.25f, 400, "#14202e"));
                                t.Span(payNum).Style(Myanmar(11.25f, 700, "#0b5394"));
                                t.Span(" — Name: ").Style(Myanmar(11.25f, 400, "#14202e"));
                                t.Span(payName).Style(Myanmar(11.25f, 700, "#0b5394"));
                            });
                            c.Item().PaddingTop(3).Text("Scan the QR code with KBZPay to pay.")
                                .Style(Myanmar(10.5f, 400, "#5b6a7a"));
                        });
                        if (qr is not null)
                            r.ConstantItem(96).Border(1).BorderColor("#dfe5ec").Padding(4)
                                .Image(qr).FitArea();
                    });

                    // --- signatures ---
                    col.Item().PaddingTop(26).Row(r =>
                    {
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().Height(44).BorderBottom(1).BorderColor("#14202e");
                            c.Item().PaddingTop(5).AlignCenter().Text("Customer Signature")
                                .Style(Myanmar(9.75f, 400, "#5b6a7a"));
                        });
                        r.ConstantItem(40);
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().Height(44).BorderBottom(1).BorderColor("#14202e");
                            c.Item().PaddingTop(5).AlignCenter().Text("Authorized Signature")
                                .Style(Myanmar(9.75f, 400, "#5b6a7a"));
                        });
                    });

                    // --- thanks (each line centred: AlignCenter belongs on the
                    // text item itself — on the full-width column it does nothing) ---
                    col.Item().PaddingTop(22).BorderTop(1).BorderColor("#dfe5ec").Column(c =>
                    {
                        c.Item().PaddingTop(12).AlignCenter().Text(meta.Thanks).Style(Latin(11.25f, 700, "#0b5394").LetterSpacing(0.02f));
                        c.Item().PaddingTop(3).AlignCenter().Text(Paper.TitleCase(meta.Group)).Style(Latin(10.5f, 600, "#5b6a7a"));
                    });
                });
            });
        });
        return doc.GeneratePdf();
    }
}
