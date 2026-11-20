# HTML-Only Frontend Migration Plan — Theingabar Invoice

**Date:** 2026-11-20 · **Status:** ANALYSIS ONLY — no files changed, nothing deployed.
**Han's decision (binding):** Frontend HTML သက်သက်ပဲ, logic အကုန် backend (C#).
**PIN lock: DROPPED** (Han, 2026-11-20 17:30 — "မလိုရင် မထည့်နဲ့"). It is not an open
decision. The app will rely on login/session only; there will be no device-level lock screen.
**Security bar (Han, 2026-11-20 17:31):** hack / cyber attack မလုပ်နိုင်တဲ့ security —
see §7 SECURITY for the concrete mechanisms and the plain threat model.

**Sources read for this plan** (all local, 2026-11-20):
- `~/workspace/theingabar-invoice-render/index.html` — 2,307 lines, 2,817,611 B (local-only PWA)
- `~/workspace/theingabar-invoice-backend/public/index.html` — 2,455 lines, 2,825,654 B
  (+ `sw.js`, `Program.cs` — 937 lines, ASP.NET Core 8, Npgsql 8.0.5)
- `~/workspace/theingabar-invoice-firebase/public/index.html` — 2,606 lines, 2,833,690 B
  (+ `sw.js`, `firestore.rules` — 199 lines, v2)
- All three share the same app: same HTML body/pages, same main `<script>` (lines ~686
  to end), same inlined libs (html2canvas 1.4.1 + jsPDF UMD on lines 18–19, ~2.6 MB of
  each file) and inlined fonts (Noto Sans/Serif Myanmar base64). Differences are only
  in the sync layer: render = local IndexedDB only; backend = + Render `/api` sync;
  firebase = + Firestore/Auth via a `<script type="module">` (lines 2425–2604) that
  dynamic-imports the Firebase SDK from `gstatic.com` (NOT bundled — needs internet).

---

## 0. What "HTML-only" means here (strict definition)

**Strict reading (this plan's default, per Han's words): zero custom JavaScript.**
No `<script>` in any page, no app-logic JS files. Pages are server-rendered HTML;
every action is a plain form POST (or link GET) followed by a full-page response
(POST → redirect → GET). Browser-native features that need no JS still work:
`<details>`, native form validation (`required`, `type`, `pattern`), `<input type=file>`,
`@media print`, CSS.

**What that rules out:** HTMX is JavaScript — NOT allowed under the strict reading
(it is a JS library; its value here is small anyway, since full-page POST+redirect
covers this app). Any "tiny progressive enhancement" JS is also out unless Han
explicitly approves a carve-out.

**Two named carve-outs Han must rule on** (see §8, Q1):
1. **Service worker** (`sw.js`, ~40 lines) — it IS a JS file, but it holds no app
   logic, only a cache list. Keeping it preserves offline *page caching* and PWA
   installability. Dropping it kills offline entirely. Either way, offline
   *writes* (issuing an invoice with no signal) are impossible without JS.
2. **Nothing else.** No exceptions are assumed.

**Consequence to accept up front:** every interaction becomes a full page load.
On Render free tier the service sleeps, so the first load after a pause takes
30–60 s — and unlike today, there is no offline copy to fall back on. This is the
single biggest UX regression of the migration (§6).

---

## 1. Feature inventory — everything the frontend JS does today

Reference copy is the backend one (`theingabar-invoice-backend/public/index.html`,
main script lines 686–2453, ~1,770 app-JS lines, 131 functions). "Copies" = R
(render/static), B (backend/Render), F (firebase). "Moves?" = can it move server-side.

