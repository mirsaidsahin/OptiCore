// Application shell: sidebar, top bar, router, command palette and notification drawer.
import {
  html, render, useState, useEffect, useRef, useMemo, useStore, useErrorBoundary, app, call, t, cx, go, currentRoute,
  applyTheme, patchSettings, uiState, setUiState, askElevation, ago, matches, toast,
} from './lib/core.js';
import { Icon, Logo } from './lib/icons.js';
import { Btn, Chip, Drawer, Empty, ErrorBox, FeedbackHost, Loading, MenuHost } from './lib/ui.js';

export const ROUTES = [
  { id: 'dashboard', group: 'overview', icon: 'dashboard', title: ['Pano', 'Dashboard'], keys: 'home ana ekran' },
  { id: 'insights', group: 'overview', icon: 'bulb', title: ['Performans Zekâsı', 'Performance Intelligence'], keys: 'öneri sorun yavaş health score recommendations slow' },
  { id: 'system', group: 'overview', icon: 'monitor', title: ['Sistem', 'System'], keys: 'donanım cpu gpu ram anakart bios pil batarya hardware battery' },
  { id: 'cleanup', group: 'optimize', icon: 'sparkles', title: ['Akıllı Temizlik', 'Smart Cleanup'], keys: 'temp cache önbellek çöp geri dönüşüm duplicate yinelenen junk' },
  { id: 'startup', group: 'optimize', icon: 'power', title: ['Başlangıç', 'Startup'], keys: 'açılış boot servis hizmet zamanlanmış görev services tasks' },
  { id: 'tweaks', group: 'optimize', icon: 'sliders', title: ['Windows Ayarları', 'Windows Tweaks'], keys: 'gizlilik telemetri güç planı privacy power plan visual effects dns' },
  { id: 'gaming', group: 'optimize', icon: 'gamepad', title: ['Oyun', 'Gaming'], keys: 'fps frametime oyun modu game mode ets2 fs25 steam overlay' },
  { id: 'overclock', group: 'optimize', icon: 'rocket', title: ['Hız Aşırtma', 'Overclocking'], keys: 'overclock oc hız aşırtma turbo boost xmp expo pbo afterburner xtu ryzen master voltaj' },
  { id: 'processes', group: 'manage', icon: 'activity', title: ['İşlemler', 'Processes'], keys: 'görev yöneticisi task manager kill sonlandır öncelik priority' },
  { id: 'apps', group: 'manage', icon: 'package', title: ['Uygulamalar', 'Apps'], keys: 'kaldır uninstall program bloatware store' },
  { id: 'storage', group: 'manage', icon: 'disk', title: ['Depolama', 'Storage'], keys: 'disk ssd smart büyük dosyalar large files treemap' },
  { id: 'network', group: 'manage', icon: 'wifi', title: ['Ağ', 'Network'], keys: 'internet ping dns wifi hız speed bağlantı' },
  { id: 'drivers', group: 'manage', icon: 'plug', title: ['Sürücüler', 'Drivers'], keys: 'driver aygıt device yedek backup' },
  { id: 'security', group: 'protect', icon: 'shield', title: ['Güvenlik ve Gizlilik', 'Security & Privacy'], keys: 'defender firewall güvenlik duvarı port hosts izin permissions' },
  { id: 'backup', group: 'protect', icon: 'history', title: ['Geri Alma Merkezi', 'Undo Center'], keys: 'yedek geri yükle restore point geri al undo journal günlük' },
  { id: 'diagnostics', group: 'analyze', icon: 'heart', title: ['Tanılama', 'Diagnostics'], keys: 'çökme crash bsod mavi ekran olay günlüğü event log kararlılık' },
  { id: 'benchmark', group: 'analyze', icon: 'gauge', title: ['Benchmark', 'Benchmark'], keys: 'test stres stress performans puan score' },
  { id: 'automation', group: 'analyze', icon: 'repeat', title: ['Otomatik Bakım', 'Automation'], keys: 'zamanla schedule kural rule bakım maintenance' },
  { id: 'tools', group: 'analyze', icon: 'wrench', title: ['Araçlar', 'Toolbox'], keys: 'sağ tık context menu shredder güvenli sil snapshot kilit locksmith güncelleme update' },
  { id: 'settings', group: 'foot', icon: 'cog', title: ['Ayarlar', 'Settings'], keys: 'tema theme dil language' },
];

