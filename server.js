/* Theingabar Invoice — backend (Render Web Service).
 *
 * Node built-in http server, one dependency only: `pg` (PostgreSQL client).
 * - Serves the PWA frontend statically from ./public/
 * - REST API under /api/invoices, backed by PostgreSQL when DATABASE_URL
 *   is set, otherwise by a local JSON file ./data/invoices.json
 * - Optional API-key auth: if INVOICE_API_KEY is set, every /api/* request
 *   except /api/health must send header X-API-Key. The key is never logged.
 *
 * Env: PORT (Render sets it; default 10000), DATABASE_URL, INVOICE_API_KEY,
 * NODE_ENV.
 */
"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const PORT = Number(process.env.PORT || 10000);
const PUBLIC_DIR = path.join(__dirname, "public");
const DATA_DIR = path.join(__dirname, "data");
const DATA_FILE = path.join(DATA_DIR, "invoices.json");
const API_KEY = process.env.INVOICE_API_KEY || "";
const MAX_ITEMS = 20;

let Pool = null;
try {
  Pool = require("pg").Pool;
} catch (e) {
  console.log("[backend] pg module not available, JSON store only:", e.message);
}

/* ------------------------------------------------------------------ */
/* Validation                                                          */
/* ------------------------------------------------------------------ */

function validateInvoice(body) {
  if (!body || typeof body !== "object" || Array.isArray(body)) {
    return "body must be a JSON object";
  }
  if (typeof body.date !== "string" || body.date.trim() === "") {
    return "date is required";
  }
  if (typeof body.customer !== "string" || body.customer.trim() === "") {
    return "customer is required";
  }
  if (!Array.isArray(body.items) || body.items.length === 0) {
    return "items must be a non-empty array";
  }
  if (body.items.length > MAX_ITEMS) {
    return "at most " + MAX_ITEMS + " items";
  }
  let sum = 0;
  for (const it of body.items) {
    if (!it || typeof it !== "object") return "each item must be an object";
    if (typeof it.desc !== "string") return "each item needs a desc string";
    if (!Number.isInteger(it.amount) || it.amount < 0) {
      return "each item amount must be an integer >= 0";
    }
    sum += it.amount;
  }
  if (!Number.isInteger(body.total) || body.total < 0) {
    return "total must be an integer >= 0";
  }
  if (body.total !== sum) {
    return "total (" + body.total + ") does not equal sum of items (" + sum + ")";
  }
  if (body.uid !== undefined && (typeof body.uid !== "string" || body.uid.length > 128)) {
    return "uid must be a short string";
  }
  if (body.no !== undefined && body.no !== null && (typeof body.no !== "string" || !/^\d{8}-\d{3}$/.test(body.no))) {
    return "no must look like YYYYMMDD-NNN";
  }
  if (body.userId !== undefined && body.userId !== null && (typeof body.userId !== "string" || body.userId.length > 128)) {
    return "userId must be a short string";
  }
  if (body.createdAt !== undefined && body.createdAt !== null && (!Number.isInteger(body.createdAt) || body.createdAt < 0)) {
    return "createdAt must be a non-negative integer (ms)";
  }
  return null;
}

function cleanInvoice(body) {
  return {
    uid: typeof body.uid === "string" && body.uid ? body.uid : crypto.randomUUID(),
    no: typeof body.no === "string" && body.no ? body.no : null,
    userId: typeof body.userId === "string" && body.userId ? body.userId : null,
    createdAt: Number.isInteger(body.createdAt) && body.createdAt >= 0 ? body.createdAt : null,
    date: body.date.trim(),
    customer: body.customer.trim(),
    items: body.items.map((it) => ({ desc: it.desc, amount: it.amount })),
    total: body.total,
  };
}

/* ------------------------------------------------------------------ */
/* Stores: PostgreSQL, or local JSON file as fallback                   */
/* ------------------------------------------------------------------ */

let pool = null;
let storeName = "json";

