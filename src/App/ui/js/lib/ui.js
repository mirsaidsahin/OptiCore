// Shared UI components. Every page is built from these.
import {
  html, useState, useEffect, useMemo, useRef, useStore, createStore, call, feedback, app, t, cx, num, pct, askElevation,
} from './core.js';
import { Icon } from './icons.js';

export { Icon };

// ---------------------------------------------------------------- structure
export function Page({ title, desc, actions, children }) {
  return html`<div class="page">
    <div class="page-head">
      <div><h1>${title}</h1>${desc && html`<p>${desc}</p>`}</div>
      ${actions && html`<div class="actions">${actions}</div>`}
    </div>
    ${children}
  </div>`;
}

export function Card({ title, sub, actions, children, pad = true, flush, class: cls, icon }) {
  if (!title && !actions) return html`<div class=${cx('card', pad && 'pad', cls)}>${children}</div>`;
  return html`<div class=${cx('card', cls)}>
    <div class="card-h">
      ${icon && html`<${Icon} name=${icon} size="16" class="dim" />`}
      <h3>${title}</h3>
      ${sub && html`<span class="sub">${sub}</span>`}
      ${actions && html`<div class="sp row gap-s">${actions}</div>`}
    </div>
    <div class=${cx('card-b', flush && 'flush')}>${children}</div>
  </div>`;
}

export const Section = ({ children }) => html`<div class="sect">${children}</div>`;

// ---------------------------------------------------------------- controls
/** Button. When onClick returns a promise the button shows a spinner and ignores clicks until it settles. */
export function Btn({ kind, size, icon, onClick, busy, disabled, children, title, class: cls }) {
  const [pending, setPending] = useState(false);
  const alive = useRef(true);
  useEffect(() => () => { alive.current = false; }, []);
  const click = (e) => {
    if (!onClick || pending) return;
    const result = onClick(e);
    if (result && typeof result.then === 'function') {
      setPending(true);
      result.finally(() => { if (alive.current) setPending(false); });
    }
  };
  const working = busy || pending;
  return html`<button class=${cx('btn', kind, size, !children && 'icon', cls)} onClick=${click} disabled=${disabled || working} title=${title}>
    ${working ? html`<span class="spin"></span>` : icon && html`<${Icon} name=${icon} />`}${children}
  </button>`;
}

export function Switch({ on, onChange, disabled, mixed, title }) {
  return html`<button class=${cx('switch', on && 'on', mixed && 'mixed')} role="switch" aria-checked=${!!on} disabled=${disabled} title=${title}
    onClick=${(e) => { e.stopPropagation(); onChange && onChange(!on); }}></button>`;
}

export const Chip = ({ tone, icon, children, title, class: cls }) =>
  html`<span class=${cx('chip', tone, cls)} title=${title}>${icon && html`<${Icon} name=${icon} />`}${children}</span>`;

export const Dot = ({ tone }) => html`<i class=${cx('dot', tone)}></i>`;

export function Tabs({ value, onChange, items }) {
  return html`<div class="tabs" role="tablist">
    ${items.map((it) => html`<button key=${it.id} role="tab" class=${value === it.id ? 'on' : ''} onClick=${() => onChange(it.id)}>
      ${it.icon && html`<${Icon} name=${it.icon} size="14" />`}${it.label}${it.count != null && html`<span class="n">${it.count}</span>`}
    </button>`)}
  </div>`;
}

export function Search({ value, onInput, placeholder, class: cls, autofocus }) {
  const ref = useRef(null);
  useEffect(() => { if (autofocus && ref.current) ref.current.focus(); }, []);
  return html`<div class=${cx('search', cls)}>
    <${Icon} name="search" />
    <input ref=${ref} class="input" type="text" value=${value} placeholder=${placeholder || t('Ara…', 'Search…')} onInput=${(e) => onInput(e.target.value)} spellcheck=${false} />
  </div>`;
}

export function Select({ value, onChange, options, class: cls, disabled }) {
  return html`<select class=${cx('select', cls)} value=${value} disabled=${disabled} onChange=${(e) => onChange(e.target.value)}>
    ${options.map((o) => html`<option key=${o.value} value=${o.value}>${o.label}</option>`)}
  </select>`;
}

export const Check = ({ checked, onChange, disabled }) =>
  html`<input type="checkbox" class="check" checked=${checked} disabled=${disabled} onClick=${(e) => e.stopPropagation()} onChange=${(e) => onChange(e.target.checked)} />`;

