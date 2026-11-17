// Smart Cleanup: junk categories with a preview, plus duplicate, large-file, empty-folder and project-leftover finders.
import {
  html, useState, useEffect, useMemo, useRpc, useJob, call, act, t, cx, num, bytes, date, dateTime, ago, confirmDialog, toast, app, useStore,
} from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Check, Banner, Empty, Loading, JobBar, Icon, Stat, Async, Select, AdminChip, SimulationNote, Table } from '../lib/ui.js';
import { Donut, Legend, PALETTE } from '../lib/charts.js';

const GROUPS = [
  ['system', 'Windows', 'Windows', 'windows'],
  ['browsers', 'Tarayıcılar', 'Browsers', 'globe'],
  ['apps', 'Uygulamalar', 'Apps', 'package'],
  ['dev', 'Geliştirici önbellekleri', 'Developer caches', 'terminal'],
];

function useScan(method) {
  const job = useJob();
  const [result, setResult] = useState(null);
  const run = (params) => act(async () => { setResult(null); setResult(await job.run(method, params)); });
  return { job, result, setResult, run };
}

/** Lets the user choose which folders a file scan covers. */
function RootPicker({ roots, setRoots }) {
  const defaults = useRpc('cleanup.roots');
  const [custom, setCustom] = useState('');
  useEffect(() => { if (defaults.data && roots === null) setRoots(defaults.data); }, [defaults.data]);
  const all = [...new Set([...(defaults.data || []), ...(roots || [])])];
  const pick = () => call('shell.pickFolder').then((p) => { if (p) setRoots([...(roots || []), p]); }).catch(() => {});
  const info = app.get().info;
  return html`<div class="col" style=${{ gap: '6px' }}>
    <div class="row wrap gap-s">
      ${all.map((r) => html`<label key=${r} class="chip" style=${{ cursor: 'pointer', height: '26px' }}>
        <${Check} checked=${(roots || []).includes(r)} onChange=${(on) => setRoots(on ? [...(roots || []), r] : (roots || []).filter((x) => x !== r))} />
        <span class="mono">${r.length > 46 ? '…' + r.slice(-44) : r}</span>
      </label>`)}
    </div>
    <div class="row">
      <input class="input sm grow" placeholder=${t('Başka bir klasör ekleyin (ör. D:\\Arşiv)', 'Add another folder (e.g. D:\\Archive)')} value=${custom} onInput=${(e) => setCustom(e.target.value)}
        onKeyDown=${(e) => { if (e.key === 'Enter' && custom.trim()) { setRoots([...(roots || []), custom.trim()]); setCustom(''); } }} />
      ${info.host === 'desktop' && html`<${Btn} size="sm" icon="folder" onClick=${pick}>${t('Seç…', 'Browse…')}<//>`}
    </div>
  </div>`;
}

// ---------------------------------------------------------------- junk cleanup
function Junk({ autostart }) {
  const last = useRpc('cleanup.last');
  const scanJob = useJob();
  const cleanJob = useJob();
  const info = useStore(app, (s) => s.info);
  const [scan, setScan] = useState(null);
  const [selected, setSelected] = useState(new Set());
  const [open, setOpen] = useState(null);
  const [done, setDone] = useState(null);

  const adopt = (s) => {
    setScan(s);
    setSelected(new Set(s.categories.filter((c) => c.defaultOn && c.available && c.bytes > 0).map((c) => c.id)));
  };
  useEffect(() => { if (last.data && !scan) adopt(last.data); }, [last.data]);

  const runScan = () => act(async () => { setDone(null); adopt(await scanJob.run('cleanup.scan')); });
  useEffect(() => { if (autostart) runScan(); }, []);

  const chosen = scan ? scan.categories.filter((c) => selected.has(c.id)) : [];
  const chosenBytes = chosen.reduce((s, c) => s + c.bytes, 0);

  const clean = async () => {
    const recycle = selected.has('recyclebin');
    const ok = await confirmDialog({
      title: t(`${bytes(chosenBytes)} temizlensin mi?`, `Clean ${bytes(chosenBytes)}?`),
      body: t(`${chosen.length} kategori temizlenecek. Kullanımda olan dosyalar atlanır.`, `${chosen.length} categories will be cleaned. Files that are in use are skipped.`)
        + (recycle ? '\n\n' + t('Geri Dönüşüm Kutusu da boşaltılacak: içindeki dosyalar geri getirilemez.', 'The Recycle Bin will be emptied too: its files cannot be restored.') : '')
        + (info.dryRun ? '\n\n' + t('Simülasyon modu açık: hiçbir dosya silinmeyecek.', 'Simulation mode is on: no file will be deleted.') : ''),
      details: chosen.map((c) => `${c.title}: ${bytes(c.bytes)}`).join('\n'),
      confirm: t('Temizle', 'Clean'), danger: recycle,
    });
    if (!ok) return;
    await act(async () => {
      const r = await cleanJob.run('cleanup.clean', { ids: [...selected] });
      setDone(r);
      if (!r.simulated) { setScan(null); last.reload(); }
    });
  };

  if (!scan && !scanJob.running) {
    return html`<div class="col gap-l">
      ${done && html`<${Result} done=${done} onClose=${() => setDone(null)} />`}
      <div class="card"><${Empty} icon="sparkles" title=${t('Ne kadar yer açılabilir?', 'How much space can be freed?')}
        text=${t('Tarama hiçbir şeyi silmez: geçici dosyaları ve yeniden oluşturulabilen önbellekleri bulur, boyutlarını ölçer ve size gösterir. Neyin temizleneceğine siz karar verirsiniz.',
          'Scanning deletes nothing: it finds temporary files and caches that can be rebuilt, measures them and shows you. You decide what gets cleaned.')}
        action=${html`<${Btn} kind="primary" size="lg" icon="search" onClick=${runScan}>${t('Taramayı başlat', 'Start scan')}<//>`} /></div>
      <${WhatWeSkip} />
    </div>`;
  }

  const segments = scan ? GROUPS.map(([id, tr, en], i) => ({ label: t(tr, en), value: scan.categories.filter((c) => c.group === id).reduce((s, c) => s + c.bytes, 0), color: PALETTE[i] })).filter((s) => s.value > 0) : [];
  return html`<div class="col gap-l">
    <${JobBar} job=${scanJob.job} onCancel=${scanJob.cancel} />
    <${JobBar} job=${cleanJob.job} onCancel=${cleanJob.cancel} />
    ${done && html`<${Result} done=${done} onClose=${() => setDone(null)} />`}
    ${scan && html`
      <div class="card pad row gap-l wrap">
        <${Donut} segments=${segments} size=${120} stroke=${15}><b class="num" style=${{ fontSize: '15px' }}>${bytes(scan.totalBytes)}</b><span class="tiny dim">${t('bulundu', 'found')}</span><//>
        <div class="grow col" style=${{ minWidth: '240px' }}>
          <div><div class="up">${t('Seçili temizlik', 'Selected to clean')}</div><div class="big acc">${bytes(chosenBytes)}</div></div>
          <${Legend} items=${segments.map((s) => ({ label: s.label, color: s.color, value: bytes(s.value) }))} />
          <div class="tiny dim">${t('Tarama', 'Scanned')}: ${dateTime(scan.time)}</div>
        </div>
        <div class="col" style=${{ alignItems: 'flex-end' }}>
          <${Btn} kind="primary" size="lg" icon="sparkles" disabled=${chosenBytes === 0 || cleanJob.running} onClick=${clean}>${t('Seçilenleri temizle', 'Clean selected')}<//>
          <${Btn} kind="ghost" icon="refresh" busy=${scanJob.running} onClick=${runScan}>${t('Yeniden tara', 'Scan again')}<//>
        </div>
      </div>
      ${scan.categories.some((c) => !c.available && c.bytes > 0) && html`<${Banner} tone="warn" icon="shield">
        ${t(`${bytes(scan.categories.filter((c) => !c.available).reduce((s, c) => s + c.bytes, 0))} daha, yalnızca yönetici olarak çalıştırıldığında temizlenebilir.`,
          `Another ${bytes(scan.categories.filter((c) => !c.available).reduce((s, c) => s + c.bytes, 0))} can only be cleaned when running as administrator.`)}<//>`}
      ${GROUPS.map(([id, tr, en, icon]) => {
        const items = scan.categories.filter((c) => c.group === id);
        if (!items.length) return null;
        const allOn = items.filter((c) => c.available && c.bytes > 0);
        return html`<${Card} key=${id} title=${t(tr, en)} icon=${icon} flush sub=${bytes(items.reduce((s, c) => s + c.bytes, 0))}
          actions=${allOn.length > 1 && html`<${Btn} size="sm" kind="ghost" onClick=${() => {
            const every = allOn.every((c) => selected.has(c.id));
            const next = new Set(selected);
            allOn.forEach((c) => (every ? next.delete(c.id) : next.add(c.id)));
            setSelected(next);
          }}>${allOn.every((c) => selected.has(c.id)) ? t('Seçimi kaldır', 'Deselect all') : t('Tümünü seç', 'Select all')}<//>`}>
          ${items.map((c) => html`<div key=${c.id}>
            <div class="item click" onClick=${() => setOpen(open === c.id ? null : c.id)}>
              <${Check} checked=${selected.has(c.id)} disabled=${!c.available || c.bytes === 0}
                onChange=${(on) => { const next = new Set(selected); if (on) next.add(c.id); else next.delete(c.id); setSelected(next); }} />
              <div class="grow">
                <div class="row gap-s"><span class="t">${c.title}</span>${!c.available && html`<${AdminChip} />`}${c.note && html`<${Chip} tone="info" icon="info">${t('Not', 'Note')}<//>`}</div>
                <div class="s">${c.description}</div>
              </div>
              <div style=${{ textAlign: 'right' }}><div class=${cx('b num', c.bytes === 0 && 'dim')}>${bytes(c.bytes)}</div><div class="tiny dim num">${num(c.files)} ${t('öğe', 'items')}</div></div>
              <${Icon} name=${open === c.id ? 'chevD' : 'chevR'} size="15" class="dim" />
            </div>
            ${open === c.id && html`<div style=${{ padding: '4px 16px 14px 44px', borderBottom: '1px solid var(--line)' }}>
              ${c.note && html`<${Banner} tone="info">${c.note}<//>`}
              ${c.parts.length > 0 && html`<div class="row wrap gap-s mt-s">${c.parts.slice(0, 14).map((p) => html`<${Chip} key=${p.label}>${p.label} · ${bytes(p.bytes)}<//>`)}</div>`}
              ${c.largest.length > 0 ? html`<div class="mt-s tiny dim">${t('En büyük dosyalar', 'Largest files')}</div>
                <div class="code" style=${{ maxHeight: '170px', marginTop: '4px' }}>${c.largest.slice(0, 12).map((f) => `${bytes(f.bytes).padStart(9)}  ${f.path}`).join('\n')}</div>`
                : c.bytes === 0 && html`<div class="small dim mt-s">${t('Burada temizlenecek bir şey yok.', 'Nothing to clean here.')}</div>`}
            </div>`}
          </div>`)}
        <//>`;
      })}
      <${WhatWeSkip} />`}
  </div>`;
}

function Result({ done, onClose }) {
  return html`<${Banner} tone=${done.simulated ? 'acc' : 'good'} icon=${done.simulated ? 'flask' : 'check'} action=${html`<${Btn} size="sm" kind="ghost" icon="x" onClick=${onClose} />`}>
    <b>${done.simulated ? t(`Simülasyon: ${bytes(done.freedBytes)} temizlenecekti`, `Simulation: ${bytes(done.freedBytes)} would have been freed`) : t(`${bytes(done.freedBytes)} yer açıldı`, `${bytes(done.freedBytes)} freed`)}</b>
    <span class="muted"> · ${t(`${num(done.deleted)} dosya`, `${num(done.deleted)} files`)}${done.skipped > 0 ? ' · ' + t(`${num(done.skipped)} dosya kullanımda olduğu için atlandı`, `${num(done.skipped)} skipped because they were in use`) : ''}</span>
  <//>`;
}

function WhatWeSkip() {
  return html`<${Card} title=${t('OptiCore\'un bilerek dokunmadıkları', 'What OptiCore deliberately leaves alone')} icon="shieldCheck">
    <div class="grid c2 small muted" style=${{ gap: '8px 24px' }}>
      <div><b style=${{ color: 'var(--text)' }}>${t('Kayıt defteri "temizliği"', 'Registry "cleaning"')}</b><br />${t('Ölçülebilir hiçbir hız kazancı sağlamaz, ama yanlış bir silme programları bozabilir.', 'It brings no measurable speed gain, while one wrong deletion can break programs.')}</div>
      <div><b style=${{ color: 'var(--text)' }}>Prefetch</b><br />${t('Silmek bilgisayarı hızlandırmaz; tersine uygulamaların ilk açılışını yavaşlatır.', 'Deleting it does not speed up the PC; it slows down the first launch of apps instead.')}</div>
      <div><b style=${{ color: 'var(--text)' }}>${t('Windows Installer önbelleği', 'Windows Installer cache')}</b><br />${t('Programların onarılması ve kaldırılması için gereklidir.', 'Needed to repair and uninstall programs.')}</div>
      <div><b style=${{ color: 'var(--text)' }}>${t('Tarayıcı geçmişi, çerezler, parolalar', 'Browser history, cookies, passwords')}</b><br />${t('Bunlar çöp değil, sizin verileriniz. Yalnızca yeniden oluşturulabilen önbellek temizlenir.', 'These are your data, not junk. Only cache that can be rebuilt is cleaned.')}</div>
    </div>
  <//>`;
}

// ---------------------------------------------------------------- selection helpers for file tools
function useSelection() {
  const [sel, setSel] = useState(new Set());
  return {
    sel, clear: () => setSel(new Set()), set: (paths) => setSel(new Set(paths)), has: (p) => sel.has(p),
    toggle: (p, on) => setSel((s) => { const next = new Set(s); if (on) next.add(p); else next.delete(p); return next; }),
  };
}

const reveal = (path) => act(() => call('shell.reveal', { path }));

// ---------------------------------------------------------------- duplicates
function Duplicates() {
  const [roots, setRoots] = useState(null);
  const [minSize, setMinSize] = useState('1048576');
  const scan = useScan('cleanup.duplicates');
  const s = useSelection();
  const r = scan.result;

  const keepOldest = () => s.set(r.groups.flatMap((g) => g.files.slice(1).map((f) => f.path)));
  const selectedBytes = r ? r.groups.reduce((sum, g) => sum + g.files.filter((f) => s.has(f.path)).length * g.size, 0) : 0;
  const remove = () => confirmDialog({
    title: t(`${s.sel.size} dosya Geri Dönüşüm Kutusu'na taşınsın mı?`, `Move ${s.sel.size} files to the Recycle Bin?`),
    body: t(`${bytes(selectedBytes)} yer açılacak. Dosyalar Geri Dönüşüm Kutusu'ndan geri alınabilir. Her gruptan en az bir kopya korunur.`,
      `${bytes(selectedBytes)} will be freed. The files can be restored from the Recycle Bin. At least one copy of every group is kept.`),
    confirm: t('Taşı', 'Move'),
  }).then((ok) => ok && act(() => call('cleanup.recycle', { paths: [...s.sel] }), (res) => (res.simulated ? t('Simülasyon: dosyalar taşınmadı', 'Simulation: nothing was moved') : t(`${res.done} dosya taşındı`, `${res.done} files moved`)))
    .then((res) => { if (res && !res.simulated) { scan.setResult({ ...r, groups: r.groups.map((g) => ({ ...g, files: g.files.filter((f) => !s.has(f.path)) })).filter((g) => g.files.length > 1) }); s.clear(); } }));

  return html`<div class="col gap-l">
    <div class="card pad col">
      <div class="up">${t('Taranacak klasörler', 'Folders to scan')}</div>
      <${RootPicker} roots=${roots} setRoots=${setRoots} />
      <div class="row wrap">
        <span class="small muted">${t('En küçük dosya boyutu', 'Smallest file size')}</span>
        <${Select} class="sm" value=${minSize} onChange=${setMinSize} options=${[{ value: '102400', label: '100 KB' }, { value: '1048576', label: '1 MB' }, { value: '10485760', label: '10 MB' }, { value: '104857600', label: '100 MB' }]} />
        <div class="sp"></div>
        <${Btn} kind="primary" icon="search" busy=${scan.job.running} disabled=${!roots || !roots.length} onClick=${() => { s.clear(); scan.run({ roots, minSize: +minSize }); }}>${t('Yinelenenleri bul', 'Find duplicates')}<//>
      </div>
      <div class="tiny dim">${t('Dosyalar boyuta, sonra içeriklerinin SHA-256 özetine göre karşılaştırılır; adı ya da tarihi aynı diye hiçbir dosya yinelenen sayılmaz.', 'Files are compared by size, then by the SHA-256 hash of their content; no file counts as a duplicate just because its name or date matches.')}</div>
    </div>
    <${JobBar} job=${scan.job.job} onCancel=${scan.job.cancel} />
    ${r && (r.groups.length === 0
      ? html`<div class="card"><${Empty} icon="check" title=${t('Yinelenen dosya bulunamadı', 'No duplicate files found')} /></div>`
      : html`
        <div class="card pad row wrap">
          <${Stat} label=${t('Boşa harcanan alan', 'Wasted space')} value=${bytes(r.wasted)} />
          <div style=${{ width: '24px' }}></div>
          <${Stat} label=${t('Yinelenen grup', 'Duplicate groups')} value=${num(r.count)} />
          <div class="sp row gap-s">
            <${Btn} size="sm" icon="check" onClick=${keepOldest}>${t('Her grupta en eskisini tut', 'Keep the oldest in each group')}<//>
            <${Btn} kind="primary" icon="trash" disabled=${!s.sel.size} onClick=${remove}>${t(`Seçilenleri taşı (${bytes(selectedBytes)})`, `Move selected (${bytes(selectedBytes)})`)}<//>
          </div>
        </div>
        ${r.groups.slice(0, 150).map((g, i) => html`<div key=${g.files[0].path} class="card">
          <div class="card-h"><h3>${g.files[0].path.split('\\').pop()}</h3><span class="sub">${bytes(g.size)} × ${g.files.length}</span><${Chip} tone="warn" class="sp">${t('boşa', 'wasted')} ${bytes(g.wasted)}<//></div>
          <div class="card-b flush">${g.files.map((f) => {
            const others = g.files.filter((x) => x.path !== f.path && !s.has(x.path)).length;
            return html`<div key=${f.path} class="item">
              <${Check} checked=${s.has(f.path)} disabled=${!s.has(f.path) && others === 0} onChange=${(on) => s.toggle(f.path, on)} />
              <div class="grow mono wrap-any">${f.path}</div>
              <span class="tiny dim">${date(f.modified)}</span>
              <${Btn} size="sm" kind="ghost" icon="folder" onClick=${() => reveal(f.path)} />
            </div>`;
          })}</div>
        </div>`)}`)}
  </div>`;
}

// ---------------------------------------------------------------- large files
function LargeFiles() {
  const [roots, setRoots] = useState(null);
  const [minSize, setMinSize] = useState('104857600');
  const [days, setDays] = useState('0');
  const scan = useScan('cleanup.large');
  const s = useSelection();
  const r = scan.result;
  const selectedBytes = r ? r.files.filter((f) => s.has(f.path)).reduce((sum, f) => sum + f.bytes, 0) : 0;
  const remove = () => confirmDialog({
    title: t(`${s.sel.size} dosya Geri Dönüşüm Kutusu'na taşınsın mı?`, `Move ${s.sel.size} files to the Recycle Bin?`),
    body: t(`${bytes(selectedBytes)} yer açılacak. Dosyalar Geri Dönüşüm Kutusu'ndan geri alınabilir.`, `${bytes(selectedBytes)} will be freed. The files can be restored from the Recycle Bin.`),
    confirm: t('Taşı', 'Move'),
  }).then((ok) => ok && act(() => call('cleanup.recycle', { paths: [...s.sel] }), (res) => (res.simulated ? t('Simülasyon: dosyalar taşınmadı', 'Simulation: nothing was moved') : t(`${res.done} dosya taşındı`, `${res.done} files moved`)))
    .then((res) => { if (res && !res.simulated) { scan.setResult({ ...r, files: r.files.filter((f) => !s.has(f.path)) }); s.clear(); } }));

  return html`<div class="col gap-l">
    <div class="card pad col">
      <div class="up">${t('Taranacak klasörler', 'Folders to scan')}</div>
      <${RootPicker} roots=${roots} setRoots=${setRoots} />
      <div class="row wrap">
        <span class="small muted">${t('En az', 'At least')}</span>
        <${Select} class="sm" value=${minSize} onChange=${setMinSize} options=${[{ value: '52428800', label: '50 MB' }, { value: '104857600', label: '100 MB' }, { value: '524288000', label: '500 MB' }, { value: '1073741824', label: '1 GB' }]} />
        <span class="small muted">${t('Son değişiklik', 'Last changed')}</span>
        <${Select} class="sm" value=${days} onChange=${setDays} options=${[{ value: '0', label: t('fark etmez', 'any time') }, { value: '90', label: t('90 günden eski', 'over 90 days ago') }, { value: '365', label: t('1 yıldan eski', 'over a year ago') }]} />
        <div class="sp"></div>
        <${Btn} kind="primary" icon="search" busy=${scan.job.running} disabled=${!roots || !roots.length} onClick=${() => { s.clear(); scan.run({ roots, minSize: +minSize, olderThanDays: +days }); }}>${t('Büyük dosyaları bul', 'Find large files')}<//>
      </div>
    </div>
    <${JobBar} job=${scan.job.job} onCancel=${scan.job.cancel} />
    ${r && (r.files.length === 0 ? html`<div class="card"><${Empty} icon="check" title=${t('Bu ölçütlere uyan dosya yok', 'No files match')} /></div>` : html`
      <div class="card">
        <div class="card-h"><h3>${t(`${num(r.count)} dosya, toplam ${bytes(r.total)}`, `${num(r.count)} files, ${bytes(r.total)} in total`)}</h3>
          <div class="sp"><${Btn} kind="primary" size="sm" icon="trash" disabled=${!s.sel.size} onClick=${remove}>${t(`Seçilenleri taşı (${bytes(selectedBytes)})`, `Move selected (${bytes(selectedBytes)})`)}<//></div></div>
        <${Table} rows=${r.files} rowKey="path" initialSort=${{ id: 'bytes', dir: 'desc' }} maxHeight="calc(100vh - 380px)" columns=${[
          { id: 'sel', label: '', class: 'shrink', render: (f) => html`<${Check} checked=${s.has(f.path)} onChange=${(on) => s.toggle(f.path, on)} />` },
          { id: 'path', label: t('Dosya', 'File'), sort: true, render: (f) => html`<div><div class="b ell">${f.path.split('\\').pop()}</div><div class="tiny dim mono wrap-any">${f.path}</div></div>` },
          { id: 'ext', label: t('Tür', 'Type'), sort: true, class: 'shrink', render: (f) => html`<${Chip}>${f.ext || '—'}<//>` },
          { id: 'bytes', label: t('Boyut', 'Size'), num: true, sort: true, class: 'shrink', render: (f) => bytes(f.bytes) },
          { id: 'modified', label: t('Değişiklik', 'Changed'), sort: true, class: 'shrink', render: (f) => ago(f.modified) },
          { id: 'open', label: '', class: 'shrink', render: (f) => html`<${Btn} size="sm" kind="ghost" icon="folder" onClick=${() => reveal(f.path)} />` },
        ]} />
      </div>`)}
  </div>`;
}

// ---------------------------------------------------------------- empty folders
function EmptyFolders() {
  const [roots, setRoots] = useState(null);
  const scan = useScan('cleanup.empty');
  const s = useSelection();
  const r = scan.result;
  const remove = () => act(() => call('cleanup.deleteEmpty', { paths: [...s.sel] }), (res) => (res.simulated ? t('Simülasyon: klasörler silinmedi', 'Simulation: nothing was deleted') : t(`${res.done} boş klasör silindi`, `${res.done} empty folders deleted`)))
    .then((res) => { if (res && !res.simulated) { scan.setResult({ ...r, folders: r.folders.filter((f) => !s.has(f)) }); s.clear(); } });
  return html`<div class="col gap-l">
    <div class="card pad col">
      <div class="up">${t('Taranacak klasörler', 'Folders to scan')}</div>
      <${RootPicker} roots=${roots} setRoots=${setRoots} />
      <div class="row"><span class="tiny dim grow">${t('Silme sırasında her klasör yeniden kontrol edilir; içine bir şey eklenmişse dokunulmaz.', 'Each folder is checked again at deletion time; if something was added to it meanwhile it is left alone.')}</span>
        <${Btn} kind="primary" icon="search" busy=${scan.job.running} disabled=${!roots || !roots.length} onClick=${() => { s.clear(); scan.run({ roots }); }}>${t('Boş klasörleri bul', 'Find empty folders')}<//></div>
    </div>
    <${JobBar} job=${scan.job.job} onCancel=${scan.job.cancel} />
    ${r && (r.folders.length === 0 ? html`<div class="card"><${Empty} icon="check" title=${t('Boş klasör yok', 'No empty folders')} /></div>` : html`<div class="card">
      <div class="card-h"><h3>${t(`${num(r.folders.length)} boş klasör`, `${num(r.folders.length)} empty folders`)}</h3>
        <div class="sp row gap-s"><${Btn} size="sm" onClick=${() => (s.sel.size === r.folders.length ? s.clear() : s.set(r.folders))}>${s.sel.size === r.folders.length ? t('Seçimi kaldır', 'Deselect all') : t('Tümünü seç', 'Select all')}<//>
          <${Btn} size="sm" kind="primary" icon="trash" disabled=${!s.sel.size} onClick=${remove}>${t(`${s.sel.size} klasörü sil`, `Delete ${s.sel.size} folders`)}<//></div></div>
      <div class="card-b flush" style=${{ maxHeight: 'calc(100vh - 380px)', overflow: 'auto' }}>
        ${r.folders.slice(0, 600).map((f) => html`<div key=${f} class="item"><${Check} checked=${s.has(f)} onChange=${(on) => s.toggle(f, on)} /><span class="grow mono wrap-any">${f}</span></div>`)}
      </div>
    </div>`)}
  </div>`;
}

// ---------------------------------------------------------------- project leftovers
function Artifacts() {
  const [idle, setIdle] = useState('30');
  const [path, setPath] = useState('');
  const scan = useScan('cleanup.artifacts');
  const s = useSelection();
  const r = scan.result;
  useEffect(() => { if (r) s.set(r.items.filter((x) => x.sure).map((x) => x.path)); }, [r]);
  const selectedBytes = r ? r.items.filter((x) => s.has(x.path)).reduce((sum, x) => sum + x.bytes, 0) : 0;
  const remove = () => confirmDialog({
    title: t(`${s.sel.size} klasör kalıcı olarak silinsin mi?`, `Permanently delete ${s.sel.size} folders?`),
    body: t(`${bytes(selectedBytes)} yer açılacak. Bunlar yeniden üretilebilen çıktılar (npm install, derleme, sanal ortam kurulumu), ama Geri Dönüşüm Kutusu'na gitmezler.`,
      `${bytes(selectedBytes)} will be freed. These are outputs that can be regenerated (npm install, build, virtual environment setup), but they do not go to the Recycle Bin.`),
    confirm: t('Kalıcı olarak sil', 'Delete permanently'), danger: true,
  }).then((ok) => ok && act(() => call('cleanup.deleteArtifacts', { paths: [...s.sel] }), (res) => (res.simulated ? t('Simülasyon: klasörler silinmedi', 'Simulation: nothing was deleted') : t(`${res.done} klasör silindi`, `${res.done} folders deleted`)))
    .then((res) => { if (res && !res.simulated) { scan.setResult({ ...r, items: r.items.filter((x) => !s.has(x.path)) }); s.clear(); } }));

  return html`<div class="col gap-l">
    <div class="card pad col">
      <div class="small muted">${t('Uzun süredir dokunulmamış projelerdeki node_modules, derleme çıktısı ve sanal ortam klasörlerini bulur. Bir klasörün derleme çıktısı sayılması için yanında proje dosyası (package.json, .csproj, Cargo.toml…) bulunmalıdır.',
        'Finds node_modules, build output and virtual environments in projects nobody has touched for a while. A folder only counts as build output when the matching project file (package.json, .csproj, Cargo.toml…) sits next to it.')}</div>
      <div class="row wrap">
        <input class="input sm grow" placeholder=${t('Proje klasörü (boş bırakılırsa Masaüstü, Belgeler, source, repos…)', 'Projects folder (leave empty for Desktop, Documents, source, repos…)')} value=${path} onInput=${(e) => setPath(e.target.value)} />
        <span class="small muted">${t('Dokunulmayan süre', 'Untouched for')}</span>
        <${Select} class="sm" value=${idle} onChange=${setIdle} options=${[{ value: '0', label: t('fark etmez', 'any time') }, { value: '30', label: t('30+ gün', '30+ days') }, { value: '90', label: t('90+ gün', '90+ days') }, { value: '180', label: t('180+ gün', '180+ days') }]} />
        <${Btn} kind="primary" icon="search" busy=${scan.job.running} onClick=${() => { s.clear(); scan.run({ roots: path.trim() ? [path.trim()] : [], idleDays: +idle }); }}>${t('Proje artıklarını bul', 'Find project leftovers')}<//>
      </div>
    </div>
    <${JobBar} job=${scan.job.job} onCancel=${scan.job.cancel} />
    ${r && (r.items.length === 0 ? html`<div class="card"><${Empty} icon="check" title=${t('Temizlenecek proje artığı yok', 'No project leftovers found')} text=${t('Tarananlar: ', 'Scanned: ') + r.roots.join(', ')} /></div>` : html`<div class="card">
      <div class="card-h"><h3>${t(`${num(r.items.length)} klasör, toplam ${bytes(r.total)}`, `${num(r.items.length)} folders, ${bytes(r.total)} in total`)}</h3>
        <div class="sp"><${Btn} size="sm" kind="danger" icon="trash" disabled=${!s.sel.size} onClick=${remove}>${t(`Seçilenleri sil (${bytes(selectedBytes)})`, `Delete selected (${bytes(selectedBytes)})`)}<//></div></div>
      <${Table} rows=${r.items} rowKey="path" initialSort=${{ id: 'bytes', dir: 'desc' }} maxHeight="calc(100vh - 380px)" columns=${[
        { id: 'sel', label: '', class: 'shrink', render: (x) => html`<${Check} checked=${s.has(x.path)} onChange=${(on) => s.toggle(x.path, on)} />` },
        { id: 'project', label: t('Proje', 'Project'), sort: true, render: (x) => html`<div><div class="b ell">${x.project.split('\\').pop()}</div><div class="tiny dim mono wrap-any">${x.path}</div></div>` },
        { id: 'kind', label: t('Tür', 'Kind'), sort: true, class: 'shrink', render: (x) => html`<${Chip} tone=${x.sure ? null : 'warn'} title=${x.sure ? '' : t('Genellikle derleme çıktısıdır ama bazı projeler bunu depoya ekler; emin değilseniz silmeyin.', 'Usually build output, but some projects commit it; leave it if unsure.')}>${x.kind}<//>` },
        { id: 'bytes', label: t('Boyut', 'Size'), num: true, sort: true, class: 'shrink', render: (x) => bytes(x.bytes) },
        { id: 'idleDays', label: t('Dokunulmadı', 'Untouched'), num: true, sort: true, class: 'shrink', render: (x) => t(`${num(x.idleDays)} gün`, `${num(x.idleDays)} days`) },
        { id: 'open', label: '', class: 'shrink', render: (x) => html`<${Btn} size="sm" kind="ghost" icon="folder" onClick=${() => reveal(x.path)} />` },
      ]} />
    </div>`)}
  </div>`;
}

function History() {
  const history = useRpc('cleanup.history');
  return html`<${Async} state=${history}>${(list) => {
    const real = list.filter((x) => !x.simulated);
    return html`<div class="col gap-l">
      <div class="grid c3">
        <div class="card pad"><${Stat} label=${t('Toplam açılan alan', 'Total space freed')} value=${bytes(real.reduce((s, x) => s + x.freedBytes, 0))} /></div>
        <div class="card pad"><${Stat} label=${t('Temizlik sayısı', 'Cleanups run')} value=${num(real.length)} /></div>
        <div class="card pad"><${Stat} label=${t('Son temizlik', 'Last cleanup')} value=${real[0] ? ago(real[0].time) : '–'} /></div>
      </div>
      <div class="card">${list.length === 0 ? html`<${Empty} icon="history" title=${t('Henüz temizlik yapılmadı', 'No cleanup has run yet')} />` : list.map((x, i) => html`<div key=${i} class="item">
        <${Icon} name=${x.simulated ? 'flask' : 'sparkles'} size="16" class=${x.simulated ? 'acc' : 'good'} />
        <div class="grow"><div class="t">${bytes(x.freedBytes)}${x.simulated ? ' · ' + t('simülasyon', 'simulated') : ''}</div><div class="s">${dateTime(x.time)} · ${t(`${num(x.deleted)} dosya`, `${num(x.deleted)} files`)} · ${t(`${x.categories.length} kategori`, `${x.categories.length} categories`)}</div></div>
      </div>`)}</div>
    </div>`;
  }}<//>`;
}

export default function Cleanup({ sub }) {
  const tabs = ['junk', 'duplicates', 'large', 'empty', 'artifacts', 'history'];
  const [tab, setTab] = useState(tabs.includes(sub) ? sub : 'junk');
  return html`<${Page} title=${t('Akıllı Temizlik', 'Smart Cleanup')} desc=${t('Önce ölçer, sonra gösterir, en son siler. Silinen her şey Geri Alma Merkezi\'nde kayıt altındadır.', 'It measures first, shows second and deletes last. Everything it deletes is recorded in the Undo Center.')}>
    <div class="col" style=${{ gap: '14px' }}>
      <${SimulationNote} />
      <div><${Tabs} value=${tab} onChange=${setTab} items=${[
        { id: 'junk', label: t('Gereksiz dosyalar', 'Junk files'), icon: 'sparkles' },
        { id: 'duplicates', label: t('Yinelenenler', 'Duplicates'), icon: 'copy' },
        { id: 'large', label: t('Büyük dosyalar', 'Large files'), icon: 'file' },
        { id: 'empty', label: t('Boş klasörler', 'Empty folders'), icon: 'folder' },
        { id: 'artifacts', label: t('Proje artıkları', 'Project leftovers'), icon: 'terminal' },
        { id: 'history', label: t('Geçmiş', 'History'), icon: 'history' },
      ]} /></div>
      ${tab === 'junk' ? html`<${Junk} autostart=${sub === 'scan'} />` : tab === 'duplicates' ? html`<${Duplicates} />` : tab === 'large' ? html`<${LargeFiles} />`
        : tab === 'empty' ? html`<${EmptyFolders} />` : tab === 'artifacts' ? html`<${Artifacts} />` : html`<${History} />`}
    </div>
  <//>`;
}
