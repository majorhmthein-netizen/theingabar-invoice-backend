/* Square cropper for QR codes and profile pictures (Theingabar Invoice).
 *
 * No libraries (CSP is script-src 'self'; no CDN). One cropper block per
 * upload form:
 *
 *   <input type="file" id="pic-file" name="PicFile" accept="image/...">
 *   <div class="cropper" data-input="pic-file" data-thumb="pic-thumb" hidden>
 *     <div class="crop-stage">
 *       <img class="crop-img" alt="">
 *       <div class="crop-sel"><div class="crop-handle"></div></div>
 *     </div>
 *     <div class="row">
 *       <button type="button" class="btn" data-crop-use>Use this crop</button>
 *       <button type="button" class="btn ghost" data-crop-cancel>Cancel</button>
 *     </div>
 *   </div>
 *   <img id="pic-thumb" ...>
 *
 * Pick a file -> the cropper opens with a centred square. Drag the square
 * to move it, drag the gold corner to resize it. "Use this crop" draws the
 * square to a 512x512 canvas and replaces the file input's file with the
 * cropped PNG, so the existing form POST needs no server change. "Cancel"
 * clears the pick and keeps the previous image.
 */
(function () {
  'use strict';

  var OUT = 512; // cropped output, square, px

  function setup(box) {
    var input = document.getElementById(box.getAttribute('data-input'));
    var thumb = document.getElementById(box.getAttribute('data-thumb'));
    if (!input || !thumb) return;
    var stage = box.querySelector('.crop-stage');
    var img = box.querySelector('.crop-img');
    var sel = box.querySelector('.crop-sel');
    var handle = box.querySelector('.crop-handle');
    var useBtn = box.querySelector('[data-crop-use]');
    var cancelBtn = box.querySelector('[data-crop-cancel]');
    if (!stage || !img || !sel || !handle || !useBtn || !cancelBtn) return;

    // Square position/size in displayed px.
    var sq = { x: 0, y: 0, s: 0 };

    function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

    function drawSel() {
      sel.style.left = sq.x + 'px';
      sel.style.top = sq.y + 'px';
      sel.style.width = sq.s + 'px';
      sel.style.height = sq.s + 'px';
    }

    function open(url) {
      img.onload = function () {
        var iw = img.clientWidth, ih = img.clientHeight;
        if (!iw || !ih) return;
        sq.s = Math.floor(Math.min(iw, ih) * 0.8);
        sq.x = Math.floor((iw - sq.s) / 2);
        sq.y = Math.floor((ih - sq.s) / 2);
        drawSel();
      };
      img.src = url;
      box.hidden = false;
    }

    input.addEventListener('change', function () {
      var f = input.files && input.files[0];
      if (!f) return;
      var reader = new FileReader();
      reader.onload = function () { open(reader.result); };
      reader.readAsDataURL(f);
    });

    // Drag the square to move it; drag the handle to resize it.
    function drag(target, onMove) {
      target.addEventListener('pointerdown', function (ev) {
        ev.preventDefault();
        var sx = ev.clientX, sy = ev.clientY;
        var start = { x: sq.x, y: sq.y, s: sq.s };
        var iw = img.clientWidth, ih = img.clientHeight;
        function move(e) {
          onMove(e.clientX - sx, e.clientY - sy, start, iw, ih);
          drawSel();
        }
        function up() {
          window.removeEventListener('pointermove', move);
          window.removeEventListener('pointerup', up);
        }
        window.addEventListener('pointermove', move);
        window.addEventListener('pointerup', up);
      });
    }
    drag(sel, function (dx, dy, start, iw, ih) {
      sq.x = clamp(start.x + dx, 0, iw - sq.s);
      sq.y = clamp(start.y + dy, 0, ih - sq.s);
    });
    drag(handle, function (dx, dy, start, iw, ih) {
      var s = clamp(start.s + (dx + dy) / 2, 48, Math.min(iw - start.x, ih - start.y));
      sq.s = Math.floor(s);
    });

    useBtn.addEventListener('click', function () {
      var scale = img.naturalWidth / img.clientWidth;
      if (!scale || !isFinite(scale)) return;
      var canvas = document.createElement('canvas');
      canvas.width = OUT; canvas.height = OUT;
      var ctx = canvas.getContext('2d');
      ctx.drawImage(img, sq.x * scale, sq.y * scale, sq.s * scale, sq.s * scale, 0, 0, OUT, OUT);
      canvas.toBlob(function (blob) {
        if (!blob) return;
        var dt = new DataTransfer();
        dt.items.add(new File([blob], 'cropped.png', { type: 'image/png' }));
        input.files = dt.files;
        thumb.hidden = false;
        thumb.src = URL.createObjectURL(blob);
        box.hidden = true;
      }, 'image/png');
    });

    cancelBtn.addEventListener('click', function () {
      input.value = '';
      box.hidden = true;
    });
  }

  document.querySelectorAll('.cropper').forEach(setup);
})();