async function initPostgres() {
  if (!process.env.DATABASE_URL || !Pool) return false;
  pool = new Pool({
    connectionString: process.env.DATABASE_URL,
    // Render Postgres needs SSL; sslmode in the URL is honoured by pg,
    // but force it on when the URL does not say otherwise.
    ssl: process.env.DATABASE_URL.includes("sslmode=") ? undefined : { rejectUnauthorized: false },
    max: 5,
  });
  try {
    await pool.query(
      `CREATE TABLE IF NOT EXISTS invoices (
        id SERIAL PRIMARY KEY,
        uid TEXT UNIQUE NOT NULL,
        no TEXT,
        user_id TEXT,
        client_created_at BIGINT,
        date TEXT NOT NULL,
        customer TEXT NOT NULL,
        items JSONB NOT NULL,
        total INTEGER NOT NULL,
        created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
        updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
      )`
    );
    // Migrate a DB created before the no/userId/createdAt columns existed
    // (the live Render DB was created at first deploy without them).
    await pool.query("ALTER TABLE invoices ADD COLUMN IF NOT EXISTS no TEXT");
    await pool.query("ALTER TABLE invoices ADD COLUMN IF NOT EXISTS user_id TEXT");
    await pool.query("ALTER TABLE invoices ADD COLUMN IF NOT EXISTS client_created_at BIGINT");
    // Invoice numbers are global + sequential: two invoices must never
    // share one. The client retries once with a fresh number on 409.
    await pool.query(
      "CREATE UNIQUE INDEX IF NOT EXISTS invoices_no_unique ON invoices(no) WHERE no IS NOT NULL"
    );
    // One-time data fix (v2, 2026-11-20): the Nissan Safari seed invoice
    // was first stored as 20261119-001 and was renumbered to 20261119-002
    // on the devices; make the server row agree so the server list and
    // CSV export show 002 too. Idempotent (a second run matches no rows)
    // and restricted to the seed row only (customer + date + total — the
    // same fields the device-side patch matches on). Skipped entirely if
    // a 20261119-002 row already exists, so the unique index above can
    // never be violated by this rename. A failure here is only logged —
    // it must not take the Postgres store down.
    try {
      const ren = await pool.query(
        "UPDATE invoices SET no = '20261119-002' " +
          "WHERE no = '20261119-001' AND customer = 'Nissan Safari' " +
          "AND date = '2026-11-19' AND total = 300000 " +
          "AND NOT EXISTS (SELECT 1 FROM invoices WHERE no = '20261119-002')"
      );
      if (ren.rowCount > 0) {
        console.log("[backend] renamed seed invoice 20261119-001 -> 20261119-002");
      }
    } catch (e) {
      console.error("[backend] seed invoice renumber skipped:", e.message);
    }
    storeName = "postgres";
    console.log("[backend] store: postgres (table invoices ready)");
    return true;
  } catch (e) {
    console.error("[backend] postgres init failed, falling back to JSON file:", e.message);
    try { await pool.end(); } catch (_) { /* ignore */ }
    pool = null;
    return false;
  }
}

function rowToInvoice(r) {
  return {
    id: r.id,
    uid: r.uid,
    no: r.no || null,
    userId: r.user_id || r.userId || null,
    createdAt:
      r.client_created_at == null
        ? r.createdAt == null
          ? null
          : Number(r.createdAt)
        : Number(r.client_created_at),
    date: r.date,
    customer: r.customer,
    items: r.items,
    total: r.total,
    created_at: r.created_at,
    updated_at: r.updated_at,
  };
}

/* --- JSON file store --- */
function jsonRead() {
  try {
    const raw = fs.readFileSync(DATA_FILE, "utf8");
    const parsed = JSON.parse(raw);
    if (parsed && Array.isArray(parsed.invoices)) return parsed;
  } catch (e) {
    if (e.code !== "ENOENT") console.error("[backend] could not read", DATA_FILE, e.message);
  }
  return { nextId: 1, invoices: [] };
}

