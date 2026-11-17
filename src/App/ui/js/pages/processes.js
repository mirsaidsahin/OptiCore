// Process Manager: live list, groups, tree, details, priority, efficiency mode, connections, rules.
import {
  html, useState, useEffect, useMemo, useRpc, call, act, t, cx, go, num, pct, bytes, rate, dateTime, duration, matches, confirmDialog, toast, app,
} from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Search, Table, Drawer, KV, FileIcon, Icon, Empty, Loading, Select, Switch, openMenu, copyText, Banner, Async, Modal, Stat } from '../lib/ui.js';
import { Spark, BarList } from '../lib/charts.js';

const PRIORITIES = [['idle', 'Düşük', 'Low'], ['below', 'Normalin altı', 'Below normal'], ['normal', 'Normal', 'Normal'], ['above', 'Normalin üstü', 'Above normal'], ['high', 'Yüksek', 'High']];
const groupName = (p) => p.description || p.name.replace(/\.exe$/i, '');
const heat = (ratio, color) => (ratio > 0.02 ? { background: `color-mix(in srgb, ${color} ${Math.round(Math.min(1, ratio) * 34)}%, transparent)` } : null);

function endProcess(p, tree) {
  return confirmDialog({
    title: tree ? t('İşlem ağacı sonlandırılsın mı?', 'End the process tree?') : t('İşlem sonlandırılsın mı?', 'End this process?'),
    body: t(`${p.name} (PID ${p.pid}) ${tree ? 've başlattığı tüm işlemler ' : ''}kapatılacak. Kaydedilmemiş veriler kaybolur.`,
      `${p.name} (PID ${p.pid}) ${tree ? 'and every process it started ' : ''}will be closed. Unsaved data will be lost.`),
    confirm: t('Sonlandır', 'End'), danger: true,
  }).then((ok) => ok && act(() => call('processes.kill', { pid: p.pid, tree }),
    (r) => (r.simulated ? t('Simülasyon: işlem sonlandırılmadı', 'Simulation: the process was not ended') : t(`${r.killed} işlem sonlandırıldı`, `${r.killed} process(es) ended`))));
}

