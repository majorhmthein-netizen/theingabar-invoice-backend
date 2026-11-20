/* Theingabar Invoice — the ONLY JavaScript in the HTML-only app.
   Its single job is registering the cache-only service worker (/sw.js).
   It contains no app logic: no state, no forms, no fetching, no DOM
   changes. If Han ever revokes the service-worker carve-out, delete
   this file and its <script> tag in _Layout.cshtml — nothing else
   depends on it. */
if ('serviceWorker' in navigator) {
  window.addEventListener('load', function () {
    navigator.serviceWorker.register('/sw.js');
  });
}