| # | Feature | Code today (backend copy) | Copies | Moves server-side? |
|---|---------|---------------------------|--------|--------------------|
| 1 | Page routing / tabs (Home/User/Form/Preview/Log) | `setPage` 2418–2453, `wireUsersAuth` 2280–2418 (~140 lines event wiring), pages HTML 418–545, `body[data-page]` CSS rules | R/B/F | **Yes** — server routes (`/`, `/user`, `/form`, `/preview`, `/log`), tab bar becomes plain links. URL hash (`#form`) gone. |
| 2 | Invoice form + validation + totals | `renderEditor` 759–812, `snapshot` 719, `total` 730, `fmt`/`fmtDate`/`esc` 733–745 | R/B/F | **Yes, except live totals** — form POSTs; server validates (rules already exist in `Program.cs`) and computes totals. As-you-type totals LOST. |
| 3 | Live preview as-you-type | `renderPreview` 812–838, `openPreviewDialog` 2258–2270 | R/B/F | **Partially** — preview becomes a server page (`POST /preview` renders the frozen invoice HTML). As-you-type preview LOST; preview appears only after pressing Preview. |
| 4 | Invoice numbering `YYYYMMDD-NNN` | `peekNextInvoiceNo`/`nextInvoiceNo`/`ensureNos`/`dedupeInvoiceNos` 1526–1572, `invSeq` persisted in IndexedDB `settings` | R/B/F | **Yes, and this gets BETTER** — server assigns the number in a Postgres transaction at save time; client numbering (incl. the 001→002 migration hack in `ensureNos`) deleted entirely. Race-free by construction; the existing unique index `invoices_no_unique` stays as backstop. |
| 5 | IndexedDB cache + sync | `dbOpen`/`dbGetAll`/`dbGet`/`dbPut`/`dbDel`/`dbClear` 1001–1048 (DB `theingabar-invoice`, stores `invoices` + `settings`); `boot` 1400–1444 loads settings keys `meta`, `pin`, `users`, `activeUserId`, `invSeq` | R/B/F | **No — deleted.** Server (Postgres) becomes the only store. Local-only mode disappears. |
| 6 | Render `/api` sync | `apiHeaders`/`apiJson`/`serverEntry`/`syncFromServer` 1065–1185, `serverKeyPrompt` 1185–1201; `X-API-Key` kept in `localStorage` (`theingabar-api-key`) | B only | **Yes — deleted as a client concern.** The server reads its own DB directly; the API key never leaves the server. `/api/invoices` becomes unused (see §5). |
| 7 | Firestore sync | Firebase copy: `setFirebaseStatus`/`fbBridge`/`validateInvoice`/`firestoreEntry`/`syncFromServer`/`syncSaveToServer`/`syncDeleteFromServer` 1069–1176 + module `loadInvoices`/`saveInvoice`/`deleteInvoice` 2484–2512; `firestore.rules` v2 | F only | **Yes — retired.** See §5 (single backend, one-time Firestore→Postgres migration). |
| 8 | PIN lock (app lock screen) | `pinDigest` 1201–1211, `setLockText`/`lockOpen`/`lockClose`/`showLock`/`lockApp`/`unlockApp`/`pinSubmit` 1211–1306, `lockOverlay` dialog, auto-lock on `visibilitychange` (line 1316), `persistPin` | R/B/F | **DROPPED per Han — deleted, not migrated.** Consequence: no device-level barrier; anyone holding a logged-in phone opens the app. Mitigations are server-side only: session expiry + visible Log out + admin can suspend a user (sessions revoked immediately). See §7. |
| 9 | Users — sign up / log in | `seedHanUser` 1444, `userById`/`activeUser`/`userNameFor`/`initials`/`ownEntries`/`persistUsers`/`persistActiveUser` 1461–1472, `openAddUser`/`addUserSubmit` 2031–2070, `openUserLogin`/`renderUserLoginList`/`selectLoginUser`/`loginSubmit` 1954–2005, `loginEmailSubmit` 2005–2031, `setActiveUser`/`logoutUser`/`applyAuthMode` 1910–1954 | R/B/F | **Yes** — `users` table + server session cookie; sign-up and login become form POSTs. Max 5 users enforced server-side (today only UI-enforced). |
| 10 | Mechanic login credential (per-user 4-digit PIN) | `addUserSubmit` stores `pinHash: pinDigest(pinRaw)` (exactly 4 digits, optional); `loginSubmit` checks `pinDigest(pin) !== u.pinHash` | R/B/F | **Needs a decision (§8 Q3).** This is NOT the dropped app PIN lock — it is the login credential. The current hash (`pinDigest`: 60 rounds of a 32-bit djb2-style hash) is **not a password hash and must never be reused server-side** — it is trivially brute-forceable. Existing hashes cannot be migrated (one-way) → every mechanic sets a new credential once. Recommend: proper password (min 6–8 chars) with ASP.NET Core `PasswordHasher` (PBKDF2). |
| 11 | Phone OTP (SMS 6-digit) | `openOtpStep`/`sendOtpCode`/`confirmOtpCode` 2070–2126, `toE164`/`validatePhone` 1472–1485; Firebase module: `RecaptchaVerifier` (invisible), `signInWithPhoneNumber`, `phoneConfirmation.confirm` 2528–2550 | **F only (real)**; B/R show "SMS verification is available in the Firebase version" | **Needs a decision (§8 Q2).** Server-side options: (a) Firebase Identity Toolkit REST API from C# (`accounts:sendVerificationCode` / verify) using the web API key — keeps Firebase SMS quota (Spark 10/day, Myanmar delivery untested); (b) own SMS gateway (Twilio etc., paid); (c) drop OTP — password login + admin manual phone verify only. |
| 12 | Approval gate | `requireLogin`/`requireVerified`/`isWriteAllowed`/`requireApproved`/`applyApprovalState` 1865–1910, `status` pending→approved/rejected/suspended, `setUserStatus` 2221–2233, `adminVerifyPhone` 2202–2211 | R/B/F | **Yes** — `status` column; enforced in every write handler server-side, not just in the UI (today the UI hides buttons AND `firestore.rules` enforces it for F; the Render backend does NOT enforce it — see §7). |
| 13 | Admin — hidden long-press | `attachAdminGesture` 1729–1732, `start`/`cancel` 1732–1761 (3 s press on သိန်းကမ္ဘာ) | R/B/F | **No — impossible without JS.** Replaced by an **unlisted URL** (e.g. `/admin`, no link anywhere in the UI) + verified Google sign-in. Note: obscurity is not security; the real gate is the server-side Google check (§7). |
| 14 | Admin — Google sign-in | `adminGoogleSignIn` 1708–1729 (popup on desktop, redirect on narrow screens), `isVerifiedGoogleAdminUser` 1594, `watchAuthState` 1644, `adminSessionValid`/`dropAdmin`/`grantAdmin` 1655–1676, `getRedirectResult` | **F only (real)**; B/R stubs | **Yes** — standard server-side OAuth 2.0 authorization-code flow; backend verifies the ID token (`email_verified=true` + email in `[hanmintheinn@gmail.com, majorhmthein@gmail.com]`). No JS needed. |
| 15 | Admin — metadata editor | `buildAdminMetaUI` 1795–1849 (built ONLY inside an admin session, never rendered otherwise), `openMetaDialog`/`closeMetaDialog`/`destroyAdminMetaUI` 1849–1865, `persistMeta`, `syncMetaToServer/FromServer` | R/B/F | **Yes** — `meta` table (single row) + admin-only form page `/admin/meta`. The "never rendered for non-admins" property is preserved trivially: the page 404s/redirects for non-admins. |
| 16 | Per-user KBZ Pay templates | `tplTarget`/`applyPayField`/`applyTemplateToDialog` 1761–1795, QR file upload (`tplQrFile`), `qrDataUrl` (base64 data URL in user doc / meta) | R/B/F | **Yes** — `users.kbz_name/kbz_number/qr` columns; QR upload via `multipart/form-data` POST; stored as `bytea` in Postgres (Render disk is ephemeral — never store files on disk) and rendered as `<img src="/qr/{id}">` or data URL. |
| 17 | Log + week/month totals | `renderLog` 846–938, `visibleLog`/`readOnlyLog` 838–846, `periodTotals` 1572–1580 (week = Mon–Sun, month = calendar month) | R/B/F | **Yes** — server SQL; week/month totals computed per request (mind the timezone: Asia/Yangon, not UTC — do the arithmetic in SQL with `AT TIME ZONE 'Asia/Yangon'` or in C#). Viewer mode = read-only server page. |
| 18 | CSV export | `btnCsv` handler inside `renderLog` 932–952 (client builds the CSV string, Blob download) | R/B/F | **Yes — already exists server-side.** `GET /api/export.csv` in `Program.cs` becomes `GET /log/export.csv` under session auth. |
| 19 | CSV import | `parseCsv`/`parseDateCell`/`parseCsvInvoices` 1321–1396, hidden `<input type=file id="csvFile">` 1361–1369 | R/B/F | **Yes** — file-upload form POST; server parses + validates row by row, reports per-row errors on a result page. |
| 20 | PNG / PDF export | `capture` 961–1001 (html2canvas @ scale 3 → canvas → PNG download / jsPDF A4 image-PDF), `beforeprint` auto-save 998 | R/B/F | **Server-side, hardest part (§4, §8 Q4).** Options: (a) Playwright/Chromium screenshot of the server-rendered invoice page (pixel-exact frozen layout, but Chromium on Render free = ~300–500 MB RAM + slow cold start + bigger Docker image); (b) QuestPDF (pure .NET, MIT) rebuilds the A4 PDF natively — but the layout must be re-implemented and can drift from the frozen design; PNG still needs a rasterizer; (c) drop PNG, keep PDF + Print only. |
| 21 | Print | No `window.print()` call in JS — printing is the browser's native print of the preview page via the frozen `@media print` CSS; `beforeprint` auto-saves the invoice first | R/B/F | **Yes, unchanged.** Server-rendered preview page keeps the same print CSS; auto-save on print becomes "Print button POSTs save, then shows the printable page" (no `beforeprint` hook without JS). |
| 22 | Service worker / offline | `sw.js` (40–42 lines; cache `theingabar-v8` / `theingabar-firebase-v6`; backend skips `/api/*`, firebase skips `gstatic.com`/`googleapis.com`/`firebase`); registered in `boot` ~1382–1392 | R/B/F | **Decision (§8 Q1).** Keep the SW file for page caching only, or drop offline entirely. Offline *writes* are gone either way. |
| 23 | Toasts / dialogs | `toast` 745–752, `openDialog`/`closeDialog` 1306–1321, 6 `<dialog>`s (`lockOverlay`, `userLoginDialog`, `addUserDialog`, `otpDialog`, `adminDialog`, `previewDialog`) + dynamically built `metaForm` dialog | R/B/F | **Replaced by navigation.** Toasts → server flash messages (one-request `TempData` banner on the next page); dialogs → dedicated pages (login, sign-up, OTP, admin, preview). `<dialog>` needs JS to open, so it cannot stay. |
| 24 | Rate limiting | `rateLimitKey`/`rateLimitRead`/`rateLimitSave`/`rateLimitCheck`/`rateLimitRecord`/`rateLimitCountdown`/`rateLimitResetAll` 1488–1523, stored in `localStorage` | R/B/F | **Yes — and this gets REAL.** Today's limits are client-side and trivially bypassed. Server-side, DB-backed, per-IP + per-account: login 3/min, OTP 3/min, admin 3 failures → 5-min lockout (§7). |
| 25 | Seed data | `seedIfEmpty` 1396–1400, `boot` 1400–1444 (seeds Nissan Safari `20261119-002`, 300,000 MMK, when the store is empty; `invSeq["20261119"]=2`) | R/B/F | **Yes** — server seeds on first boot if the `invoices` table is empty. The existing 001→002 rename migration in `Program.cs` stays. |
| 26 | Formatting / escaping helpers | `fmt`, `fmtDate`, `esc` 733–745 | R/B/F | **Yes** — C# equivalents; `esc` becomes Razor's automatic HTML-encoding (do not hand-roll it). |
| 27 | PWA manifest / install | `manifest.webmanifest` (name "Theingabar Invoice", `display: standalone`, icons) | R/B/F | **Stays only if the SW stays.** Without a service worker the app is a plain website; "Add to Home screen" still works as a bookmark, but there is no install prompt and no offline. |

**Size of the migration in code terms:** the main app script is ~1,770 lines (backend
copy) plus ~180 lines of Firebase module (firebase copy). Roughly 1,950 lines of
client JS are deleted; the replacement is server code (Razor Pages + handlers +
new tables). The inlined html2canvas + jsPDF (~2.6 MB) and inlined fonts also leave
the HTML — fonts become static files served by the backend; pages shrink from
~2.8 MB to tens of KB.


---

## 2. What "HTML-only + logic in backend" means per feature — and what CANNOT move

**Moves cleanly (server renders, form POSTs):** routing/tabs, form + validation,
preview (as a page, not live), numbering, users, approval gate, admin metadata,
KBZ templates, log + totals, CSV export/import, seed data, rate limiting (becomes
real), toasts/dialogs (become pages + flash messages).

**Cannot move without losing capability:**

| Capability | Why it cannot move | What replaces it |
|---|---|---|
| Offline invoicing (issue an invoice with no signal) | Needs IndexedDB + client sync queue — both are JS | **Lost.** With no signal the app shows an error page. Only mitigation is the SW page-cache carve-out (§8 Q1), which caches *pages*, not writes. |
| Live preview / live totals as-you-type | Needs JS event handlers on every keystroke | **Lost.** Totals and preview appear only after pressing Preview / Save (one round-trip). |
| PIN lock screen | Needs client-side gate before any page renders | **Dropped per Han.** Replaced by login/session only (§7). |
| Hidden admin gesture (3 s long-press on သိန်းကမ္ဘာ) | Needs JS touch/pointer handlers | **Lost.** Replaced by an unlisted `/admin` URL + Google sign-in. |
| Instant UI feedback (toasts, inline errors, dialogs) | Needs JS DOM updates | **Degraded.** Every feedback is a full page reload with a server flash message / re-rendered form showing field errors. |
| Client-side PNG/PDF (html2canvas + jsPDF) | Needs a canvas + JS PDF library in the browser | **Server-side rebuild (§4)** — QuestPDF and/or Playwright/Chromium, or drop PNG (§8 Q4). |
| Firebase Auth/Phone OTP as a client SDK | The SDK is JS loaded from `gstatic.com` | **Server-side equivalents only:** Google via OAuth code flow; phone OTP via Identity Toolkit REST API or an SMS gateway (§8 Q2). |
| PWA install prompt / offline badge | Needs SW + manifest + JS registration | **Only if the SW carve-out is approved.** Otherwise the app is a normal website. |

---

## 3. Recommended target architecture

**One backend, one database, one URL.**

- **Backend:** the existing C# service `theingabar-invoice-web` (ASP.NET Core 8,
  Docker on Render, Postgres `theingabar-invoice-db`). Extend `Program.cs` with
  **Razor Pages** (part of the `Microsoft.AspNetCore.App` shared framework — no new
  package needed; the project already uses `Microsoft.NET.Sdk.Web`). Razor gives:
  automatic HTML-encoding (XSS defence), antiforgery integration, layouts/partials.
  Alternative considered and rejected: hand-building HTML strings in minimal-API
  handlers — unmaintainable and easy to get encoding wrong.