function Details({ pid, onClose }) {
  const d = useRpc('processes.details', { pid }, { interval: 2500, deps: [pid] });
  const history = useRpc('processes.history', { name: d.data ? groupName(d.data.process) : '' }, { interval: 5000, enabled: !!d.data, deps: [d.data && d.data.process.name] });
  if (d.error && !d.data) return html`<${Drawer} title=${'PID ' + pid} onClose=${onClose}><${Banner} tone="warn">${d.error.message}<//><//>`;
  if (!d.data) return html`<${Drawer} title=${'PID ' + pid} onClose=${onClose}><${Loading} lines=${8} /><//>`;
  const { process: p, signature: s } = d.data;
  const locked = p.protected;
  const setPriority = (level) => act(() => call('processes.priority', { pid, level }).then(d.reload), t('Öncelik değiştirildi', 'Priority changed'));
  const setEco = (on) => act(() => call('processes.eco', { pid, on }).then(d.reload), on ? t('Verimlilik modu açıldı', 'Efficiency mode on') : t('Verimlilik modu kapatıldı', 'Efficiency mode off'));

  return html`<${Drawer} wide onClose=${onClose} title=${html`<span class="row"><${FileIcon} path=${p.path} />${p.description || p.name}</span>`}>
    <div class="col gap-l">
      <div class="grid c4" style=${{ gap: '10px' }}>
        <div class="card flat pad"><${Stat} label="CPU" value=${num(p.cpu, 1)} unit="%" /></div>
        <div class="card flat pad"><${Stat} label=${t('Bellek', 'Memory')} value=${bytes(p.memory)} /></div>
        <div class="card flat pad"><${Stat} label="I/O" value=${rate(p.ioRead + p.ioWrite)} /></div>
        <div class="card flat pad"><${Stat} label="GPU" value=${num(p.gpu, 1)} unit="%" /></div>
      </div>
      ${history.data && history.data.length > 3 && html`<div class="card flat" style=${{ overflow: 'hidden' }}>
        <div class="row small muted" style=${{ padding: '10px 14px 0' }}><span>${t('CPU, son dakikalar', 'CPU, last minutes')}</span></div>
        <${Spark} data=${history.data.map((x) => x.cpu)} height=${54} max=${Math.max(20, ...history.data.map((x) => x.cpu))} />
      </div>`}
      ${locked && html`<${Banner} tone="info" icon="lock">${t('Windows\'un çalışması için gerekli bir işlem. OptiCore bunu sonlandırmaz ve önceliğini değiştirmez.', 'Windows needs this process to run. OptiCore will not end it or change its priority.')}<//>`}
      ${!locked && html`<div class="card flat pad col">
        <div class="row"><span class="grow">${t('Öncelik', 'Priority')}</span>
          <${Select} class="sm" value=${d.data.priority || 'normal'} onChange=${setPriority} options=${PRIORITIES.map(([v, tr, en]) => ({ value: v, label: t(tr, en) }))} /></div>
        <div class="row"><div class="grow"><div>${t('Verimlilik modu', 'Efficiency mode')}</div><div class="tiny dim">${t('İşlemi düşük öncelikte ve verimli çekirdeklerde çalıştırır; arka plan uygulamaları için idealdir.', 'Runs the process at low priority on efficient cores; ideal for background apps.')}</div></div>
          <${Switch} on=${!!d.data.eco} onChange=${setEco} /></div>
        <div class="row wrap gap-s">
          <${Btn} size="sm" kind="danger" icon="x" onClick=${() => endProcess(p, false).then((r) => r && onClose())}>${t('Sonlandır', 'End process')}<//>
          ${d.data.children.length > 0 && html`<${Btn} size="sm" kind="danger" onClick=${() => endProcess(p, true).then((r) => r && onClose())}>${t('Ağacı sonlandır', 'End tree')}<//>`}
          <${Btn} size="sm" icon="pause" onClick=${() => act(() => call('processes.suspend', { pid }), t('İşlem duraklatıldı', 'Process suspended'))}>${t('Duraklat', 'Suspend')}<//>
          <${Btn} size="sm" icon="play" onClick=${() => act(() => call('processes.suspend', { pid, resume: true }), t('İşlem sürdürüldü', 'Process resumed'))}>${t('Sürdür', 'Resume')}<//>
          ${p.path && html`<${Btn} size="sm" icon="folder" onClick=${() => act(() => call('shell.reveal', { path: p.path }))}>${t('Konumu aç', 'Open location')}<//>`}
        </div>
      </div>`}
      <${KV} rows=${[
        ['PID', p.pid], [t('Yol', 'Path'), p.path && html`<span class="mono wrap-any">${p.path}</span>`],
        [t('Komut satırı', 'Command line'), d.data.commandLine && html`<span class="mono wrap-any">${d.data.commandLine}</span>`],
        [t('Yayıncı', 'Publisher'), p.company], [t('Sürüm', 'Version'), d.data.version],
        [t('Dijital imza', 'Digital signature'), s.signed
          ? html`<${Chip} tone=${s.trusted ? 'good' : 'warn'} icon=${s.trusted ? 'shieldCheck' : 'alert'}>${s.signer}${s.trusted ? '' : ' · ' + t('doğrulanamadı', 'not verified')}<//>`
          : html`<${Chip} tone=${p.kind === 'windows' ? null : 'warn'}>${p.kind === 'windows' ? t('Gömülü imza yok (Windows bileşeni)', 'No embedded signature (Windows component)') : t('İmzasız', 'Unsigned')}<//>`],
        [t('Başlangıç', 'Started'), p.started ? dateTime(p.started) + ' · ' + duration((Date.now() - p.started) / 1000) : null],
        [t('CPU süresi', 'CPU time'), duration(p.cpuTimeMs / 1000)],
        [t('İş parçacığı / tanıtıcı', 'Threads / handles'), `${num(p.threads)} / ${num(p.handles)}`],
        [t('Ayrılan bellek', 'Committed memory'), bytes(p.commit)],
        [t('Üst işlemler', 'Parent chain'), d.data.parents.map((x) => x.name).join(' ← ')],
        [t('Hizmetler', 'Services'), p.services && p.services.join(', ')],
      ]} />
      ${d.data.connections.length > 0 && html`<${Card} title=${t('Ağ bağlantıları', 'Network connections')} sub=${String(d.data.connections.length)} icon="globe" flush>
        <div class="tbl-wrap" style=${{ maxHeight: '240px' }}><table class="tbl"><tbody>
          ${d.data.connections.slice(0, 60).map((c, i) => html`<tr key=${i}><td class="shrink"><${Chip}>${c.protocol}<//></td>
            <td class="mono">${c.localAddress}:${c.localPort}</td><td class="mono">${c.remoteAddress ? c.remoteAddress + ':' + c.remotePort : '—'}</td><td class="dim">${c.state}</td></tr>`)}
        </tbody></table></div>
      <//>`}
    </div>
  <//>`;
}

