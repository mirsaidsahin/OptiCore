// Toolbox: context menu manager, file lock finder, secure delete, file mover, system snapshots, duplicate runtimes.
import { html, useState, useRpc, useJob, call, act, t, num, bytes, dateTime, ago, matches, confirmDialog, app, useStore } from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Icon, Empty, Banner, Async, Switch, Search, Select, JobBar, FileIcon, Stat, SimulationNote } from '../lib/ui.js';

function ContextMenu() {
  const d = useRpc('tools.menu');
  const [q, setQ] = useState('');
  const set = (x, enabled) => act(() => call('tools.menuSet', { id: x.id, enabled }), enabled ? t('Menüde gösteriliyor', 'Shown in the menu') : t('Menüden gizlendi', 'Hidden from the menu')).then((r) => r && d.reload());
  return html`<${Async} state=${d}>${(items) => {
    const groups = [...new Set(items.map((x) => x.where))];
    const visible = items.filter((x) => matches(x.label + ' ' + x.name + ' ' + (x.command || ''), q));
    return html`<div class="col gap-l">
      <${Banner} tone="info">${t('Programların sağ tık menüsüne eklediği öğeler. Gizlemek programı kaldırmaz, yalnızca menüden çıkarır; her değişiklik geri alınabilir. Windows\'un temel öğeleri listelenmez. Tüm kullanıcılar için kayıtlı öğeler yönetici yetkisi ister.',
        'Entries that programs add to the right-click menu. Hiding one does not uninstall the program, it only removes the entry; every change is reversible. Windows\' core entries are not listed. Entries registered for all users need administrator rights.')}<//>
      <${Search} value=${q} onInput=${setQ} />
      ${groups.map((g) => { const rows = visible.filter((x) => x.where === g); return rows.length > 0 && html`<${Card} key=${g} title=${g} flush>${rows.map((x) => html`<div key=${x.id} class="item">
        <div class="grow" style=${{ minWidth: 0 }}><div class="t">${x.label} ${x.kind === 'extension' && html`<${Chip}>${t('Eklenti', 'Extension')}<//>`}</div><div class="s mono ell">${x.command || x.name}</div></div>
        <${Switch} on=${x.enabled} onChange=${(on) => set(x, on)} /></div>`)}<//>`; })}
    </div>`;
  }}<//>`;
}

function Locksmith() {
  const [path, setPath] = useState('');
  const [res, setRes] = useState(null);
  const find = () => act(async () => setRes(await call('processes.whoLocks', { paths: [path.trim()] })));
  return html`<div class="col gap-l">
    <div class="card pad col"><div class="small muted">${t('"Dosya başka bir programda açık olduğundan silinemiyor" hatası mı alıyorsunuz? Dosyanın yolunu yazın; onu kilitleyen programı gösteririm.', 'Getting "the file is open in another program"? Enter its path and OptiCore shows which program holds it.')}</div>
      <div class="row"><input class="input grow" placeholder="C:\\…\\dosya.docx" value=${path} onInput=${(e) => setPath(e.target.value)} onKeyDown=${(e) => e.key === 'Enter' && path.trim() && find()} />
        <${Btn} kind="primary" icon="search" disabled=${!path.trim()} onClick=${find}>${t('Kimin kilitlediğini bul', 'Find who locks it')}<//></div></div>
    ${res && (res.length === 0 ? html`<div class="card"><${Empty} icon="check" title=${t('Dosyayı kilitleyen bir program yok', 'No program is locking the file')} /></div>` : html`<div class="card">${res.map((p) => html`<div key=${p.pid} class="item">
      <${FileIcon} path=${p.path} /><div class="grow"><div class="t">${p.name}</div><div class="s">PID ${p.pid}${p.service ? ' · ' + p.service : ''}</div></div>
      <${Btn} size="sm" kind="danger" onClick=${() => confirmDialog({ title: t('İşlem sonlandırılsın mı?', 'End the process?'), body: t(`${p.name} kapatılacak; kaydedilmemiş veriler kaybolur.`, `${p.name} will be closed; unsaved data will be lost.`), confirm: t('Sonlandır', 'End'), danger: true })
        .then((ok) => ok && act(() => call('processes.kill', { pid: p.pid }), t('İşlem sonlandırıldı', 'Process ended')).then(() => setRes(res.filter((x) => x.pid !== p.pid))))}>${t('Sonlandır', 'End')}<//></div>`)}</div>`)}
  </div>`;
}