// ---------------------------------------------------------------- data display
export function Bar({ value, max = 100, tone: tn, thick, indeterminate }) {
  const w = max > 0 ? Math.min(100, Math.max(0, (value / max) * 100)) : 0;
  return html`<div class=${cx('bar', tn, thick && 'thick', indeterminate && 'indet')}><i style=${{ width: w + '%' }}></i></div>`;
}

export const Stat = ({ label, value, unit, tone: tn, sub }) => html`<div class="stat">
  <div class="l">${label}</div>
  <div class=${cx('v', tn)}>${value}${unit && html`<small>${unit}</small>`}</div>
  ${sub && html`<div class="tiny dim">${sub}</div>`}
</div>`;

/** rows: [[label, value], ...]; rows with a null/undefined value are skipped. */
export const KV = ({ rows }) => html`<div class="kv">
  ${rows.filter((r) => r && r[1] != null && r[1] !== '').map(([k, v]) => html`<div key=${k}><span>${k}</span><b>${v}</b></div>`)}
</div>`;

export function Banner({ tone: tn = 'info', icon, children, action }) {
  const fallback = { warn: 'alert', bad: 'alert', good: 'check', info: 'info', acc: 'sparkles' }[tn] || 'info';
  return html`<div class=${cx('banner', tn)}><${Icon} name=${icon || fallback} /><div class="grow">${children}</div>${action}</div>`;
}

export const Empty = ({ icon = 'check', title, text, action }) => html`<div class="empty">
  <${Icon} name=${icon} /><b>${title}</b>${text && html`<div class="small" style=${{ maxWidth: '460px' }}>${text}</div>`}${action}
</div>`;

export const Loading = ({ lines = 4, height = 16 }) => html`<div class="col" style=${{ padding: '6px 0' }}>
  ${Array.from({ length: lines }, (_, i) => html`<div key=${i} class="sk" style=${{ height: height + 'px', width: 100 - i * 9 + '%' }}></div>`)}
</div>`;

export function ErrorBox({ error, retry }) {
  const elevation = error && error.code === 'ELEVATION_REQUIRED';
  return html`<${Banner} tone=${elevation ? 'warn' : 'bad'} icon=${elevation ? 'shieldAlert' : 'alert'}
    action=${elevation
      ? html`<${Btn} size="sm" onClick=${askElevation}>${t('Yükselt', 'Elevate')}<//>`
      : retry && html`<${Btn} size="sm" icon="refresh" onClick=${retry}>${t('Yeniden dene', 'Retry')}<//>`}>
    ${(error && error.message) || t('Bir şeyler ters gitti.', 'Something went wrong.')}
  <//>`;
}

/** Standard wrapper for useRpc results: skeleton, error, or the content. */
export function Async({ state, children, lines }) {
  if (state.data === undefined) return state.error ? html`<${ErrorBox} error=${state.error} retry=${state.reload} />` : html`<${Loading} lines=${lines} />`;
  return children(state.data);
}