- **Routes (server-rendered pages):** `GET /` (Home viewer) · `GET/POST /login`,
  `/signup` · `GET /user` · `GET/POST /form` (new/edit invoice) · `POST /preview`
  (renders frozen invoice HTML) · `GET /log` (+ `/log/export.csv`) · `POST
  /log/import` (CSV upload) · `GET /invoice/{id}/pdf`, `/invoice/{id}/png` ·
  `GET /admin` (unlisted, no links to it) · `GET/POST /admin/meta`, `/admin/users`
  · `GET/POST /auth/google`, `/auth/google/callback` · `POST /logout`.
- **Auth:** server-side sessions. Session row in Postgres
  (`sessions`: id, user_id, created_at, expires_at, revoked) + one opaque session
  id in a cookie (`HttpOnly`, `Secure`, `SameSite=Lax`, `__Host-` prefix). Admin
  sessions are the same cookie, with `is_admin` resolved server-side from the
  Google-verified identity — never from anything the client sends. Login =
  username + password (§8 Q3). Google OAuth code flow for admin only.
  (ASP.NET Core cookie-auth middleware is an acceptable implementation; a manual
  session table is preferred because the admin must be able to revoke sessions
  immediately on suspend.)
- **Retire:** the static site `theingabar-invoice` and the Firebase site
  `theingabar-invoice-firebase` (repos archived, Render services deleted *after*
  cutover verification). The live URL becomes
  `https://theingabar-invoice-web.onrender.com` only. Firebase project retired
  too — keeping it as a second auth system would preserve two sources of truth
  and the Firestore rules as a second security boundary. One-time migration:
  Firestore `users`/`meta`/`invoices` → Postgres, deduped by `uid` (the Postgres
  DB already holds the Render-synced invoices, including the `20261119-002` seed —
  the migration must not duplicate them).
