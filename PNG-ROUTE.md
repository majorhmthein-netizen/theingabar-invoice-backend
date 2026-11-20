# PNG export route — server-side, C# backend on Render free

- **Date:** 2026-11-20
- **Status:** Research only. Nothing built, no project files changed.
- **Context:** Han's Q4 decision (revised 2026-11-20 18:42, supersedes "PDF + Print only"): PDF + Print are primary, but PNG export **must** also work on demand, server-side. Dropping PNG is **off the table**.
- **Project facts (verified in repo):** `TheingabarInvoice.csproj` = `Microsoft.NET.Sdk.Web`, `net8.0`, only package `Npgsql 8.0.5`. `Dockerfile` = multi-stage, `dotnet/sdk:8.0` build → `dotnet/aspnet:8.0` runtime, non-root `app` user, `EXPOSE 10000`. `HTML-ONLY-PLAN.md` §4 already recommended QuestPDF for PDF and Playwright+Chromium for PNG, with "drop PNG" as fallback — that fallback is now closed by Han's Q4 revision, and this note re-examines the PNG choice with fresh evidence, including one route the plan did not consider (§3a).

## TL;DR — recommendation

**Primary route: render the PNG from the QuestPDF PDF bytes with PdfiumRaster (PDFium + SkiaSharp). No browser in the image.**

- `PdfiumRaster 2.0.7` (MIT, Nov 2026; PDFium 158 + SkiaSharp 4.154) renders page 1 of the already-generated QuestPDF PDF to PNG at a chosen DPI. One endpoint, no new layout code, no Chromium.
- Why it wins: the PNG is then **by construction pixel-identical to the PDF Han already approves**, so the frozen-design check is done once (PDF vs frozen HTML preview) instead of twice. It fits Render free with a wide margin (tens of MB RAM, no browser, no sandbox/`/dev/shm` problems, no cold-start penalty beyond the normal ~1 min wake). Effort ≈ half a day once the QuestPDF PDF exists.
- **Decision gate (do this first):** build the QuestPDF invoice and verify it against the frozen HTML design on Han's phone — Myanmar glyphs (stacked consonants, medial ra), table widths, QR sharpness. If that verification **passes**, add PdfiumRaster PNG. If it **fails** (QuestPDF cannot match the frozen design or shapes Myanmar wrong), the PDF itself is broken and the project has a bigger problem; the fallback is then Playwright headless-shell (§1) for **both** PNG and PDF (Chromium print-to-PDF from the same frozen HTML).
- **Rejected:** SkiaSharp hand-redraw (§2) — full layout re-implementation, high drift risk, days of work. wkhtmltoimage — archived, 2012 engine. PuppeteerSharp + system Chromium — same browser cost as Playwright, no advantage. Hosted screenshot API — invoice data leaves the server.

---

## 1. Playwright + Chromium headless screenshot

Screenshot the server-rendered invoice page (the frozen HTML/CSS) with headless Chromium. Pixel-exact by definition — this is its one decisive advantage.

### Packages / versions (checked 2026-11-20)

| Item | Version / note |
|---|---|
| `Microsoft.Playwright` (NuGet) | **1.65.1** latest (Nov 2026); **1.64.1** stable 2026-10-22, bundles Chromium **156.0.8078.4**. Pin **1.64.1** at build time (known-good, stable); 1.65.x only if a fresh check confirms it. |
| Browser install | **Not** in the NuGet package. Browsers download separately via the CLI script shipped in the package build output: `playwright.ps1 install chromium` (PowerShell) / `playwright.sh install chromium` (bash). It must run **during the Docker build, in the final stage** (or the browser cache must be copied into it), as the same user that runs the app. Default cache `~/.cache/ms-playwright`; set `PLAYWRIGHT_BROWSERS_PATH=/app/.cache/ms-playwright` explicitly — the runtime user is non-root `app`, so the default `/home/app/.cache` path must exist and be writable, or the env var must point at an owned directory. |
| `--only-shell` | Since Playwright 1.57 (Nov 2025, Chromium → Chrome for Testing), plain `install chromium` downloads **two** builds: full headed Chrome for Testing **and** `chromium-headless-shell`. Measured: ~301 MB both vs **~114 MB shell-only** (Playwright 1.63, Linux x64). For a headless-only screenshot service, install with `--only-shell` (flag passthrough through the .NET `playwright.sh` script to be confirmed at first build — it is a core CLI flag, but verify it actually takes effect in the .NET script). |

