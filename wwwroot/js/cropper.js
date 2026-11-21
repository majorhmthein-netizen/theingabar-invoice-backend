/* Auto 1:1 crop for QR codes and profile pictures (Theingabar Invoice).
 *
 * No libraries (CSP is script-src 'self'; no CDN). No crop UI is shown
 * anywhere (Han 2026-11-21: don't show anything about cropping):
 * picking a photo centre-crops it to a square behind the scenes and
 * replaces the file input's file with the cropped PNG, so the existing
 * form POST needs no server change. The thumbnail updates to show the
 * result.
 *
 *   <input type="file" id="pic-file" name="PicFile" accept="image/..."
 *          data-out="128" data-thumb="pic-thumb">
 *   <img id="pic-thumb" ...>
 *
 * data-out = cropped output size in px (square). QR uses 512 (it must
 * stay scannable in the printed invoice); avatar uses 128 (shown at
 * 28px in the toolbar and 64px on the profile page). Default 512.
 * A photo that can't be read clears the pick silently.
 */
(function () {
  'use strict';

  function setup(input) {
    var thumb = document.getElementById(input.getAttribute('data-thumb'));
    if (!thumb) return;
    var out = parseInt(input.getAttribute('data-out') || '512', 10);
    if (!out || out <= 0) out = 512;

    input.addEventListener('change', function () {
      var f = input.files && input.files[0];
      if (!f) return;
      var objectUrl = URL.createObjectURL(f);
      var img = new Image();
      img.onload = function () {
        var s = Math.min(img.naturalWidth, img.naturalHeight);
        if (!s) { URL.revokeObjectURL(objectUrl); input.value = ''; return; }
        var sx = Math.floor((img.naturalWidth - s) / 2);
        var sy = Math.floor((img.naturalHeight - s) / 2);
        var canvas = document.createElement('canvas');
        canvas.width = out; canvas.height = out;
        var ctx = canvas.getContext('2d');
        ctx.drawImage(img, sx, sy, s, s, 0, 0, out, out);
        URL.revokeObjectURL(objectUrl);
        canvas.toBlob(function (blob) {
          if (!blob) { input.value = ''; return; }
          var dt = new DataTransfer();
          dt.items.add(new File([blob], 'cropped.png', { type: 'image/png' }));
          input.files = dt.files;
          thumb.hidden = false;
          thumb.src = URL.createObjectURL(blob);
        }, 'image/png');
      };
      img.onerror = function () {
        URL.revokeObjectURL(objectUrl);
        input.value = '';
      };
      img.src = objectUrl;
    });
  }

  document.querySelectorAll('input[type="file"][data-thumb]').forEach(setup);
})();
