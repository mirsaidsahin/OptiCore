// Undo Center: every change OptiCore made, verified against the live system, and reversible.
import { html, useState, useRpc, call, act, t, cx, num, bytes, dateTime, ago, confirmDialog, saveText, matches } from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Search, Drawer, Icon, Empty, Loading, Banner, Async, ElevationHint, SimulationNote, Stat } from '../lib/ui.js';

export const SOURCES = {
  tweaks: ['Windows Ayarları', 'Windows Tweaks', 'sliders'], startup: ['Başlangıç', 'Startup', 'power'], cleanup: ['Temizlik', 'Cleanup', 'sparkles'],
  processes: ['İşlemler', 'Processes', 'activity'], privacy: ['Gizlilik', 'Privacy', 'eyeOff'], backup: ['Yedekleme', 'Backup', 'history'],
  gaming: ['Oyun', 'Gaming', 'gamepad'], network: ['Ağ', 'Network', 'wifi'], apps: ['Uygulamalar', 'Apps', 'package'], automation: ['Otomatik bakım', 'Automation', 'repeat'],
  tools: ['Araçlar', 'Tools', 'wrench'], drivers: ['Sürücüler', 'Drivers', 'plug'],
};
const STATUS = {
  intact: ['Hâlâ geçerli', 'Still in effect', 'good'], restored: ['Eski değerine dönmüş', 'Back at the old value', 'warn'], changed: ['Başka bir şey değiştirmiş', 'Changed by something else', 'warn'],
  undone: ['Geri alındı', 'Undone', null], failed: ['Uygulanamadı', 'Failed', 'bad'], simulated: ['Simülasyon', 'Simulated', 'acc'], 'n/a': ['Kayıt', 'Record', null],
};
const src = (id) => SOURCES[id] || [id, id, 'dot'];

function describe(e) {
  const b = e.before || {}, a = e.after || {};
  const val = (s) => (s.exists === '0' ? t('(yok)', '(absent)') : s.data ?? '');
  switch (e.kind) {
    case 'reg.value': return `${val(b)}  →  ${val(a)}`;
    case 'reg.key': return t('anahtar silindi', 'key deleted');
    case 'service.start': return `${b.mode ?? '?'}  →  ${a.mode}`;
    case 'task.enabled': return `${b.enabled === '1' ? t('etkin', 'enabled') : t('devre dışı', 'disabled')}  →  ${a.enabled === '1' ? t('etkin', 'enabled') : t('devre dışı', 'disabled')}`;
    case 'power.scheme': return `${b.guid ?? '?'}  →  ${a.guid}`;
    case 'command': return `${a.file ? a.file.split('\\').pop() : ''} ${a.args ?? ''}`;
    case 'files.deleted': case 'files.recycled': return a.bytes ? bytes(+a.bytes) + (a.files ? ` · ${num(+a.files)} ${t('dosya', 'files')}` : '') : '';
    default: return Object.entries(a).filter(([, v]) => v != null).map(([k, v]) => `${k}: ${v}`).join(', ');
  }
}