function jsonWrite(doc) {
  fs.mkdirSync(DATA_DIR, { recursive: true });
  const tmp = DATA_FILE + ".tmp";
  fs.writeFileSync(tmp, JSON.stringify(doc, null, 2), "utf8");
  fs.renameSync(tmp, DATA_FILE); // atomic replace
}

/* --- store operations (same shape for both stores) --- */
async function listInvoices() {
  if (pool) {
    const res = await pool.query(
      "SELECT id, uid, no, user_id, client_created_at, date, customer, items, total, created_at, updated_at FROM invoices ORDER BY created_at DESC, id DESC"
    );
    return res.rows.map(rowToInvoice);
  }
  const doc = jsonRead();
  return doc.invoices
    .slice()
    .sort((a, b) => (b.created_at || "").localeCompare(a.created_at || "") || b.id - a.id)
    .map((r) => rowToInvoice(r));
}

async function createInvoice(inv) {
  if (pool) {
    try {
      const res = await pool.query(
        "INSERT INTO invoices (uid, no, user_id, client_created_at, date, customer, items, total) VALUES ($1,$2,$3,$4,$5,$6,$7,$8) " +
          "RETURNING id, uid, no, user_id, client_created_at, date, customer, items, total, created_at, updated_at",
        [inv.uid, inv.no, inv.userId, inv.createdAt, inv.date, inv.customer, JSON.stringify(inv.items), inv.total]
      );
      return rowToInvoice(res.rows[0]);
    } catch (e) {
      if (e.code === "23505") {
        if (e.constraint === "invoices_no_unique") {
          // Another phone already used this invoice number: the client
          // re-numbers once and retries (see syncSaveToServer).
          const dup = new Error("invoice number already exists");
          dup.code = "DUPLICATE_NO";
          throw dup;
        }
        // uid already exists (e.g. re-seed after a retry): return the existing row
        const res = await pool.query(
          "SELECT id, uid, no, user_id, client_created_at, date, customer, items, total, created_at, updated_at FROM invoices WHERE uid = $1",
          [inv.uid]
        );
        if (res.rows.length) return rowToInvoice(res.rows[0]);
      }
      throw e;
    }
  }
  const doc = jsonRead();
  const existing = doc.invoices.find((r) => r.uid === inv.uid);
  if (existing) return rowToInvoice(existing);
  if (inv.no) {
    const dupNo = doc.invoices.find((r) => r.no === inv.no);
    if (dupNo) {
      const err = new Error("invoice number already exists");
      err.code = "DUPLICATE_NO";
      throw err;
    }
  }
  const now = new Date().toISOString();
  const row = {
    id: doc.nextId++,
    uid: inv.uid,
    no: inv.no,
    user_id: inv.userId,
    client_created_at: inv.createdAt,
    date: inv.date,
    customer: inv.customer,
    items: inv.items,
    total: inv.total,
    created_at: now,
    updated_at: now,
  };
  doc.invoices.push(row);
  jsonWrite(doc);
  return rowToInvoice(row);
}

async function updateInvoice(id, inv) {
  if (pool) {
    try {
      const res = await pool.query(
        "UPDATE invoices SET no=COALESCE($2, no), user_id=COALESCE($3, user_id), " +
          "client_created_at=COALESCE($4, client_created_at), date=$5, customer=$6, items=$7, total=$8, updated_at=now() WHERE id=$1 " +
          "RETURNING id, uid, no, user_id, client_created_at, date, customer, items, total, created_at, updated_at",
        [id, inv.no, inv.userId, inv.createdAt, inv.date, inv.customer, JSON.stringify(inv.items), inv.total]
      );
      return res.rows.length ? rowToInvoice(res.rows[0]) : null;
    } catch (e) {
      if (e.code === "23505" && e.constraint === "invoices_no_unique") {
        const dup = new Error("invoice number already exists");
        dup.code = "DUPLICATE_NO";
        throw dup;
      }
      throw e;
    }
  }
  const doc = jsonRead();
  const row = doc.invoices.find((r) => r.id === id);
  if (!row) return null;
  if (inv.no) {
    const dupNo = doc.invoices.find((r) => r.id !== id && r.no === inv.no);
    if (dupNo) {
      const err = new Error("invoice number already exists");
      err.code = "DUPLICATE_NO";
      throw err;
    }
    row.no = inv.no;
  }
  if (inv.userId != null) row.user_id = inv.userId;
  if (inv.createdAt != null) row.client_created_at = inv.createdAt;
  row.date = inv.date;
  row.customer = inv.customer;
  row.items = inv.items;
  row.total = inv.total;
  row.updated_at = new Date().toISOString();
  jsonWrite(doc);
  return rowToInvoice(row);
}

