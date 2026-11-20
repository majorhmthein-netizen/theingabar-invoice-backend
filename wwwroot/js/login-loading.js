// Login loading state: after tapping "Log in", show "logging in..." on the
// button and "Code is sending via sms" with a countdown while the server
// sends the code (the SMSPoh send can take up to 45s).
(function () {
  var form = document.getElementById('request-form');
  if (!form) return;
  var btn = document.getElementById('send-code-btn');
  var status = document.getElementById('sending-status');
  var count = document.getElementById('sending-countdown');
  form.addEventListener('submit', function () {
    if (btn) { btn.disabled = true; btn.textContent = 'logging in...'; }
    if (status) status.hidden = false;
    var seconds = 45;
    if (count) count.textContent = seconds + 's';
    var timer = setInterval(function () {
      seconds -= 1;
      if (seconds <= 0) {
        clearInterval(timer);
        if (status) status.textContent = 'Code is sending via sms — still sending…';
        return;
      }
      if (count) count.textContent = seconds + 's';
    }, 1000);
  });
})();