function SetDetails({ id, onClose, onChanged }) {
  const d = useRpc('backup.verify', { id }, { deps: [id] });
  const revert = (entries) => confirmDialog({
    title: entries ? t('Bu değişiklik geri alınsın mı?', 'Undo this change?') : t('Bu işlemin tamamı geri alınsın mı?', 'Undo this whole action?'),
    body: t('Kayıtlı önceki değerler birebir geri yüklenir.', 'The recorded previous values are restored exactly.'), confirm: t('Geri al', 'Undo'),
  }).then((ok) => ok && act(() => call('journal.revert', entries ? { id, entries } : { id }), (r) => t(`${r.restored} değişiklik geri alındı` + (r.failed ? `, ${r.failed} başarısız` : ''), `${r.restored} change(s) undone` + (r.failed ? `, ${r.failed} failed` : '')))
    .then((r) => { if (r) { d.reload(); onChanged(); } }));

  return html`<${Drawer} wide onClose=${onClose} title=${d.data ? d.data.title : '…'}
    actions=${d.data && d.data.canRevert && html`<${Btn} size="sm" kind="primary" icon="undo" onClick=${() => revert(null)}>${t('Tümünü geri al', 'Undo all')}<//>`}>
    <${Async} state=${d}>${(s) => html`<div class="col">
      <div class="row wrap gap-s small muted">
        <${Chip} icon=${src(s.source)[2]}>${t(src(s.source)[0], src(s.source)[1])}<//>
        <span>${dateTime(s.time)}</span>
        ${s.simulated && html`<${Chip} tone="acc" icon="flask">${t('Simülasyon: sistem değişmedi', 'Simulation: the system was not changed')}<//>`}
        ${s.revertedAt && html`<${Chip} icon="undo">${t('Geri alındı', 'Undone')} · ${ago(s.revertedAt)}<//>`}
        ${s.bytesFreed > 0 && html`<${Chip} tone="good">${bytes(s.bytesFreed)}<//>`}
      </div>
      ${s.drifted > 0 && html`<${Banner} tone="warn" icon="alert">${t(`${s.drifted} değişiklik artık OptiCore'un bıraktığı gibi değil. Bir Windows güncellemesi ya da başka bir program değiştirmiş olabilir.`,
        `${s.drifted} change(s) are no longer as OptiCore left them. A Windows update or another program may have altered them.`)}<//>`}
      <div class="card flat">
        ${s.entries.map((e) => {
          const [tr, en, tone] = STATUS[e.status] || STATUS['n/a'];
          return html`<div key=${e.index} class="item" style=${{ alignItems: 'flex-start' }}>
            <div class="grow" style=${{ minWidth: 0 }}>
              <div class="row gap-s wrap"><${Chip}>${e.kind}<//><${Chip} tone=${tone}>${t(tr, en)}<//>${e.error === 'ELEVATION_REQUIRED' && html`<${Chip} tone="warn" icon="shield">${t('Yönetici gerekti', 'Needed admin')}<//>`}</div>
              <div class="mono wrap-any mt-s">${e.target}</div>
              <div class="mono dim wrap-any">${describe(e)}</div>
              ${e.error && e.error !== 'ELEVATION_REQUIRED' && html`<div class="tiny bad">${e.error}</div>`}
            </div>
            ${e.applied && e.reversible && !e.reverted && html`<${Btn} size="sm" kind="ghost" icon="undo" title=${t('Yalnızca bunu geri al', 'Undo only this')} onClick=${() => revert([e.index])} />`}
          </div>`;
        })}
      </div>
    </div>`}<//>
  <//>`;
}

function Journal() {
  const list = useRpc('journal.list', {}, { interval: 15000 });
  const [open, setOpen] = useState(null);
  const [query, setQuery] = useState('');
  const [source, setSource] = useState('all');
  const sets = list.data || [];
  const visible = sets.filter((s) => (source === 'all' || s.source === source) && matches(s.title, query));
  const real = sets.filter((s) => !s.simulated);
  const sources = [...new Set(sets.map((s) => s.source))];

  const revertAll = () => confirmDialog({
    title: t('OptiCore\'un yaptığı her şey geri alınsın mı?', 'Undo everything OptiCore ever changed?'),
    body: t('Geri alınabilir tüm değişiklikler, en yeniden en eskiye doğru önceki değerlerine döndürülür. Silinen önbellek dosyaları geri gelmez (zaten yeniden oluşturulurlar).',
      'Every reversible change is returned to its previous value, newest first. Deleted cache files do not come back (they are rebuilt anyway).'),
    confirm: t('Hepsini geri al', 'Undo everything'), danger: true,
  }).then((ok) => ok && act(() => call('backup.revertAll'), (r) => t(`${r.sets} işlem, ${r.restored} değişiklik geri alındı`, `${r.sets} actions, ${r.restored} changes undone`)).then((r) => r && list.reload()));

  return html`<div class="col gap-l">
    <div class="grid c4">
      <div class="card pad"><${Stat} label=${t('Kayıtlı işlem', 'Recorded actions')} value=${num(real.length)} /></div>
      <div class="card pad"><${Stat} label=${t('Geri alınabilir', 'Can be undone')} value=${num(sets.filter((s) => s.canRevert).length)} /></div>
      <div class="card pad"><${Stat} label=${t('Geri alınmış', 'Already undone')} value=${num(sets.filter((s) => s.revertedAt).length)} /></div>
      <div class="card pad"><${Stat} label=${t('Açılan alan', 'Space freed')} value=${bytes(real.reduce((sum, s) => sum + (s.bytesFreed || 0), 0))} /></div>
    </div>
    <div class="row wrap">
      <${Search} class="grow" value=${query} onInput=${setQuery} />
      <${Tabs} value=${source} onChange=${setSource} items=${[{ id: 'all', label: t('Tümü', 'All') }, ...sources.map((s) => ({ id: s, label: t(src(s)[0], src(s)[1]) }))]} />
      <${Btn} kind="danger" icon="undo" disabled=${!sets.some((s) => s.canRevert)} onClick=${revertAll}>${t('Her şeyi geri al', 'Undo everything')}<//>
    </div>
    <div class="card">
      ${list.data === undefined ? html`<div style=${{ padding: '16px' }}><${Loading} lines=${6} /></div>` : !visible.length
        ? html`<${Empty} icon="history" title=${t('Kayıt yok', 'Nothing recorded')} text=${t('OptiCore sistemde bir şey değiştirdiğinde önce buraya yazar, sonra uygular.', 'Whenever OptiCore changes something on the system it writes it here first, then applies it.')} />`
        : visible.map((s) => html`<div key=${s.id} class="item click" onClick=${() => setOpen(s.id)}>
          <div class="finding-ico" style=${{ width: '30px', height: '30px', borderRadius: '9px' }}><${Icon} name=${src(s.source)[2]} size="15" /></div>
          <div class="grow" style=${{ minWidth: 0 }}>
            <div class="t ell">${s.title}</div>
            <div class="s">${dateTime(s.time)} · ${t(src(s.source)[0], src(s.source)[1])} · ${t(`${s.count} kayıt`, `${s.count} entries`)}${s.failed ? ' · ' + t(`${s.failed} başarısız`, `${s.failed} failed`) : ''}</div>
          </div>
          ${s.bytesFreed > 0 && html`<${Chip} tone="good">${bytes(s.bytesFreed)}<//>`}
          ${s.simulated ? html`<${Chip} tone="acc" icon="flask">${t('Simülasyon', 'Simulated')}<//>` : s.revertedAt ? html`<${Chip} icon="undo">${t('Geri alındı', 'Undone')}<//>` : s.canRevert ? html`<${Chip} tone="info">${t('Geri alınabilir', 'Reversible')}<//>` : html`<${Chip}>${t('Kalıcı', 'Permanent')}<//>`}
          <${Icon} name="chevR" size="15" class="dim" />
        </div>`)}
    </div>
    ${open && html`<${SetDetails} id=${open} onClose=${() => setOpen(null)} onChanged=${list.reload} />`}
  </div>`;
}

function RestorePoints() {
  const points = useRpc('backup.restorePoints');
  const [name, setName] = useState('');
  const types = { install: t('Program kurulumu', 'App install'), uninstall: t('Program kaldırma', 'App removal'), driver: t('Sürücü kurulumu', 'Driver install'), settings: t('Ayar değişikliği', 'Settings change'), manual: t('Elle oluşturuldu', 'Manual'), update: t('Windows güncellemesi', 'Windows update'), other: t('Diğer', 'Other'), cancelled: t('İptal edildi', 'Cancelled') };
  const create = () => act(() => call('backup.createRestorePoint', { description: name.trim() || 'OptiCore' }), (r) => (r.simulated ? t('Simülasyon: nokta oluşturulmadı', 'Simulation: no restore point was created')
    : r.skipped ? t('Windows son 24 saatte bir nokta oluşturduğu için yenisini eklemedi', 'Windows already made a point in the last 24 hours and did not add another') : t('Geri yükleme noktası oluşturuldu', 'Restore point created')))
    .then((r) => { if (r) { setName(''); points.reload(); } });
  return html`<div class="col gap-l">
    <${Banner} tone="info" icon="info">${t('Geri yükleme noktası, Windows\'un sistem dosyalarının ve kayıt defterinin anlık görüntüsüdür; kişisel dosyalarınızı içermez. OptiCore\'un kendi değişiklikleri için gerekli değildir (onlar tek tek geri alınabilir), ama sürücü ya da büyük program kurulumlarından önce iyi bir emniyet kemeridir.',
      'A restore point is a snapshot of Windows system files and the registry; it does not contain your personal files. OptiCore\'s own changes do not need one (each can be undone individually), but it is a good seat belt before driver or large software installs.')}<//>
    <div class="card pad row wrap">
      <input class="input grow" placeholder=${t('Açıklama (ör. "Sürücü güncellemesinden önce")', 'Description (e.g. "Before driver update")')} value=${name} onInput=${(e) => setName(e.target.value)} />
      <${Btn} kind="primary" icon="plus" onClick=${create}>${t('Geri yükleme noktası oluştur', 'Create restore point')}<//>
      <${Btn} icon="history" onClick=${() => act(() => call('backup.openSystemRestore'))}>${t('Sistem Geri Yükleme\'yi aç', 'Open System Restore')}<//>
      <${Btn} kind="ghost" icon="cog" onClick=${() => act(() => call('backup.openSystemProtection'))}>${t('Sistem Koruması ayarları', 'System Protection settings')}<//>
    </div>
    <${Async} state=${points}>${(r) => html`
      ${r.denied && html`<${ElevationHint}>${t('Geri yükleme noktalarını listelemek ve oluşturmak yönetici yetkisi ister.', 'Listing and creating restore points needs administrator rights.')}<//>`}
      ${!r.denied && html`<div class="card">${r.points.length === 0
        ? html`<${Empty} icon="history" title=${t('Geri yükleme noktası yok', 'No restore points')} text=${t('Sistem Koruması kapalı olabilir.', 'System Protection may be turned off.')} />`
        : r.points.map((p) => html`<div key=${p.id} class="item"><${Icon} name="history" size="16" class="dim" />
            <div class="grow"><div class="t">${p.description}</div><div class="s">${dateTime(p.time)} · ${types[p.type] || p.type}</div></div><span class="dim tiny">#${p.id}</span></div>`)}</div>`}`}<//>
  </div>`;
}

function Export() {
  const run = () => act(async () => saveText(`opticore-config-${new Date().toISOString().slice(0, 10)}.json`, JSON.stringify(await call('backup.export'), null, 2)), t('Yapılandırma kaydedildi', 'Configuration saved'));
  return html`<div class="card"><${Empty} icon="download" title=${t('Yapılandırmayı dışa aktar', 'Export the configuration')}
    text=${t('OptiCore ayarlarınızı, uygulanmış Windows ayarlarını, başlangıç öğelerinin durumunu ve tüm değişiklik günlüğünü (önceki ve sonraki değerleriyle) tek bir JSON dosyasına yazar. Yedek olarak saklayabilir ya da sorun giderirken paylaşabilirsiniz.',
      'Writes your OptiCore settings, the applied Windows tweaks, the state of startup items and the complete change journal (with before and after values) into one JSON file. Keep it as a backup or share it when troubleshooting.')}
    action=${html`<${Btn} kind="primary" icon="download" onClick=${run}>${t('JSON olarak kaydet', 'Save as JSON')}<//>`} /></div>`;
}

export default function Backup({ sub }) {
  const tabs = ['journal', 'restore', 'export'];
  const [tab, setTab] = useState(tabs.includes(sub) ? sub : 'journal');
  return html`<${Page} title=${t('Geri Alma Merkezi', 'Undo Center')} desc=${t('OptiCore\'un güvenlik ağı: hiçbir değişiklik kayda geçmeden uygulanmaz ve geri alınabilir olan her şey tek tıkla eski hâline döner.', 'OptiCore\'s safety net: no change is applied before it is recorded, and everything reversible returns to its previous state in one click.')}>
    <div class="col" style=${{ gap: '14px' }}>
      <${SimulationNote} />
      <div><${Tabs} value=${tab} onChange=${setTab} items=${[
        { id: 'journal', label: t('Değişiklik günlüğü', 'Change journal'), icon: 'history' },
        { id: 'restore', label: t('Geri yükleme noktaları', 'Restore points'), icon: 'flag' },
        { id: 'export', label: t('Dışa aktar', 'Export'), icon: 'download' },
      ]} /></div>
      ${tab === 'journal' ? html`<${Journal} />` : tab === 'restore' ? html`<${RestorePoints} />` : html`<${Export} />`}
    </div>
  <//>`;
}