export function JobBar({ job, onCancel }) {
  if (!job || job.state !== 'running') return null;
  return html`<div class="card pad">
    <div class="row">
      <div class="grow">
        <div class="b">${job.title}</div>
        <div class="small muted ell">${job.message || t('Çalışıyor…', 'Working…')}</div>
      </div>
      <span class="num muted">${job.progress >= 0 ? pct(job.progress * 100) : ''}</span>
      ${onCancel && html`<${Btn} size="sm" kind="ghost" icon="x" onClick=${onCancel}>${t('İptal', 'Cancel')}<//>`}
    </div>
    <div class="mt-s"><${Bar} value=${job.progress >= 0 ? job.progress * 100 : 0} indeterminate=${job.progress < 0} /></div>
  </div>`;
}

export const AdminChip = () => html`<${Chip} tone="warn" icon="shield" title=${t('Yönetici yetkisi gerektirir', 'Needs administrator rights')}>${t('Yönetici', 'Admin')}<//>`;

// ---------------------------------------------------------------- table
/**
 * columns: [{id, label, render?(row), sort?: true | (row) => value, num?, align?, class?, title?}]
 * Sorting is client-side; long lists render in pages of `limit` rows.
 */
export function Table({ columns, rows, rowKey, onRow, selectedKey, initialSort, empty, limit = 250, maxHeight }) {
  const [sort, setSort] = useState(initialSort || null);
  const [shown, setShown] = useState(limit);
  const sorted = useMemo(() => {
    if (!sort) return rows;
    const col = columns.find((c) => c.id === sort.id);
    if (!col) return rows;
    const get = typeof col.sort === 'function' ? col.sort : (r) => r[col.id];
    const dir = sort.dir === 'asc' ? 1 : -1;
    return [...rows].sort((a, b) => {
      const x = get(a), y = get(b);
      if (x == null && y == null) return 0;
      if (x == null) return 1;
      if (y == null) return -1;
      return (typeof x === 'string' ? x.localeCompare(String(y), undefined, { sensitivity: 'base', numeric: true }) : x - y) * dir;
    });
  }, [rows, sort, columns]);

  const toggle = (col) => {
    if (!col.sort) return;
    setSort((s) => (s && s.id === col.id ? { id: col.id, dir: s.dir === 'asc' ? 'desc' : 'asc' } : { id: col.id, dir: col.num ? 'desc' : 'asc' }));
  };
  const key = (row, i) => (rowKey ? (typeof rowKey === 'function' ? rowKey(row) : row[rowKey]) : i);

  if (!rows.length && empty) return empty;
  return html`<div class="tbl-wrap" style=${maxHeight ? { maxHeight } : null}>
    <table class="tbl">
      <thead><tr>
        ${columns.map((c) => html`<th key=${c.id} class=${cx(c.sort && 'sort', (c.num || c.align === 'r') && 'r', c.align === 'c' && 'c', c.class)} title=${c.title} onClick=${() => toggle(c)}>
          ${c.label}${sort && sort.id === c.id && html`<span class="arr">${sort.dir === 'asc' ? '↑' : '↓'}</span>`}
        </th>`)}
      </tr></thead>
      <tbody>
        ${sorted.slice(0, shown).map((row, i) => html`<tr key=${key(row, i)} class=${cx(onRow && 'click', selectedKey != null && key(row, i) === selectedKey && 'sel')} onClick=${onRow ? () => onRow(row) : null}>
          ${columns.map((c) => html`<td key=${c.id} class=${cx((c.num || c.align === 'r') && 'r', c.align === 'c' && 'c', c.class)}>${c.render ? c.render(row) : row[c.id]}</td>`)}
        </tr>`)}
      </tbody>
    </table>
    ${sorted.length > shown && html`<div class="row" style=${{ justifyContent: 'center', padding: '10px' }}>
      <${Btn} size="sm" kind="ghost" onClick=${() => setShown((n) => n + limit)}>${t(`${num(sorted.length - shown)} satır daha göster`, `Show ${num(sorted.length - shown)} more rows`)}<//>
    </div>`}
  </div>`;
}

// ---------------------------------------------------------------- overlays
export function Modal({ title, onClose, children, footer, wide }) {
  useEffect(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose && onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);
  return html`<div class="modal-back" onMouseDown=${(e) => { if (e.target === e.currentTarget && onClose) onClose(); }}>
    <div class=${cx('modal', wide && 'wide')} role="dialog" aria-modal="true">
      <div class="modal-h"><h2>${title}</h2><div class="sp"></div>${onClose && html`<${Btn} kind="ghost" size="sm" icon="x" onClick=${onClose} />`}</div>
      <div class="modal-b">${children}</div>
      ${footer && html`<div class="modal-f">${footer}</div>`}
    </div>
  </div>`;
}

export function Drawer({ title, onClose, children, wide, actions }) {
  useEffect(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);
  return html`<div>
    <div class="drawer-back" onClick=${onClose}></div>
    <aside class=${cx('drawer', wide && 'wide')}>
      <div class="drawer-h"><h2 class="grow ell">${title}</h2>${actions}<${Btn} kind="ghost" size="sm" icon="x" onClick=${onClose} /></div>
      <div class="drawer-b">${children}</div>
    </aside>
  </div>`;
}

/** Renders toasts and the confirm dialog requested through core.js (toast, confirmDialog). */
export function FeedbackHost() {
  const { toasts, dialog } = useStore(feedback);
  const close = (ok) => { dialog.resolve(ok); feedback.set({ dialog: null }); };
  const icons = { good: 'check', bad: 'alert', warn: 'alert', info: 'info' };
  return html`<div>
    ${dialog && html`<${Modal} title=${dialog.title} onClose=${() => close(false)} wide=${dialog.wide}
      footer=${html`
        <${Btn} kind="ghost" onClick=${() => close(false)}>${dialog.cancel || t('Vazgeç', 'Cancel')}<//>
        ${!dialog.hideConfirm && html`<${Btn} kind=${dialog.danger ? 'danger' : 'primary'} onClick=${() => close(true)}>${dialog.confirm || t('Onayla', 'Confirm')}<//>`}`}>
      <div class="muted" style=${{ whiteSpace: 'pre-line' }}>${dialog.body}</div>
      ${dialog.details && html`<div class="code mt">${dialog.details}</div>`}
    <//>`}
    <div class="toasts">
      ${toasts.map((x) => html`<div key=${x.id} class=${cx('toast', x.kind)}>
        <${Icon} name=${icons[x.kind] || 'info'} />
        <div class="grow"><div class="b">${x.title}</div>${x.body && html`<div class="small muted">${x.body}</div>`}</div>
      </div>`)}
    </div>
  </div>`;
}

// ---------------------------------------------------------------- context menu
const menuStore = createStore({ menu: null });

/** items: [{label, icon, onClick, danger, disabled} | 'sep'] */
export function openMenu(event, items) {
  event.preventDefault();
  event.stopPropagation();
  const x = Math.min(event.clientX, window.innerWidth - 230);
  const y = Math.min(event.clientY, window.innerHeight - items.length * 34 - 20);
  menuStore.set({ menu: { x, y, items } });
}

export function MenuHost() {
  const { menu } = useStore(menuStore);
  useEffect(() => {
    const close = () => menuStore.set({ menu: null });
    window.addEventListener('click', close);
    window.addEventListener('blur', close);
    window.addEventListener('resize', close);
    return () => { window.removeEventListener('click', close); window.removeEventListener('blur', close); window.removeEventListener('resize', close); };
  }, []);
  if (!menu) return null;
  return html`<div class="menu" style=${{ left: menu.x + 'px', top: Math.max(8, menu.y) + 'px' }}>
    ${menu.items.filter(Boolean).map((it, i) => it === 'sep'
      ? html`<hr key=${i} />`
      : html`<button key=${i} class=${it.danger ? 'danger' : ''} disabled=${it.disabled} onClick=${it.onClick}>
          ${it.icon && html`<${Icon} name=${it.icon} />`}${it.label}
        </button>`)}
  </div>`;
}

// ---------------------------------------------------------------- file icons (fetched once, cached)
const iconCache = new Map();
const iconStore = createStore({ v: 0 });
let iconQueue = new Set();
let iconTimer = null;

function requestIcon(path) {
  iconCache.set(path, undefined);
  iconQueue.add(path);
  if (iconTimer) return;
  iconTimer = setTimeout(async () => {
    const paths = [...iconQueue];
    iconQueue = new Set();
    iconTimer = null;
    try {
      const result = await call('icons.get', { paths });
      paths.forEach((p) => iconCache.set(p, result[p] || null));
    } catch {
      paths.forEach((p) => iconCache.set(p, null));
    }
    iconStore.set((s) => ({ v: s.v + 1 }));
  }, 40);
}

export function FileIcon({ path, large, fallback = 'package' }) {
  useStore(iconStore, (s) => s.v);
  if (path && !iconCache.has(path)) requestIcon(path);
  const url = path ? iconCache.get(path) : null;
  return url
    ? html`<img class=${cx('ico', large && 'lg')} src=${url} alt="" />`
    : html`<span class=${cx('ico ph', large && 'lg')}><${Icon} name=${fallback} /></span>`;
}

// ---------------------------------------------------------------- small conveniences
/** "Run as administrator" hint shown at the top of pages whose data is richer when elevated. */
export function ElevationHint({ children }) {
  const info = useStore(app, (s) => s.info);
  if (!info || info.elevated) return null;
  return html`<${Banner} tone="warn" icon="shield" action=${html`<${Btn} size="sm" onClick=${askElevation}>${t('Yönetici olarak aç', 'Run as admin')}<//>`}>${children}<//>`;
}

export function SimulationNote() {
  const info = useStore(app, (s) => s.info);
  if (!info || !info.dryRun) return null;
  return html`<${Banner} tone="acc" icon="flask">${t('Simülasyon modu açık: taramalar gerçek, ama hiçbir değişiklik sisteme uygulanmaz.', 'Simulation mode is on: scans are real, but no change is applied to the system.')}<//>`;
}

export function copyText(text) {
  return navigator.clipboard.writeText(text).catch(() => {});
}

/** Link-styled button that opens a URL through the engine (which only allows web links and Windows settings pages). */
export const ExtLink = ({ url, children }) =>
  html`<a href="#" onClick=${(e) => { e.preventDefault(); call('shell.open', { url }).catch(() => {}); }}>${children}</a>`;

/** Opens a web page or a Windows settings page (ms-settings:...) through the engine. */
export const openUrl = (url) => call('shell.open', { url }).catch(() => {});
