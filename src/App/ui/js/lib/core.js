// Core glue: Preact + htm, the engine transport, a tiny store, i18n and formatters.
import { h, render, Component, createContext } from '../../vendor/preact.module.js';
import {
  useState, useEffect, useRef, useMemo, useCallback, useLayoutEffect, useContext, useErrorBoundary,
} from '../../vendor/hooks.module.js';
import htm from '../../vendor/htm.module.js';

export const html = htm.bind(h);
export { h, render, Component, createContext, useState, useEffect, useRef, useMemo, useCallback, useLayoutEffect, useContext, useErrorBoundary };

// ---------------------------------------------------------------- store
export function createStore(initial) {
  let state = initial;
  const subs = new Set();
  return {
    get: () => state,
    set(patch) {
      state = { ...state, ...(typeof patch === 'function' ? patch(state) : patch) };
      subs.forEach((f) => f(state));
    },
    subscribe(f) { subs.add(f); return () => subs.delete(f); },
  };
}

export function useStore(store, select = (s) => s) {
  const [value, setValue] = useState(() => select(store.get()));
  useEffect(() => store.subscribe((s) => setValue(() => select(s))), []);
  return value;
}

/** Application state shared by every page. */
export const app = createStore({
  lang: 'tr', theme: 'dark', accent: 'violet',
  info: null,       // app.info: version, elevated, dryRun, host
  settings: null,   // settings.get
  unread: 0,
});

// ---------------------------------------------------------------- i18n
/** Picks the text for the current UI language. Every UI string is written as t('Türkçe', 'English'). */
export const t = (tr, en) => (app.get().lang === 'en' ? en : tr);
export const lang = () => app.get().lang;
const locale = () => (app.get().lang === 'en' ? 'en-US' : 'tr-TR');

// ---------------------------------------------------------------- transport
export class RpcError extends Error {
  constructor(error) {
    super((error && error.message) || 'Error');
    this.code = (error && error.code) || 'FAILED';
  }
}

const webview = window.chrome && window.chrome.webview;
const token = document.querySelector('meta[name="opticore-token"]')?.content || '';
const pending = new Map();
let seq = 0;

if (webview) {
  webview.addEventListener('message', (e) => {
    const msg = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if (msg.ok) p.resolve(msg.result); else p.reject(new RpcError(msg.error));
  });
}

/** Calls an engine method. Rejects with RpcError (code: ELEVATION_REQUIRED, NOT_FOUND, DENIED, ...). */
export function call(method, params = {}) {
  const id = ++seq;
  const body = JSON.stringify({ id, method, params, lang: app.get().lang });
  if (webview) {
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      webview.postMessage(body);
    });
  }
  return fetch('/api/rpc', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-OptiCore-Token': token }, body })
    .then((r) => {
      if (!r.ok) throw new RpcError({ code: 'TRANSPORT', message: 'HTTP ' + r.status });
      return r.json();
    })
    .then((msg) => {
      if (msg.ok) return msg.result;
      throw new RpcError(msg.error);
    });
}

/** Starts an engine job and resolves with its result, reporting progress along the way. */
export async function runJob(method, params = {}, onUpdate) {
  let job = await call(method, params);
  for (;;) {
    onUpdate && onUpdate(job);
    if (job.state !== 'running') break;
    await new Promise((r) => setTimeout(r, 350));
    job = await call('jobs.get', { id: job.id });
  }
  if (job.state === 'done') return job.result;
  throw new RpcError({ code: job.errorCode || (job.state === 'cancelled' ? 'CANCELLED' : 'FAILED'), message: job.error || '' });
}