async function deleteInvoice(id) {
  if (pool) {
    const res = await pool.query("DELETE FROM invoices WHERE id=$1", [id]);
    return res.rowCount > 0;
  }
  const doc = jsonRead();
  const before = doc.invoices.length;
  doc.invoices = doc.invoices.filter((r) => r.id !== id);
  if (doc.invoices.length === before) return false;
  jsonWrite(doc);
  return true;
}

/* ------------------------------------------------------------------ */
/* CSV export (same columns as the PWA log export)                     */
/* ------------------------------------------------------------------ */

function fmtDate(iso) {
  if (!iso) return "";
  const p = String(iso).split("-");
  return p.length === 3 ? p[2] + ". " + p[1] + ". " + p[0] : iso;
}

function csvCell(v) {
  v = String(v == null ? "" : v);
  return /[",\n]/.test(v) ? '"' + v.replace(/"/g, '""') + '"' : v;
}

function invoicesToCsv(list) {
  const head = ["Date", "Customer"];
  for (let i = 1; i <= MAX_ITEMS; i++) head.push("Item " + i, "Item " + i + " Amount");
  head.push("Items Sum", "Invoice Total", "Check", "No.");
  const lines = [head.join(",")];
  for (const e of list) {
    const row = [fmtDate(e.date), e.customer];
    let sum = 0;
    for (let i = 0; i < MAX_ITEMS; i++) {
      const it = e.items[i];
      row.push(it ? it.desc : "", it ? it.amount : "");
      if (it) sum += Number(it.amount) || 0;
    }
    row.push(sum, e.total, sum === e.total ? "OK" : "CHECK", e.no || "");
    lines.push(row.map(csvCell).join(","));
  }
  return "\ufeff" + lines.join("\r\n") + "\r\n";
}

/* ------------------------------------------------------------------ */
/* HTTP helpers                                                        */
/* ------------------------------------------------------------------ */

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".webmanifest": "application/manifest+json; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".svg": "image/svg+xml",
  ".css": "text/css; charset=utf-8",
  ".ico": "image/x-icon",
};

function sendJson(res, status, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8" });
  res.end(body);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on("data", (c) => {
      size += c.length;
      if (size > 1024 * 1024) {
        reject(new Error("body too large"));
        req.destroy();
        return;
      }
      chunks.push(c);
    });
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    req.on("error", reject);
  });
}

function checkApiKey(req, res) {
  if (!API_KEY) return true; // dev mode: no key configured
  if (req.headers["x-api-key"] === API_KEY) return true;
  sendJson(res, 401, { error: "unauthorized" });
  return false;
}

