/* Theingabar Invoice — cache-only service worker (HTML-only carve-out).
   No app logic lives here: it only caches static assets and already
   visited pages so the app shell stays readable on weak signal.
   Offline WRITES are impossible without app JavaScript: any form submit
   made offline fails in the browser, exactly as a plain website would.
   Cache name is bumped when the asset list changes. */
const CACHE = 'theingabar-html-v15';
const STATIC_ASSETS = [
  '/css/fonts.css',
  '/css/tailwind.css',
  '/css/invoice.css',
  '/css/app2.css',
  '/css/export.css',
  '/css/cropper.css',
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
  );
});

self.addEventListener('fetch', (event) => {
  if (event.request.method !== 'GET') return;
  const url = new URL(event.request.url);
  // Never cache API responses, exports, auth flows, or per-user images
  // (QR codes and avatars change on re-upload — caching them served
  // the old image after a change).
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/auth/') ||
      url.pathname.startsWith('/qr/') || url.pathname.startsWith('/avatar/')) return;
  if (url.pathname.endsWith('.pdf') || url.pathname.endsWith('.png')) return;
  const isStatic = url.pathname.startsWith('/css/') || url.pathname.startsWith('/fonts/') ||
    url.pathname.startsWith('/js/') || url.pathname === '/manifest.webmanifest' ||
    url.pathname === '/seed-qr.jpg';
  event.respondWith(
    isStatic
      ? caches.match(event.request, { ignoreSearch: true }).then((cached) =>
          cached || fetch(event.request).then((res) => {
            const copy = res.clone();
            caches.open(CACHE).then((cache) => cache.put(event.request, copy));
            return res;
          }))
      : fetch(event.request).then((res) => {
          if (res.ok) {
            const copy = res.clone();
            caches.open(CACHE).then((cache) => cache.put(event.request, copy));
          }
          return res;
        }).catch(() => caches.match(event.request, { ignoreSearch: true }))
  );
});
