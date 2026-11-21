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
 *     <p class="hint crop-loading" hidden>Loading photo…</p>
 *     <p class="field-error crop-error" hidden>Couldn't read that photo — try another one.</p>
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
 *
 * The selection overlay stays hidden until the photo has loaded AND the
 * square has been measured: before that, an unmeasured 0x0 selection
 * would dim the whole stage with its shadow and hide the photo (seen
 * 2026-11-21 on Han's phone — grey box, no photo, handle peeking out).
 * The photo is loaded via an object URL (a big camera photo as a base64
 * data URL is slow to decode) and measured after layout, with a
 * natural-size fallback if the layout size still reads 0.
 */
(function () {
  'use strict';

  var OUT = 512; // cropped output, square, px
  var MAX_H = 320; // CSS max-height of the displayed photo, px

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
    var loading = box.querySelector('.crop-loading');
    var error = box.querySelector('.crop-error');
    if (!stage || !img || !sel || !handle || !useBtn || !cancelBtn) return;

    // Square position/size in displayed px.
    var sq = { x: 0, y: 0, s: 0 };
    var objectUrl = null;

    function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

    function drawSel() {
      sel.style.left = sq.x + 'px';
      sel.style.top = sq.y + 'px';
      sel.style.width = sq.s + 'px';
      sel.style.height = sq.s + 'px';
    }

    function showReady() {
      box.classList.add('ready');
      if (loading) loading.hidden = true;
    }

    function showLoading() {
      box.classList.remove('ready');
      if (error) error.hidden = true;
      if (loading) loading.hidden = false;
    }

    function measure() {
      var iw = img.clientWidth, ih = img.clientHeight;
      if ((!iw || !ih) && img.naturalWidth && img.naturalHeight) {
        // Layout size not ready yet (photo loaded before the first
        // layout pass): compute the displayed size from the natural
        // size and the CSS fit box.
        var maxW = box.clientWidth || 320;
        var scale = Math.min(1, maxW / img.naturalWidth, MAX_H / img.naturalHeight);
        iw = Math.max(1, Math.floor(img.naturalWidth * scale));
        ih = Math.max(1, Math.floor(img.naturalHeight * scale));
      }
      if (!iw || !ih) return; // still nothing to measure; stay loading
      sq.s = Math.floor(Math.min(iw, ih) * 0.8);
      sq.x = Math.floor((iw - sq.s) / 2);
      sq.y = Math.floor((ih - sq.s) / 2);
      drawSel();
      showReady();
    }

    function open(file) {
      showLoading();
      stage.hidden = false;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      objectUrl = URL.createObjectURL(file);
      img.onload = function () { requestAnimationFrame(measure); };
      img.onerror = function () {
        stage.hidden = true;
        if (loading) loading.hidden = true;
        if (error) error.hidden = false;
      };
      img.src = objectUrl;
      box.hidden = false;
    }

    input.addEventListener('change', function () {
      var f = input.files && input.files[0];
      if (!f) return;
      open(f);
    });

    // Drag the square to move it; drag the handle to resize it.
    function drag(target, onMove) {
      target.addEventListener('pointerdown', function (ev) {
        if (!box.classList.contains('ready')) return;
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

    // Rotating the phone changes the displayed size: keep the square
    // inside the photo instead of leaving it stranded off-screen.
    window.addEventListener('resize', function () {
      if (box.hidden || !box.classList.contains('ready')) return;
      var iw = img.clientWidth, ih = img.clientHeight;
      if (!iw || !ih) return;
      sq.s = Math.min(sq.s, iw, ih);
      sq.x = clamp(sq.x, 0, iw - sq.s);
      sq.y = clamp(sq.y, 0, ih - sq.s);
      drawSel();
    });

    useBtn.addEventListener('click', function () {
      if (!box.classList.contains('ready')) return;
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