function Shredder() {
  const job = useJob();
  const [path, setPath] = useState('');
  const [passes, setPasses] = useState('1');
  const run = () => confirmDialog({
    title: t('Kalıcı ve geri getirilemez biçimde silinsin mi?', 'Delete permanently and irrecoverably?'),
    body: t('Dosyanın içeriğinin üzerine rastgele veri yazılır, ardından silinir. Geri Dönüşüm Kutusu\'na gitmez ve hiçbir yolla geri getirilemez.', 'The file\'s content is overwritten with random data, then deleted. It does not go to the Recycle Bin and cannot be recovered in any way.'),
    details: path, confirm: t('Kalıcı olarak sil', 'Delete permanently'), danger: true,
  }).then((ok) => ok && act(() => job.run('tools.shred', { paths: [path.trim()], passes: +passes }), (r) => (r.simulated ? t('Simülasyon: hiçbir şey silinmedi', 'Simulation: nothing was deleted') : t(`${r.shredded} dosya güvenle silindi`, `${r.shredded} files shredded`))).then((r) => r && setPath('')));
  return html`<div class="col gap-l">
    <${Banner} tone="warn" icon="alert">${t('SSD\'lerde dürüst bir not: SSD\'ler yazmaları farklı hücrelere dağıttığı için üzerine yazma eski verinin her kopyasının yok olduğunu garanti edemez. SSD\'de gerçek koruma için disk şifrelemesi (BitLocker/Cihaz şifreleme) kullanın; bu araç mekanik disklerde ve USB belleklerde tam etkilidir.',
      'An honest note about SSDs: because SSDs spread writes across cells, overwriting cannot guarantee every old copy is gone. On an SSD, real protection comes from disk encryption (BitLocker/Device encryption); this tool is fully effective on hard disks and USB sticks.')}<//>
    <div class="card pad row wrap"><input class="input grow" placeholder=${t('Dosya ya da klasör yolu', 'File or folder path')} value=${path} onInput=${(e) => setPath(e.target.value)} />
      <${Select} value=${passes} onChange=${setPasses} options=${[{ value: '1', label: t('1 geçiş (yeterli)', '1 pass (sufficient)') }, { value: '3', label: t('3 geçiş', '3 passes') }, { value: '7', label: t('7 geçiş', '7 passes') }]} />
      <${Btn} kind="danger" icon="scissors" disabled=${!path.trim() || job.running} onClick=${run}>${t('Güvenle sil', 'Shred')}<//></div>
    <${JobBar} job=${job.job} onCancel=${job.cancel} />
    <div class="tiny dim">${t('Günümüz disklerinde tek geçiş yeterlidir; çoklu geçiş eski manyetik diskler için tasarlanmış bir yöntemdir.', 'On modern disks a single pass is enough; multiple passes are a method designed for old magnetic disks.')}</div>
  </div>`;
}

function Mover() {
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const volumes = useRpc('system.volumes');
  return html`<div class="col gap-l">
    <div class="small muted">${t('Büyük dosyaları (oyun kayıtları, videolar, sanal makineler) başka bir sürücüye taşıyarak sistem diskinde yer açın. Taşıma Geri Alma Merkezi\'ne kaydedilir ve geri alınabilir.', 'Free up the system drive by moving large files (game recordings, videos, virtual machines) to another drive. Moves are recorded in the Undo Center and can be undone.')}</div>
    <div class="card pad col">
      <div class="field"><label>${t('Taşınacak dosya ya da klasör', 'File or folder to move')}</label><input class="input" value=${from} onInput=${(e) => setFrom(e.target.value)} /></div>
      <div class="field"><label>${t('Hedef klasör', 'Destination folder')}</label><input class="input" value=${to} onInput=${(e) => setTo(e.target.value)} placeholder="D:\\Arşiv" /></div>
      <div class="row wrap gap-s small muted">${(volumes.data || []).map((v) => html`<span key=${v.letter}>${v.letter}: ${bytes(v.freeBytes)} ${t('boş', 'free')}</span>`)}</div>
      <div><${Btn} kind="primary" icon="arrowDown" disabled=${!from.trim() || !to.trim()} onClick=${() => act(() => call('tools.move', { paths: [from.trim()], destination: to.trim() }), (r) => (r.simulated ? t('Simülasyon: taşınmadı', 'Simulation: nothing moved') : t(`${r.moved} öğe taşındı`, `${r.moved} items moved`))).then((r) => r && setFrom(''))}>${t('Taşı', 'Move')}<//></div>
    </div>
  </div>`;
}