function ProcessList({ initialSort }) {
  const list = useRpc('processes.list', {}, { interval: 2000 });
  const [query, setQuery] = useState('');
  const [kind, setKind] = useState('all');
  const [mode, setMode] = useState('groups');
  const [open, setOpen] = useState(null);
  const [expanded, setExpanded] = useState({});
  const items = list.data ? list.data.items : [];

  const filtered = useMemo(() => items.filter((p) => (kind === 'all' || p.kind === kind) && matches(p.name + ' ' + (p.description || '') + ' ' + p.pid + ' ' + (p.company || ''), query)), [items, kind, query]);
  const maxMem = useMemo(() => Math.max(1, ...filtered.map((p) => p.memory)), [filtered]);

  const rows = useMemo(() => {
    if (mode === 'list') return filtered.map((p) => ({ ...p, key: 'p' + p.pid, label: p.name, sub: p.description }));
    if (mode === 'tree') {
      const byPid = new Map(filtered.map((p) => [p.pid, p]));
      const kids = new Map();
      const roots = [];
      for (const p of filtered) {
        const parent = byPid.get(p.parentPid);
        if (parent && parent.pid !== p.pid && parent.started <= p.started) { if (!kids.has(parent.pid)) kids.set(parent.pid, []); kids.get(parent.pid).push(p); }
        else roots.push(p);
      }
      const out = [];
      const walk = (p, depth) => {
        out.push({ ...p, key: 'p' + p.pid, label: p.name, sub: p.description, depth });
        (kids.get(p.pid) || []).sort((x, y) => y.cpu - x.cpu || x.name.localeCompare(y.name)).forEach((c) => walk(c, depth + 1));
      };
      roots.sort((x, y) => x.name.localeCompare(y.name)).forEach((r) => walk(r, 0));
      return out;
    }
    const groups = new Map();
    for (const p of filtered) {
      const name = groupName(p);
      if (!groups.has(name)) groups.set(name, []);
      groups.get(name).push(p);
    }
    const out = [];
    for (const [name, members] of groups) {
      const main = members.reduce((a, b) => (b.memory > a.memory ? b : a));
      const sum = (f) => members.reduce((s, p) => s + f(p), 0);
      out.push({
        key: 'g' + name, label: name, count: members.length, members, path: main.path, pid: main.pid, kind: main.kind, protected: members.some((m) => m.protected),
        cpu: sum((p) => p.cpu), memory: sum((p) => p.memory), ioRead: sum((p) => p.ioRead), ioWrite: sum((p) => p.ioWrite), gpu: Math.max(...members.map((p) => p.gpu)),
        threads: sum((p) => p.threads), handles: sum((p) => p.handles), name: main.name,
      });
    }
    return out;
  }, [filtered, mode]);

  // In group mode, expanded groups show their members right below them (after sorting).
  const columns = [
    { id: 'label', label: t('Ad', 'Name'), sort: (r) => r.label.toLowerCase(), render: (r) => html`<div class="row" style=${{ paddingLeft: (r.depth || 0) * 18 + (r.child ? 26 : 0) + 'px' }}>
        ${r.count > 1 ? html`<${Btn} size="sm" kind="ghost" icon=${expanded[r.key] ? 'chevD' : 'chevR'} onClick=${(e) => { e.stopPropagation(); setExpanded((x) => ({ ...x, [r.key]: !x[r.key] })); }} />` : mode === 'groups' && !r.child ? html`<span style=${{ width: '26px' }}></span>` : null}
        <${FileIcon} path=${r.path} fallback=${r.kind === 'windows' ? 'windows' : 'package'} />
        <div class="grow" style=${{ minWidth: 0 }}><div class="ell b">${r.label}${r.count > 1 ? html` <span class="dim">(${r.count})</span>` : ''}</div>
          ${r.sub && r.sub !== r.label && html`<div class="tiny dim ell">${r.sub}</div>`}</div>
        ${r.protected && html`<${Icon} name="lock" size="12" class="dim" />`}
      </div>` },
    { id: 'pid', label: 'PID', num: true, sort: true, class: 'shrink', render: (r) => (r.count > 1 ? '' : r.pid) },
    { id: 'cpu', label: 'CPU', num: true, sort: true, class: 'shrink', render: (r) => html`<span style=${{ display: 'block', margin: '-8px -12px', padding: '8px 12px', ...heat(r.cpu / 40, 'var(--accent)') }}>${r.cpu >= 0.05 ? pct(r.cpu, 1) : '0'}</span>` },
    { id: 'memory', label: t('Bellek', 'Memory'), num: true, sort: true, class: 'shrink', render: (r) => html`<span style=${{ display: 'block', margin: '-8px -12px', padding: '8px 12px', ...heat(r.memory / maxMem, '#22d3ee') }}>${bytes(r.memory)}</span>` },
    { id: 'io', label: 'I/O', num: true, sort: (r) => r.ioRead + r.ioWrite, class: 'shrink', render: (r) => (r.ioRead + r.ioWrite > 512 ? rate(r.ioRead + r.ioWrite) : html`<span class="dim">0</span>`) },
    { id: 'gpu', label: 'GPU', num: true, sort: true, class: 'shrink', render: (r) => (r.gpu >= 0.1 ? pct(r.gpu, 1) : html`<span class="dim">0</span>`) },
    { id: 'threads', label: t('İş parç.', 'Threads'), num: true, sort: true, class: 'shrink', render: (r) => num(r.threads) },
  ];

  const menu = (e, r) => openMenu(e, [
    { label: t('Ayrıntılar', 'Details'), icon: 'info', onClick: () => setOpen(r.pid) },
    r.path && { label: t('Dosya konumunu aç', 'Open file location'), icon: 'folder', onClick: () => act(() => call('shell.reveal', { path: r.path })) },
    r.path && { label: t('Yolu kopyala', 'Copy path'), icon: 'copy', onClick: () => copyText(r.path) },
    'sep',
    !r.protected && { label: t('Verimlilik modu', 'Efficiency mode'), icon: 'leaf', onClick: () => act(() => call('processes.eco', { pid: r.pid, on: true }), t('Verimlilik modu açıldı', 'Efficiency mode on')) },
    !r.protected && { label: t('Sonlandır', 'End process'), icon: 'x', danger: true, onClick: () => endProcess(r, false) },
    !r.protected && { label: t('Ağacı sonlandır', 'End process tree'), icon: 'x', danger: true, onClick: () => endProcess(r, true) },
  ]);

  // Table handles sorting; expanded group members are appended as extra rows via a wrapper.
  const tableRows = useMemo(() => {
    if (mode !== 'groups') return rows;
    return rows.map((g) => ({ ...g, expandedMembers: expanded[g.key] ? g.members : null }));
  }, [rows, expanded, mode]);

  const now = list.data;
  return html`<div class="col">
    <div class="row wrap">
      <${Search} class="grow" value=${query} onInput=${setQuery} placeholder=${t('Ad, PID ya da yayıncıya göre ara…', 'Search by name, PID or publisher…')} />
      <${Tabs} value=${kind} onChange=${setKind} items=${[
        { id: 'all', label: t('Tümü', 'All'), count: items.length },
        { id: 'app', label: t('Uygulamalar', 'Apps'), count: items.filter((p) => p.kind === 'app').length },
        { id: 'background', label: t('Arka plan', 'Background'), count: items.filter((p) => p.kind === 'background').length },
        { id: 'windows', label: 'Windows', count: items.filter((p) => p.kind === 'windows').length },
      ]} />
      <${Tabs} value=${mode} onChange=${setMode} items=${[{ id: 'groups', label: t('Gruplu', 'Grouped'), icon: 'layers' }, { id: 'list', label: t('Liste', 'List'), icon: 'list' }, { id: 'tree', label: t('Ağaç', 'Tree'), icon: 'activity' }]} />
    </div>
    <div class="card" onContextMenu=${(e) => { const tr = e.target.closest('tr[data-pid]'); if (tr) { const r = rows.find((x) => String(x.pid) === tr.dataset.pid); if (r) menu(e, r); } }}>
      ${!now ? html`<div class="pad" style=${{ padding: '16px' }}><${Loading} lines=${10} /></div>` : html`<${GroupedTable} columns=${columns} rows=${tableRows} mode=${mode} onRow=${(r) => setOpen(r.pid)}
        initialSort=${mode === 'tree' ? null : initialSort} />`}
    </div>
    ${open != null && html`<${Details} pid=${open} onClose=${() => setOpen(null)} />`}
  </div>`;
}