- **Keep:** the frozen invoice HTML/CSS (server renders the same paper, byte-for-byte
  same print output); Tailwind `tailwind.css` (CSS only, still fine); fonts as
  static files in `wwwroot` instead of inlined base64; the Postgres `invoices`
  table and its unique index; `/api/health`.
- **Remove:** `/api/invoices` + `/api/export.csv` as client APIs (nothing calls
  them in HTML-only) — or keep them dormant behind the session cookie instead of
  `X-API-Key`. Recommendation: remove them to shrink the attack surface; the CSV
  download becomes the session-authenticated page route. `INVOICE_API_KEY` then
  leaves the system entirely (no client ever held it except in `localStorage` —
  that copy disappears with the old frontend).
- **Numbering (race-free):** new table `invoice_no_seq(date_key TEXT PRIMARY KEY,
  seq INT)`. At save, in one transaction:
  `INSERT INTO invoice_no_seq VALUES ($date,1) ON CONFLICT (date_key) DO UPDATE
  SET seq = invoice_no_seq.seq + 1 RETURNING seq` → `no = YYYYMMDD-NNN`. The
  existing unique index stays as a backstop (on conflict, retry once with a fresh
  number). No client-side `invSeq`, no `ensureNos`/`dedupeInvoiceNos`.

---

