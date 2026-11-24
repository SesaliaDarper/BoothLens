'use strict';
(() => {
  const $ = s => document.querySelector(s);
  let activePath = '', checkedPath = '', busy = false, generation = 0, waitingTimer;
  async function request(url, data) {
    const options = { cache: 'no-store' };
    if (data !== undefined) {
      const session = await fetch('/api/session', { cache: 'no-store' });
      if (!session.ok) throw Error('本地会话读取失败，请刷新软件。');
      options.method = 'POST'; options.headers = { 'Content-Type': 'application/json', 'X-BoothLens-Token': (await session.json()).token }; options.body = JSON.stringify(data);
    }
    const r = await fetch(url, options); const result = await r.json();
    if (!r.ok) throw Error(result.error || `HTTP ${r.status}`); return result;
  }
  function error(e) { $('#bridge-error').hidden = false; $('#bridge-error').textContent = e.message || String(e); }
  function buttons(status) {
    const valid = !busy && status && checkedPath === $('#bridge-project-path').value.trim();
    $('#bridge-install').disabled = !valid || !['not-installed', 'update-available', 'installed'].includes(status.state);
    $('#bridge-install').textContent = status?.installed ? '更新 / 修复组件' : '安装连接组件';
    $('#bridge-uninstall').disabled = !valid || !status.installed || !status.managed || status.state === 'conflict';
    $('#bridge-inspect').disabled = busy; $('#bridge-project-path').disabled = busy; $('#bridge-project-list').disabled = busy; $('#bridge-browse').disabled = busy;
  }
  let currentStatus;
  function render(status) {
    currentStatus = status;
    const target = $('#bridge-result'); target.replaceChildren();
    const line = document.createElement('p'); line.className = status.state === 'conflict' || status.state === 'invalid-project' || status.state === 'busy' ? 'error' : 'inline-note';
    line.textContent = status.message || status.state; target.append(line);
    if (status.version) { const version = document.createElement('p'); version.className = 'field-note'; version.textContent = `组件版本 ${status.version}${status.managed ? ' · BoothLens 管理' : ''}`; target.append(version); }
    if (status.legacyScripts?.length) { const old = document.createElement('p'); old.className = 'field-note'; old.textContent = '发现旧脚本：' + status.legacyScripts.join('、') + '。安装时会先核验并备份迁移。'; target.append(old); }
    if (status.backupPath) { const backup = document.createElement('p'); backup.className = 'path'; backup.textContent = '备份：' + status.backupPath; target.append(backup); }
    buttons(status);
  }
  async function inspect() {
    if (busy) return;
    const path = $('#bridge-project-path').value.trim(); checkedPath = ''; currentStatus = null; buttons(); $('#bridge-error').hidden = true;
    if (!path) return;
    const sequence = ++generation;
    try { const status = await request('/api/unity/bridge?projectPath=' + encodeURIComponent(path)); if (sequence !== generation) return; activePath = status.projectPath || path; checkedPath = path; render(status); }
    catch (e) { if (sequence === generation) error(e); }
  }
  async function waitForEditor(path) {
    clearTimeout(waitingTimer);
    if (!$('#bridge-dialog').open || activePath !== path) return;
    try {
      const result = await request('/api/unity/projects');
      const normalize = s => String(s).replaceAll('\\', '/').replace(/\/$/, '').toLowerCase();
      const project = result.projects?.find(p => normalize(p.projectPath) === normalize(path));
      let note = $('#bridge-heartbeat');
      if (!note) { note = document.createElement('p'); note.id = 'bridge-heartbeat'; note.className = 'inline-note'; $('#bridge-result').append(note); }
      if (project && project.bridgeVersion === currentStatus?.version) { note.textContent = `已连接 Unity：${project.name} · 组件 ${project.bridgeVersion}${project.busy ? ' · Editor 正忙，空闲后即可导入' : ' · 可以导入资源'}`; document.dispatchEvent(new CustomEvent('boothlens-unity-connected')); return; }
      note.textContent = '组件已安装，正在等待 Unity 解析包、编译及心跳；工程未打开时，请打开工程。';
    } catch (e) { error(e); }
    waitingTimer = setTimeout(() => waitForEditor(path), 2500);
  }
  async function mutate(action) {
    if (busy || !checkedPath || checkedPath !== $('#bridge-project-path').value.trim()) return;
    const path = checkedPath;
    const text = action === 'uninstall' ? `从工程卸载 BoothLens 连接组件？\n${path}\n\n只归档组件文件，保留任务记录与项目素材；旧版脚本不会自动恢复。` : `向此工程安装 / 更新 BoothLens UPM 连接组件？\n${path}\n\n请确认 Unity 不在 Play Mode，且当前导入已结束。已有受支持旧脚本会连同 .meta 备份，组件仅安装到 Packages。`;
    if (!confirm(text)) return;
    busy = true; clearTimeout(waitingTimer); $('#bridge-error').hidden = true; buttons();
    try { const status = await request(`/api/unity/bridge/${action}`, { projectPath: path }); activePath = status.projectPath || path; checkedPath = path; render(status); if (action === 'install' && status.installed && status.state === 'installed') await waitForEditor(activePath); }
    catch (e) { error(e); }
    finally { busy = false; buttons(currentStatus); }
  }
  async function openManager() {
    const dialog = $('#bridge-dialog'); if (!dialog.open) dialog.showModal(); $('#bridge-error').hidden = true;
    const list = $('#bridge-project-list'); list.replaceChildren(new Option('正在发现工程…', ''));
    try {
      const result = await request('/api/unity/bridge/projects'); list.replaceChildren(new Option('选择工程，或浏览工程文件夹', ''));
      for (const p of result.projects || []) list.append(new Option(`${p.name} · ${p.connected ? '已连接' : '尚未安装 / 连接'} · ${p.projectPath}`, p.projectPath));
      $('#bridge-discovery-message').textContent = result.message || '';
      if (!$('#bridge-project-path').value && result.projects?.length === 1) $('#bridge-project-path').value = result.projects[0].projectPath;
      if ($('#bridge-project-path').value) await inspect();
    } catch (e) { list.replaceChildren(new Option('请手动选择工程文件夹', '')); error(e); }
  }
  document.querySelectorAll('[data-bridge-manage]').forEach(b => b.addEventListener('click', openManager));
  $('#bridge-project-list').addEventListener('change', async e => { if (!e.target.value) return; $('#bridge-project-path').value = e.target.value; clearTimeout(waitingTimer); await inspect(); });
  $('#bridge-project-path').addEventListener('input', () => { ++generation; checkedPath = ''; activePath = ''; currentStatus = null; clearTimeout(waitingTimer); buttons(); });
  $('#bridge-inspect').addEventListener('click', inspect);
  $('#bridge-install').addEventListener('click', () => mutate('install'));
  $('#bridge-uninstall').addEventListener('click', () => mutate('uninstall'));
  $('#bridge-dialog').addEventListener('close', () => clearTimeout(waitingTimer));
  $('#bridge-browse').addEventListener('click', () => {
    if (window.chrome?.webview) window.chrome.webview.postMessage({ type: 'boothlens-pick-unity-project' });
    else { $('#bridge-discovery-message').textContent = '浏览器备用模式请粘贴工程根目录路径，再点击“检查状态”。'; $('#bridge-project-path').focus(); }
  });
  window.chrome?.webview?.addEventListener('message', async e => { if (e.data?.type !== 'boothlens-unity-project-picked' || !$('#bridge-dialog').open || busy) return; $('#bridge-project-path').value = e.data.path; await inspect(); });
})();
