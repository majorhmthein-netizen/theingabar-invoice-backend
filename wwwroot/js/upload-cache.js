/* All user uploads load from the phone (front end), not the server
 * (Han 2026-11-21: profile photos, QR codes — all user uploads —
 * cached, so they load fast). Any <img data-cache="KEY"> shows the
 * picture saved in localStorage under KEY. If nothing is stored yet,
 * the image loads from the server once and is then saved locally, so
 * the next view is instant. cropper.js saves a freshly picked photo
 * under the same key at upload time, so a new picture is instant too.
 *
 * Keys are per upload, so one user's picture never shows on another
 * user's page: tgb-avatar-{userId}, tgb-qr-{userId}, tgb-qr-meta.
 * Logging out clears every tgb-* key, so a different user on the same
 * phone never sees the previous user's uploads.
 *
 * The server copies stay as the backup: they are what a new phone (or
 * a cleared browser) loads from, and what the upload forms write to.
 * The invoice PDF/PNG are rendered server-side from the stored bytes,
 * so the printed QR is unaffected by this front-end cache. */
(function () {
  'use strict';

  function fromLocal(img, key) {
    try {
      var data = localStorage.getItem(key);
      if (data) { img.src = data; return true; }
    } catch (e) { /* private mode — fall through to the server */ }
    return false;
  }

  function saveLocal(img, key) {
    try {
      if (localStorage.getItem(key)) return; // already stored
      var w = img.naturalWidth, h = img.naturalHeight;
      if (!w || !h) return;
      // Cap the stored copy at 512px on the long side: plenty for a
      // QR (which must stay scannable) and an avatar, and it bounds
      // localStorage. The stored copy is only ever shown on screen.
      var scale = Math.min(1, 512 / Math.max(w, h));
      var canvas = document.createElement('canvas');
      canvas.width = Math.max(1, Math.round(w * scale));
      canvas.height = Math.max(1, Math.round(h * scale));
      canvas.getContext('2d').drawImage(img, 0, 0, canvas.width, canvas.height);
      localStorage.setItem(key, canvas.toDataURL('image/png'));
    } catch (e) { /* storage full or private mode — the server copy still works */ }
  }

  document.addEventListener('DOMContentLoaded', function () {
    if (document.body && document.body.getAttribute('data-auth') === 'logged-out') {
      try {
        Object.keys(localStorage).forEach(function (k) {
          if (k.indexOf('tgb-') === 0) localStorage.removeItem(k);
        });
      } catch (e) {}
      return;
    }
    document.querySelectorAll('img[data-cache]').forEach(function (img) {
      var key = img.getAttribute('data-cache');
      if (!key) return;
      if (fromLocal(img, key)) return;
      if (img.complete && img.naturalWidth > 0) { saveLocal(img, key); return; }
      img.addEventListener('load', function () { saveLocal(img, key); });
    });
  });
})();
