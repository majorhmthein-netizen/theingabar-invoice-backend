// Login loading state: after tapping "Log in", show "logging in..." on the
// button and disable it (prevents double-submit while the server checks
// the PIN). No SMS is sent anymore (OTP was removed 2026-11-21).
(function () {
  var form = document.getElementById('request-form');
  if (!form) return;
  var btn = document.getElementById('login-btn');
  form.addEventListener('submit', function () {
    if (btn) { btn.disabled = true; btn.textContent = 'logging in...'; }
  });
})();