/* ------------------------------------------------------------------ */
/* Server                                                              */
/* ------------------------------------------------------------------ */

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, "http://localhost");
    const pathname = decodeURIComponent(url.pathname);

    /* --- API --- */
    if (pathname === "/api/health") {
      sendJson(res, 200, { ok: true, store: storeName, time: new Date().toISOString() });
      return;
    }
    if (pathname === "/api/invoices" || pathname.startsWith("/api/invoices/")) {
      if (!checkApiKey(req, res)) return;

      if (pathname === "/api/invoices" && req.method === "GET") {
        sendJson(res, 200, await listInvoices());
        return;
      }
      if (pathname === "/api/invoices" && req.method === "POST") {
        let body;
        try {
          body = JSON.parse(await readBody(req));
        } catch (_) {
          sendJson(res, 400, { error: "invalid JSON" });
          return;
        }
        const err = validateInvoice(body);
        if (err) {
          sendJson(res, 400, { error: err });
          return;
        }
        let created;
        try {
          created = await createInvoice(cleanInvoice(body));
        } catch (e) {
          if (e && e.code === "DUPLICATE_NO") {
            sendJson(res, 409, { error: "invoice number already exists" });
            return;
          }
          throw e;
        }
        sendJson(res, 201, created);
        return;
      }
      const m = pathname.match(/^\/api\/invoices\/(\d+)$/);
      if (m) {
        const id = Number(m[1]);
        if (req.method === "PUT") {
          let body;
          try {
            body = JSON.parse(await readBody(req));
          } catch (_) {
            sendJson(res, 400, { error: "invalid JSON" });
            return;
          }
          const err = validateInvoice(body);
          if (err) {
            sendJson(res, 400, { error: err });
            return;
          }
          let updated;
          try {
            updated = await updateInvoice(id, cleanInvoice(body));
          } catch (e) {
            if (e && e.code === "DUPLICATE_NO") {
              sendJson(res, 409, { error: "invoice number already exists" });
              return;
            }
            throw e;
          }
          if (!updated) {
            sendJson(res, 404, { error: "invoice not found" });
            return;
          }
          sendJson(res, 200, updated);
          return;
        }
        if (req.method === "DELETE") {
          if (!(await deleteInvoice(id))) {
            sendJson(res, 404, { error: "invoice not found" });
            return;
          }
          sendJson(res, 200, { ok: true });
          return;
        }
      }
      sendJson(res, 404, { error: "not found" });
      return;
    }
    if (pathname === "/api/export.csv") {
      if (!checkApiKey(req, res)) return;
      if (req.method !== "GET") {
        sendJson(res, 405, { error: "method not allowed" });
        return;
      }
      const csv = invoicesToCsv(await listInvoices());
      res.writeHead(200, {
        "Content-Type": "text/csv; charset=utf-8",
        "Content-Disposition": 'attachment; filename="theingabar-invoice-log.csv"',
      });
      res.end(csv);
      return;
    }
    if (pathname.startsWith("/api/")) {
      sendJson(res, 404, { error: "not found" });
      return;
    }

    /* --- Static frontend --- */
    if (req.method !== "GET" && req.method !== "HEAD") {
      sendJson(res, 405, { error: "method not allowed" });
      return;
    }
    let rel = pathname === "/" ? "index.html" : pathname.replace(/^\/+/, "");
    const filePath = path.normalize(path.join(PUBLIC_DIR, rel));
    if (!filePath.startsWith(PUBLIC_DIR)) {
      sendJson(res, 403, { error: "forbidden" });
      return;
    }
    if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
      sendJson(res, 404, { error: "not found" });
      return;
    }
    const ext = path.extname(filePath).toLowerCase();
    res.writeHead(200, {
      "Content-Type": MIME[ext] || "application/octet-stream",
      "Cache-Control": ext === ".html" ? "no-cache" : "public, max-age=3600",
    });
    if (req.method === "HEAD") {
      res.end();
      return;
    }
    fs.createReadStream(filePath).pipe(res);
  } catch (e) {
    console.error("[backend] request failed:", e.message);
    if (!res.headersSent) sendJson(res, 500, { error: "internal error" });
    else res.end();
  }
});

initPostgres()
  .then(() => {
    if (!process.env.DATABASE_URL) {
      console.log("[backend] store: local JSON file ./data/invoices.json (no DATABASE_URL)");
    }
    if (!API_KEY) {
      console.log("[backend] WARNING: INVOICE_API_KEY is not set — API is open (dev mode). Set it on Render.");
    } else {
      console.log("[backend] API key auth enabled for /api/* (except /api/health)");
    }
    server.listen(PORT, () => {
      console.log("[backend] listening on port " + PORT + " (store: " + storeName + ")");
    });
  })
  .catch((e) => {
    console.error("[backend] failed to start:", e);
    process.exit(1);
  });
