'use strict';
(() => {
  const key = 'boothlens.theme', media = window.matchMedia('(prefers-color-scheme: dark)');
  let preference = 'system';
  try { const stored = localStorage.getItem(key); if (['light', 'dark', 'system'].includes(stored)) preference = stored; } catch {}
  function apply() {
    const dark = preference === 'dark' || (preference === 'system' && media.matches);
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    document.documentElement.style.colorScheme = dark ? 'dark' : 'light';
    const select = document.getElementById('theme-choice'); if (select) select.value = preference;
    const toggle = document.getElementById('theme-toggle');
    if (toggle) { toggle.textContent = dark ? '☀ 浅色' : '☾ 深色'; toggle.title = `当前${dark ? '深色' : '浅色'}${preference === 'system' ? '（跟随系统）' : ''}，点击切换；设置中可选择跟随系统`; toggle.setAttribute('aria-label', toggle.title); }
  }
  function set(value) { preference = ['light', 'dark', 'system'].includes(value) ? value : 'system'; try { localStorage.setItem(key, preference); } catch {} apply(); }
  apply(); media.addEventListener('change', apply);
  window.addEventListener('storage', event => { if (event.key === key) { preference = ['light', 'dark', 'system'].includes(event.newValue) ? event.newValue : 'system'; apply(); } });
  document.addEventListener('DOMContentLoaded', () => {
    document.getElementById('theme-choice')?.addEventListener('change', event => set(event.target.value));
    document.getElementById('theme-toggle')?.addEventListener('click', () => set(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark')); apply();
  });
})();
