// Progressive enhancement for the public website. Every page works without this file: it adds the colour-theme toggle,
// the mobile menu, the password strength hint and the "send again" countdown. It reads and writes no personal data
// (the theme choice is stored in the same localStorage key the portal uses) and loads nothing from other servers.
(function () {
  'use strict';
  var root = document.documentElement;
  var KEY = 'nexa.theme'; // "Light" | "Dark" | "System" (shared with the portal's ThemeService)
  root.classList.add('js');

  function stored() {
    try { return window.localStorage.getItem(KEY); } catch (e) { return null; }
  }
  function apply(preference) {
    if (preference === 'Dark') { root.setAttribute('data-theme', 'dark'); }
    else if (preference === 'Light') { root.setAttribute('data-theme', 'light'); }
    else { root.removeAttribute('data-theme'); }
  }
  function isDark() {
    var explicit = root.getAttribute('data-theme');
    if (explicit) { return explicit === 'dark'; }
    try { return window.matchMedia('(prefers-color-scheme: dark)').matches; } catch (e) { return false; }
  }
  function syncToggle() {
    var buttons = document.querySelectorAll('[data-mk-theme-toggle]');
    for (var i = 0; i < buttons.length; i++) {
      buttons[i].setAttribute('aria-label', isDark() ? 'Switch to light theme' : 'Switch to dark theme');
    }
  }

  // Apply the saved choice straight away (this script sits in <head>) so the page does not flash the wrong colours.
  apply(stored());

  function setMenu(open) {
    var header = document.querySelector('.mk-header');
    var button = document.querySelector('[data-mk-menu-toggle]');
    if (!header || !button) { return; }
    header.classList.toggle('mk-header-open', open);
    button.setAttribute('aria-expanded', open ? 'true' : 'false');
    button.setAttribute('aria-label', open ? 'Close menu' : 'Open menu');
  }

  var LABELS = ['Not started', 'Too short or too simple', 'Fair', 'Good', 'Strong'];
  function score(pw) {
    if (!pw) { return 0; }
    var s = 0;
    if (pw.length >= 12) { s++; }
    if (pw.length >= 16) { s++; }
    var kinds = (/[a-z]/.test(pw) ? 1 : 0) + (/[A-Z]/.test(pw) ? 1 : 0) + (/[0-9]/.test(pw) ? 1 : 0) + (/[^A-Za-z0-9]/.test(pw) ? 1 : 0);
    if (kinds >= 2 || pw.indexOf(' ') >= 0) { s++; }
    var distinct = {}; var n = 0;
    for (var i = 0; i < pw.length; i++) { if (!distinct[pw[i]]) { distinct[pw[i]] = 1; n++; } }
    if (n >= 8) { s++; }
    return pw.length < 12 ? 1 : s;
  }
  function showStrength(input) {
    var meter = document.querySelector('[data-mk-strength-for="' + input.id + '"]');
    if (!meter) { return; }
    var s = score(input.value);
    meter.setAttribute('data-score', String(s));
    var text = meter.querySelector('[data-mk-strength-text]');
    if (text) { text.textContent = LABELS[s]; }
  }

  document.addEventListener('click', function (event) {
    var target = event.target;
    if (!target || !target.closest) { return; }
    var toggle = target.closest('[data-mk-theme-toggle]');
    if (toggle) {
      var next = isDark() ? 'Light' : 'Dark';
      apply(next);
      try { window.localStorage.setItem(KEY, next); } catch (e) { /* the choice then lasts for this page only */ }
      syncToggle();
      return;
    }
    if (target.closest('[data-mk-menu-toggle]')) {
      var header = document.querySelector('.mk-header');
      setMenu(!(header && header.classList.contains('mk-header-open')));
      return;
    }
    if (target.closest('.mk-nav a')) { setMenu(false); }
  });

  document.addEventListener('keydown', function (event) {
    if (event.key === 'Escape') { setMenu(false); }
  });

  document.addEventListener('input', function (event) {
    var input = event.target;
    if (input && input.matches && input.matches('input[type="password"]')) { showStrength(input); }
  });

  // "Send it again" links wait a little before they work, mirroring the server's throttle.
  function startDelays() {
    var links = document.querySelectorAll('[data-mk-delay]');
    Array.prototype.forEach.call(links, function (link) {
      if (link.getAttribute('data-mk-delay-started')) { return; }
      link.setAttribute('data-mk-delay-started', '1');
      var remaining = parseInt(link.getAttribute('data-mk-delay'), 10) || 0;
      var original = link.textContent;
      if (remaining <= 0) { return; }
      link.setAttribute('aria-disabled', 'true');
      link.setAttribute('tabindex', '-1');
      var timer = window.setInterval(function () {
        remaining--;
        if (remaining <= 0) {
          window.clearInterval(timer);
          link.removeAttribute('aria-disabled');
          link.removeAttribute('tabindex');
          link.textContent = original;
        } else {
          link.textContent = original + ' (in ' + remaining + 's)';
        }
      }, 1000);
      link.textContent = original + ' (in ' + remaining + 's)';
    });
  }

  function ready() {
    syncToggle();
    startDelays();
    var passwords = document.querySelectorAll('input[type="password"]');
    for (var i = 0; i < passwords.length; i++) { showStrength(passwords[i]); }
  }
  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', ready); } else { ready(); }
  // Blazor's enhanced navigation swaps the page body without a reload.
  document.addEventListener('enhancedload', function () { setMenu(false); ready(); });
})();
