/* Theingabar Invoice — cache-only service worker (HTML-only carve-out).
   No app logic lives here: it caches the app shell (static assets +
   the Home page) and the pages the user visits, and refreshes those
   cached pages whenever the network answers, so the app keeps working
   offline with the last-loaded data (Han 2026-11-21).
   Offline WRITES are impossible without app JavaScript: any form submit
   made offline fails in the browser, exactly as a plain website would.
   Cache name is bumped when the asset list or the caching logic changes. */
const CACHE = 'theingabar-html-v30';
const STATIC_ASSETS = [
  '/',

  '/css/fonts.css',
  '/css/tailwind.css',
  '/css/invoice.css',
  '/css/app2.css',
  '/css/export.css',
  '/js/sw-register.js',
  '/js/nav.js',
  '/js/countdown.js',
  '/js/login-loading.js',
  '/js/export.js',
  '/js/cropper.js',
  '/fonts/noto-sans-myanmar-myanmar.woff2',
  '/fonts/noto-sans-myanmar-latin.woff2',
  '/fonts/archivo-latin.woff2',
  '/fonts/noto-serif-myanmar-700.woff2',
  '/fonts/noto-serif-myanmar-900.woff2',
  '/fonts/ttf/U-Moe-Handwritten.ttf',
  '/fonts/ttf/NotoSansMyanmar-Light.ttf',
  '/manifest.webmanifest',
  '/icon-192.png',
  '/icon-512.png',
  '/icon-maskable-512.png',
  '/apple-touch-icon.png',
  '/img/theingabar-logo-header.jpg',
  '/img/theingabar-logo-watermark.jpg',
  '/img/theingabar-wordmark.png',
  '/seed-qr.jpg'
];

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(CACHE).then((cache) => cache.addAll(STATIC_ASSETS)).then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
      .then(() => precacheAll())
  );
});

// Download ALL app data into the cache (Han 2026-11-21: "အားလုံး download
// app data cache" + "Offline work, update ရင် overwrite"): fetch the
// manifest (/api/cache-list — every invoice page, every month log, the
// user's avatar + QR) and store each URL. Runs when the worker activates
// (after each deploy) and whenever the app asks for a refresh. Every
// entry OVERWRITES the cached copy (cache.put replaces), so an update
// never leaves a stale page behind — the fresh version wins. A single
// failing URL is skipped, never aborts the rest.
function precacheAll() {
  return fetch('/api/cache-list', { credentials: 'same-origin' })
    .then((res) => (res && res.ok) ? res.json() : null)
    .then((data) => {
      if (!data || !Array.isArray(data.urls)) return;
      return caches.open(CACHE).then((cache) =>
        Promise.all(data.urls.map((u) =>
          fetch(u, { credentials: 'same-origin' }).then((res) => {
            if (res && res.ok) return cache.put(u, res);
          }).catch(() => {})
        ))
      );
    }).catch(() => {});
}

self.addEventListener('message', (event) => {
  if (event.data && event.data.type === 'precache') {
    event.waitUntil(precacheAll());
  }
});

// App pages (everything not static): the app's DATA lives in these
// server-rendered pages (Home, invoice pages, profile...), so they are
// cached on every successful visit and the cache is refreshed whenever
// the network answers — that is what makes the app work offline with
// reasonably fresh data (Han 2026-11-21). Two rules:
//   - If a cached copy exists, the network gets 3 s; a slow or dead
//     connection serves the cached page at once instead of hanging,
//     and a late network answer still refreshes the cache in the
//     background for the next visit.
//   - If no cached copy exists, wait for the network (a first visit
//     must not fail just because the connection is slow); if the
//     network fails too, fall back to the cached Home page rather
//     than the browser's offline error page.
function fetchWithTimeout(request, ms) {
  return new Promise(function (resolve, reject) {
    var done = false;
    var timer = setTimeout(function () {
      if (!done) { done = true; reject(new Error('timeout')); }
    }, ms);
    fetch(request).then(function (res) {
      if (!done) { done = true; clearTimeout(timer); resolve(res); }
      else if (res && res.ok) {
        // Late answer: refresh the cache for the next visit.
        var copy = res.clone();
        caches.open(CACHE).then(function (cache) { cache.put(request, copy); });
      }
    }, function (err) {
      if (!done) { done = true; clearTimeout(timer); reject(err); }
    });
  });
}

function putInCache(request, res) {
  if (!res || !res.ok) return;
  var copy = res.clone();
  caches.open(CACHE).then(function (cache) { cache.put(request, copy); });
}

self.addEventListener('fetch', (event) => {
  if (event.request.method !== 'GET') return;
  const url = new URL(event.request.url);
  // Never cache API responses, auth flows, or file exports.
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/auth/')) return;
  if (url.pathname.endsWith('.pdf') || url.pathname.endsWith('.png')) return;
  // Per-user images (avatar + QR): cache them so they load instantly
  // (Han 2026-11-21: photos loaded from the server on every visit).
  // Stale-while-revalidate: serve the cached copy at once, refresh it
  // in the background so a re-uploaded photo shows on the next view.
  // The uploading user sees the new photo immediately through the
  // localStorage cache (upload-cache.js / cropper.js), which wins over
  // this copy. (Earlier the SW skipped /qr/ + /avatar/ entirely, so a
  // re-upload never showed stale — that cost a server fetch every time.)
  if (url.pathname.startsWith('/qr/') || url.pathname.startsWith('/avatar/')) {
    event.respondWith(
      caches.match(event.request, { ignoreSearch: true }).then((cached) => {
        var network = fetch(event.request).then((res) => {
          putInCache(event.request, res);
          return res;
        }).catch(() => null);
        if (cached) { event.waitUntil(network); return cached; }
        return network;
      })
    );
    return;
  }
  const isStatic = url.pathname.startsWith('/css/') || url.pathname.startsWith('/fonts/') ||
    url.pathname.startsWith('/js/') || url.pathname.startsWith('/img/') ||
    url.pathname === '/manifest.webmanifest' ||
    url.pathname === '/seed-qr.jpg';
  if (isStatic) {
    event.respondWith(
      caches.match(event.request, { ignoreSearch: true }).then((cached) =>
        cached || fetch(event.request).then((res) => {
          putInCache(event.request, res);
          return res;
        }))
    );
    return;
  }
  // App pages: network-first, but never hang on a dead/slow connection
  // when a cached copy exists; fall back to cached Home when nothing
  // else is available.
  event.respondWith(
    caches.match(event.request, { ignoreSearch: true }).then((cached) => {
      if (cached) {
        return fetchWithTimeout(event.request, 3000).then((res) => {
          putInCache(event.request, res);
          return res;
        }).catch(() => cached);
      }
      return fetch(event.request).then((res) => {
        putInCache(event.request, res);
        return res;
      }).catch(() => caches.match('/', { ignoreSearch: true }));
    })
  );
});
