# Phase 5 — Migration + Cutover (PLAN ONLY, not executed)

HTML-only build, Theingabar Invoice. This phase moves live data to the new
single backend and retires the old sites. **Nothing in this document has been
executed.** Every step that touches live data or live services needs Han's
separate approval at cutover time.

## 1. Where live data lives today (2026-11-20)

| Source | What it holds | Status at cutover |
|---|---|---|
| Postgres `theingabar-invoice-db` (Render) | Invoices created on the **web-service origin** (`theingabar-invoice-web`), incl. seed `20261119-002`. Users/meta created via the PWA v2 flows on that origin. | Already the target DB. No move needed. |
| Firestore `theingabar-invoice` (project `theingabar-invoice`) | Invoices/users created on the **Firebase origin** (`theingabar-invoice-firebase`). Collection `invoices`, docs `{uid, date, customer, items, total, createdAt, updatedAt}`. | Must be imported once. |
| Phone IndexedDB (each mechanic's phone, old PWA) | Invoices created offline on any origin that never synced (offline cache). | Must be uploaded before the old sites retire. |

The new HTML-only app uses the SAME Postgres database and the SAME
`invoices` table + numbering (`YYYYMMDD-NNN`, in-transaction,
`invoices_no_unique` as backstop). No schema change is needed at cutover.

## 2. Preconditions (check before touching anything)

- [ ] New backend deployed to Render and verified live (see §3).
- [ ] Han has signed in as Admin on the NEW backend via Google
      (`/admin` → Sign in with Google) and can see the seed invoice.
- [ ] Han has set a password for his own mechanic login OR uses Google
      admin only (seed user `han` has no password hash; password login
      for Han needs a password set via the User page or a DB row — admin
      can set it in a later step; not required for cutover).
- [ ] CSV export of the live Postgres taken (Render dashboard → the
      app's `/log/export` as admin, or `pg_dump` via Render) — the
      rollback copy.
- [ ] Firestore console export taken (browser): Firestore → `invoices`
      collection → export. The Firebase console can export a collection
      to Cloud Storage as JSON; if that is unavailable on Spark, use the
      old PWA's Log → CSV export on a phone that is synced (it lists
      every Firestore invoice) and save the CSV.
- [ ] Every mechanic's phone has opened the old PWA while online at
      least once (so its IndexedDB has synced TO Firestore) OR has
      exported its local log to CSV.

## 3. Deploy the new backend (exact steps)

Deploy route is the standing one (browser task; Han has no PC).

1. **Google Cloud Console (browser, new step):** in the Firebase project
   `theingabar-invoice` (Google account majorhmthein@gmail.com) →
   APIs & Services → Credentials → Create OAuth client ID (Web
   application). Authorized redirect URI (exact):
   `https://theingabar-invoice-web.onrender.com/auth/google/signin-google`
   Copy the Client ID + Client Secret.
2. **Render → `theingabar-invoice-web` → Environment**, add:
   `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` (paste from step 1).
   Keep `DATABASE_URL` and `INVOICE_API_KEY` exactly as they are —
   **never regenerate `INVOICE_API_KEY`** (phones have it saved).
   No Blueprint sync, no new database.
3. **GitHub (browser):** upload the working copy
   `~/workspace/theingabar-invoice-backend-csharp/` to
   `majorhmthein-netizen/theingabar-invoice-backend`, branch `main`,
   replacing: `Program.cs`, `Db.cs`, `Auth.cs`, `PageHelpers.cs`,
   `TheingabarInvoice.csproj`, `Dockerfile`, `render.yaml`,
   `Pages/…`, `Services/…`, `wwwroot/…`, `public/…`.
   The old `server.js` / `package.json` are already deleted in the
   live repo (C# deploy 2026-11-20); do NOT re-add them.
4. **Render → `theingabar-invoice-web` → Manual Deploy → Deploy
   latest commit.** Runtime is already Docker (flipped 2026-11-20);
   no runtime change needed. First build takes ~1–3 min
   (`dotnet restore/publish` + native PDFium runtimes).
5. **Verify live** (browser + curl):
   - `GET /api/health` → `{"ok":true,"store":"postgres",…}`
   - Home shows seed `20261119-002`, 300,000 MMK.
   - `GET /invoice/1.pdf` → valid PDF (check the header renders
     centred — the QuestPDF layout was verified locally 2026-11-20).
   - `GET /invoice/1.png` → PNG 1653×2339 (proves PDFium native
     lib loaded in the Render container; if this 503s, see §7).
   - Sign in as Admin via Google at `/admin`; approve a test user.
6. If any verification fails: **do not proceed to §4**. The old
   sites stay live and untouched; fix forward in the working copy
   and redeploy.

## 4. Data migration (one time, after §3 is green)

### 4a. Firestore → Postgres

- Dedupe key is the Firestore document `uid` (stored in Postgres
  `invoices.uid`, unique). Import skips any `uid` already present —
  re-running the import is safe.
- Numbering: imported invoices KEEP their Firestore `no` when it
  matches `YYYYMMDD-NNN` and is not already taken; on collision the
  importer assigns the next free number for that date (same
  in-transaction allocator as the app) and records the remap in the
  import report. The seed `20261119-002` must never be overwritten.
- Import path (no-PC friendly): the old Firebase PWA's Log → CSV
  export (all invoices, synced) → new app → Log → CSV import
  (approved user, admin preferred). The import handler already
  validates rows (date/customer/items/total == sum) and assigns
  numbers server-side; it ignores the CSV `No.` column.
  If the CSV route is insufficient (e.g. users/meta also need
  moving), the fallback is a one-off admin-only `/admin/import`
  endpoint accepting the Firestore JSON export — **not built yet**;
  build it only if the CSV route proves inadequate.
- Users: Firestore `users` docs carry `localId`/`username`/`phone`/
  `status`/`isAdmin`. The new `users` table is keyed by `local_id`.
  Import users by `localId` (skip existing); every imported mechanic
  starts `pending` with NO password (old `pinDigest` hashes are
  never reused) — each sets a new password on first login, then the
  Admin approves. The seed admin `han` is untouched.

### 4b. Phone IndexedDB → new backend

- On each phone, open the OLD PWA (any origin) while online →
  Log → CSV export → open the NEW app → Log → CSV import.
  Repeat until the phone's log count matches the new server's count
  for that user.
- Phones that never synced to Firestore hold local-only invoices;
  the CSV export from the old PWA includes them (IndexedDB is the
  source for that phone's export).

### 4c. Verification (counts must match before retiring)

- New Postgres invoice count == Postgres count + Firestore count +
  phone-only count (minus exact-`uid` dupes, which the import
  reports).
- Spot-check: seed `20261119-002` still 300,000; week/month totals
  on the new Log page equal the old PWA's totals for the same week.
- Every mechanic can log in (new password) and sees their own
  invoices; Admin sees all.

## 5. Cutover (retire the old sites)

Only after §4c is green:

1. **Suspend (do NOT delete)** the Render Static Sites
   `theingabar-invoice` and `theingabar-invoice-firebase`
   (Dashboard → Suspend Service). Keep them suspended 30 days;
   delete only after Han confirms the month is clean.
2. Firebase project `theingabar-invoice`: leave the project (rules
   stay as-is); do NOT delete the Firestore data until the 30-day
   window passes.
3. The old PWA copies keep working under `/legacy/` on the new
   backend until cutover; remove the `public/` legacy copy in a
   later cleanup deploy (after the 30 days).
4. **X-API-Key leaves the system at cutover** (per Han's decision):
   after §4c, the legacy `/api/*` endpoints (key-authenticated)
   are disabled in a follow-up deploy — the new app needs no API
   key (sessions only). Until that deploy, `/api/*` keeps working
   with the existing key; do not publish the key anywhere.
5. Tell every mechanic the single URL:
   `https://theingabar-invoice-web.onrender.com` — log in again on
   every open (Q7: no persistent session).

## 6. Rollback

- The old sites are suspended, not deleted: Resume Service in
  Render restores them in ~1 min. Firestore data is untouched.
- Postgres is shared, so there is no "old DB" to roll back to —
  the rollback copy is the §2 CSV/`pg_dump` taken before migration.
  Any invoice created on the new backend during the cutover window
  keeps its number; do NOT re-import after a rollback without
  re-running the §4c count check.

## 7. Known risks / open items at cutover

- **PNG on Render (PdfiumRaster native lib):** verified locally
  (Linux x64 `libpdfium.so` ships in the publish output and links
  only against libc/pthread/libm). If `/invoice/1.png` 503s on
  Render, the PDF endpoint is unaffected and the fallback is
  Playwright + headless Chromium (rejected locally for the free
  tier's 512 MB — see `PNG-ROUTE.md` §4; needs a paid plan).
- **Google OAuth live flow:** implemented (server-side code flow,
  allowlist hanmintheinn@gmail.com / majorhmthein@gmail.com,
  `email_verified` check, callback
  `/auth/google/signin-google`) but never run against a real
  Google account. First Admin sign-in is step §3.5.
- **Live Postgres 18:** the new code was verified against local
  Postgres 16 only. Same SQL, same Npgsql — expected to work;
  verify via `/api/health` (`store: postgres`) in §3.5.
- **First Render Docker build with the new files:** `Pages/`,
  `Services/`, `wwwroot/fonts/ttf/`, PdfiumRaster + SkiaSharp
  native runtimes all publish from the Dockerfile as written;
  confirm in the build log.
- **Render free Postgres expires 90 days from 2026-11-20** — export
  CSV / upgrade before expiry (standing warning).
- **DataProtection keys** persist under `./data/protection-keys`
  unencrypted (no certificate configured). Acceptable for this app;
  note it if a security review ever asks.
- **QR fallback wrinkle (pre-existing design):** a mechanic with no
  own QR uploaded gets the SHOP QR on their invoices while the
  name/number printed are theirs. Mechanics should upload their own
  QR on the User page.

## 8. Dry-run (what was / was not dry-run locally)

- Done 2026-11-20 (local Postgres 16, port 5000): full smoke suite
  49/49 — signup → pending → approval → invoice create/numbering/
  preview/PDF/PNG/print page/CSV export+import/week-month totals/
  admin approve/metadata/rate-limit/CSRF/unauthenticated redirects/
  logout/suspended refusal; legacy `/api/*` still works.
- NOT dry-run (needs live services): Firestore export/import
  against real Firestore data; Google OAuth against real Google
  accounts; Render Docker build; live Postgres 18; phone checks
  (install/offline, Myanmar glyphs, QR scan, button heights).