/** Table that keeps expanded group members directly under their group regardless of the sort order. */
function GroupedTable({ columns, rows, mode, onRow, initialSort }) {
  const [sort, setSort] = useState(initialSort || { id: 'cpu', dir: 'desc' });
  useEffect(() => { if (mode === 'tree') setSort(null); else if (!sort) setSort({ id: 'cpu', dir: 'desc' }); }, [mode]);
  const sorted = useMemo(() => {
    if (!sort) return rows;
    const col = columns.find((c) => c.id === sort.id);
    const get = typeof col.sort === 'function' ? col.sort : (r) => r[col.id];
    const dir = sort.dir === 'asc' ? 1 : -1;
    return [...rows].sort((a, b) => { const x = get(a), y = get(b); return (typeof x === 'string' ? x.localeCompare(y) : x - y) * dir; });
  }, [rows, sort]);
  const flat = [];
  for (const r of sorted.slice(0, 400)) {
    flat.push(r);
    if (r.expandedMembers) for (const m of [...r.expandedMembers].sort((a, b) => b.memory - a.memory)) flat.push({ ...m, key: r.key + ':' + m.pid, label: m.name, sub: 'PID ' + m.pid, child: true });
  }
  const click = (c) => { if (c.sort) setSort((s) => (s && s.id === c.id ? { id: c.id, dir: s.dir === 'asc' ? 'desc' : 'asc' } : { id: c.id, dir: c.num ? 'desc' : 'asc' })); };
  return html`<div class="tbl-wrap" style=${{ maxHeight: 'calc(100vh - 290px)' }}><table class="tbl">
    <thead><tr>${columns.map((c) => html`<th key=${c.id} class=${cx(c.sort && 'sort', c.num && 'r', c.class)} onClick=${() => click(c)}>${c.label}${sort && sort.id === c.id && html`<span class="arr">${sort.dir === 'asc' ? '↑' : '↓'}</span>`}</th>`)}</tr></thead>
    <tbody>${flat.map((r) => html`<tr key=${r.key} class="click" data-pid=${r.pid} onClick=${() => onRow(r)}>
      ${columns.map((c) => html`<td key=${c.id} class=${cx(c.num && 'r', c.class)}>${c.render(r)}</td>`)}
    </tr>`)}</tbody>
  </table></div>`;
}