const GROUPS = [
  ['overview', 'Genel Bakış', 'Overview'],
  ['optimize', 'Optimize Et', 'Optimize'],
  ['manage', 'Yönet', 'Manage'],
  ['protect', 'Koru', 'Protect'],
  ['analyze', 'Analiz ve Araçlar', 'Analyze & Tools'],
];

const title = (r) => t(r.title[0], r.title[1]);
const pages = new Map();

function Boundary({ children }) {
  const [error, reset] = useErrorBoundary();
  if (error) return html`<div class="page"><${ErrorBox} error=${error} retry=${reset} /></div>`;
  return children;
}

function PageHost({ route }) {
  const [mod, setMod] = useState(() => pages.get(route.id) || null);
  const [error, setError] = useState(null);
  useEffect(() => {
    let alive = true;
    setError(null);
    const cached = pages.get(route.id);
    if (cached) { setMod(cached); return undefined; }
    setMod(null);
    import(`./pages/${route.id}.js`)
      .then((m) => { pages.set(route.id, m); if (alive) setMod(m); })
      .catch((e) => { if (alive) setError(e); });
    return () => { alive = false; };
  }, [route.id]);

  if (error) return html`<div class="page"><${ErrorBox} error=${error} /></div>`;
  if (!mod || pages.get(route.id) !== mod) return html`<div class="page"><${Loading} lines=${6} height=${22} /></div>`;
  return html`<${Boundary} key=${route.id}><${mod.default} sub=${route.sub} /><//>`;
}

function Sidebar({ current, narrow, info, unread }) {
  const item = (r) => html`<button key=${r.id} class=${cx('nav-item', current === r.id && 'on')} onClick=${() => go(r.id)} title=${narrow ? title(r) : null}>
    <${Icon} name=${r.icon} /><span>${title(r)}</span>
  </button>`;
  return html`<aside class="side">
    <div class="brand">
      <${Logo} class="brand-mark" />
      <div class="brand-text"><div class="brand-name">Opti<b>Core</b></div><div class="brand-tag">${t('Sistem zekâsı', 'System intelligence')}</div></div>
    </div>
    <nav class="nav">
      ${GROUPS.map(([id, tr, en]) => html`<div key=${id} class="nav-group">
        <div class="nav-title">${t(tr, en)}</div>
        ${ROUTES.filter((r) => r.group === id).map(item)}
      </div>`)}
    </nav>
    <div class="side-foot">
      ${ROUTES.filter((r) => r.group === 'foot').map(item)}
      <div class="row tiny dim txt" style=${{ padding: '2px 9px' }}>
        <span>v${info.version}</span><span class="sp">${info.elevated ? t('Yönetici', 'Administrator') : t('Standart kullanıcı', 'Standard user')}</span>
      </div>
    </div>
  </aside>`;
}

