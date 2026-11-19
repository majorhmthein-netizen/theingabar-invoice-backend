# Theingabar Invoice — Backend (Render Web Service)

Web Service backend for the Theingabar Invoice PWA. The same Node server
serves the PWA frontend (from `public/`) and stores invoices on the server,
so the invoice log is shared across phones — no longer phone-only.

- **Server:** Node 20, built-in `http` only. One dependency: `pg`
  (PostgreSQL client). No Express.
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
| GET | `/api/export.csv` | Same columns as the PWA log export (Item 1–20 amounts, items sum, total, check) |

Validation (all stores): `date` and `customer` required, items max 20,
each amount an integer ≥ 0, `total` must equal the sum of item amounts.

## Deploy to Render (via GitHub + Blueprint)

1. Create a GitHub repo, e.g. **`theingabar-invoice-backend`**, branch
   `main`, and upload all files in this folder (including `public/`).
2. Render Dashboard → **New → Blueprint** → pick that repo. Render
   creates two resources from `render.yaml`:
   - PostgreSQL **`theingabar-invoice-db`** (free, Postgres 16)
   - Web Service **`theingabar-invoice-web`** (Node, free,
     build `npm install`, start `node server.js`, health check
     `/api/health`)
3. When it is Live, open the Web Service URL
   (e.g. `https://theingabar-invoice-web.onrender.com`).
4. **First open — enter the server key:** Render Dashboard → the Web
   Service → **Environment** → copy the generated `INVOICE_API_KEY`
   value → in the app, open **Shop Metadata → Server key**, paste it,
   save. The app then syncs; the status line in the Invoice Log should
   read **Server: Synced**.
5. **Seeding:** on the first successful sync, if the server log is empty
   and the phone's local log is not, the app posts each local invoice to
   the server — so the Nissan Safari invoice (19.11.2026, 120,000 +
   50,000 + 130,000 = 300,000 MMK) uploads itself. No manual seed step.

Manual alternative (no Blueprint): Render → **New → PostgreSQL**, then
**New → Web Service** (Node, same build/start commands), set
`DATABASE_URL` from the database and `INVOICE_API_KEY` to a long random
value yourself.

## ⚠️ Free PostgreSQL expires after 90 days

Render's free PostgreSQL instance **expires after 90 days** and its data
is deleted. Before expiry, either:
- **Export CSV regularly** (Invoice Log → Export CSV, or
  `GET /api/export.csv`) as a backup, or
- **Upgrade the database to a paid plan** in Render (Dashboard →
  the database → Change plan) to keep the data.

## Local development

```sh
npm install
node server.js          # no DATABASE_URL -> JSON file store, open API
# then: http://localhost:10000/  (or $PORT)
```

With Postgres locally, set `DATABASE_URL` (see `.env.example`).

## Caveats

- **Free Web Service sleeps:** Render's free Web Service spins down
  after inactivity; the first open after a pause takes ~30–60 s to wake.
  The app shows "Offline" until then and retries on the next open.
- **PIN reset erases the phone only.** "Forgot PIN? Erase all app data"
  clears the phone's IndexedDB; the server copy stays, and the next
  sync will bring those invoices back. To wipe the server log too,
  delete the rows (or the database) in Render.
- **Offline deletes:** an invoice deleted while offline is removed from
  the phone, but its server copy is only deleted when the phone is back
  online at delete time — otherwise it reappears on the next sync.
- The live Static Site `theingabar-invoice` is a separate service and is
  untouched by this repo. After this deploys, use the Web Service URL
  as the app URL (it serves the same PWA, plus the API).
