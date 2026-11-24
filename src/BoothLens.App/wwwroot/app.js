'use strict';
const $ = (s, root = document) => root.querySelector(s);
const $$ = (s, root = document) => [...root.querySelectorAll(s)];
const state = {
  token: null, view: 'library', catalogPage: 1, catalogQuery: '', bindPath: '', importPath: '',
  detailPath: '', detailVersion: 0, scan: null, pollTimer: null, pollBusy: false,
  projects: [], projectsRequest: 0, projectsLoading: false, importSubmitting: false, previewRequest: 0,
  resources: { library: { page: 1, query: '', status: '', category: '', root: '' }, files: { page: 1, query: '', status: '', category: '', root: '' } },
  roots: [], rootsPromise: null, rootsLoaded: false, baseline: null, structureRequest: 0, structureBusy: false, repairBusy: false,
  cache: new Map(), requests: { library: 0, files: 0 }
};
const titles = { library: '资源库', files: '扫描整理', catalog: '已保存商品', discover: 'BOOTH 搜索', jobs: '导入记录', settings: '设置' };
document.documentElement.dataset.uiVersion = 'local-import-v2';
const statusNames = { bound: '已绑定', auto: '已匹配', manual: '手动绑定', confirmed: '已确认', review: '待确认', unknown: '未匹配', failed: '识别失败', error: '识别失败', running: '识别中', pending: '待识别', ignored: '已忽略' };
function el(tag, cls, text) { const n = document.createElement(tag); if (cls) n.className = cls; if (text !== undefined) n.textContent = text; return n; }
function button(text, fn, cls = 'button subtle') { const b = el('button', cls, text); b.type = 'button'; b.addEventListener('click', () => Promise.resolve().then(fn).catch(noticeError)); return b; }
function notice(text, bad = false) { const n = el('div', 'notice' + (bad ? ' error' : ''), text); $('#notices').append(n); setTimeout(() => n.remove(), 7000); }
function noticeError(e) { notice(e.message || String(e), true); }
async function api(path, data, method = data === undefined ? 'GET' : 'POST') {
  const options = { headers: {} };
  if (data !== undefined || method !== 'GET') {
    if (!state.token) state.token = (await api('/api/session')).token;
    options.method = method; options.headers = { 'X-BoothLens-Token': state.token };
    if (data !== undefined) { options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(data); }
  }
  const response = await fetch(path, options);
  const text = await response.text(); let value;
  try { value = text ? JSON.parse(text) : {}; } catch { throw Error(`HTTP ${response.status}：接口未返回 JSON`); }
  if (!response.ok) throw Error(value.error || value.message || `HTTP ${response.status}`);
  return value;
}
function empty(target, text) {
  const box = el('div', 'empty'), loading = /^正在/.test(text);
  if (loading) { box.classList.add('is-loading'); box.append(el('span', 'empty-loader'), el('p', '', text)); }
  else {
    const resourceView = target.id === 'library-results' ? 'library' : target.id === 'files-results' ? 'files' : '';
    const filtered = resourceView && ['query', 'status', 'category', 'root'].some(key => state.resources[resourceView][key]);
    const title = resourceView ? (filtered ? '这里还没有符合条件的资源' : '从你的第一份素材开始') : target.id === 'catalog-results' ? '商品档案，等待收藏' : target.id === 'jobs-results' ? '导入记录会保留在这里' : '暂时没有搜索结果';
    const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg'), use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    icon.classList.add('icon'); icon.setAttribute('aria-hidden', 'true'); use.setAttribute('href', resourceView ? '#i-folder' : target.id === 'jobs-results' ? '#i-import' : '#i-search'); icon.append(use);
    const tile = el('span', 'tile-icon empty-icon'); tile.append(icon);
    box.append(tile, el('h3', '', title), el('p', 'empty-description', filtered ? '试试其他关键词、分类或匹配状态，也可以清除筛选查看全部素材。' : resourceView ? '扫描本机素材目录，把文件夹与归档整理成资源卡片，再关联对应的 BOOTH 商品。' : text));
    if (resourceView && !filtered) { const link = el('a', 'button primary', '扫描素材目录'); link.href = '#files'; box.append(link, el('span', 'empty-footnote', '仅在主动扫描时读取目录，原文件保留在原位。')); }
    else if (target.id === 'jobs-results') box.append(el('span', 'empty-footnote', '在资源详情中选择导入包，即可发送到已打开的 Unity Editor。'));
  }
  target.replaceChildren(box);
}
function errorPanel(target, error, retry) { const box = el('div', 'empty'); box.append(el('p', 'error', error.message || String(error)), button('重试加载', retry)); target.replaceChildren(box); }
function isBound(f) { return f.itemId != null && Number(f.itemId) !== 0 && !['review', 'unknown'].includes(f.status); }
function cover(item, allowed = true, resource = item) {
  const n = el('div', 'cover'); n.append(el('span', 'cover-placeholder', '◇'));
  const id = item.id ?? item.itemId ?? resource.itemId;
  const remote = allowed && id != null && (Number(id) > 0 || item.thumb || resource.thumb) ? `/img/${encodeURIComponent(id)}` : '';
  // BOOTH 商品封面最高优先级；只有未绑定/未找到商品时才显示本地配图。
  const local = !(allowed && Number(id) > 0) && resource.path && (resource.hasLocalCover || resource.localCoverPath) ? `/api/resources/cover?path=${encodeURIComponent(resource.path)}` : '';
  if (local || remote) {
    const img = el('img'), source = el('span', 'cover-source', remote ? '商品封面' : '本地配图');
    img.src = remote || local; img.alt = item.title || resource.name || '资源封面'; img.loading = 'lazy';
    img.addEventListener('error', () => { img.remove(); source.remove(); });
    n.append(img, source);
  }
  return n;
}
async function revealResource(path) {
  try { await api('/api/resources/reveal', { path }); }
  catch (e) { notice(`打开所在位置失败：${e.message}`, true); }
}
function pager(target, page, total, fn) {
  const pages = Math.ceil(total / 24); target.hidden = pages <= 1; target.replaceChildren(); if (pages <= 1) return;
  const prev = button('上一页', () => fn(page - 1)); prev.disabled = page <= 1;
  const next = button('下一页', () => fn(page + 1)); next.disabled = page >= pages;
  target.append(prev, el('span', 'secondary', `${page} / ${pages}`), next);
}
async function show(view) {
  state.view = titles[view] ? view : 'library';
  for (const key of Object.keys(titles)) { $(`#view-${key}`).hidden = key !== state.view; const link = $(`[data-view=${key}]`); if (key === state.view) link.setAttribute('aria-current', 'page'); else link.removeAttribute('aria-current'); }
  $('#current-view').textContent = titles[state.view]; document.title = `${titles[state.view]} · BoothLens`;
  await ({ library: () => loadResources('library'), files: async () => { await loadResources('files'); if (state.view === 'files') await loadStructure(); }, catalog: loadCatalog, jobs: loadJobs, settings: loadSettings, discover: async () => {} })[state.view]();
}
async function loadRoots(force = false) {
  if (state.rootsPromise) return state.rootsPromise;
  if (state.rootsLoaded && !force) return;
  const pending = (async () => {
    try {
      const result = await api('/api/roots');
      state.roots = (result.roots || []).map(r => typeof r === 'string' ? { path: r, mode: 'auto' } : r).filter(r => r.path);
      state.rootsLoaded = true;
      for (const view of ['library', 'files']) fillRootSelect($(`#${view}-root`), '全部根目录', state.resources[view].root);
      fillRootSelect($('#scan-stored-root'), '选择根目录或在下方输入路径');
      fillRootSelect($('#structure-root'), '使用上方输入的素材根目录');
      if (!$('#scan-root').value.trim() && state.roots.length) {
        $('#scan-root').value = state.roots[0].path; $('#scan-stored-root').value = state.roots[0].path;
        $('#structure-root').value = state.roots[0].path;
        $('#scan-mode').value = scanModeNames[state.roots[0].mode] ? state.roots[0].mode : 'auto';
      }
      $('#roots-message').hidden = true;
    } catch (e) {
      $('#roots-message').hidden = false; $('#roots-message').textContent = `根目录列表读取失败：${e.message}。仍可填写完整路径并手动重试读取目录。`;
    }
  })();
  state.rootsPromise = pending;
  try { await pending; } finally { if (state.rootsPromise === pending) state.rootsPromise = null; }
}
function fillRootSelect(select, placeholder, preferred = select.value) {
  const first = el('option', '', placeholder); first.value = ''; select.replaceChildren(first);
  const roots = [...state.roots]; if (preferred && !roots.some(r => r.path === preferred)) roots.push({ path: preferred });
  for (const root of roots) { const option = el('option', '', root.path); option.value = root.path; select.append(option); }
  select.value = preferred || '';
}
function effectiveRepairRoot() { return $('#scan-root').value.trim() || $('#scan-stored-root').value; }
function structureRoot() { return $('#structure-root').value || effectiveRepairRoot(); }
async function loadStructure() {
  await loadRoots();
  const root = structureRoot(), target = $('#structure-folders'), request = ++state.structureRequest;
  if (!root) { target.replaceChildren(el('p', 'field-note', '请先选择素材根目录，或在上方输入完整路径。')); return; }
  target.replaceChildren(el('p', 'field-note', '正在读取目录列表（不联网）…'));
  try {
    const result = await api(`/api/structure?root=${encodeURIComponent(root)}`); if (request !== state.structureRequest) return;
    target.replaceChildren();
    for (const folder of result.folders || []) {
      const kind = folder.kind || 'auto', effective = folder.effectiveKind || kind;
      const row = button('', () => { $('#structure-path').value = folder.path; $('#structure-kind').value = ['category', 'asset'].includes(kind) ? kind : 'auto'; $('#structure-path').focus(); }, 'structure-folder');
      row.append(el('strong', '', folder.name || folder.path), el('span', 'badge', `${{ category: '分类', asset: '完整资源', library: '资源库', auto: '自动' }[effective] || effective} · ${folder.resourceCount ?? 0} 个资源`), el('span', 'path', folder.path)); target.append(row);
    }
    if (!target.childElementCount) target.append(el('p', 'field-note', '暂无目录条目，可直接输入需要纠正的文件夹路径。'));
  } catch (e) { if (request === state.structureRequest) target.replaceChildren(el('p', 'error', `目录列表读取失败：${e.message}。可直接输入路径操作。`)); }
}
function repairConfirmation(root, prefix = '') { return confirm(`${prefix}${prefix ? '\n\n' : ''}只重建目录索引，保留绑定及候选，原文件不动。\n不联网，不访问 BOOTH。\n重建根目录：${root}\n\n确认继续？`); }
async function submitRepair(root) {
  const result = await api('/api/scan/repair', { root });
  renderScan({ discovered: 0, processed: 0, bound: 0, review: 0, unknown: 0, skipped: 0, ...result, state: result.state || 'running' });
  $('#scan-result').hidden = false; $('#scan-result').textContent = '离线目录修复已提交：只重建索引，保留绑定及候选，原文件不动；不访问 BOOTH。';
  notice('已提交离线修复，可切换页面等待完成。'); schedulePoll(); await checkScan();
}
async function repairStructure() {
  const root = effectiveRepairRoot(); if (!root) throw Error('请填写素材根目录，或选择已保存的根目录。');
  if (!repairConfirmation(root)) return;
  state.repairBusy = true; updateStructureControls();
  try { await submitRepair(root); } finally { state.repairBusy = false; updateStructureControls(); }
}
async function applyBoundary(path, kind, root, closeDetail = false) {
  if (!path) throw Error('请填写需要纠正的文件夹完整路径。');
  if (!root) throw Error('请在“目录结构”中选择要重建的素材根目录。');
  const names = { category: '分类目录：展开下层资源，每个资源单独显示', asset: '完整资源：合并内部条目，子目录只保留为成员', auto: '自动判断：清除该目录的手动边界' };
  if (!repairConfirmation(root, `将以下目录设为${names[kind]}。\n${path}`)) return;
  state.structureBusy = true; updateStructureControls();
  let saved = false;
  try {
    await api('/api/resources/boundary', { path, kind }); saved = true;
    if (closeDetail) $('#detail-dialog').close();
    await submitRepair(root);
  } catch (e) { throw Error(saved ? `边界已保存，离线修复提交失败：${e.message}。请使用“修复目录结构（不联网）”重试。` : e.message); }
  finally { state.structureBusy = false; updateStructureControls(); }
}
function updateStructureControls() {
  const disabled = state.scan?.state === 'running' || state.structureBusy || state.repairBusy;
  $('#scan-repair').disabled = disabled; $('#structure-submit').disabled = disabled; $('#scan-submit').disabled = disabled;
  $$('.boundary-action').forEach(b => { b.disabled = disabled; });
}
async function detailBoundary(f, kind) {
  await loadRoots();
  const root = resourceRoot(f) || structureRoot();
  if (!root) {
    $('#structure-path').value = f.path; $('#structure-kind').value = kind; $('#detail-dialog').close(); location.hash = 'files'; openStructurePanel();
    notice('已填入目录边界，请选择素材根目录后应用并离线修复。'); return;
  }
  await applyBoundary(f.path, kind, root, true);
}
function isContainer(f) {
  const kind = String(f.kind || '').toLowerCase();
  return ['folder', 'directory', 'dir', 'archive', 'zip', '7z', 'rar', 'unitypackage', 'container', 'package'].includes(kind) || /\.(unitypackage|zip|7z|rar|tar|tgz|gz)$/i.test(f.path || f.name || '');
}
function categoryOf(f) { return (Array.isArray(f.category) ? f.category.join('/') : String(f.category || '未分类')).split(/[\\/]+/).filter(Boolean).join(' / '); }
function statusBadge(f) { const text = f.error ? '识别失败' : statusNames[f.status] || statusNames[f.recognitionState] || (isBound(f) ? '已绑定' : '未匹配'); return el('span', `badge ${f.error ? 'bad' : isBound(f) ? 'good' : 'warn'}`, text); }
async function fetchResources(config, force) {
  const key = JSON.stringify([config.query, config.status]);
  if (force) state.cache.delete(key);
  if (!state.cache.has(key)) {
    const request = (async () => {
      const files = []; let page = 1; let total = Infinity;
      while (files.length < total) {
        const r = await api(`/api/resources?page=${page}&status=${encodeURIComponent(config.status)}&q=${encodeURIComponent(config.query)}`);
        const batch = r.files || []; total = Number(r.total ?? batch.length); if (!batch.length) break;
        files.push(...batch); page++; if (page > 10000) throw Error('资源分页超出预期，请缩小搜索范围。');
      }
      return [...new Map(files.filter(isContainer).map(f => [f.path, f])).values()];
    })();
    state.cache.set(key, request); request.catch(() => { if (state.cache.get(key) === request) state.cache.delete(key); });
  }
  return state.cache.get(key);
}
function fetchResourceBaseline(force = false) {
  if (force) state.baseline = null;
  if (!state.baseline) {
    const pending = fetchResources({ query: '', status: '' }, force);
    state.baseline = pending; pending.catch(() => { if (state.baseline === pending) state.baseline = null; });
  }
  return state.baseline;
}
function normalizedPath(path) { return String(path || '').replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase(); }
function pathInRoot(path, root) { const p = normalizedPath(path), r = normalizedPath(root); return Boolean(r) && (p === r || p.startsWith(r + '/')); }
function resourceRoot(f) {
  const stored = state.roots.filter(r => pathInRoot(f.path, r.path)).sort((a, b) => a.path.length - b.path.length)[0];
  return f.root || f.rootPath || stored?.path || '';
}
function inSelectedRoot(f, root) { return !root || normalizedPath(resourceRoot(f)) === normalizedPath(root) || pathInRoot(f.path, root); }
function renderResourceScope(view, count, total) {
  const config = state.resources[view], select = $(`#${view}-filter`);
  $(`#${view}-scope`).textContent = `当前范围：${config.root || '全部根目录'} · ${config.category || '全部分类'} · ${select.selectedOptions[0]?.textContent || '全部状态'}${config.query ? ` · 搜索“${config.query}”` : ''}；显示 ${count} / 已索引 ${total} 个资源`;
}
async function loadResources(view, force = false) {
  const config = state.resources[view], target = $(`#${view}-results`), request = ++state.requests[view];
  empty(target, '正在读取资源容器…');
  try {
    await loadRoots(force); if (request !== state.requests[view]) return;
    const baselineRequest = fetchResourceBaseline(force);
    const [baseline, matched] = await Promise.all([baselineRequest, config.query || config.status ? fetchResources(config, force) : baselineRequest]);
    if (request !== state.requests[view]) return;
    const all = matched.filter(f => inSelectedRoot(f, config.root));
    const categories = [...new Set(baseline.flatMap(f => { const parts = categoryOf(f).split(' / '); return parts.map((_, i) => parts.slice(0, i + 1).join(' / ')); }))].sort((a, b) => a.localeCompare(b));
    if (config.category && !categories.includes(config.category)) categories.push(config.category);
    renderCategories(view, categories, all);
    const files = config.category ? all.filter(f => categoryOf(f) === config.category || categoryOf(f).startsWith(config.category + ' / ')) : all;
    renderResourceScope(view, files.length, baseline.length);
    config.page = Math.max(1, Math.min(config.page, Math.ceil(files.length / 24) || 1));
    $(`#${view}-total`).hidden = false; $(`#${view}-total`).textContent = files.length;
    target.replaceChildren();
    if (!files.length) empty(target, '没有资源容器。请到“扫描与整理”主动扫描素材目录，或调整分类与筛选。');
    else { const grid = el('div', 'grid resource-grid'); files.slice((config.page - 1) * 24, config.page * 24).forEach(f => grid.append(resourceCard(f, view))); target.append(grid); }
    pager($(`#${view}-pagination`), config.page, files.length, async p => { config.page = p; await loadResources(view); });
  } catch (e) { if (request === state.requests[view]) errorPanel(target, e, () => loadResources(view, true)); }
}
function renderCategories(view, categories, all) {
  const root = $(`#${view}-categories`), config = state.resources[view]; root.replaceChildren();
  const add = (name, value, count) => { const b = button(`${name} · ${count}`, async () => { config.category = value; config.page = 1; await loadResources(view); }, 'category-chip'); b.setAttribute('aria-pressed', String(config.category === value)); root.append(b); };
  add('全部分类', '', all.length); categories.forEach(category => add(category, category, all.filter(f => categoryOf(f) === category || categoryOf(f).startsWith(category + ' / ')).length));
}
function resourceCard(f, view) {
  const card = el('article', 'card resource-card');
  const open = button('', () => resourceDetail(f), 'resource-open'); open.setAttribute('aria-label', `查看资源：${f.name || f.path}`);
  const body = el('div', 'card-body'); body.append(el('div', 'category-breadcrumb', categoryOf(f)), el('div', 'card-title', f.name || f.path), el('div', 'card-meta', isBound(f) ? f.title || `BOOTH ${f.itemId}` : '尚未绑定商品'));
  const meta = el('div', 'resource-status'); meta.append(statusBadge(f), el('span', 'secondary', `${f.kind || '容器'} · ${f.members?.length || 0} 个成员`)); body.append(meta);
  if (f.aliases?.length) body.append(el('div', 'secondary', `${f.aliases.length} 处关联存储`));
  if (f.error) body.append(el('p', 'error card-error', f.error));
  open.append(cover(f, isBound(f)), body); card.append(open);
  card.append(button('打开所在位置', () => revealResource(f.path), 'button subtle card-reveal'));
  if (f.error || ['failed', 'error'].includes(f.recognitionState)) card.append(button('清除候选以备重查', () => retryResource(f), 'button subtle card-retry'));
  return card;
}
function section(root, title) { const n = el('section', 'detail-section'); n.append(el('h3', '', title)); root.append(n); return n; }
function openBind(f) { state.bindPath = f.path; $('#bind-path').textContent = f.path; $('#bind-input').value = f.itemId ?? ''; if (!$('#bind-dialog').open) $('#bind-dialog').showModal(); }
function importable(f) { return /\.(unitypackage|zip|7z|rar|tar|tgz|gz)$/i.test(f.path || f.name || ''); }
function folderSource(f) { return ['folder', 'directory', 'dir'].includes(String(f.kind || '').toLowerCase()); }
function packageChoices(f) { const members = f.members || []; const candidates = importable(f) ? [f, ...members] : members; return [...new Map(candidates.filter(importable).map(m => [m.path, m])).values()]; }
async function resourceDetail(f) {
  const dialog = $('#detail-dialog'); state.detailPath = f.path; const version = ++state.detailVersion;
  $('#detail-title').textContent = f.name || '资源容器'; if (!dialog.open) dialog.showModal();
  renderResourceDetail(f, f.item);
  if (isBound(f) && !f.item) {
    try { const r = await api(`/api/items/${encodeURIComponent(f.itemId)}`); if (version === state.detailVersion && dialog.open) renderResourceDetail(f, r.item); }
    catch (e) { if (version === state.detailVersion && dialog.open) { const error = el('div', 'detail-section'); error.append(el('p', 'error', `商品详情读取失败：${e.message}`), button('重试详情', () => resourceDetail(f))); $('#detail-body').append(error); } }
  }
}
async function uploadLocalCover(f) {
  const input = document.createElement('input'); input.type = 'file'; input.accept = 'image/jpeg,image/png,image/webp,image/gif';
  input.addEventListener('change', async () => { const file = input.files?.[0]; if (!file) return; if (file.size > 8 * 1024 * 1024) { notice('本地配图不能超过 8 MB。', true); return; } const bytes = new Uint8Array(await file.arrayBuffer()); let binary = ''; for (let i = 0; i < bytes.length; i += 8192) binary += String.fromCharCode(...bytes.subarray(i, i + 8192)); try { const result = await api('/api/resources/cover', { path: f.path, imageBase64: btoa(binary) }); notice('本地配图已更新；未绑定商品时会显示它。'); await refreshResources(); if ($('#detail-dialog').open) await resourceDetail(resourceFrom(result, f)); } catch (e) { notice(`更新本地配图失败：${e.message}`, true); } }); input.click(); }
