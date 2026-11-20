/* Theingabar Invoice — export actions on the invoice page.
   Share (Web Share API -> Viber, Telegram, ...), Save to device
   (anchor download + "Save successful" popup), Print (system dialog).
   No "Download" wording anywhere. Loaded only on /invoice/{id}. */
(function () {
  var card = document.getElementById('export-card');
  if (!card) return;
  var state = { fmt: 'png' };
  var thumbImg = document.getElementById('export-thumb');
  var thumbPdf = document.getElementById('export-thumb-pdf');
  var nameEl = document.getElementById('export-name');
  var toastEl = document.getElementById('export-toast');
  var toastTimer = null;

  function fileName() { return 'invoice-' + card.dataset.no + '.' + state.fmt; }
  function fileUrl() { return state.fmt === 'png' ? card.dataset.png : card.dataset.pdf; }

  function showToast(msg) {
    if (!toastEl) return;
    toastEl.textContent = msg;
    toastEl.classList.add('show');
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.classList.remove('show'); }, 2600);
  }

  function setFormat(fmt) {
    state.fmt = fmt;
    var pills = card.querySelectorAll('.fmt-pill');
    for (var i = 0; i < pills.length; i++) {
      pills[i].classList.toggle('active', pills[i].dataset.fmt === fmt);
    }
    if (nameEl) nameEl.textContent = fileName();
    if (thumbImg && thumbPdf) {
      thumbImg.hidden = fmt !== 'png';
      thumbPdf.hidden = fmt !== 'pdf';
    }
  }

  var pills = card.querySelectorAll('.fmt-pill');
  for (var i = 0; i < pills.length; i++) {
    pills[i].addEventListener('click', function () { setFormat(this.dataset.fmt); });
  }

  var shareBtn = document.getElementById('export-share');
  if (shareBtn) {
    shareBtn.addEventListener('click', function () {
      var url = fileUrl();
      var name = fileName();
      fetch(url, { credentials: 'same-origin' })
        .then(function (res) { if (!res.ok) throw new Error('fetch failed'); return res.blob(); })
        .then(function (blob) {
          var file = new File([blob], name, { type: blob.type || (state.fmt === 'png' ? 'image/png' : 'application/pdf') });
          if (navigator.canShare && navigator.canShare({ files: [file] })) {
            return navigator.share({ files: [file] });
          }
          if (navigator.share) {
            return navigator.share({ title: name, url: location.origin + url });
          }
          showToast('Sharing is not supported here — use Save to device.');
          return null;
        })
        .catch(function () { showToast('Could not share — use Save to device.'); });
    });
  }

  var saveBtn = document.getElementById('export-save');
  if (saveBtn) {
    saveBtn.addEventListener('click', function () {
      var a = document.createElement('a');
      a.href = fileUrl();
      a.download = fileName();
      document.body.appendChild(a);
      a.click();
      a.remove();
      showToast('Save successful');
    });
  }

  var printBtn = document.getElementById('export-print');
  if (printBtn) {
    printBtn.addEventListener('click', function () { window.print(); });
  }
})();