### Docker image size

- Official image `mcr.microsoft.com/playwright/dotnet:v1.64.1-noble` (Ubuntu 24.04, SDK + browsers + deps): **1,397 MB compressed** vs `mcr.microsoft.com/dotnet/aspnet:10.0` at **98 MB** (manifest layer sums, mertozen.com). **Do not use it as the runtime base** — it is a test image, includes the .NET SDK, and is ~14× the current runtime image.
- Self-managed on `aspnet:8.0`: a real project (HtmlPdfPlus ADR) measured cutting to headless-shell-only taking its working image **763 MB → 370 MB**. Expect the invoice image to grow from ~200 MB to roughly **350–450 MB** (headless shell ~114 MB + ~322 MB system libraries + fonts). That is a deploy-time cost (slower Render builds/pulls, more of the 500 free build-minutes/month), not a runtime blocker.

### OS dependencies in `aspnet:8.0` (Debian 12 bookworm)

The base image has **none** of Chromium's libraries. Install in the runtime stage (or run `playwright.sh install-deps chromium`, which needs the CLI present). The dependency set, per Playwright's own docs and the widely-used list:

```
libglib2.0-0 libnss3 libnspr4 libatk1.0-0 libatk-bridge2.0-0 libatspi2.0-0
libcups2 libdrm2 libxkbcommon0 libxcomposite1 libxdamage1 libxfixes3
libxrandr2 libgbm1 libasound2 libpango-1.0-0 libpangocairo-1.0-0
libdbus-1-3 libexpat1 libxcb1 libx11-6 libx11-xcb1 libxext6
fonts-liberation
```

Plus **Myanmar fonts**: Chromium renders Myanmar only if a Myanmar font is present in the image. Copy the same Noto Sans Myanmar files the project already bundles for QuestPDF into `/usr/share/fonts/truetype/noto/` and run `fc-cache -f`. Without this the invoice renders as tofu boxes — a silent, total failure.

### RAM at screenshot time — the deciding numbers

Render free is a **hard 512 MB ceiling**; exceeding it OOM-kills the whole container (the invoice app goes down, not just the export).

| Measurement | Value | Source |
|---|---|---|
| Playwright 1.64 + headless Chromium, **1 light page open** | **219–227 MiB peak** (headless shell) | rscomputers-ks.com lab, Oct 2026, 10-page benchmark |
| Fresh browser launch, screenshot job (page with 12 SVGs + 200 paragraphs) | **613–622 MiB peak RSS** | curlshot bench, Playwright 1.63, chrome-headless-shell, Nov 2026 — note: RSS double-counts shared pages, and the test page is heavier than a static invoice, so this is a pessimistic upper bound, not the expected invoice number |
| Rule of thumb, single instance | **~150–300 MB** per page/instance | privatebyte.com; screenshotrun.com ("~300 MB per instance") |
| Concurrency 2, same bench | **~900–1,150 MiB peak RSS** | curlshot bench — two simultaneous renders do not fit in 512 MB |
| ASP.NET Core 8 + Npgsql baseline | **~80–150 MB** (estimate, not measured — measure on the live service) | — |