function Palette({ onClose }) {
  const [query, setQuery] = useState('');
  const [index, setIndex] = useState(0);
  const [extra, setExtra] = useState([]);
  const input = useRef(null);
  const state = app.get();

  useEffect(() => {
    input.current && input.current.focus();
    // Individual tweaks are searchable too: "telemetri", "game mode"...
    call('tweaks.list').then((list) => setExtra((list.items || list || []).map((tw) => ({
      icon: 'sliders', label: tw.title, hint: t('Windows ayarı', 'Windows tweak'), keys: tw.id + ' ' + (tw.description || ''), run: () => go('tweaks/' + tw.id),
    })))).catch(() => {});
  }, []);

  const commands = useMemo(() => [
    ...ROUTES.map((r) => ({ icon: r.icon, label: title(r), hint: t('Sayfa', 'Page'), keys: r.keys + ' ' + r.title.join(' '), run: () => go(r.id) })),
    { icon: 'bolt', label: t('Şu an neden yavaş? Canlı teşhis', 'Why is it slow right now? Live diagnosis'), hint: t('Eylem', 'Action'), keys: 'slow yavaş teşhis diagnose', run: () => go('insights/live') },
    { icon: 'sparkles', label: t('Temizlik taraması başlat', 'Start a cleanup scan'), hint: t('Eylem', 'Action'), keys: 'scan tara temizle clean', run: () => go('cleanup/scan') },
    { icon: state.theme === 'light' ? 'moon' : 'sun', label: t('Temayı değiştir', 'Toggle theme'), hint: t('Eylem', 'Action'), keys: 'dark light koyu açık tema', run: () => patchSettings({ theme: state.theme === 'light' ? 'dark' : 'light' }) },
    { icon: 'globe', label: state.lang === 'en' ? 'Türkçe\'ye geç' : 'Switch to English', hint: t('Eylem', 'Action'), keys: 'dil language türkçe english', run: () => patchSettings({ language: state.lang === 'en' ? 'tr' : 'en' }) },
    !state.info.elevated && { icon: 'shield', label: t('Yönetici olarak yeniden başlat', 'Restart as administrator'), hint: t('Eylem', 'Action'), keys: 'admin elevate yükselt', run: () => askElevation() },
    ...extra,
  ].filter(Boolean), [extra, state.lang, state.theme]);

  const results = useMemo(() => commands.filter((c) => matches(c.label + ' ' + c.keys, query)).slice(0, 40), [commands, query]);
  useEffect(() => setIndex(0), [query]);

  const pick = (c) => { onClose(); c.run(); };
  const onKey = (e) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setIndex((i) => Math.min(results.length - 1, i + 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setIndex((i) => Math.max(0, i - 1)); }
    else if (e.key === 'Enter' && results[index]) pick(results[index]);
    else if (e.key === 'Escape') onClose();
  };

  return html`<div class="palette-back" onMouseDown=${(e) => { if (e.target === e.currentTarget) onClose(); }}>
    <div class="palette">
      <input ref=${input} value=${query} placeholder=${t('Bir sayfa, ayar ya da eylem arayın…', 'Search pages, tweaks and actions…')} onInput=${(e) => setQuery(e.target.value)} onKeyDown=${onKey} spellcheck=${false} />
      <div class="palette-list">
        ${results.map((c, i) => html`<div key=${c.label} class=${cx('palette-item', i === index && 'on')} onMouseEnter=${() => setIndex(i)} onClick=${() => pick(c)}>
          <${Icon} name=${c.icon} /><span class="grow ell">${c.label}</span><span class="tiny dim">${c.hint}</span>
        </div>`)}
        ${!results.length && html`<div class="empty small">${t('Eşleşen bir şey yok.', 'Nothing matches.')}</div>`}
      </div>
    </div>
  </div>`;
}

function NoticeDrawer({ onClose }) {
  const [items, setItems] = useState(null);
  const load = () => call('notices.list').then((r) => setItems(r.items)).catch(() => setItems([]));
  useEffect(() => {
    load();
    call('notices.read', {}).then(() => app.set({ unread: 0 })).catch(() => {});
  }, []);
  const tones = { info: 'info', success: 'good', warn: 'warn', critical: 'bad' };
  return html`<${Drawer} title=${t('Bildirimler', 'Notifications')} onClose=${onClose}
    actions=${items && items.length > 0 && html`<${Btn} size="sm" kind="ghost" icon="trash" onClick=${() => call('notices.clear').then(load)}>${t('Temizle', 'Clear')}<//>`}>
    ${items === null && html`<${Loading} />`}
    ${items && !items.length && html`<${Empty} icon="bell" title=${t('Her şey sakin', 'All quiet')} text=${t('OptiCore dikkatinizi gerektiren bir şey fark ettiğinde burada görünür.', 'When OptiCore notices something that needs your attention, it shows up here.')} />`}
    ${items && items.map((n) => html`<div key=${n.id} class=${cx('finding', tones[n.level] === 'bad' ? 'critical' : tones[n.level] === 'warn' ? 'warning' : tones[n.level])} style=${{ padding: '12px 0', cursor: n.route ? 'pointer' : 'default' }}
        onClick=${() => { if (n.route) { onClose(); go(n.route); } }}>
      <div class="finding-ico"><${Icon} name=${n.level === 'critical' || n.level === 'warn' ? 'alert' : n.level === 'success' ? 'check' : 'info'} /></div>
      <div class="grow"><h4>${n.title}</h4><p>${n.body}</p><div class="tiny dim mt-s">${ago(n.time)}</div></div>
    </div>`)}
  <//>`;
}