function Connections() {
  const conns = useRpc('processes.connections', {}, { interval: 4000 });
  const [query, setQuery] = useState('');
  const [state, setState] = useState('all');
  return html`<div class="col">
    <div class="row wrap">
      <${Search} class="grow" value=${query} onInput=${setQuery} placeholder=${t('İşlem, adres ya da port ara…', 'Search process, address or port…')} />
      <${Tabs} value=${state} onChange=${setState} items=${[{ id: 'all', label: t('Tümü', 'All') }, { id: 'established', label: t('Bağlı', 'Established') }, { id: 'listen', label: t('Dinleyen', 'Listening') }]} />
    </div>
    <div class="card"><${Async} state=${conns}>${(list) => html`<${Table} rowKey=${(c) => c.protocol + c.localAddress + c.localPort + c.remoteAddress + c.remotePort + c.pid} maxHeight="calc(100vh - 280px)"
      rows=${list.filter((c) => (state === 'all' || c.state === state) && matches(`${c.process} ${c.localAddress}:${c.localPort} ${c.remoteAddress || ''}:${c.remotePort} ${c.pid}`, query))}
      initialSort=${{ id: 'process', dir: 'asc' }}
      columns=${[
        { id: 'process', label: t('İşlem', 'Process'), sort: true, render: (c) => html`<div class="row"><${FileIcon} path=${c.path} /><span class="b ell">${c.process}</span><span class="dim tiny">${c.pid}</span></div>` },
        { id: 'protocol', label: t('Protokol', 'Protocol'), sort: true, class: 'shrink', render: (c) => html`<${Chip}>${c.protocol}<//>` },
        { id: 'local', label: t('Yerel', 'Local'), sort: (c) => c.localPort, render: (c) => html`<span class="mono">${c.localAddress}:${c.localPort}</span>` },
        { id: 'remote', label: t('Uzak', 'Remote'), sort: (c) => c.remoteAddress || '', render: (c) => html`<span class="mono">${c.remoteAddress ? c.remoteAddress + ':' + c.remotePort : '—'}</span>` },
        { id: 'state', label: t('Durum', 'State'), sort: true, class: 'shrink', render: (c) => html`<${Chip} tone=${c.state === 'established' ? 'good' : c.state === 'listen' ? 'info' : null}>${c.state}<//>` },
      ]} />`}<//></div>
  </div>`;
}