**Realistic invoice estimate: ~350–450 MB peak for one serialized render** (app baseline + one headless-shell page). That fits 512 MB, but with only ~60–150 MB of headroom, on 0.5 CPU, with .NET GC and Chromium spikes coinciding. Two concurrent PNG requests would OOM-kill the service. Mitigations are mandatory, not optional: a `SemaphoreSlim(1)` around all renders, launch-per-request + dispose (or one shared browser with a fresh context closed per request), `--disable-dev-shm-usage` (Render gives no `--ipc=host`, and the default 64 MB `/dev/shm` will crash Chromium without this flag), `--no-sandbox` only if running as root (we run as `app`, so the sandbox can stay on).

**Verdict on 512 MB: workable for a single serialized render, with thin margin and a real OOM risk under concurrency or a heavier-than-expected page. Not comfortable for a shop invoicing service where an OOM kills the whole app.** Note: paying for Render Starter ($7/mo) does **not** help — it is the same 512 MB; the next step that buys RAM is $25/mo (1 CPU / 2 GB).

### Cold start after sleep

- Render free spins down after **15 min** idle; wake takes **~45–60 s** (Render's own figure, corroborated by multiple 2026 write-ups). The first PNG request after sleep therefore takes ~60 s **before** Chromium even starts.
- Chromium first launch on 0.5 CPU: ~2 s (measured ~1.6–2.2 s/job in the curlshot bench on a 6-core box; expect slower on 0.5 CPU — call it 2–5 s, unmeasured).
- Per-request render after warm-up: ~1–2 s. Acceptable for on-demand export if the client timeout is generous (60–90 s) and the PNG request never blocks the health-check path.

---

## 2. SkiaSharp rasterization alternative

SkiaSharp is a 2D drawing canvas, **not** an HTML/CSS layout engine. There is **no maintained HTML→PNG renderer on SkiaSharp** (the old `HtmlRenderer`/`CoreHtmlToImage`-v1 line is abandoned; `CoreHtmlToImage` v2 moved to headless Chromium — i.e. back to §1).

What it would take:

- Re-implement the entire frozen A4 invoice layout a **second** time in C# draw calls: header block, customer/vehicle table, services table with dynamic rows, totals, QR code placement, footer — every x/y, padding, border, font size and line-break rule hand-coded against the frozen HTML/CSS.
- Text: SkiaSharp alone does **not** shape complex scripts. Myanmar needs `SkiaSharp.HarfBuzz` (`SKShaper`) + `HarfBuzzSharp` (current: HarfBuzz 14.4.x; SkiaSharp stable **3.119.0**, Apr 2025; the 4.x line — 4.157.0, Nov 2026 — is now current, which also matters for version conflicts with QuestPDF's pinned SkiaSharp). HarfBuzz's Myanmar shaper does handle the script **if** the font carries the `mym2` OpenType script table — Noto Sans Myanmar does — but shaping is only half the job; line breaking, table cell wrapping and measuring shaped runs is all hand-rolled.
- Fonts: the Noto Sans Myanmar files must be bundled and loaded via `SKTypeface.FromStream` (no system fontconfig in the minimal image unless added).

**Layout-drift risk: HIGH.** Two sources of truth (frozen HTML/CSS vs C# draw code). Every future invoice change must be made twice and re-verified twice; text measurement differences (line breaks, column widths) will diverge silently. Effort: days, not hours, plus an ongoing maintenance tax.

**Verdict: reject.** It is the worst of both worlds — all of QuestPDF's re-implementation risk (§3a) with none of the PDF's reuse value, and it produces a PNG that can drift from *both* the frozen HTML and the PDF.

---

## 3. Third options

### (a) QuestPDF PDF → PNG via PDFium (RECOMMENDED — see TL;DR)

Render the QuestPDF PDF bytes to PNG with a PDFium binding. This route was **not** considered in `HTML-ONLY-PLAN.md` (its option (b) was SkiaSharp redraw; it noted only that "PNG still needs a rasterizer").

- **`PdfiumRaster` 2.0.7** (NuGet, MIT, Nov 2026): rendering-only library — PDFium 158.0.8125 renders the page, SkiaSharp 4.154.0 encodes PNG/JPEG/WebP. `netstandard2.1`, Linux x64 supported via NuGet native assets. API is one call: `PdfImageConverter.SavePng(pdfBytes, pageNumber: 1, ...)` / `RenderPage(...)`; default 300 DPI, `ScreenPreview` preset at 96 DPI, DPI selectable (150–200 DPI is the right invoice range: A4 @ 200 DPI = 1654×2339 px, crisp on a phone, ~15 MB bitmap in memory).
- **Alternative:** `PDFtoImage` (sungaila, MIT) — same idea (PDFium + SkiaSharp), older and less actively released; keep as fallback if PdfiumRaster has an issue.
- **Cost:** native PDFium + SkiaSharp assets add roughly **30–80 MB** to the image (estimate — measure at build); RAM per render is one bitmap plus PDFium working set, i.e. **tens of MB**, an order of magnitude under Chromium. No apt packages, no browser, no sandbox, no `/dev/shm`, no font install for Chromium (QuestPDF embeds the font in the PDF; PDFium renders embedded fonts).
- **Caveats:** PDFium is native code parsing PDF input — keep the package current. Its docs note PDFium is not thread-safe and the library serializes native calls in-process; still wrap renders in a semaphore for safety. **Version check required:** PdfiumRaster pulls SkiaSharp 4.154.0; QuestPDF pins its own SkiaSharp version — confirm at restore time that the two resolve to one version without a binding conflict (if they clash, `PDFtoImage` or an explicit SkiaSharp pin is the escape hatch).
- **The real gate is upstream of PNG:** this route is only as good as the QuestPDF PDF. QuestPDF has integrated HarfBuzz text shaping since 2022.6, so Myanmar *should* shape correctly with Noto Sans Myanmar — but "should" is not a verification. The PDF must be visually checked against the frozen HTML preview anyway (already required by the plan, §4 risk note); once it passes, the PNG is free and exact.

### (b) PuppeteerSharp + system Chromium

Same browser, same RAM, same image growth as §1, with a smaller maintainer base than Playwright and no `--only-shell` equivalent story. No advantage over Playwright. Reject.

### (c) wkhtmltoimage

**Archived 2023-01-02**, last stable June 2020. Qt WebKit frozen in 2012: no modern flexbox `gap`, no CSS grid, no custom properties, limited JS, needs `xvfb-run` headless. The frozen invoice uses modern CSS — it will render wrong. Reject outright.

### (d) Hosted screenshot API (e.g. HTML-to-image SaaS)

Zero server cost, but invoice data (customer name, phone, vehicle, amounts) leaves Han's server to a third party, plus an API key, billing, and a network dependency on every export. Wrong trade for a shop invoicing app. Reject.

### (e) Client-side PNG (html2canvas, already in the PWA)

Noted for completeness: the PWA already renders the frozen HTML in the user's browser and already inlines html2canvas 1.4.1 + jsPDF. A client-side PNG costs the server nothing — but Han's Q4 decision explicitly requires **server-side** PNG, and html2canvas has its own fidelity limits (it re-implements CSS in canvas; it is not a browser screenshot). Keep only as an emergency fallback if both server routes fail; do not present it as the solution.

---

## 4. Comparison

| Route | PNG matches frozen HTML? | RAM peak (1 render) | Image growth | Effort | Main risk |
|---|---|---|---|---|---|
| **B — PdfiumRaster over QuestPDF PDF (recommended)** | Exactly matches the **PDF**; matches frozen HTML iff the QuestPDF PDF does (one verification covers both) | Tens of MB | ~30–80 MB | ~0.5 day after PDF exists | QuestPDF Myanmar/layout verification (already owed); SkiaSharp version pin check |
| A — Playwright headless-shell screenshot | **Yes, pixel-exact** (real browser, same HTML/CSS) | ~350–450 MB est.; OOM risk if concurrent | ~+150–250 MB | ~1–2 days (Dockerfile, fonts, serialization, timeouts) | OOM-kills whole service at 512 MB; cold start ~60 s+; build complexity |
| SkiaSharp hand-redraw | No — third implementation, drifts | Tens of MB | Small | Days + ongoing | Layout drift from both HTML and PDF |
| wkhtmltoimage | No (2012 engine) | Small | Small | — | Archived, wrong rendering |
| PuppeteerSharp | Yes (same as A) | Same as A | Same as A | Same as A | Same as A, less maintained |
| Hosted API | Yes | ~0 | ~0 | Hours | Invoice data leaves server; cost |

---

## 5. Recommendation — firm

**Build PNG as Route B: `GET /invoice/{id}/png` = generate the QuestPDF PDF in memory → `PdfiumRaster` render page 1 at 200 DPI → `image/png`. Keep Playwright (§1) as the documented fallback, to be used only if the QuestPDF PDF fails its frozen-design verification.**

### Concrete next steps (in order — do not start PNG before step 1 passes)

1. **Build the QuestPDF invoice first** (already planned work). Render the seed invoice; compare against the frozen HTML preview **by eye on Han's phone**: Myanmar headings (`သိန်းကမ္ဘာ`, service names), stacked consonants/medial ra, table column widths, totals alignment, QR code. This is the gate for both PDF and PNG.
2. **Add the package:** `dotnet add package PdfiumRaster --version 2.0.7`. At restore, check for a SkiaSharp version conflict with QuestPDF's pinned SkiaSharp; resolve with an explicit pin or fall back to `PDFtoImage`.
3. **One endpoint:** `GET /invoice/{id}/png` — reuse the existing PDF generation path (same invoice data, same QuestPDF document), render in memory, return `image/png` with a download filename `invoice-{id}.png`. Serialize renders with a semaphore (cheap insurance; PDFium is not thread-safe).
4. **Dockerfile: no changes expected.** No apt packages, no browser, no font install — QuestPDF already bundles Noto Sans Myanmar, and PDFium renders the embedded font. (If the PDFium native `.so` fails to load on bookworm, that is the first unproven item in §6 — check the container log, do not guess.)
5. **Verify on Render:** deploy, hit `/invoice/{id}/png` for the seed invoice, check the PNG on Han's phone against the frozen preview, and confirm the service stays up (no OOM, no restart loop).
6. **Fallback trigger (only if step 1 fails):** if QuestPDF cannot match the frozen design or shapes Myanmar wrong, switch the *whole* export path to Playwright headless-shell (§1) — Chromium print-to-PDF for the PDF and screenshot for the PNG, both from the same frozen HTML. At that point also budget the Render upgrade to the $25/mo 2 GB instance, because 512 MB + Chromium is not a comfortable production combination (Starter $7 buys no extra RAM).

### Effort estimate

- Route B: ~0.5 day (package + endpoint + deploy + phone verification), after the QuestPDF PDF exists.
- Route A (fallback only): ~1–2 days (Dockerfile with apt deps + `--only-shell` browser install as `app` user + font install + render serialization + long client timeouts + load testing).

---

## 6. What remains unproven — and how to prove it

| # | Unproven item | How to prove it |
|---|---|---|
| 1 | **QuestPDF renders Myanmar correctly** (HarfBuzz shaping + Noto Sans Myanmar `mym2`) and matches the frozen layout | Render seed invoice → PDF → view on Han's phone next to the frozen PWA preview. This is the gate for Route B *and* for the PDF itself. |
| 2 | **PdfiumRaster loads on `aspnet:8.0` bookworm** (native PDFium `.so` + SkiaSharp native assets under the non-root `app` user) | First Render deploy with the package: one `GET /invoice/{id}/png` call; a 500 with a native-load error in the log means a missing system lib — fix from the log, don't guess. |
| 3 | **No SkiaSharp version conflict** between QuestPDF's pin and PdfiumRaster's 4.154.0 | `dotnet restore` / build output; check the resolved version in the build log before deploying. |
| 4 | **Actual RAM / image size on Render** for Route B | Render Metrics after deploy + a few PNG requests; expect well under 512 MB, but confirm rather than assume. |
| 5 | **Playwright fallback numbers** (only if Route B's gate fails) | First Docker build with `--only-shell` install; measure `docker stats` peak RSS for one render locally, then one render on Render free; confirm `--only-shell` flag passthrough in `playwright.sh`, confirm `--disable-dev-shm-usage` is set, confirm Noto Sans Myanmar is installed (no tofu). |
| 6 | **Cold-start behaviour for PNG** | After a 15+ min idle period, time a PNG request end-to-end from Han's phone; confirm it completes inside the client timeout (set 90 s). |

## Sources (checked 2026-11-20)

- NuGet: `Microsoft.Playwright` 1.65.1 latest / 1.64.1 stable 2026-10-22, Chromium 156.0.8078.4 — nuget.org/packages/Microsoft.Playwright
- Playwright .NET Docker docs — playwright.dev/dotnet/docs/docker (official image `mcr.microsoft.com/playwright/dotnet:v1.64.1-noble`; `--ipc=host` recommendation; "intended for testing and development only")
- Official Playwright image size 1,397 MB compressed vs `aspnet:10.0` 98 MB — mertozen.com (manifest layer sums)
- `--only-shell` / two-browser install since 1.57, ~301 MB → ~114 MB — thepromptshelf.dev (verified against Playwright 1.63 `browsers.json` + Chrome for Testing bucket)
- Headless-shell-only image 763 MB → 370 MB — HtmlPdfPlus ADR006 (github.com/FRACerqueira/HtmlPdfPlus)
- 1-page headless-shell peak 219–227 MiB; 10 pages ~1 GB — rscomputers-ks.com lab, Oct 2026 (Playwright 1.64)
- Fresh-launch screenshot peak RSS ~613–622 MiB (1 job), ~900–1,150 MiB (concurrency 2) — curlshot bench, Playwright 1.63, Nov 2026
- "~300 MB per Chromium instance" — screenshotrun.com; "150–300 MB per page" — privatebyte.com
- Render free: 512 MB / 0.5 CPU, spins down after 15 min idle, ~45–60 s wake; Starter $7 = same 512 MB, 2 GB = $25/mo — Render pricing/free-tier docs via actualdatabase.com, justcreateapp.com, temps.sh (Oct 2026)
- Chromium apt deps for Debian/Ubuntu — cazzulino.com (aspnet:6.0 + Chromium walkthrough); Playwright `install-deps` docs
- SkiaSharp 3.119.0 stable (Apr 2025); 4.157.0 stable (Nov 2026) — nuget.org/packages/SkiaSharp
- HarfBuzzSharp / SkiaSharp.HarfBuzz, Myanmar = complex script, HarfBuzz shapes via `mym2` — nuget.org/packages/HarfBuzzSharp; HarfBuzz shaping docs (Myanmar uses Indic shaper only with `mym2`)
- QuestPDF HarfBuzz shaping integrated since 2022.6.0 — nuget.org/packages/QuestPDF
- `PdfiumRaster` 2.0.7 (PDFium 158.0.8125, SkiaSharp 4.154.0, MIT) — nuget.org/packages/PdfiumRaster/2.0.7; `PDFtoImage` — github.com/sungaila/PDFtoImage
- wkhtmltoimage archived 2023-01-02, Qt WebKit frozen 2012 — nutrient.io; renderscreenshot.com; rendex.dev
- Project ground truth: `~/workspace/theingabar-invoice-backend-csharp/TheingabarInvoice.csproj`, `Dockerfile`, `HTML-ONLY-PLAN.md` §4 + §8 Q4 (read 2026-11-20)
