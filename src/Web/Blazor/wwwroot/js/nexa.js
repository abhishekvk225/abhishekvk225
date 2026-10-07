// Minimal browser helpers for the Blazor shell. Every function swallows browser failures
// (private windows, blocked storage, insecure context) and reports them through its return value.
(function () {
  'use strict';
  window.nexa = {
    storage: {
      get: function (key) {
        try { return window.localStorage.getItem(key); } catch (e) { return null; }
      },
      set: function (key, value) {
        try { window.localStorage.setItem(key, value); } catch (e) { /* storage unavailable: preference lasts for this session only */ }
      }
    },
    theme: {
      prefersDark: function () {
        try { return window.matchMedia('(prefers-color-scheme: dark)').matches; } catch (e) { return false; }
      }
    },
    clipboard: {
      copy: async function (text) {
        try {
          if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(text);
            return true;
          }
          var area = document.createElement('textarea');
          area.value = text;
          area.setAttribute('readonly', '');
          area.style.position = 'fixed';
          area.style.opacity = '0';
          document.body.appendChild(area);
          area.select();
          var ok = document.execCommand('copy');
          document.body.removeChild(area);
          return ok;
        } catch (e) { return false; }
      }
    }
  };
})();