function Usage() {
  const [days, setDays] = useState(1);
  const usage = useRpc('processes.usage', { days }, { deps: [days] });
  return html`<div class="col">
    <div class="row"><div class="muted small grow">${t('OptiCore çalışırken her uygulamanın harcadığı işlemci süresi ve ortalama belleği kaydedilir.', 'While OptiCore runs it records the processor time and average memory each app consumes.')}</div>
      <${Tabs} value=${days} onChange=${setDays} items=${[{ id: 1, label: t('Bugün', 'Today') }, { id: 7, label: t('7 gün', '7 days') }, { id: 30, label: t('30 gün', '30 days') }]} /></div>
    <${Async} state=${usage}>${(list) => (list.length === 0
      ? html`<div class="card"><${Empty} icon="chart" title=${t('Kayıt birikiyor', 'Recording')} text=${t('İlk özet birkaç dakika içinde burada görünür.', 'The first summary appears here within a few minutes.')} /></div>`
      : html`<div class="grid c2">
        <${Card} title=${t('İşlemci süresi', 'Processor time')} icon="cpu"><${BarList} items=${list.slice(0, 12).map((x) => ({ label: x.name, value: x.cpuSeconds, right: duration(x.cpuSeconds) }))} /><//>
        <${Card} title=${t('Ortalama bellek', 'Average memory')} icon="ram"><${BarList} items=${[...list].sort((a, b) => b.avgMemoryMb - a.avgMemoryMb).slice(0, 12).map((x) => ({ label: x.name, value: x.avgMemoryMb, right: bytes(x.avgMemoryMb * 1048576), color: '#22d3ee' }))} /><//>
      </div>`)}<//>
  </div>`;
}

