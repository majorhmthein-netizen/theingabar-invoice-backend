# Theingabar Invoice — Backend (Render Web Service, C# / ASP.NET Core 8)

Web Service backend for the Theingabar Invoice PWA. The same C# server
serves the PWA frontend (from `public/`) and stores invoices on the server,
so the invoice log is shared across phones — no longer phone-only.

This is a faithful port of the original Node server (`server.js`): same API,
same validation messages, same JSON shapes, same Postgres schema and
startup migrations, same JSON-file fallback store.

- **Server:** ASP.NET Core 8 minimal API (single file `Program.cs`).
  One dependency: `Npgsql` (PostgreSQL client). No other framework.
- **Store:** PostgreSQL when `DATABASE_URL` is set (table `invoices`,
  created on boot), otherwise a local JSON file `./data/invoices.json`
  (atomic write) so it still works locally.
- **Auth:** if `INVOICE_API_KEY` is set, every `/api/*` request except
  `/api/health` must send header `X-API-Key`. If it is not set, the API is
  open (dev mode) and the server logs a warning. The key is never logged.
- **Frontend sync:** on open, the PWA fetches `/api/invoices` — the server
  is the source of truth and the phone's IndexedDB is rewritten to match.
  Saving posts/puts to the server; deleting deletes from the server too.
  Offline, everything still works on the phone and syncs on the next open.
  A **Server** status line (Synced / Offline / Not configured) sits in the
  Invoice Log section, and a **Server key** button in Shop Metadata stores
  the key in `localStorage` only (never in IndexedDB, never shown again).
- **PIN lock:** unchanged, local only.

## API

| Method | Path | Notes |
|---|---|---|
| GET | `/api/health` | `{ok, store, time}` — no key needed |
| GET | `/api/invoices` | Newest first |
| POST | `/api/invoices` | Body `{date, customer, items:[{desc,amount}], total}`; `total` must equal the sum of items or the server rejects it (400); max 20 items |
| PUT | `/api/invoices/:id` | Same body; updates the server row |
| DELETE | `/api/invoices/:id` | |
| GET | `/api/export.csv` | Same columns as the PWA log export (Item 1–20 amounts, items sum, total, check, No.) |

Validation (all stores): `date` and `customer` required, items max 20,
each amount an integer ≥ 0, `total` must equal the sum of item amounts.
Invoice numbers are global + sequential (`YYYYMMDD-NNN`); a duplicate
number is rejected with 409 and the client re-numbers once and retries.
A duplicate `uid` returns the existing row (201).

## Deploy to Render — switching the existing Node service to Docker

Render has **no native .NET runtime**, so this service runs from the
`Dockerfile` (multi-stage: .NET 8 SDK build → ASP.NET Core 8 runtime,
non-root). A Blueprint **cannot** change an existing service's runtime
(Node → Docker) — Render rejects it. Switch the live service in the
Dashboard instead:

1. Push this folder to the GitHub repo
   **`theingabar-invoice-backend`**, branch `main` (replace the Node files;
   `public/` is unchanged).
2. Render Dashboard → Web Service **`theingabar-invoice-web`** → **Settings**
   → **Build** → **Source** → **Edit** → set **Runtime: Docker** (leave the
   repo/branch as-is) → **Deploy**. This keeps the service name, the URL
   (`https://theingabar-invoice-web.onrender.com`), all env vars and the
   `DATABASE_URL` link to the existing database `theingabar-invoice-db`.
   The existing `render.yaml` is for a fresh Blueprint (or reference) — it
   declares the same service/database names so a fresh apply reuses the
   existing database; verify in the Dashboard that no second database was
   created.
3. **Do NOT regenerate `INVOICE_API_KEY`.** The live key is already set on
   the service and every phone has it saved — rotating it breaks all saved
   keys. `render.yaml` declares it as `sync: false` for exactly this reason.
4. When it is Live, open the Web Service URL and check
   `/api/health` → `{"ok":true,"store":"postgres"}`. The first deploy runs
   the Postgres startup migrations (new columns, unique index on `no`,
   one-time 001 → 002 seed rename) — all idempotent.
5. **Seeding:** on the first successful sync, if the server log is empty
   and the phone's local log is not, the app posts each local invoice to
   the server — so the Nissan Safari invoice (19.11.2026, 120,000 +
   50,000 + 130,000 = 300,000 MMK, No. 20261119-002) uploads itself.
   No manual seed step.

Manual alternative (new service): Render → **New → Web Service** →
Language **Docker**, Dockerfile Path `./Dockerfile`, set `DATABASE_URL`
from the existing database and `INVOICE_API_KEY` to the **same** value as
the old service (copy it from Dashboard → old service → Environment).

## ⚠️ Free PostgreSQL expires after 90 days

Render's free PostgreSQL instance **expires after 90 days** and its data
is deleted. Before expiry, either:
- **Export CSV regularly** (Invoice Log → Export CSV, or
  `GET /api/export.csv`) as a backup, or
- **Upgrade the database to a paid plan** in Render (Dashboard →
  the database → Change plan) to keep the data.

## Local development

```sh
dotnet restore
dotnet run            # no DATABASE_URL -> JSON file store, open API
# then: http://localhost:10000/  (or $PORT)
# With a key:  INVOICE_API_KEY=testkey dotnet run
# With Postgres: DATABASE_URL=postgres://user:pass@host:5432/db dotnet run
```