function App() {
  const state = useStore(app);
  const [route, setRoute] = useState(currentRoute());
  const [palette, setPalette] = useState(false);
  const [notices, setNotices] = useState(false);
  const [narrow, setNarrow] = useState(() => !!uiState('sidebarNarrow', false));
  const content = useRef(null);
  const active = ROUTES.find((r) => r.id === route.id) || ROUTES[0];

  useEffect(() => {
    const onHash = () => setRoute(currentRoute());
    const onKey = (e) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); setPalette((p) => !p); }
    };
    window.addEventListener('hashchange', onHash);
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('hashchange', onHash); window.removeEventListener('keydown', onKey); };
  }, []);

  useEffect(() => { if (content.current) content.current.scrollTop = 0; }, [route.id]);

  useEffect(() => {
    const poll = () => call('notices.list').then((r) => app.set({ unread: r.unread })).catch(() => {});
    poll();
    const id = setInterval(poll, 20000);
    return () => clearInterval(id);
  }, []);

  const info = state.info;
  return html`<div class=${cx('app', narrow && 'narrow')}>
    <${Sidebar} current=${active.id} narrow=${narrow} info=${info} />
    <div class="main">
      <header class="top">
        <${Btn} kind="ghost" size="sm" icon="menu" title=${t('Kenar çubuğunu daralt/genişlet', 'Collapse or expand the sidebar')}
          onClick=${() => { setNarrow((n) => { setUiState('sidebarNarrow', !n); return !n; }); }} />
        <div class="top-title">${title(active)}</div>
        <div class="top-search" onClick=${() => setPalette(true)}>
          <${Icon} name="search" /><span>${t('Ara ya da komut çalıştır', 'Search or run a command')}</span><span class="kbd">Ctrl K</span>
        </div>
        <div class="sp"></div>
        ${info.dryRun && html`<${Chip} tone="acc" icon="flask" title=${t('Taramalar gerçek; hiçbir değişiklik sisteme uygulanmaz.', 'Scans are real; no change is applied to the system.')}>${t('Simülasyon', 'Simulation')}<//>`}
        ${info.elevated
          ? html`<${Chip} tone="good" icon="shieldCheck">${t('Yönetici', 'Administrator')}<//>`
          : html`<span style=${{ cursor: 'pointer' }} onClick=${askElevation}><${Chip} tone="warn" icon="shield" title=${t('Bazı veriler ve işlemler yönetici yetkisi ister. Yükseltmek için tıklayın.', 'Some data and actions need administrator rights. Click to elevate.')}>${t('Sınırlı mod', 'Limited mode')}<//></span>`}
        <${Btn} kind="ghost" size="sm" title=${state.lang === 'en' ? 'Türkçe' : 'English'} onClick=${() => patchSettings({ language: state.lang === 'en' ? 'tr' : 'en' })}>${state.lang === 'en' ? 'EN' : 'TR'}<//>
        <${Btn} kind="ghost" size="sm" icon=${state.theme === 'light' ? 'moon' : 'sun'} title=${t('Tema', 'Theme')} onClick=${() => patchSettings({ theme: state.theme === 'light' ? 'dark' : 'light' })} />
        <div style=${{ position: 'relative' }}>
          <${Btn} kind="ghost" size="sm" icon="bell" title=${t('Bildirimler', 'Notifications')} onClick=${() => setNotices(true)} />
          ${state.unread > 0 && html`<span class="nav-badge" style=${{ position: 'absolute', top: '-3px', right: '-3px', pointerEvents: 'none' }}>${state.unread > 9 ? '9+' : state.unread}</span>`}
        </div>
      </header>
      <main class="content" ref=${content}>
        <${PageHost} key=${state.lang} route=${route} />
      </main>
    </div>
    ${palette && html`<${Palette} onClose=${() => setPalette(false)} />`}
    ${notices && html`<${NoticeDrawer} onClose=${() => setNotices(false)} />`}
    <${FeedbackHost} />
    <${MenuHost} />
  </div>`;
}

async function boot() {
  const root = document.getElementById('app');
  try {
    const [info, settings] = await Promise.all([call('app.info'), call('settings.get')]);
    app.set({ info, settings, lang: settings.language === 'en' ? 'en' : 'tr', theme: settings.theme, accent: settings.accent });
    applyTheme();
    root.innerHTML = '';
    render(html`<${App} />`, root);
  } catch (e) {
    root.innerHTML = '';
    render(html`<div class="boot"><${Logo} size=${56} /><div class="boot-name">OptiCore</div>
      <div class="muted">${'Motor ile bağlantı kurulamadı / The engine is not reachable.'}</div><div class="dim small">${String(e.message || e)}</div></div>`, root);
  }
}

window.addEventListener('unhandledrejection', (e) => {
  if (e.reason && e.reason.code && e.reason.code !== 'CANCELLED') toast('bad', e.reason.message || String(e.reason));
});

boot();