function DiffList({ title, d, render }) {
  const total = d.added.length + d.removed.length + d.changed.length;
  if (!total) return null;
  return html`<${Card} title=${title} sub=${String(total)} flush>
    ${d.added.map((x) => html`<div key=${'a' + x.name} class="item"><${Chip} tone="good">+<//><span class="grow">${x.name}</span><span class="dim small">${render ? render(x.value) : x.value}</span></div>`)}
    ${d.removed.map((x) => html`<div key=${'r' + x.name} class="item"><${Chip} tone="bad">−<//><span class="grow">${x.name}</span><span class="dim small">${render ? render(x.value) : x.value}</span></div>`)}
    ${d.changed.map((x) => html`<div key=${'c' + x.name} class="item"><${Chip} tone="warn">~<//><span class="grow">${x.name}</span><span class="small mono">${render ? render(x.from) : x.from} → ${render ? render(x.to) : x.to}</span></div>`)}
  <//>`;
}

function Snapshots() {
  const list = useRpc('tools.snapshots');
  const job = useJob();
  const [label, setLabel] = useState('');
  const [from, setFrom] = useState(null);
  const [to, setTo] = useState('now');
  const [diff, setDiff] = useState(null);
  const snaps = list.data || [];
  const onoff = (v) => (v === 'True' || v === true ? t('açık', 'on') : v === 'False' || v === false ? t('kapalı', 'off') : v);
  return html`<div class="col gap-l">
    <div class="small muted">${t('Anlık görüntü; kurulu uygulamaları, başlangıç öğelerini, hizmetleri, sürücüleri ve ayarları kaydeder. Bir sorun başladığında "o günden bu yana ne değişti?" sorusunu yanıtlar.', 'A snapshot records installed apps, startup items, services, drivers and settings. When a problem starts, it answers "what changed since then?"')}</div>
    <div class="card pad row"><input class="input grow" placeholder=${t('Ad (ör. "Temiz kurulumdan sonra")', 'Name (e.g. "After clean install")')} value=${label} onInput=${(e) => setLabel(e.target.value)} />
      <${Btn} kind="primary" icon="camera" busy=${job.running} onClick=${() => act(async () => { await job.run('tools.snapshot', { label }); setLabel(''); list.reload(); }, t('Anlık görüntü alındı', 'Snapshot taken'))}>${t('Anlık görüntü al', 'Take snapshot')}<//></div>
    <${JobBar} job=${job.job} />
    ${snaps.length > 0 && html`<${Card} title=${t('Karşılaştır', 'Compare')} icon="scale">
      <div class="row wrap"><${Select} value=${from || snaps[snaps.length - 1].id} onChange=${setFrom} options=${snaps.map((s) => ({ value: s.id, label: (s.label || s.id) + ' · ' + dateTime(s.time) }))} />
        <span>→</span><${Select} value=${to} onChange=${setTo} options=${[{ value: 'now', label: t('Şu anki durum', 'Current state') }, ...snaps.map((s) => ({ value: s.id, label: (s.label || s.id) + ' · ' + dateTime(s.time) }))]} />
        <${Btn} icon="scale" onClick=${() => act(async () => setDiff(await call('tools.snapshotDiff', { from: from || snaps[snaps.length - 1].id, to })))}>${t('Farkları göster', 'Show differences')}<//></div>
    <//>`}
    ${diff && html`<div class="col gap-l">
      ${['apps', 'startup', 'services', 'drivers', 'system', 'tweaks'].every((k) => !diff[k].added.length && !diff[k].removed.length && !diff[k].changed.length)
        ? html`<div class="card"><${Empty} icon="check" title=${t('Hiçbir şey değişmemiş', 'Nothing changed')} /></div>` : ''}
      <${DiffList} title=${t('Sistem', 'System')} d=${diff.system} />
      <${DiffList} title=${t('Uygulamalar', 'Apps')} d=${diff.apps} />
      <${DiffList} title=${t('Başlangıç', 'Startup')} d=${diff.startup} render=${onoff} />
      <${DiffList} title=${t('Hizmetler', 'Services')} d=${diff.services} />
      <${DiffList} title=${t('Sürücüler', 'Drivers')} d=${diff.drivers} />
      <${DiffList} title=${t('Windows ayarları', 'Windows tweaks')} d=${diff.tweaks} />
    </div>`}
    ${snaps.length > 0 && html`<${Card} title=${t('Kayıtlı anlık görüntüler', 'Saved snapshots')} icon="camera" flush>${snaps.map((s) => html`<div key=${s.id} class="item">
      <div class="grow"><div class="t">${s.label || s.id}</div><div class="s">${dateTime(s.time)} · ${s.apps} ${t('uygulama', 'apps')} · ${s.drivers} ${t('sürücü', 'drivers')}</div></div>
      <${Btn} size="sm" kind="ghost" icon="trash" onClick=${() => call('tools.snapshotDelete', { id: s.id }).then(list.reload)} /></div>`)}<//>`}
  </div>`;
}

function Duplicates() {
  const d = useRpc('tools.duplicates');
  return html`<${Async} state=${d}>${(groups) => (groups.length === 0 ? html`<div class="card"><${Empty} icon="check" title=${t('Yinelenen çalışma zamanı ya da uygulama yok', 'No duplicate runtimes or apps')} /></div>` : html`<div class="col gap-l">
    ${groups.map((g) => html`<${Card} key=${g.family} title=${g.family} icon="layers" actions=${g.normal ? html`<${Chip} tone="good">${t('Normal', 'Normal')}<//>` : html`<${Chip} tone="warn">${t('İncele', 'Review')}<//>`}>
      <div class="small muted mb">${g.note}</div>
      ${g.items.map((a) => html`<div key=${a.id} class="row small" style=${{ padding: '4px 0' }}><span class="grow">${a.name}</span><span class="dim">${a.version || ''}</span><span class="num">${a.sizeBytes ? bytes(a.sizeBytes) : ''}</span></div>`)}
    <//>`)}
    <div class="tiny dim">${t('Kaldırmak için Uygulamalar sayfasını kullanın.', 'Use the Apps page to uninstall.')}</div>
  </div>`)}<//>`;
}

export default function Tools({ sub }) {
  const tabs = ['menu', 'lock', 'shred', 'move', 'snapshot', 'duplicates'];
  const [tab, setTab] = useState(tabs.includes(sub) ? sub : 'menu');
  return html`<${Page} title=${t('Araçlar', 'Toolbox')} desc=${t('Günlük sorunlar için küçük ama güçlü araçlar.', 'Small but sharp tools for everyday problems.')}>
    <div class="col" style=${{ gap: '14px' }}>
      <${SimulationNote} />
      <div><${Tabs} value=${tab} onChange=${setTab} items=${[
        { id: 'menu', label: t('Sağ tık menüsü', 'Context menu'), icon: 'cursor' }, { id: 'lock', label: t('Kilitli dosya', 'Locked file'), icon: 'lock' },
        { id: 'shred', label: t('Güvenli silme', 'Secure delete'), icon: 'scissors' }, { id: 'move', label: t('Dosya taşıyıcı', 'File mover'), icon: 'arrowDown' },
        { id: 'snapshot', label: t('Anlık görüntü', 'Snapshots'), icon: 'camera' }, { id: 'duplicates', label: t('Yinelenen uygulamalar', 'Duplicate apps'), icon: 'layers' },
      ]} /></div>
      ${tab === 'menu' ? html`<${ContextMenu} />` : tab === 'lock' ? html`<${Locksmith} />` : tab === 'shred' ? html`<${Shredder} />` : tab === 'move' ? html`<${Mover} />` : tab === 'snapshot' ? html`<${Snapshots} />` : html`<${Duplicates} />`}
    </div>
  <//>`;
}