async function clearLocalCover(f) { try { const result = await api(`/api/resources/cover?path=${encodeURIComponent(f.path)}`, undefined, 'DELETE'); notice('已清除本地配图；可重新选择，或标记刷新后扫描以自动选图。'); await refreshResources(); if ($('#detail-dialog').open) await resourceDetail(resourceFrom(result, f)); } catch (e) { notice(`清除本地配图失败：${e.message}`, true); } }
async function unbindResource(f) {
  if (!confirm(`清除“${f.name || f.path}”的商品绑定？\n\n保留素材、成员、本地配图和导入记录。清除后显示本地配图，普通重扫不会恢复旧绑定；可重新指定商品，或主动重查。`)) return;
  const result = await api('/api/resources/unbind', { path: f.path });
  notice('已清除绑定，可重新指定商品。'); await refreshResources();
  if ($('#detail-dialog').open && state.detailPath === f.path) await resourceDetail(resourceFrom(result, f));
}
function detailFold(root, title) { const n = el('details', 'detail-section detail-fold'); n.append(el('summary', '', title)); root.append(n); return n; }
function openStructurePanel() { $('#structure-panel').open = true; $('#structure-title').scrollIntoView({ behavior: 'smooth', block: 'start' }); }
function renderResourceDetail(f, item) {
  const root = $('#detail-body'); root.replaceChildren(); $('#detail-title').textContent = f.name || f.path;
  const top = el('div', 'detail-top'), meta = el('div', 'detail-meta');
  meta.append(el('div', 'category-breadcrumb', categoryOf(f)), statusBadge(f));
  if (isBound(f)) meta.append(el('strong', '', item?.title || f.title || `BOOTH ${f.itemId}`));
  if (item?.shopName) meta.append(el('p', 'secondary', item.shopName));
  if (item?.priceText) meta.append(el('p', 'price', item.priceText));
  const actions = el('div', 'detail-actions');
  actions.append(button('打开位置', () => revealResource(f.path)), button(isBound(f) ? '更换绑定' : '指定商品', () => openBind(f)), ...(f.itemId != null ? [button('清除绑定', () => unbindResource(f))] : []), button('更换本地图', () => uploadLocalCover(f)), ...(f.localCoverPath ? [button('清除本地图', () => clearLocalCover(f))] : []));
  if (item?.canonicalUrl && /^https?:\/\//i.test(item.canonicalUrl)) { const a = el('a', 'button subtle', '商品页面'); a.href = item.canonicalUrl; a.target = '_blank'; a.rel = 'noopener noreferrer'; actions.append(a); }
  meta.append(actions); top.append(cover(item || f, isBound(f), f), meta); root.append(top);
  if (f.error) root.append(el('p', 'error', f.error));
  const choices = packageChoices(f);
  const imports = section(root, '本地资源导入');
  const refreshPackages = button('重新读取本地包', async () => {
    refreshPackages.disabled = true;
    try { const result = await api('/api/resources/members', { path: f.path }); await refreshResources(); await resourceDetail(resourceFrom(result, f)); notice('本地包列表已刷新；候选、商品绑定和识别状态保持不变。'); }
    finally { refreshPackages.disabled = false; }
  });
  refreshPackages.disabled = f.status === 'ignored'; imports.append(refreshPackages);
  if (folderSource(f)) {
    const copy = button('复制原始文件夹到 Unity…', () => openImport(f, f, true)); copy.disabled = f.status === 'ignored'; imports.append(copy);
    imports.append(el('p', 'field-note', '原始模型、贴图、Prefab 或源码可复制到 Assets/_BoothItems。含 Unity 包时优先选择下方包导入；整目录复制不会自动展开其中的压缩包。'));
  }
  if (!choices.length) imports.append(el('p', 'field-note', '当前索引未记录可导入包。可先重新读取本地包，或复制原始文件夹；无需先确认 BOOTH 商品。'));
  if (choices.length) {
    const packages = section(root, `导入包 · ${choices.length}`);
    for (const member of choices) {
      const row = el('div', 'row'), main = el('div', 'row-main'); main.append(el('strong', '', member.name || member.path));
      const supported = /\.(unitypackage|zip)$/i.test(member.path), b = button(supported ? '导入' : '先解压', () => openImport(f, member)); b.disabled = f.status === 'ignored' || !supported;
      row.append(main, button('打开位置', () => revealResource(member.path)), b); packages.append(row);
    }
    if (!isBound(f)) packages.append(el('p', 'field-note', '可直接导入本地包，无需先确认商品。导入不会自动确认候选或改变商品绑定。'));
  }
  if (f.candidates?.length) {
    const candidates = detailFold(root, `候选商品 · ${f.candidates.length}（可稍后确认，不影响导入）`);
    for (const candidate of f.candidates) {
      const row = el('div', 'row candidate-row'), main = el('div', 'row-main'); main.append(el('strong', '', candidate.name || `BOOTH ${candidate.id}`), el('p', 'secondary', [candidate.brand, candidate.id, candidate.price == null ? '' : '¥' + candidate.price].filter(Boolean).join(' · ')));
      const confirmButton = button('确认', async () => { confirmButton.disabled = true; try { const result = await api('/api/resources/confirm', { path: f.path, id: candidate.id }); notice('已确认资源。'); await refreshResources(); await resourceDetail(resourceFrom(result, f)); } finally { confirmButton.disabled = false; } }, 'button primary');
      row.append(main, confirmButton); candidates.append(row);
    }
  }
  const info = detailFold(root, '路径与识别信息'); info.append(el('p', 'path', f.path));
  if (resourceRoot(f)) info.append(el('p', 'path', `根目录：${resourceRoot(f)}`));
  if (f.recognitionState) info.append(el('p', 'secondary', `识别：${statusNames[f.recognitionState] || f.recognitionState}`));
  info.append(button(isBound(f) ? '下次扫描刷新成员与配图' : '清除候选，下次扫描重查', () => retryResource(f)));
  if (item?.descShort) detailFold(root, '商品说明').append(el('p', 'description', item.descShort));
  if (f.aliases?.length) {
    const aliases = detailFold(root, `关联存储 · ${f.aliases.length}`);
    for (const alias of f.aliases) { const path = typeof alias === 'string' ? alias : alias.path || ''; const row = el('div', 'row'); row.append(el('p', 'path row-main', path)); if (path) row.append(button('打开位置', () => revealResource(path))); aliases.append(row); }
  }
  const structure = detailFold(root, '高级：目录结构与修复');
  if (['folder', 'directory', 'dir'].includes(String(f.kind || '').toLowerCase()) || !/\.[^\\/.]+$/.test(f.path)) {
    const boundary = el('div', 'boundary-actions'); boundary.append(button('设为分类目录', () => detailBoundary(f, 'category'), 'button subtle boundary-action'), button('设为完整资源', () => detailBoundary(f, 'asset'), 'button subtle boundary-action')); structure.append(boundary);
  }
  structure.append(button('编辑目录边界', () => { $('#structure-path').value = f.path; const repairRoot = resourceRoot(f); if (repairRoot) fillRootSelect($('#structure-root'), '使用上方输入的素材根目录', repairRoot); $('#detail-dialog').close(); location.hash = 'files'; openStructurePanel(); }));
  const packagePaths = new Set(choices.map(m => m.path)), otherMembers = (f.members || []).filter(m => !packagePaths.has(m.path));
  if (otherMembers.length) {
    const members = detailFold(root, `其他成员 · ${otherMembers.length}`), list = el('div', 'member-list');
    for (const member of otherMembers) { const row = el('div', 'member-row'); row.append(el('strong', '', member.name || member.path), el('span', 'badge', member.kind || '文件'), el('p', 'path', member.path), button('打开位置', () => revealResource(member.path))); list.append(row); } members.append(list);
  }
  updateStructureControls();
}
function resourceFrom(result, fallback) { const value = result?.resource || result?.file || result; return value?.path ? { ...fallback, ...value } : fallback; }
async function manageCategories() {
  const dialog = $('#categories-dialog'); if (!dialog.open) dialog.showModal();
  const target = $('#category-management'); target.replaceChildren(el('p', 'field-note', '正在读取分类…'));
  try {
    const result = await api('/api/categories'); target.replaceChildren();
    if (!result.categories?.length) { target.append(el('p', 'field-note', '暂无可管理的分类，先扫描素材目录。')); return; }
    for (const c of result.categories) {
      const row = el('div', 'category-management-row'), text = el('div'); row.dataset.removed = String(c.effectiveRemoved);
      text.append(el('strong', '', c.category), el('p', 'path', c.root), el('p', 'secondary', `${c.count} 个直属资源${c.removed ? ' · 已移除' : c.effectiveRemoved ? ' · 父分类已移除' : ''}`));
      const action = button(c.removed ? '恢复' : '移除', async () => {
        if (!c.removed && !confirm(`从资源库移除分类“${c.category}”？\n\n根目录：${c.root}\n该分类及子分类的资源归入“未分类”。磁盘文件、商品绑定、配图与导入记录保持不动，可随时恢复。`)) return;
        action.disabled = true;
        try { await api(`/api/categories/${c.removed ? 'restore' : 'remove'}`, { root: c.root, category: c.category });
          for (const config of Object.values(state.resources)) { config.category = ''; config.page = 1; }
          await refreshResources(); await manageCategories(); notice(c.removed ? '已恢复分类。' : '已移除分类；资源归入未分类，原文件保持不动。');
        } finally { action.disabled = false; }
      });
      action.disabled = c.effectiveRemoved && !c.removed; if (action.disabled) { action.textContent = '先恢复父分类'; }
      row.append(text, action); target.append(row);
    }
  } catch (e) { errorPanel(target, e, manageCategories); }
}
$$('.category-manage').forEach(b => b.addEventListener('click', () => manageCategories().catch(noticeError)));
async function refreshResources() { state.cache.clear(); state.baseline = null; state.rootsLoaded = false; const view = state.view; if (view === 'library' || view === 'files') await loadResources(view); if (view === 'files') await loadStructure(); }
async function retryResource(f) {
  try { const result = await api('/api/resources/retry', { path: f.path }); notice(isBound(f) ? '已标记刷新。下次主动扫描时重新读取成员与本地配图；手动绑定保留。' : '已清除候选缓存。下次主动扫描此资源时将重新在线识别；本次操作不会启动扫描。'); await refreshResources(); if ($('#detail-dialog').open && state.detailPath === f.path) await resourceDetail(resourceFrom(result, f)); await checkScan(); }
  catch (e) { notice(`重试识别失败：${e.message}。可手动指定商品，或在扫描页重新扫描该资源。`, true); }
}
async function openImport(f, member, copyFolder = false) {
  if (copyFolder ? !folderSource(member) : !importable(member)) throw Error('请选择本地资源文件夹或包文件。');
  if (!copyFolder && !/\.(unitypackage|zip)$/i.test(member.path)) throw Error('此归档需先手动解压，然后重新扫描其文件夹。');
  $('#import-description').textContent = copyFolder ? '将所选原始文件夹复制到 Assets/_BoothItems 的独立新目录，包含其中的源码和素材；压缩包不会自动展开。不会确认商品或改变绑定。' : '仅导入下方已选择的包文件，不批量复制容器里的说明、PSD 或贴图。导入记录中会显示结果。';
  state.importPath = f.path; state.importMemberPath = member.path; $('#import-path').textContent = member.path;
  $('.form-error', $('#import-form')).hidden = true;
  const mode = $('#import-mode'); mode.replaceChildren();
  const native = /\.unitypackage$/i.test(member.path);
  const option = el('option', '', copyFolder ? '复制原始文件夹到 Assets/_BoothItems' : native ? '通过 Unity 导入 .unitypackage' : '自动识别 ZIP 内的 Unity 包（推荐）');
  option.value = copyFolder ? 'copy' : native ? 'unitypackage' : 'auto'; mode.append(option);
  if (!native && !copyFolder) { const extract = el('option', '', '明确解压普通 ZIP 到 Unity 工程（需再次确认）'); extract.value = 'extract'; mode.append(extract); }
  $('#import-project').replaceChildren();
  if (!$('#import-dialog').open) $('#import-dialog').showModal();
  await loadUnityProjects();
}
document.addEventListener('boothlens-unity-connected', () => { if ($('#import-dialog').open) loadUnityProjects().catch(noticeError); });
function selectedUnityProject() { return state.projects.find(p => String(p.id) === $('#import-project').value); }
function updateImportTarget() {
  const target = $('#import-target'), project = selectedUnityProject(); target.replaceChildren();
  if (state.projectsLoading) target.append(el('span', 'secondary', '正在确认已打开的 Editor 心跳…'));
  else if (!project) target.append(el('span', 'secondary', state.projects.length ? '请选择一个已打开的工程，确认完整路径后再导入。' : '未选择目标工程。'));
  else {
    target.append(el('strong', '', `目标工程：${project.name || project.projectPath}`), el('p', 'path', project.projectPath || ''), el('p', 'secondary', `Editor PID ${project.processId ?? '—'}${project.isActive ? ' · 最近聚焦' : ''}${project.busy ? ' · 正忙，请稍后刷新' : ''}${!project.bridgeReady ? ' · 桥接尚未就绪' : ''}`));
    if (project.blockReason) target.append(el('p', 'error', project.blockReason));
    if (project.pendingJobId && !project.canResetPending) target.append(el('p', 'secondary', `任务 #${project.pendingJobId} 正在等待 Unity 完成。先切换到 Unity 检查 Import Unity Package 窗口；若导入早已结束且没有该窗口，使用 Unity 菜单 Tools → BoothLens → Resolve pending import 清理本次会话记录及重复等待任务，再重新提交一次。`));
    if (project.canResetPending) target.append(button('清理旧会话的待处理队列', async () => {
      if (!confirm(`清理工程“${project.name}”中旧会话遗留的导入任务及等待队列？\n\n只取消任务记录，不删除项目资源或源包；不会自动重放导入。清理后请重新选择需要的包。`)) return;
      const result = await api('/api/unity/reset-pending', { projectId: project.id }); notice(result.message || '旧队列已清理，请重新提交需要的导入。'); await loadUnityProjects();
    }));
  }
  $('#import-submit').disabled = state.projectsLoading || state.importSubmitting || !project || !project.bridgeReady || Boolean(project.busy);
}
async function loadUnityProjects() {
  const request = ++state.projectsRequest, select = $('#import-project'), previous = select.value;
  state.projectsLoading = true; state.projects = []; select.disabled = true;
  const pending = el('option', '', '正在确认 Editor 心跳…'); pending.value = ''; select.replaceChildren(pending);
  $('#import-project-refresh').disabled = true; $('#import-project-message').textContent = ''; $('#import-bridge-help').hidden = true; updateImportTarget();
  try {
    const result = await api('/api/unity/projects'); if (request !== state.projectsRequest) return;
    state.projects = Array.isArray(result.projects) ? result.projects.filter(p => p.id != null && p.projectPath) : [];
    select.replaceChildren(); const placeholder = el('option', '', state.projects.length ? '请选择已打开的 Unity 工程' : '没有发现已打开的工程'); placeholder.value = ''; select.append(placeholder);
    for (const project of state.projects) { const option = el('option', '', `${project.name || project.projectPath}${project.isActive ? ' · 最近聚焦' : ''}${project.busy ? ' · 正忙' : ''}${!project.bridgeReady ? ' · 桥接未就绪' : ''}`); option.value = String(project.id); select.append(option); }
    const active = state.projects.filter(p => p.isActive);
    const chosen = state.projects.find(p => String(p.id) === previous) || (state.projects.length === 1 ? state.projects[0] : active.length === 1 ? active[0] : null);
    select.value = chosen ? String(chosen.id) : '';
    $('#import-project-message').textContent = result.message || (state.projects.length > 1 ? '已发现多个已打开工程，请核对目标；最近聚焦的 Editor 优先。' : '仅列出桥接心跳确认存活的 Editor，不使用设置中的历史路径。');
    $('#import-bridge-help').hidden = state.projects.some(p => p.bridgeReady);
  } catch (e) {
    if (request !== state.projectsRequest) return;
    const option = el('option', '', '工程列表读取失败'); option.value = ''; select.replaceChildren(option);
    $('#import-project-message').textContent = `工程列表读取失败：${e.message}。请确认 BoothLens 服务运行，再刷新列表。`;
    $('#import-bridge-help').hidden = false; notice(`读取 Unity 工程失败：${e.message}`, true);
  } finally {
    if (request === state.projectsRequest) { state.projectsLoading = false; select.disabled = !state.projects.length || state.importSubmitting; $('#import-project-refresh').disabled = state.importSubmitting; updateImportTarget(); }
  }
}
async function loadCatalog() {
  const target = $('#catalog-results'); empty(target, '正在读取已保存商品…');
  try { const r = await api(`/api/items?q=${encodeURIComponent(state.catalogQuery)}&page=${state.catalogPage}`); target.replaceChildren(); $('#catalog-total').hidden = false; $('#catalog-total').textContent = r.total;
    if (!r.items?.length) empty(target, '还没有已保存商品。添加商品仅建立商品档案，本地容器仍请从扫描页添加。');
    else { const grid = el('div', 'grid'); for (const item of r.items) { const card = button('', () => catalogDetail(item.id), 'card'); const body = el('div', 'card-body'); body.append(el('div', 'card-title', item.title), el('div', 'card-meta', `${item.shopName || '手动资源'} · ${item.id}`), el('div', 'price', item.priceText || '')); card.append(cover(item), body); grid.append(card); } target.append(grid); }
    pager($('#catalog-pagination'), state.catalogPage, r.total, async p => { state.catalogPage = p; await loadCatalog(); });
  } catch (e) { errorPanel(target, e, loadCatalog); }
}
async function catalogDetail(id) {
  const dialog = $('#detail-dialog'); state.detailPath = ''; ++state.detailVersion; $('#detail-title').textContent = '商品详情'; empty($('#detail-body'), '正在加载…'); if (!dialog.open) dialog.showModal();
  try { const r = await api(`/api/items/${encodeURIComponent(id)}`); renderCatalogDetail(r.item, r.files || []); }
  catch (e) { errorPanel($('#detail-body'), e, () => catalogDetail(id)); }
}
function renderCatalogDetail(item, files) {
  $('#detail-title').textContent = item.title; const root = $('#detail-body'); root.replaceChildren();
  const top = el('div', 'detail-top'), meta = el('div', 'detail-meta'); meta.append(el('strong', '', item.shopName || '手动资源'), el('span', 'badge', `ID ${item.id}`), el('p', '', item.priceText || ''));
  if (item.canonicalUrl && /^https?:\/\//i.test(item.canonicalUrl)) { const a = el('a', 'button subtle', '打开来源页面'); a.href = item.canonicalUrl; a.target = '_blank'; a.rel = 'noopener noreferrer'; meta.append(a); } top.append(cover(item), meta); root.append(top);
  const tags = el('div', 'tags'); for (const tag of item.tags || []) tags.append(el('span', 'badge', tag)); root.append(tags);
  if (item.fetchError) root.append(el('p', 'error', item.fetchError));
  if (item.descShort || item.sections?.length || item.id > 0) {
    const description = detailFold(root, '商品说明');
    if (item.descShort) description.append(el('p', 'description', item.descShort));
    if (item.sections?.length) for (const s of item.sections) { const block = section(description, s.title); block.append(el('p', 'description', s.body)); }
    else if (item.id > 0) description.append(button('加载完整说明', async () => { renderCatalogDetail(await api(`/api/items/${item.id}/description`, {}), files); $('.detail-fold', root).open = true; }));
  }
  const local = section(root, `关联本地容器（${files.filter(isContainer).length}）`); for (const f of files.filter(isContainer)) local.append(button(f.name || f.path, () => resourceDetail(f))); if (!files.filter(isContainer).length) local.append(el('p', 'secondary', '到“扫描与整理”扫描本地资源并绑定商品。'));
}
function renderScan(status) {
  state.scan = status; const running = status.state === 'running'; const names = { running: '正在扫描', completed: '扫描完成', completed_with_errors: '扫描完成（部分识别出错）', cancelled: '扫描已取消', failed: '扫描失败', idle: '尚未开始扫描' };
  $('#scan-progress').hidden = false; $('#scan-state').textContent = names[status.state] || status.state || '等待状态';
  for (const key of ['discovered', 'processed', 'bound', 'review', 'unknown', 'skipped']) $(`#scan-${key}`).textContent = Number(status[key] || 0);
  if (!$('#scan-root').value.trim() && status.root) $('#scan-root').value = status.root;
  $('#scan-current').textContent = status.currentPath || status.message || (running ? '正在发现资源容器…' : '仅主动预览或扫描时读取素材目录；缓存命中资源不遍历成员、不发起在线请求。');
  const scanMessage = [status.error, ...(status.warnings || [])].filter(Boolean).join('；'); $('#scan-error').hidden = !scanMessage; $('#scan-error').textContent = scanMessage;
  const progress = $('#scan-meter'); progress.max = Math.max(Number(status.discovered) || 0, Number(status.processed) || 0, 1); progress.value = Number(status.processed) || 0;
  $('#scan-cancel').hidden = !running; $('#scan-submit').disabled = running; $('#scan-retry').hidden = !['failed', 'cancelled', 'completed_with_errors'].includes(status.state);
  $('#scan-live').hidden = !running; $('#scan-live').textContent = running ? `任务进行中：已处理 ${status.processed || 0} / 已发现 ${status.discovered || 0}。切换页面不影响任务。` : '';
  updateStructureControls();
}
function schedulePoll() { clearTimeout(state.pollTimer); if (state.scan?.state === 'running') state.pollTimer = setTimeout(checkScan, 2000); }
async function checkScan() {
  if (state.pollBusy) return; state.pollBusy = true;
  try {
    const previous = state.scan; const status = await api('/api/scan/status'); renderScan(status); $('#scan-poll-error').hidden = true;
    const terminal = ['completed', 'completed_with_errors', 'cancelled', 'failed'].includes(status.state);
    if (terminal && (previous?.state === 'running' || previous?.scanId !== status.scanId)) { await refreshResources(); if (previous?.state === 'running') notice(`扫描${status.state === 'completed' ? '完成' : status.state === 'completed_with_errors' ? '完成（部分识别出错）' : status.state === 'cancelled' ? '已取消' : '失败'}：已处理 ${status.processed || 0} 个容器。`, status.state === 'failed'); }
  } catch (e) { $('#scan-poll-error').hidden = false; $('#scan-poll-message').textContent = `扫描状态读取失败：${e.message}`; }
  finally { state.pollBusy = false; schedulePoll(); }
}
const scanModeNames = { auto: '自动识别', library: '整个资源库', category: '单个分类', asset: '单个资源' };
function invalidateScanPreview() { ++state.previewRequest; $('#scan-preview-result').hidden = true; }
async function previewScan() {
  const root = $('#scan-root').value.trim(), mode = $('#scan-mode').value, target = $('#scan-preview-result');
  if (!root) throw Error('请输入要预览的素材目录或归档文件路径。');
  const request = ++state.previewRequest; target.hidden = false; target.replaceChildren(el('p', 'secondary', '正在预览目录结构，不启动在线扫描…'));
  const result = await api('/api/scan/preview', { root, mode }); if (request !== state.previewRequest) return;
  target.replaceChildren(el('strong', '', `结构预览 · ${Number(result.discovered ?? result.files?.length ?? 0)} 个资源容器`), el('p', 'path', result.root || root), el('p', 'secondary', `请求模式：${scanModeNames[result.mode || mode] || result.mode || mode}；推断模式：${scanModeNames[result.resolvedMode] || result.resolvedMode || '按所选模式处理'}`));
  if (result.message || result.detectionExplanation) target.append(el('p', 'secondary', result.message || result.detectionExplanation));
  const warnings = result.warnings ? (Array.isArray(result.warnings) ? result.warnings : [result.warnings]) : [];
  if (result.warning) warnings.push(result.warning);
  for (const warning of warnings) target.append(el('p', 'preview-warning', typeof warning === 'string' ? warning : warning.message || JSON.stringify(warning)));
  const list = el('div', 'preview-list');
  for (const file of (Array.isArray(result.files) ? result.files : []).slice(0, 100)) {
    const row = el('div', 'preview-row');
    if (typeof file === 'string') row.append(el('p', 'path', file));
    else row.append(el('strong', '', file.name || file.path || '资源容器'), el('span', 'category-breadcrumb', categoryOf(file)), el('p', 'path', file.path || ''));
    const folderPath = typeof file === 'string' ? file : file.path;
    if (folderPath) row.append(button('编辑目录边界', () => { $('#structure-path').value = folderPath; const repairRoot = $('#scan-stored-root').value || root; fillRootSelect($('#structure-root'), '使用上方输入的素材根目录', repairRoot); openStructurePanel(); }));
    list.append(row);
  }
  if (list.childElementCount) target.append(list);
  if (result.files?.length > 100) target.append(el('p', 'field-note', '仅展示前 100 个预览容器。'));
  target.append(el('p', 'field-note', '预览不会启动在线识别。请核对分类与资源边界；有歧义时选择明确的识别模式后重新预览。'));
}
async function startScan() {
  const root = $('#scan-root').value.trim(); if (!root) throw Error('请输入素材目录或归档文件路径。');
  const result = await api('/api/scan', { root, mode: $('#scan-mode').value, online: true });
  renderScan({ discovered: 0, processed: 0, bound: 0, review: 0, unknown: 0, skipped: 0, ...result, state: result.state || 'running' });
  $('#scan-result').hidden = false; $('#scan-result').textContent = '扫描任务已提交。在线请求会节流，页面可继续使用；名称候选等待手动确认。';
  schedulePoll(); await checkScan();
}
async function loadJobs() { const target = $('#jobs-results'); try { const jobs = await api('/api/jobs'); target.replaceChildren(); if (!jobs.length) { empty(target, '还没有导入记录。'); return; } const list = el('div', 'list'); for (const job of jobs) { const row = el('div', 'row'), main = el('div', 'row-main'); main.append(el('strong', '', `#${job.id} · ${job.title || job.localPath || ''}`), el('p', job.state === 'failed' ? 'error' : '', job.message || ''), el('p', 'path', job.targetPath || '')); row.append(main, el('span', 'badge', job.state)); list.append(row); } target.append(list); } catch (e) { errorPanel(target, e, loadJobs); } }
async function loadSettings() { const s = await api('/api/settings'); $('#settings-status').textContent = s.hasCookie ? '已设置本次会话 Cookie。' : '未设置 Cookie，公共商品通常无需登录。'; }
function onForm(selector, fn) { const form = $(selector); form.addEventListener('submit', async e => { e.preventDefault(); const error = $('.form-error', form); if (error) error.hidden = true; const submit = $('[type=submit]', form); submit.disabled = true; try { await fn(form); } catch (ex) { if (error) { error.textContent = ex.message; error.hidden = false; } else noticeError(ex); } finally { if (selector === '#import-form') updateImportTarget(); else if (selector === '#structure-form') updateStructureControls(); else submit.disabled = selector === '#scan-form' && state.scan?.state === 'running'; } }); }
$$('[data-close]').forEach(b => b.addEventListener('click', () => b.closest('dialog').close()));
$('#open-add').addEventListener('click', () => $('#add-dialog').showModal());
$$('[data-add-mode]').forEach(b => b.addEventListener('click', () => { const custom = b.dataset.addMode === 'custom'; $('#add-form').hidden = custom; $('#custom-form').hidden = !custom; $$('[data-add-mode]').forEach(x => x.setAttribute('aria-pressed', String(x === b))); }));
for (const view of ['library', 'files']) {
  onForm(`#${view}-form`, async () => { const config = state.resources[view]; config.query = $(`#${view}-query`).value.trim(); config.page = 1; await loadResources(view); });
  $(`#${view}-filter`).addEventListener('change', () => { const config = state.resources[view]; config.status = $(`#${view}-filter`).value; config.page = 1; loadResources(view).catch(noticeError); });
  $(`#${view}-root`).addEventListener('change', () => { const config = state.resources[view]; config.root = $(`#${view}-root`).value; config.page = 1; loadResources(view).catch(noticeError); });
  $(`#${view}-clear`).addEventListener('click', () => { Object.assign(state.resources[view], { page: 1, query: '', status: '', category: '', root: '' }); for (const suffix of ['query', 'filter', 'root']) $(`#${view}-${suffix}`).value = ''; loadResources(view).catch(noticeError); });
  $(`#${view}-refresh`).addEventListener('click', () => loadResources(view, true).catch(noticeError));
}
onForm('#catalog-form', async () => { state.catalogQuery = $('#catalog-query').value.trim(); state.catalogPage = 1; await loadCatalog(); });
onForm('#add-form', async () => { await api('/api/items', { input: $('#add-input').value }); $('#add-dialog').close(); notice('商品档案已保存，可在“已保存商品”查看；本地资源请主动扫描。'); if (state.view === 'catalog') await loadCatalog(); });
onForm('#custom-form', async () => { const file = $('#custom-image').files[0]; let imageBase64 = null; if (file) { if (file.size > 8 * 1024 * 1024) throw Error('封面上限为 8 MB。'); const bytes = new Uint8Array(await file.arrayBuffer()); let binary = ''; for (let i = 0; i < bytes.length; i += 8192) binary += String.fromCharCode(...bytes.subarray(i, i + 8192)); imageBase64 = btoa(binary); } await api('/api/custom', { title: $('#custom-title').value, sourceUrl: $('#custom-source').value, imageBase64 }); $('#add-dialog').close(); notice('手动商品档案已保存。'); if (state.view === 'catalog') await loadCatalog(); });
onForm('#scan-form', startScan);
$('#scan-preview').addEventListener('click', async () => { const b = $('#scan-preview'); b.disabled = true; try { await previewScan(); } catch (e) { $('#scan-preview-result').hidden = false; $('#scan-preview-result').replaceChildren(el('p', 'error', `结构预览失败：${e.message}`)); noticeError(e); } finally { b.disabled = false; } });
$('#scan-root').addEventListener('input', () => { invalidateScanPreview(); $('#scan-stored-root').value = state.roots.some(r => r.path === $('#scan-root').value.trim()) ? $('#scan-root').value.trim() : ''; });
$('#scan-stored-root').addEventListener('change', () => { const path = $('#scan-stored-root').value; if (!path) return; $('#scan-root').value = path; $('#structure-root').value = path; const stored = state.roots.find(r => r.path === path); $('#scan-mode').value = scanModeNames[stored?.mode] ? stored.mode : 'auto'; invalidateScanPreview(); loadStructure().catch(noticeError); });
$('#scan-repair').addEventListener('click', () => repairStructure().catch(noticeError));
$('#structure-root').addEventListener('change', () => loadStructure().catch(noticeError));
$('#structure-refresh').addEventListener('click', async () => { await loadRoots(true); await loadStructure(); });
onForm('#structure-form', async () => { await loadRoots(); await applyBoundary($('#structure-path').value.trim(), $('#structure-kind').value, structureRoot()); });
$('#scan-mode').addEventListener('change', invalidateScanPreview);
$('#scan-cancel').addEventListener('click', async () => { const b = $('#scan-cancel'); b.disabled = true; try { await api('/api/scan/cancel', {}); notice('已请求取消，正在等待当前步骤结束。'); await checkScan(); } catch (e) { notice(`取消请求失败：${e.message}`, true); } finally { b.disabled = false; } });
$('#scan-retry').addEventListener('click', () => $('#scan-form').requestSubmit());
$('#scan-status-retry').addEventListener('click', () => checkScan());
onForm('#bind-form', async () => { await api('/api/link', { path: state.bindPath, input: $('#bind-input').value }); $('#bind-dialog').close(); notice('已绑定；后续扫描将保留此次手动选择。'); await refreshResources(); if ($('#detail-dialog').open && state.detailPath === state.bindPath) { const all = await fetchResources({ query: '', status: '' }, true); const f = all.find(x => x.path === state.bindPath); if (f) await resourceDetail(f); } });
onForm('#import-form', async () => {
  const project = selectedUnityProject(), mode = $('#import-mode').value;
  if (state.projectsLoading || !project || !project.bridgeReady) throw Error('请先刷新工程列表并选择已打开且桥接就绪的 Editor。');
  if (project.busy) throw Error('所选 Editor 正忙，请稍后刷新再导入。');
  if (mode === 'copy' && !confirm(`复制整个原始文件夹到工程“${project.name || project.projectPath}”？\n${state.importMemberPath}\n\n目标：${project.projectPath}\\Assets\\_BoothItems（新目录）\n包含子目录与源码，不会自动展开内部压缩包，不改变商品识别或绑定。`)) return;
  if (mode === 'extract' && !confirm(`将所选 ZIP 解压到工程“${project.name || project.projectPath}”的 Assets/_BoothItems 新目录，是否继续？\n${project.projectPath}`)) return;
  state.importSubmitting = true; $('#import-project').disabled = true; $('#import-project-refresh').disabled = true; updateImportTarget();
  try { const job = await api('/api/resources/import', { path: state.importPath, memberPath: state.importMemberPath, mode, projectId: project.id }); $('#import-dialog').close(); notice(`任务 #${job.id}：${job.message || job.state}`, job.state === 'failed'); }
  finally { state.importSubmitting = false; $('#import-project').disabled = !state.projects.length; $('#import-project-refresh').disabled = false; updateImportTarget(); }
});
$('#import-project').addEventListener('change', updateImportTarget);
$('#import-project-refresh').addEventListener('click', () => loadUnityProjects().catch(noticeError));
onForm('#settings-form', async () => { await api('/api/settings', { cookieHeader: $('#cookie-header').value || undefined }); $('#cookie-header').value = ''; notice('本次会话 Cookie 已更新。'); await loadSettings(); });
onForm('#discover-form', async () => { const target = $('#discover-results'); empty(target, '正在搜索 BOOTH…'); try { const hits = await api(`/api/search/booth?q=${encodeURIComponent($('#discover-query').value.trim())}`); target.replaceChildren(); $('#discover-total').hidden = false; $('#discover-total').textContent = hits.length; if (!hits.length) { empty(target, '没有搜索结果，试试商品原文关键词。'); return; } const list = el('div', 'list'); for (const hit of hits) { const row = el('div', 'row'), main = el('div', 'row-main'); main.append(el('strong', '', hit.name), el('p', 'secondary', `${hit.brand || ''} · ${hit.id} · ¥${hit.price ?? '?'}`)); const save = button(hit.alreadyLocal ? '已保存商品' : '保存商品并获取封面', async () => { save.disabled = true; try { await api('/api/items', { input: String(hit.id) }); save.textContent = '已保存商品'; notice('商品已保存。'); } finally { save.disabled = false; } }); row.append(main, save); list.append(row); } target.append(list); } catch (e) { errorPanel(target, e, () => $('#discover-form').requestSubmit()); } });
$('#refresh-jobs').addEventListener('click', () => loadJobs().catch(noticeError));
window.addEventListener('hashchange', () => show(location.hash.slice(1)).catch(noticeError));
document.addEventListener('keydown', e => { if (e.key === '/' && !e.target.matches('input,textarea,select')) { e.preventDefault(); const input = $(`#${state.view}-query`); if (input) input.focus(); } });
show(location.hash.slice(1) || 'library').catch(noticeError);
loadRoots().then(checkScan).catch(noticeError); // Read roots/status only: opening BoothLens never starts a scan.
setInterval(() => { if (state.view === 'jobs' && !document.hidden) loadJobs().catch(() => {}); }, 4000);
