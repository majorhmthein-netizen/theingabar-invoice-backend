/* Theingabar Invoice — server-side PDF export (QuestPDF 2024.12, Community).
 *
 * Mirrors the APPROVED Safari template layout (Han 2026-11-21): two-column
 * letterhead, boxed customer panel, navy items-table header, boxed payment
 * panel, two signature lines, thanks + group line. Title is the gold logo
 * (trial 2026-11-21, was U Moe text) + both group lines gold (Han-ordered,
 * vs the template's serif title and grey group line). Same text, same sizes (px → pt at 96 dpi = ×0.75), same
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

    static byte[]? _logoBytes;
    static byte[]? LogoBytes()
    {
        // Gold shop logo for the invoice header (trial, Han 2026-11-21).
        // Falls back to the U Moe text title when the file is missing.
        if (_logoBytes is not null) return _logoBytes;
        foreach (var baseDir in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "img"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "img"),
        })
        {
            var p = Path.Combine(baseDir, "theingabar-logo-header.jpg");
            if (File.Exists(p)) return _logoBytes = File.ReadAllBytes(p);
        }
        return null;
    }

    static byte[]? _watermarkBytes;
    static byte[]? WatermarkBytes()
    {
        // Faint logo for the watermark behind the items table (Han 2026-11-21).
        // The file itself is pre-faded to ~10% so the text stays readable
        // and it costs almost no ink. Returns null when missing (table alone).
        if (_watermarkBytes is not null) return _watermarkBytes;
        foreach (var baseDir in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "img"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "img"),
        })
        {
            var p = Path.Combine(baseDir, "theingabar-logo-watermark.jpg");
            if (File.Exists(p)) return _watermarkBytes = File.ReadAllBytes(p);
        }
        return null;
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
            // Items-table size tier (Han 2026-11-21): few items keep the
            // default/max size; 11–15 shrink a step; 16–20 shrink most,
            // so a full 20-item invoice still fits on one A4 page.
            var tier = Paper.ItemTier(row.Items.Count);
            var ct = Paper.CompactTier(row.Items.Count);
            // One-page fit (Han 2026-11-21 "1 ရွက်ထဲ"): the description
            // shrinks a touch more at the two highest compact tiers.
            var itemSize = ct == 4 ? 8.2f : ct == 3 ? 9.2f : tier == 2 ? 8.8f : tier == 1 ? 9.8f : 10.9f;
            var cellPadV = ct == 4 ? 3 : ct == 3 ? 4 : ct == 2 ? 4 : ct == 1 ? 5 : 6;
            // Fixed-block sizes per compact tier (index = ct 0..4:
            // ≤4, 5–7, 8–10, 11–15, 16–20 items). The QR never goes
            // below 64pt — it must stay scannable. Page margins shrink
            // a little at the two highest tiers for extra room.
            var cMarginTop = new[] { 30f, 30f, 30f, 26f, 24f }[ct];
            var cMarginBottom = new[] { 28f, 28f, 28f, 24f, 22f }[ct];
            var cLogoH = new[] { 68f, 55f, 45f, 40f, 35f }[ct];
            var cHeadPadT = new[] { 12f, 8f, 6f, 4f, 4f }[ct];
            var cGroupPadT = new[] { 4f, 3f, 3f, 2f, 2f }[ct];
            var cServPadT = new[] { 10f, 6f, 4f, 4f, 2f }[ct];
            var cServLH = new[] { 1.9f, 1.9f, 1.7f, 1.6f, 1.5f }[ct];
            var cAddrPadT = new[] { 8f, 6f, 4f, 4f, 2f }[ct];
            var cCustPadT = new[] { 14f, 10f, 8f, 6f, 3f }[ct];
            var cCustPad = new[] { 14f, 11f, 8f, 6f, 4f }[ct];
            var cTablePadT = new[] { 14f, 10f, 8f, 6f, 3f }[ct];
            var cHeadPadV = new[] { 7f, 6f, 5f, 5f, 3f }[ct];
            var cTotalPadT = new[] { 8f, 6f, 6f, 4f, 3f }[ct];
            var cQrPadT = new[] { 14f, 10f, 8f, 6f, 3f }[ct];
            var cQr = new[] { 96f, 76f, 64f, 64f, 64f }[ct];
            var cThanksPadT = new[] { 22f, 14f, 8f, 5f, 3f }[ct];
            var cThanksPadT2 = new[] { 12f, 8f, 6f, 4f, 3f }[ct];
            var cThanksGroupPadT = new[] { 3f, 3f, 3f, 2f, 1f }[ct];
            // Paper size (Han 2026-11-21): A4 (full page) is the default;
            // a half-A4 invoice is the same A4 layout scaled by 0.70 onto
            // an A5 page, so it still fits on one page (the printer's
            // "fit to page" does the same shrink). Margins scale too.
            var isHalf = row.PaperSize == "Half";
            var s = isHalf ? 0.70f : 1.0f;
            container.Page(page =>
            {
                page.Size(isHalf ? PageSizes.A5 : PageSizes.A4);
                page.MarginHorizontal(42 * s);
                page.MarginTop(cMarginTop * s);
                page.MarginBottom(cMarginBottom * s);
                page.DefaultTextStyle(TextStyle.Default.FontFamily("Noto Sans Myanmar"));
                page.Content().Scale(s).Column(col =>
                {
                    // --- letterhead: top rule + brand row (template layout) ---
                    col.Item().LineHorizontal(3).LineColor("#14202e");
                    col.Item().PaddingTop(cHeadPadT).Row(r =>
                    {
                        r.RelativeItem().Column(h =>
                        {
                            var logo = LogoBytes();
                            if (logo is not null)
                                h.Item().Height(cLogoH).Image(logo).FitArea();
                            else
                                h.Item().Text(meta.Shop).Style(ShopTitle(33, "#0b5394"));
                            h.Item().PaddingTop(cGroupPadT).Text(meta.Group)
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
                    col.Item().PaddingTop(cServPadT).Text(meta.Services)
                        .Style(Myanmar(10.1f, 700, "#14202e")).LineHeight(cServLH);
                    col.Item().PaddingTop(cAddrPadT).BorderTop(1).BorderColor("#dfe5ec").Row(r =>
                    {
                        r.RelativeItem().PaddingTop(cAddrPadT).Text(t =>
                        {
                            t.Span("Address: ").Style(Myanmar(9.75f, 800, "#0b5394"));
                            t.Span(meta.Address).Style(Myanmar(9.75f, 600, "#14202e"));
                        });
                        r.RelativeItem().PaddingTop(cAddrPadT).AlignRight().Text(t =>
                        {
                            t.Span("Contact: ").Style(Myanmar(9.75f, 800, "#0b5394"));
                            t.Span(meta.PhonesDisplay).Style(Myanmar(9.75f, 600, "#14202e"));
                        });
                    });

                    // --- customer box ---
                    col.Item().PaddingTop(cCustPadT).Border(1).BorderColor("#dfe5ec").Padding(cCustPad).Row(r =>
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
                        });
                    });

                    // --- items table (navy header) + faint logo watermark behind it ---
                    col.Item().PaddingTop(cTablePadT).Layers(layers =>
                    {
                        // Watermark (Han 2026-11-21): the faded logo
                        // alone — the group name and id text lines were
                        // removed, so the watermark is just the mark.
                        layers.Layer().AlignCenter().AlignMiddle().Column(c =>
                        {
                            var wm = WatermarkBytes();
                            if (wm is not null)
                                c.Item().AlignCenter().Width(220).Image(wm).FitArea();
                        });
                        layers.PrimaryLayer().Table(t =>
                    {
                        t.ColumnsDefinition(cd =>
                        {
                            cd.ConstantColumn(34);
                            cd.RelativeColumn();
                            cd.ConstantColumn(100);
                        });
                        t.Header(h =>
                        {
                            // "No." header left blank (Han 2026-11-21): "No."
                            // beside DESCRIPTION reads as "no description" at
                            // a glance. Row numbers 1,2,3… stay in the body.
                            h.Cell().Background("#14202e").PaddingVertical(cHeadPadV).PaddingHorizontal(8)
                                .Text("").Style(Latin(9.4f, 700, "#ffffff").LetterSpacing(0.06f));
                            h.Cell().Background("#14202e").PaddingVertical(cHeadPadV).PaddingHorizontal(8)
                                .Text("DESCRIPTION").Style(Latin(9.4f, 700, "#ffffff").LetterSpacing(0.06f));
                            h.Cell().Background("#14202e").PaddingVertical(cHeadPadV).PaddingHorizontal(8)
                                .AlignRight().Text("AMOUNT (MMK)").Style(Latin(9.4f, 700, "#ffffff").LetterSpacing(0.06f));
                        });
                        for (var i = 0; i < row.Items.Count; i++)
                        {
                            var it = row.Items[i];
                            t.Cell().BorderBottom(1).BorderColor("#dfe5ec").PaddingVertical(cellPadV).PaddingHorizontal(8)
                                .Text((i + 1).ToString()).Style(Myanmar(itemSize, 400, "#7b8a99"));
                            t.Cell().BorderBottom(1).BorderColor("#dfe5ec").PaddingVertical(cellPadV).PaddingHorizontal(8)
                                .Text(string.IsNullOrWhiteSpace(it.Desc) ? "—" : it.Desc)
                                .Style(Myanmar(itemSize, 300, "#000000")).LineHeight(1.55f);
                            t.Cell().BorderBottom(1).BorderColor("#dfe5ec").PaddingVertical(cellPadV).PaddingHorizontal(8)
                                .AlignRight().Text(Paper.Fmt(it.Amount)).Style(Myanmar(itemSize, 700, "#000000"));
                        }
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(cTotalPadT).PaddingHorizontal(8)
                            .Text("");
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(cTotalPadT).PaddingHorizontal(8)
                            .Text("Total (MMK)").Style(Myanmar(12.4f, 800, "#14202e"));
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(cTotalPadT).PaddingHorizontal(8)
                            .AlignRight().Text(Paper.Fmt(row.Total)).Style(Myanmar(12.4f, 800, "#0b5394"));
                        // Deposit (စရံငွေ, Han 2026-11-21): shown only when
                        // > 0; the final total is Total - Deposit. A zero
                        // deposit is not printed (Total alone stands).
                        if (row.Deposit > 0)
                        {
                            t.Cell().PaddingVertical(cellPadV).PaddingHorizontal(8).Text("");
                            t.Cell().PaddingVertical(cellPadV).PaddingHorizontal(8)
                                .Text("Deposit (စရံငွေ)").Style(Myanmar(itemSize, 400, "#14202e"));
                            t.Cell().PaddingVertical(cellPadV).PaddingHorizontal(8)
                                .AlignRight().Text(Paper.Fmt(row.Deposit)).Style(Myanmar(itemSize, 700, "#000000"));
                            t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(cTotalPadT).PaddingHorizontal(8)
                                .Text("");
                            t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(cTotalPadT).PaddingHorizontal(8)
                                .Text("Final Total (MMK)").Style(Myanmar(12.4f, 800, "#14202e"));
                            t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(cTotalPadT).PaddingHorizontal(8)
                                .AlignRight().Text(Paper.Fmt(row.Total - row.Deposit)).Style(Myanmar(12.4f, 800, "#0b5394"));
                        }
                        });
                    });

                    // --- QR alone (Han 2026-11-21): the payment box is
                    // gone; only the QR stays, centred under the table.
                    if (qr is not null)
                        col.Item().PaddingTop(cQrPadT).AlignCenter().Width(cQr).Border(1).BorderColor("#dfe5ec").Padding(4)
                            .Image(qr).FitArea();

                    // --- thanks (each line centred: AlignCenter belongs on the
                    // text item itself — on the full-width column it does nothing) ---
                    col.Item().PaddingTop(cThanksPadT).BorderTop(1).BorderColor("#dfe5ec").Column(c =>
                    {
                        c.Item().PaddingTop(cThanksPadT2).AlignCenter().Text(meta.Thanks).Style(Latin(11.25f, 700, "#0b5394").LetterSpacing(0.02f));
                        c.Item().PaddingTop(cThanksGroupPadT).AlignCenter().Text(Paper.TitleCase(meta.Group)).Style(Latin(10.5f, 600, "#5b6a7a"));
                    });
                });
            });
        });
        return doc.GeneratePdf();
    }

    /// <summary>A month's log as a PDF file (လချုပ်, Han 2026-11-21):
    /// the month's invoices as a table (No, Date, Customer, Amount) plus
    /// the month total. Saved as a file, not printed. Plain A4, same
    /// fonts and navy header as the invoice paper.</summary>
    public static async Task<byte[]> RenderMonthLogAsync(
        string label, List<InvoiceRow> rows, long total)
    {
        EnsureFonts();
        var meta = await AppDb.GetMetaAsync();
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
                    col.Item().LineHorizontal(3).LineColor("#14202e");
                    col.Item().PaddingTop(12).Text(meta.Shop)
                        .Style(ShopTitle(26, "#0b5394"));
                    col.Item().PaddingTop(4).Text(meta.Group)
                        .Style(Latin(9, 700, "#D4AF37").LetterSpacing(0.18f));
                    col.Item().PaddingTop(10).Text("Monthly Log — " + label)
                        .Style(Latin(14, 700, "#14202e"));
                    col.Item().PaddingTop(4).Text(
                            rows.Count + (rows.Count == 1 ? " invoice" : " invoices"))
                        .Style(Myanmar(10.5f, 400, "#5b6a7a"));
                    col.Item().PaddingTop(14).Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(70);
                            c.ConstantColumn(90);
                            c.RelativeColumn();
                            c.ConstantColumn(110);
                        });
                        t.Header(h =>
                        {
                            h.Cell().Background("#14202e").PaddingVertical(7).PaddingHorizontal(8)
                                .Text("No.").Style(Latin(9.4f, 700, "#ffffff").LetterSpacing(0.06f));
                            h.Cell().Background("#14202e").PaddingVertical(7).PaddingHorizontal(8)
                                .Text("Date").Style(Latin(9.4f, 700, "#ffffff").LetterSpacing(0.06f));
                            h.Cell().Background("#14202e").PaddingVertical(7).PaddingHorizontal(8)
                                .Text("Customer / Vehicle").Style(Latin(9.4f, 700, "#ffffff").LetterSpacing(0.06f));
                            h.Cell().Background("#14202e").PaddingVertical(7).PaddingHorizontal(8)
                                .AlignRight().Text("Amount (MMK)").Style(Latin(9.4f, 700, "#ffffff").LetterSpacing(0.06f));
                        });
                        foreach (var r in rows)
                        {
                            t.Cell().PaddingVertical(6).PaddingHorizontal(8)
                                .Text(r.No ?? "").Style(Myanmar(10.9f, 700, "#14202e"));
                            t.Cell().PaddingVertical(6).PaddingHorizontal(8)
                                .Text(Paper.FmtDate(r.Date)).Style(Myanmar(10.9f, 400, "#14202e"));
                            t.Cell().PaddingVertical(6).PaddingHorizontal(8)
                                .Text(string.IsNullOrWhiteSpace(r.Customer) ? "—" : r.Customer)
                                .Style(Myanmar(10.9f, 400, "#14202e"));
                            t.Cell().PaddingVertical(6).PaddingHorizontal(8)
                                .AlignRight().Text(Paper.Fmt(r.Total)).Style(Myanmar(10.9f, 700, "#000000"));
                        }
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(8).PaddingHorizontal(8).Text("");
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(8).PaddingHorizontal(8).Text("");
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(8).PaddingHorizontal(8)
                            .Text("Total (MMK)").Style(Myanmar(12.4f, 800, "#14202e"));
                        t.Cell().BorderTop(2).BorderColor("#14202e").PaddingTop(8).PaddingHorizontal(8)
                            .AlignRight().Text(Paper.Fmt(total)).Style(Myanmar(12.4f, 800, "#0b5394"));
                    });
                });
            });
        });
        return doc.GeneratePdf();
    }
}
