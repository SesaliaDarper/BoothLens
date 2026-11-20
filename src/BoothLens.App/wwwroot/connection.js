'use strict';
(() => {
  let checking = false;
  async function checkConnection() {
    if (checking || document.hidden) return;
    checking = true;
    try {
      const r = await fetch('/api/health', { cache: 'no-store', signal: AbortSignal.timeout(4000) });
      if (!r.ok) throw Error('HTTP ' + r.status);
      const result = await r.json(); if (!result.ok) throw Error('Bad health response');
      document.getElementById('connection-status').hidden = true;
    } catch {
      document.getElementById('connection-status').hidden = false;
    } finally { checking = false; }
  }
  async function downloadBridge(link) {
    if (link.dataset.busy) return;
    link.dataset.busy = '1'; link.setAttribute('aria-busy', 'true');
    const label = link.textContent; link.textContent = '准备下载…';
    const status = link.closest('.bridge-help')?.querySelector('.bridge-download-status');
    if (status) { status.hidden = false; status.textContent = '正在读取本机桥接文件…'; }
    try {
      const response = await fetch(link.getAttribute('href'), { cache: 'no-store', signal: AbortSignal.timeout(10000) });
      if (!response.ok) { let message = `HTTP ${response.status}`; try { message = (await response.json()).error || message; } catch {} throw Error(message); }
      const blob = await response.blob();
      if (!blob.size || response.headers.get('content-type')?.includes('application/json')) throw Error('下载内容不是桥接文件。');
      const url = URL.createObjectURL(blob), save = document.createElement('a');
      save.href = url; save.download = link.download; document.body.append(save); save.click(); save.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
      if (status) status.textContent = '文件已准备并交给浏览器保存。若浏览器拦截 .cs，请使用 ZIP 或发布包中的 unity 文件夹。';
    } catch (e) {
      if (status) status.textContent = `本机下载失败：${e.message}。请确认 BoothLens 启动窗口仍在运行；也可使用下方 GitHub 备用下载，或发布包中的 unity 文件夹。`;
      checkConnection();
    } finally { delete link.dataset.busy; link.removeAttribute('aria-busy'); link.textContent = label; }
  }
  document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('a[download="BoothRelay.cs"], a[download="BoothRelay.zip"]').forEach(link => link.addEventListener('click', event => { event.preventDefault(); downloadBridge(link); }));
    document.getElementById('connection-retry')?.addEventListener('click', checkConnection);
    window.addEventListener('focus', checkConnection);
    document.addEventListener('visibilitychange', checkConnection);
    checkConnection(); setInterval(checkConnection, 15000);
  });
})();