function Rules() {
  const rules = useRpc('processes.rules.list');
  const [edit, setEdit] = useState(null);
  const save = () => act(() => call('processes.rules.save', { rule: edit }).then((r) => { rules.setData(r); setEdit(null); }), t('Kural kaydedildi', 'Rule saved'));
  return html`<div class="col">
    <${Banner} tone="info" icon="repeat" action=${html`<${Btn} size="sm" kind="primary" icon="plus" onClick=${() => setEdit({ id: Math.random().toString(16).slice(2, 10), match: '', priority: 'below', eco: false, enabled: true, hits: 0 })}>${t('Kural ekle', 'Add rule')}<//>`}>
      ${t('Kurallar, belirlediğiniz uygulama her açıldığında önceliğini ya da verimlilik modunu otomatik ayarlar. Örneğin bir yedekleme aracını hep düşük öncelikte çalıştırabilirsiniz.',
        'Rules set an app\'s priority or efficiency mode automatically every time it starts. For example, always run a backup tool at low priority.')}
    <//>
    <div class="card"><${Async} state=${rules}>${(list) => (list.length === 0
      ? html`<${Empty} icon="repeat" title=${t('Henüz kural yok', 'No rules yet')} />`
      : list.map((r) => html`<div key=${r.id} class="item">
          <${Icon} name=${r.eco ? 'leaf' : 'sliders'} size="16" class="dim" />
          <div class="grow"><div class="t mono">${r.match}</div><div class="s">${r.eco ? t('Verimlilik modu', 'Efficiency mode') : t('Öncelik: ', 'Priority: ') + t(...(PRIORITIES.find((x) => x[0] === r.priority) || PRIORITIES[2]).slice(1))} · ${t(`${r.hits} kez uygulandı`, `applied ${r.hits} times`)}</div></div>
          <${Switch} on=${r.enabled} onChange=${(on) => call('processes.rules.save', { rule: { ...r, enabled: on } }).then(rules.setData)} />
          <${Btn} size="sm" kind="ghost" icon="trash" onClick=${() => call('processes.rules.delete', { id: r.id }).then(rules.setData)} />
        </div>`))}<//></div>
    ${edit && html`<${Modal} title=${t('İşlem kuralı', 'Process rule')} onClose=${() => setEdit(null)}
      footer=${html`<${Btn} kind="ghost" onClick=${() => setEdit(null)}>${t('Vazgeç', 'Cancel')}<//><${Btn} kind="primary" onClick=${save} disabled=${!edit.match.trim()}>${t('Kaydet', 'Save')}<//>`}>
      <div class="col">
        <div class="field"><label>${t('Çalıştırılabilir dosya adı', 'Executable name')}</label><input class="input" placeholder="ornek.exe" value=${edit.match} onInput=${(e) => setEdit({ ...edit, match: e.target.value.trim() })} /></div>
        <div class="row"><span class="grow">${t('Verimlilik modunda çalıştır', 'Run in efficiency mode')}</span><${Switch} on=${edit.eco} onChange=${(eco) => setEdit({ ...edit, eco })} /></div>
        ${!edit.eco && html`<div class="field"><label>${t('Öncelik', 'Priority')}</label><${Select} value=${edit.priority} onChange=${(priority) => setEdit({ ...edit, priority })} options=${PRIORITIES.map(([v, tr, en]) => ({ value: v, label: t(tr, en) }))} /></div>`}
      </div>
    <//>`}
  </div>`;
}

export default function Processes({ sub }) {
  const pageTabs = ['list', 'connections', 'usage', 'rules'];
  const [tab, setTab] = useState(pageTabs.includes(sub) ? sub : 'list');
  const sortBy = sub === 'memory' ? { id: 'memory', dir: 'desc' } : sub === 'disk' ? { id: 'io', dir: 'desc' } : { id: 'cpu', dir: 'desc' };
  return html`<${Page} title=${t('İşlemler', 'Processes')} desc=${t('Çalışan her şey, ne kadar kaynak kullandığı ve kimin başlattığı.', 'Everything that is running, what it uses and what started it.')}>
    <div class="mb"><${Tabs} value=${tab} onChange=${setTab} items=${[
      { id: 'list', label: t('İşlemler', 'Processes'), icon: 'activity' },
      { id: 'connections', label: t('Bağlantılar', 'Connections'), icon: 'globe' },
      { id: 'usage', label: t('Kullanım geçmişi', 'Usage history'), icon: 'chart' },
      { id: 'rules', label: t('Kurallar', 'Rules'), icon: 'repeat' },
    ]} /></div>
    ${tab === 'list' ? html`<${ProcessList} key=${sub || 'cpu'} initialSort=${sortBy} />` : tab === 'connections' ? html`<${Connections} />` : tab === 'usage' ? html`<${Usage} />` : html`<${Rules} />`}
  <//>`;
}