// ---------------------------------------------------------------- hooks
/** Loads engine data, optionally polling. Keeps the previous data while refreshing so the UI never flickers. */
export function useRpc(method, params, opts = {}) {
  const { interval = 0, enabled = true, deps = [] } = opts;
  const [state, setState] = useState({ data: undefined, error: null, loading: true });
  const alive = useRef(true);
  const version = useRef(0);
  const paramsRef = useRef(params);
  paramsRef.current = params;

  const load = useCallback(async (silent) => {
    const mine = ++version.current;
    if (!silent) setState((s) => ({ ...s, loading: true }));
    try {
      const data = await call(method, paramsRef.current || {});
      if (alive.current && mine === version.current) setState({ data, error: null, loading: false });
    } catch (error) {
      if (alive.current && mine === version.current) setState((s) => ({ data: s.data, error, loading: false }));
    }
  }, [method]);

  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => {
    if (!enabled) return undefined;
    load(false);
    if (!interval) return undefined;
    const id = setInterval(() => { if (!document.hidden) load(true); }, interval);
    return () => clearInterval(id);
  }, [method, enabled, interval, ...deps]);

  return {
    ...state,
    reload: () => load(true),
    setData: (data) => setState((s) => ({ ...s, data: typeof data === 'function' ? data(s.data) : data })),
  };
}

/** Runs one job at a time and exposes its live state: {job, running, run, cancel, reset}. */
export function useJob() {
  const [job, setJob] = useState(null);
  const alive = useRef(true);
  useEffect(() => () => { alive.current = false; }, []);
  const run = useCallback((method, params) => runJob(method, params, (j) => { if (alive.current) setJob(j); }), []);
  const cancel = useCallback(() => { if (job) call('jobs.cancel', { id: job.id }).catch(() => {}); }, [job]);
  return { job, running: !!job && job.state === 'running', run, cancel, reset: () => setJob(null) };
}

export function useInterval(fn, ms) {
  const ref = useRef(fn);
  ref.current = fn;
  useEffect(() => {
    if (!ms) return undefined;
    const id = setInterval(() => ref.current(), ms);
    return () => clearInterval(id);
  }, [ms]);
}

// ---------------------------------------------------------------- global feedback (toasts, dialogs)
export const feedback = createStore({ toasts: [], dialog: null });
let toastSeq = 0;

export function toast(kind, title, body) {
  const id = ++toastSeq;
  feedback.set((s) => ({ toasts: [...s.toasts.slice(-3), { id, kind, title, body }] }));
  setTimeout(() => feedback.set((s) => ({ toasts: s.toasts.filter((x) => x.id !== id) })), kind === 'bad' ? 7000 : 4200);
}

/** Asks the user to confirm. Resolves true/false. */
export function confirmDialog(options) {
  return new Promise((resolve) => feedback.set({ dialog: { ...options, resolve } }));
}

/** Downloads and installs the pending update (job 'update.install'), then offers to restart. `run` is useJob().run. */
export async function installUpdate(run) {
  const r = await run('update.install');
  if (r.installed) {
    const ok = await confirmDialog({ title: t(`OptiCore ${r.version} kuruldu`, `OptiCore ${r.version} installed`), body: t('Yeni sürümü kullanmak için OptiCore yeniden başlatılsın mı?', 'Restart OptiCore to use the new version?'), confirm: t('Yeniden başlat', 'Restart') });
    if (ok) await call('shell.restartApp');
  }
  return r;
}

/** Shown when an action needs administrator rights. */
export function askElevation() {
  const info = app.get().info;
  return confirmDialog({
    title: t('Yönetici yetkisi gerekiyor', 'Administrator rights needed'),
    body: info && info.host !== 'desktop'
      ? t('Bu işlem Windows\'un korumalı alanlarına dokunuyor. Tarayıcı önizlemesinde yükseltme yapılamaz; masaüstü uygulamasını yönetici olarak çalıştırın.',
          'This action touches protected parts of Windows. Elevation is not possible in the browser preview; run the desktop app as administrator.')
      : t('Bu işlem Windows\'un korumalı alanlarına dokunuyor. OptiCore yönetici olarak yeniden başlatılsın mı?',
          'This action touches protected parts of Windows. Restart OptiCore as administrator?'),
    confirm: t('Yönetici olarak yeniden başlat', 'Restart as administrator'),
    hideConfirm: info && info.host !== 'desktop',
  }).then((ok) => { if (ok) call('shell.relaunchElevated').catch((e) => toast('bad', e.message)); return false; });
}

