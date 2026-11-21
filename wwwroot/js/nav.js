/* Theingabar Invoice — navigation only, no app logic.
   Invoices, auth and data stay on the server; this file only shapes how
   the device back button behaves, which plain HTML cannot do.
   - Single-push: the first navigation away from Home pushes one history
     entry; every later in-app navigation replaces it. So the device back
     button always lands on Home, never steps through pages in order.
   - Double-back-to-exit: on Home, the first back press shows a toast and
     re-arms after 2 s; a second back press inside 2 s leaves the app.
   - No in-app back buttons: pages offer none; the device back button is
     the only way back. */
(function () {
  'use strict';
  var FLAG = 'tgb-pushed';
  var lastToastAt = 0;
  var toastTimer = null;

  function isHome() {
    return location.pathname === '/' || location.pathname === '';
  }
  function sameOrigin(url) {
    try { return new URL(url, location.href).origin === location.origin; }
    catch (e) { return false; }
  }
  function getFlag() {
    try { return sessionStorage.getItem(FLAG) === '1'; } catch (e) { return false; }
  }
  function setFlag() {
    try { sessionStorage.setItem(FLAG, '1'); } catch (e) {}
  }
  function clearFlag() {
    try { sessionStorage.removeItem(FLAG); } catch (e) {}
  }
  function showToast(text) {
    var el = document.querySelector('.exit-toast');
    if (!el) {
      el = document.createElement('div');
      el.className = 'exit-toast';
      el.setAttribute('role', 'status');
      document.body.appendChild(el);
    }
    el.textContent = text;
    el.classList.add('show');
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.classList.remove('show'); }, 2000);
  }
  /* One sentinel entry above Home so the first back press can be caught
     instead of leaving the app. Re-armed on a timer (not at once) so a
     second press inside 2 s still exits. */
  function armExit() {
    if (!isHome()) return;
    if (!history.state || !history.state.tgbExit) {
      try { history.pushState({ tgbExit: 1 }, ''); } catch (e) {}
    }
  }
  function go(url) {
    if (toastTimer) { clearTimeout(toastTimer); toastTimer = null; }
    if (!getFlag()) { setFlag(); location.href = url; }
    else { location.replace(url); }
  }

  if (isHome()) {
    clearFlag();
    armExit();
    window.addEventListener('pageshow', armExit);
    window.addEventListener('popstate', function () {
      var now = Date.now();
      if (now - lastToastAt < 2000) return; /* second press: let the browser exit */
      lastToastAt = now;
      showToast('Press back again to exit');
      if (toastTimer) clearTimeout(toastTimer);
      toastTimer = setTimeout(armExit, 2000);
    });
  }
  window.addEventListener('pagehide', function () {
    if (toastTimer) { clearTimeout(toastTimer); toastTimer = null; }
  });

  document.addEventListener('click', function (ev) {
    if (ev.metaKey || ev.ctrlKey || ev.shiftKey || ev.altKey || ev.button !== 0) return;
    var a = ev.target && ev.target.closest ? ev.target.closest('a[href]') : null;
    if (!a) {
      /* Whole-row tap (Han 2026-11-21): a .log-row[data-href] opens its
         invoice wherever the row is tapped — except on a link, button or
         form inside it, so History Edit / Delete keep working. */
      var row = ev.target && ev.target.closest ? ev.target.closest('.log-row[data-href]') : null;
      if (!row) return;
      if (ev.target.closest('a[href],button,form,input,select,textarea')) return;
      ev.preventDefault();
      go(row.getAttribute('data-href'));
      return;
    }
    var href = a.getAttribute('href');
    if (!href || href.charAt(0) === '#' || a.target === '_blank' || a.hasAttribute('download')) return;
    if (!sameOrigin(href)) return;
    ev.preventDefault();
    go(a.href);
  });

  /* GET forms navigate like links. POST forms keep the browser's own
     behaviour: POST-redirect replaces the entry by itself. */
  document.addEventListener('submit', function (ev) {
    var f = ev.target;
    if (!f || (f.method || 'get').toLowerCase() !== 'get') return;
    ev.preventDefault();
    var qs = new URLSearchParams(new FormData(f)).toString();
    go(f.action + (qs ? '?' + qs : ''));
  });
})();