## 4. Exports, server-side (the hard part)

- **PDF — recommended: QuestPDF** (pure .NET, MIT licence, no native deps, works
  on Render free). The frozen A4 invoice layout is re-implemented once in C#.
  Risk: layout drift from the frozen design — must be verified by rendering the
  seed invoice and comparing it against the current PDF/PNG pixel-for-pixel (or at
  least by eye on Han's phone).
- **PNG — recommended: Playwright + Chromium** headless screenshot of the
  server-rendered invoice page (pixel-exact, reuses the frozen HTML/CSS). Cost:
  Chromium is ~300–500 MB RAM and slow to cold-start on Render free; the Docker
  image grows; first PNG after a sleep can take 10–30 s. **Fallback if that proves
  unworkable: drop PNG and keep PDF + Print only** (§8 Q4 — PNG is how invoices
  are shared to customers, so dropping it is a real loss; do not decide it
  silently).
- **Print — unchanged:** the server-rendered preview keeps the frozen `@media
  print` CSS; the browser's native print dialog does the work. The old
  `beforeprint` auto-save becomes an explicit flow: the Print button POSTs a save
  first, then returns the printable page.
- **QR — unchanged in effect:** stored as `bytea` in Postgres (never on Render's
  ephemeral disk), served from `/qr/{id}` or embedded as a data URL.

---

## 5. What the offline story becomes

**Lost as a working mode.** Without JS there is no IndexedDB, no sync queue, no
offline writes. The honest statement: *the app needs internet to do anything*.
Two mitigations, neither restores offline invoicing:
1. **SW page-cache carve-out (§8 Q1):** keep `sw.js` (cache list only) so already
   visited pages load while offline — read-only, and any form submit fails.
2. **Nothing.** Pure online app; offline shows the browser's error page.
Note the compounding factor: Render free sleeps, so even *online* use starts with
a 30–60 s cold start after a pause. Today the PWA masks that with its offline
copy; after migration nothing masks it. If Han issues invoices in places with weak
signal, this is the migration's biggest practical regression — say so plainly when
presenting the plan.

---

## 6. Phased plan (rough effort — estimates, not commitments)

| Phase | Work | Rough effort |
|---|---|---|
| 0 — Decisions + data audit | Answer §8 questions; audit Firestore vs Postgres overlap (which invoices/users exist in only one place); confirm OTP route and PNG route; confirm session length | 0.5 day |
| 1 — Backend foundation | Postgres tables (`users`, `meta`, `sessions`, `invoice_no_seq`, `rate_limits`); Razor Pages shell + layout + flash messages; session auth + logout; CSRF + security headers + rate limiting; Google OAuth skeleton for admin | 3–5 days |
| 2 — Invoices | Home / Form / Preview / Log pages; server numbering (§3); week/month totals (Asia/Yangon); CSV export/import; seed migration; frozen paper rendered server-side | 3–4 days |
| 3 — Users + approval + admin | Sign-up / login (new credential); approval gate enforced in handlers; admin Google sign-in; `/admin/meta` editor; per-user KBZ templates + QR upload; admin user list (approve/reject/suspend + session revoke) | 3–4 days |
| 4 — OTP + PNG/PDF | OTP via chosen route (§8 Q2); PDF via QuestPDF (layout verification vs frozen design); PNG via Playwright (or drop decision) | 3–5 days |
| 5 — Migration + cutover | Firestore→Postgres migration script (dedupe by `uid`); deploy alongside the old PWA (new routes first, old `public/` kept until cutover); live verify on all pages; Han's phone checks; retire static + Firebase sites; archive repos | 2–3 days |
| **Total** | | **~2–3.5 weeks of build + verify** (single builder), plus Han's phone checks |

Build order keeps the current live sites untouched until Phase 5: the new pages are
added to the C# service under new routes while the old PWA keeps serving from
`public/`. Cutover = flip the root route to the new Home page, then delete the old
frontend.

---

## 7. SECURITY — server-side security for the HTML-only + C# architecture

**Han's bar:** hack / cyber attack မလုပ်နိုင်တဲ့ security ရှိရင်ရပြီ.
Plain statement first: **no system is literally unhackable.** What this section
promises is a concrete set of mechanisms that stop the realistic attacks on this
app. The threat model is stated at the end, including what it does *not* cover.

### 7.1 Mechanisms (name the mechanism, not a slogan)

1. **Server-side session auth — the only gate.** Login creates an opaque,
   random session id stored in Postgres (`sessions` table); the browser holds
   only that id in a cookie. The server resolves the user, role and approval
   status from the session on every request. No user id, role, `isAdmin` or
   `status` is ever taken from the request, a hidden form field, a cookie value
   or a URL — anything the client sends that claims identity is ignored.
   Sessions expire (length per §8 Q7) and can be revoked server-side instantly
   (admin suspends a user → all their sessions die on the next request).
2. **No secrets in the frontend HTML.** No `X-API-Key`, no Firebase web `apiKey`,
   no Google OAuth client secret, no tokens in any page. `INVOICE_API_KEY`
   leaves the system (nothing calls `/api` any more). The Google client secret
   lives only in a Render env var. The Firebase web config (currently inlined in
   the firebase copy's module script, including `apiKey
   AIzaSyCynZaEjeWbuNOP5ysejO4NLJneG9uGMHo`) disappears with the Firebase copy.
3. **All validation + authorization enforced server-side, never trusting the
   client.** Invoice rules already exist in `Program.cs` (date/customer required,
   1–20 items, amounts int ≥ 0, total == sum, `no` format) — extend the same
   treatment to users (username 3–20 chars, phone `09…` 9–11 digits, max 5
   users), meta (admin-only fields) and CSV import (per-row validation). Every
   write handler re-checks: logged in → phone verified → `status == "approved"`
   → owns the invoice (or is admin). The UI hiding a button is convenience only;
   the handler is the enforcement. (Today the Render backend does **not**
   enforce the approval gate at all — anyone with the API key can write any
   invoice. The migration closes that hole.)
4. **Rate limiting / lockout on login + OTP endpoints (server-side, DB-backed).**
   Login: 3 attempts/min per account + per IP, then a 60 s block with a visible
   wait. OTP send: 3/min per phone number. Admin login: 3 failures → 5-minute
   lockout. Counters live in Postgres (`rate_limits`), not in the browser —
   today's `localStorage` limits are trivially bypassed by clearing storage or
   using another browser. An admin can reset a locked account's counters.
5. **CSRF protection on every form POST.** ASP.NET Core antiforgery tokens in
   every form (`[ValidateAntiForgeryToken]` / Razor Pages automatic validation);
   `SameSite=Lax` on the session cookie as a second layer. No `GET` request ever
   changes data (no delete-by-link, no approve-by-link).
6. **Secure cookies.** Session cookie: `HttpOnly` (JS cannot read it — and there
   is no JS), `Secure` (HTTPS only — Render serves HTTPS), `SameSite=Lax`,
   `Path=/`, `__Host-` prefix (no `Domain` attribute, so it cannot be scoped to a
   parent domain). No "remember me" token that survives password/session revocation.
7. **No sensitive data in URLs.** No user ids, phone numbers, invoice ids that
   imply ownership, or tokens in query strings (nothing to leak into logs,
   history or a shared link). Invoice access is by session ownership check, not
   by an unguessable URL. Server logs never record phone numbers, passwords,
   PINs or session ids.
8. **Credential hashing server-side.** Mechanic passwords hashed with ASP.NET
   Core `PasswordHasher` (PBKDF2, per-user salt, iterated) — or `BCrypt.Net-Next`
   if preferred. **Never** plaintext, never the old client-side `pinDigest`
   (60 rounds of a 32-bit hash — brute-forceable in seconds on a laptop; it was
   only ever an app-lock convenience, never a credential hash). Because the old
   hashes cannot be reversed, every mechanic sets a new password once at
   cutover (or the admin sets a temporary one). No password/PIN is ever logged
   or emailed.
9. **Injection / XSS / headers.** SQL: parameterized queries only (Npgsql, as
   today — no string-concatenated SQL). XSS: Razor auto-encodes all output; no
   `Html.Raw` on user content; user-supplied text (names, item descriptions) is
   never rendered as HTML. Headers on every response: `Content-Security-Policy:
   default-src 'self'; script-src 'none'` (enforceable precisely because there
   is no JS), `X-Content-Type-Options: nosniff`, `Referrer-Policy:
   strict-origin-when-cross-origin`, `Strict-Transport-Security` (HSTS).
   Uploads: QR images validated by content type/size server-side, stored as
   `bytea`, served with a safe content type — never executed.
10. **Firebase, if it stays in the picture.** Recommendation is to retire it
    (§3). If Han instead keeps Firebase as the auth provider, the C# backend
    must verify Firebase ID tokens on every request with the Firebase Admin SDK
    (today the Render backend does **no** token verification — the API key is the
    only check — and the Firestore rules v2 would remain a second, separate
    security boundary that must stay in sync with the server's). The Firebase
    web `apiKey` is public-by-design, but it must not appear in server-rendered
    HTML unless the client actually needs it — in HTML-only it does not.
11. **Admin Google sign-in (server-side).** Standard OAuth 2.0 authorization-code
    flow; the backend exchanges the code, verifies the ID token signature and
    claims (`email_verified == true`, `email` in the two-address allowlist,
    `iss`/`aud` correct), and only then creates an admin session. Knowing an
    admin email address alone grants nothing — same guarantee as the Firestore
    rules v2 today, enforced in C# instead.

### 7.2 Threat model — what this stops, and what it does not

**Stops:** unauthenticated access to any page or action; one mechanic reading or
changing another's invoices or KBZ template; a pending (unapproved) user writing
or printing; tampering with invoice numbers, totals or ownership via edited form
fields; brute-forcing logins or OTP codes; SQL injection; XSS; CSRF; theft of an
API key or token from the frontend (there is none to steal); a forged admin
session.

**Does NOT cover (state plainly):**
- **A stolen logged-in phone.** With the PIN lock dropped, there is no
  device-level barrier: whoever holds a logged-in phone opens the app. The only
  mitigations are session expiry (§8 Q7), a visible Log out button, and the
  admin revoking the user's sessions the moment a phone is lost. If Han later
  wants a device barrier, the replacement is a server-side re-authentication
  prompt (password) before sensitive actions — not a client-side PIN.
- **A compromised server, Render account, GitHub account or database.** If the
  attacker owns the server or the DB, no app-level control helps. (Separate
  hygiene: strong unique passwords + 2FA on GitHub/Render/Google — outside this
  migration.)
- **Render free-tier limits.** The free Postgres expires after 90 days (export
  CSV / upgrade before expiry — standing warning) and the free web service
  sleeps (cold starts). Neither is a security hole, but the DB expiry is a data
  loss risk.
- **SMS interception** (SIM swap / SS7) for OTP codes, and **a mechanic sharing
  their password**. Both are outside the app's control.

### 7.3 PIN lock — dropped, and what that costs security-wise

The old lock was a *device* lock: it stopped a casual snoop on an unattended
phone, but it was never a security boundary — the hash was a weak 32-bit digest,
the lock lived entirely in the browser, and anyone who cleared site data or used
another browser bypassed it. Dropping it removes that casual barrier and nothing
else. The replacement security story is the server-side session in §7.1: the app
is now protected by login + server enforcement instead of by a screen on one
phone. Net effect: **stronger against remote/network attackers, weaker against
someone physically holding a logged-in phone.** That trade-off is Han's call and
is recorded here, not hidden.

---

## 8. Open decisions Han must make before building

PIN lock is **not** on this list — it is dropped (§1 #8, §7.3).

| # | Decision | Options | Recommendation |
|---|---|---|---|
| Q1 | Offline: keep a service worker for page caching, or drop offline entirely? (Offline *writes* are gone either way.) | (a) Keep `sw.js` (cache list only — a JS file, but no app logic) → pages load offline, read-only. (b) Drop it → pure online website. | (a) — the cache-only SW is the least-bad way to keep the app usable on weak signal, and it contains no app logic. But it is still a JS file, so it needs Han's explicit OK under "HTML-only". |
| Q2 | Phone OTP — keep it? By what route? | (a) Firebase Identity Toolkit REST API from C# (keeps Firebase SMS quota: Spark 10/day; Myanmar delivery untested). (b) Own SMS gateway, e.g. Twilio (paid per SMS). (c) Drop OTP — password login + admin manual phone verify only. | (a) if Firebase is retired only as a *database* but kept as the SMS sender; otherwise (c) — OTP is the most complex piece of the migration and the least proven on Myanmar numbers. |
| Q3 | Mechanic login credential | (a) Proper password, min 6–8 chars, server-hashed (recommended). (b) Keep a 4-digit PIN, server-hashed + strict lockout (weaker; 10,000 combinations — only the rate limit makes it viable). Either way, everyone sets a new credential once (old hashes cannot migrate). | (a). |
| Q4 | PNG export | (a) Playwright/Chromium screenshot on Render (pixel-exact; heavy, slow cold start). (b) SkiaSharp redraw (lighter; layout drift risk). (c) Drop PNG — PDF + Print only. | (a) if Render free runs Chromium acceptably — prove it in Phase 4 before promising it; otherwise (c). |
| Q5 | Retire the static site + Firebase site? | (a) Retire both — one URL (`theingabar-invoice-web.onrender.com`). (b) Keep Firebase as auth-only (backend verifies ID tokens via Admin SDK). | (a) — one backend, one DB, one security boundary. |
| Q6 | Admin access without long-press | Unlisted `/admin` URL + Google sign-in (no link in the UI). Accept? | Yes — the only HTML-only option. The real protection is the server-side Google check, not the hidden URL. |
| Q7 | Session length — how long before a mechanic must log in again? | e.g. 7 days / 30 days (sliding) for mechanics; shorter (e.g. 12 h) for admin sessions. | 30 days sliding for mechanics, 12 h for admin. (Longer = more convenient, weaker if a phone is lost — this is the direct trade-off for the dropped PIN lock.) |
| Q8 | Live preview/totals — accept the loss? | Totals + preview appear only after pressing Preview/Save (one round-trip each). No as-you-type anything. | Accept — there is no HTML-only alternative. |

---

## 9. Biggest risks (ranked)

1. **Offline loss + Render cold starts (§5).** The two compound: the app needs
   internet for everything, and the first load after a sleep takes 30–60 s.
   Mitigation: SW page-cache carve-out (Q1); nothing else available.
2. **PNG on Render free (Q4).** Chromium may be too heavy/slow; prove it early
   (Phase 4) or the cutover ships without PNG.
3. **OTP to Myanmar numbers (Q2).** Never tested end-to-end on any route; the
   Spark quota is 10 SMS/day. A failed OTP blocks sign-ups.
4. **QuestPDF layout drift.** The frozen invoice must be re-implemented; a
   drifted PDF breaks the "invoice ပုံစံ မပြောင်း" rule. Verify against the
   current PDF before cutover.
5. **Data migration dedupe.** Two live stores (Postgres + Firestore) with
   overlapping invoices; dedupe by `uid`, verify counts and the `20261119-002`
   seed before deleting anything.
6. **Google OAuth on Render.** Redirect URI must match exactly; test the admin
   flow on Han's phone before cutover (popup/redirect behaviour differs by
   screen size today).
7. **Session/cookie behaviour on phones.** The app becomes a website: no
   install prompt without the SW, and iOS/Android browsers handle third-party
   vs first-party cookies differently — test login persistence on Han's phone.
8. **Full-page latency.** Every action is a round-trip; on a slow connection the
   app will feel much slower than today's instant SPA. Nothing to do about it
   inside HTML-only.

---

## 10. What stays frozen

The printed invoice format/layout stays exactly as approved (Han's rule,
2026-11-20 01:54) — the server renders the same paper from the same HTML/CSS.
Invoice No. stays global sequential `YYYYMMDD-NNN`, next is `20261119-003`.
Shop metadata, services line (ဂီယာဘောက်), KBZ Pay 09428332233 / Han Min Thein,
QR handling, week/month totals, max 5 users, approval gate — all preserved,
only the machinery underneath changes.