/**
 * Runs an action with uniform feedback: a success toast, an elevation prompt, or an error toast.
 * Returns the action's result, or undefined when it failed.
 */
export async function act(fn, success) {
  try {
    const result = await fn();
    if (success) toast('good', typeof success === 'function' ? success(result) : success);
    return result === undefined ? true : result;
  } catch (e) {
    if (e.code === 'ELEVATION_REQUIRED') await askElevation();
    else if (e.code !== 'CANCELLED') toast('bad', e.message || String(e));
    return undefined;
  }
}

// ---------------------------------------------------------------- formatters
const numberFormats = new Map();
function nf(digits, minDigits = 0) {
  const key = locale() + ':' + digits + ':' + minDigits;
  let f = numberFormats.get(key);
  if (!f) numberFormats.set(key, (f = new Intl.NumberFormat(locale(), { maximumFractionDigits: digits, minimumFractionDigits: minDigits })));
  return f;
}

export const num = (v, digits = 0) => (v == null || Number.isNaN(v) ? '–' : nf(digits).format(v));
export const pct = (v, digits = 0) => (v == null ? '–' : lang() === 'en' ? num(v, digits) + '%' : '%' + num(v, digits));

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
export function bytes(b, digits) {
  if (b == null) return '–';
  let v = Math.abs(b), i = 0;
  while (v >= 1024 && i < UNITS.length - 1) { v /= 1024; i++; }
  const d = digits ?? (i === 0 ? 0 : v >= 100 ? 0 : v >= 10 ? 1 : 2);
  return (b < 0 ? '-' : '') + num(v, d) + ' ' + UNITS[i];
}
export const rate = (b) => bytes(b) + '/s';
/** Network speed in bits per second, the unit ISPs advertise. */
export function bits(bytesPerSecond) {
  const v = (bytesPerSecond || 0) * 8;
  if (v >= 1e9) return num(v / 1e9, 2) + ' Gbps';
  if (v >= 1e6) return num(v / 1e6, v >= 1e8 ? 0 : 1) + ' Mbps';
  if (v >= 1e3) return num(v / 1e3, 0) + ' Kbps';
  return num(v, 0) + ' bps';
}
export const ghz = (mhz) => (mhz ? num(mhz / 1000, 2) + ' GHz' : '–');
export const ms = (v, digits = 0) => (v == null ? '–' : num(v, digits) + ' ms');
export const temp = (v) => (v == null ? '–' : num(v, 0) + ' °C');
/** Where the headline temperature came from (LiveSample.tempSource). */
export const tempSource = (s) => (s === 'lhm' ? 'LibreHardwareMonitor' : s === 'ohm' ? 'OpenHardwareMonitor' : s === 'gpu' ? t('Ekran kartı sensörü', 'GPU sensor') : s === 'acpi' ? t('ACPI termal bölgesi', 'ACPI thermal zone') : '');
export const noTempText = () => t('Bu bilgisayar işlemci sıcaklığını Windows\'a bildirmiyor. LibreHardwareMonitor açıksa OptiCore sıcaklığı oradan okur.', 'This PC does not report its CPU temperature to Windows. If LibreHardwareMonitor is running, OptiCore reads the temperature from it.');

export function duration(seconds) {
  if (seconds == null) return '–';
  const s = Math.max(0, Math.round(seconds));
  const d = Math.floor(s / 86400), hh = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
  if (d > 0) return t(`${d} gün ${hh} sa`, `${d}d ${hh}h`);
  if (hh > 0) return t(`${hh} sa ${m} dk`, `${hh}h ${m}m`);
  if (m > 0) return t(`${m} dk`, `${m} min`);
  return t(`${s} sn`, `${s}s`);
}

