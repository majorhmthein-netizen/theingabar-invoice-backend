// Live countdown for the PIN lockout: ticks the remaining time down
// each second, then re-enables the Log in button.
(function () {
  var el = document.getElementById('lockout-countdown');
  if (!el) return;
  var seconds = parseInt(el.getAttribute('data-seconds'), 10);
  if (isNaN(seconds) || seconds <= 0) return;
  var btn = document.getElementById('send-code-btn');
  var err = document.getElementById('lockout-error');
  function fmt(s) {
    if (s >= 60) return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
    return s + 's';
  }
  var timer = setInterval(function () {
    seconds -= 1;
    if (seconds <= 0) {
      clearInterval(timer);
      if (err) err.textContent = 'You can try again now.';
      if (btn) btn.removeAttribute('disabled');
      return;
    }
    el.textContent = fmt(seconds);
  }, 1000);
})();