const toDate = (v) => (v instanceof Date ? v : typeof v === 'number' ? new Date(v) : v ? new Date(v) : null);
export function date(v) {
  const d = toDate(v);
  return d && !Number.isNaN(+d) ? d.toLocaleDateString(locale(), { year: 'numeric', month: 'short', day: 'numeric' }) : '–';
}
export function dateTime(v) {
  const d = toDate(v);
  return d && !Number.isNaN(+d) ? d.toLocaleString(locale(), { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '–';
}
export function clock(v) {
  const d = toDate(v);
  return d && !Number.isNaN(+d) ? d.toLocaleTimeString(locale(), { hour: '2-digit', minute: '2-digit' }) : '–';
}
export function ago(v) {
  const d = toDate(v);
  if (!d || Number.isNaN(+d)) return '–';
  const diff = (Date.now() - d) / 1000;
  const rtf = new Intl.RelativeTimeFormat(locale(), { numeric: 'auto' });
  if (diff < 60) return t('az önce', 'just now');
  if (diff < 3600) return rtf.format(-Math.floor(diff / 60), 'minute');
  if (diff < 86400) return rtf.format(-Math.floor(diff / 3600), 'hour');
  if (diff < 86400 * 30) return rtf.format(-Math.floor(diff / 86400), 'day');
  if (diff < 86400 * 365) return rtf.format(-Math.floor(diff / (86400 * 30)), 'month');
  return rtf.format(-Math.floor(diff / (86400 * 365)), 'year');
}

/** good / warn / bad by thresholds (higher is worse). */
export const tone = (v, warn, bad) => (v >= bad ? 'bad' : v >= warn ? 'warn' : 'good');
export const cx = (...parts) => parts.filter(Boolean).join(' ');
export const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

/** Case- and Turkish-insensitive substring match for search boxes. */
export function matches(text, query) {
  if (!query) return true;
  const norm = (s) => String(s ?? '').toLocaleLowerCase('tr-TR').replace(/ı/g, 'i');
  const hay = norm(text);
  return norm(query).split(/\s+/).filter(Boolean).every((part) => hay.includes(part));
}

/** Saves text to a file: a save dialog in the desktop app, a browser download in the preview. */
export async function saveText(name, text, mime = 'application/json') {
  try {
    return await call('shell.saveText', { name, text });
  } catch (e) {
    if (e.code !== 'UNSUPPORTED') throw e;
    const url = URL.createObjectURL(new Blob([text], { type: mime + ';charset=utf-8' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 3000);
    return true;
  }
}

// ---------------------------------------------------------------- navigation
export function go(route) { window.location.hash = '#/' + route; }
export function currentRoute() {
  const raw = window.location.hash.replace(/^#\/?/, '');
  const [id, ...rest] = raw.split('/');
  return { id: id || 'dashboard', sub: rest.join('/') };
}

// ---------------------------------------------------------------- settings helpers
export async function patchSettings(patch) {
  const settings = await call('settings.patch', patch);
  app.set({ settings, lang: settings.language === 'en' ? 'en' : 'tr', theme: settings.theme, accent: settings.accent });
  applyTheme();
  return settings;
}

export function applyTheme() {
  const { theme, accent, lang: l, settings } = app.get();
  const resolved = theme === 'system' ? (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark') : theme;
  const root = document.documentElement;
  root.dataset.theme = resolved;
  root.dataset.accent = accent || 'violet';
  root.lang = l;
  root.classList.toggle('reduce', !!(settings && settings.reduceMotion));
  call('shell.setTheme', { theme: resolved }).catch(() => {});
}

/** Reads or writes a value in the free-form UI state bag that is saved with the settings. */
export function uiState(key, fallback) {
  const bag = app.get().settings?.ui || {};
  return key in bag ? bag[key] : fallback;
}
export function setUiState(key, value) {
  return patchSettings({ ui: { [key]: value } });
}
