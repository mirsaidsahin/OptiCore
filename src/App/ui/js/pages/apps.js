// Uninstaller: Win32 and Store apps, bloat detection, batch and silent removal, leftover sweeping, portable apps.
import { html, useState, useMemo, useRpc, useJob, call, act, t, num, bytes, date, matches, confirmDialog } from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Search, Check, FileIcon, Empty, Loading, Banner, Async, Stat, JobBar, Table, Modal, Switch, SimulationNote } from '../lib/ui.js';

function Leftovers({ app, onClose }) {
  const d = useRpc('apps.leftovers', { id: app.id, name: app.name, publisher: app.publisher, location: app.location });
  const [sel, setSel] = useState(null);
  const items = d.data ? d.data.items : [];
  const chosen = sel ?? new Set(items.map((x) => x.path));
  const remove = () => act(() => call('apps.removeLeftovers', {
    folders: items.filter((x) => chosen.has(x.path) && x.kind === 'folder').map((x) => x.path),
    keys: items.filter((x) => chosen.has(x.path) && x.kind === 'registry').map((x) => x.path),
    files: items.filter((x) => chosen.has(x.path) && x.kind === 'shortcut').map((x) => x.path),
  }), (r) => (r.simulated ? t('Simülasyon: hiçbir şey silinmedi', 'Simulation: nothing was removed') : t(`${r.done} artık temizlendi`, `${r.done} leftovers removed`))).then((r) => r && onClose());
  return html`<${Modal} wide title=${t(`Artıklar: ${app.name}`, `Leftovers: ${app.name}`)} onClose=${onClose}
    footer=${html`<${Btn} kind="ghost" onClick=${onClose}>${t('Kapat', 'Close')}<//>${items.length > 0 && html`<${Btn} kind="primary" icon="trash" disabled=${!chosen.size} onClick=${remove}>${t(`Seçilenleri temizle (${chosen.size})`, `Remove selected (${chosen.size})`)}<//>`}`}>
    ${d.data === undefined ? html`<${Loading} />` : !items.length ? html`<${Empty} icon="check" title=${t('Artık bulunamadı', 'No leftovers found')} text=${t('Kaldırıcı arkasında bir şey bırakmamış.', 'The uninstaller left nothing behind.')} />` : html`
      <${Banner} tone="info">${t('Klasörler ve kısayollar Geri Dönüşüm Kutusu\'na taşınır; kayıt defteri anahtarları silinmeden önce yedeklenir ve Geri Alma Merkezi\'nden geri yüklenebilir. Tanımadığınız öğenin işaretini kaldırın.',
        'Folders and shortcuts go to the Recycle Bin; registry keys are backed up before deletion and can be restored from the Undo Center. Untick anything you do not recognize.')}<//>
      <div class="col mt" style=${{ gap: 0 }}>${items.map((x) => html`<div key=${x.path} class="item" style=${{ padding: '8px 0' }}>
        <${Check} checked=${chosen.has(x.path)} onChange=${(on) => { const n = new Set(chosen); if (on) n.add(x.path); else n.delete(x.path); setSel(n); }} />
        <${Chip}>${{ folder: t('Klasör', 'Folder'), registry: t('Kayıt', 'Registry'), shortcut: t('Kısayol', 'Shortcut') }[x.kind]}<//>
        <div class="grow" style=${{ minWidth: 0 }}><div class="mono wrap-any">${x.path}</div><div class="tiny dim">${x.reason}</div></div>
        ${x.bytes > 0 && html`<b class="num">${bytes(x.bytes)}</b>`}
      </div>`)}</div>`}
  <//>`;
}

export default function Apps({ sub }) {
  const list = useRpc('apps.list');
  const measure = useJob();
  const uninstall = useJob();
  const [tab, setTab] = useState(sub === 'bloat' ? 'bloat' : 'all');
  const [query, setQuery] = useState('');
  const [sel, setSel] = useState(new Set());
  const [silent, setSilent] = useState(true);
  const [leftovers, setLeftovers] = useState(null);
  const [results, setResults] = useState(null);
  const items = list.data ? list.data.items : [];
  const portable = useRpc('apps.portable', {}, { enabled: tab === 'portable' });

  const rows = useMemo(() => items.filter((a) => (tab === 'all' || (tab === 'bloat' && a.bloat) || (tab === 'store' && a.kind === 'store') || (tab === 'win32' && a.kind === 'win32'))
    && matches(a.name + ' ' + (a.publisher || ''), query)), [items, tab, query]);

  const run = async () => {
    const apps = items.filter((a) => sel.has(a.id));
    const ok = await confirmDialog({
      title: t(`${apps.length} uygulama kaldırılsın mı?`, `Uninstall ${apps.length} apps?`),
      body: (silent ? t('Destekleyen uygulamalar sessizce kaldırılır; diğerleri kendi kaldırma sihirbazlarını açar.', 'Apps that support it are removed silently; the others open their own uninstall wizard.')
        : t('Her uygulamanın kendi kaldırma sihirbazı sırayla açılır.', 'Each app\'s own uninstall wizard opens in turn.')) + '\n\n' + t('Ardından geride kalan dosyaları taramayı önereceğim.', 'Afterwards you can scan for leftovers.'),
      details: apps.map((a) => '• ' + a.name).join('\n'), confirm: t('Kaldır', 'Uninstall'), danger: true,
    });
    if (!ok) return;
    const r = await act(() => uninstall.run('apps.uninstall', { ids: [...sel], silent }));
    if (r) { setResults(r); setSel(new Set()); list.reload(); }
  };

  return html`<${Page} title=${t('Uygulamalar', 'Apps')} desc=${t('Kurulu her şey: masaüstü programları ve Mağaza uygulamaları. Toplu ya da sessiz kaldırın, ardından kalan artıkları süpürün.', 'Everything installed: desktop programs and Store apps. Remove in bulk or silently, then sweep up what is left behind.')}
    actions=${html`<${Btn} icon="gauge" kind="ghost" busy=${measure.running} onClick=${() => act(async () => { await measure.run('apps.measure'); list.reload(); })}>${t('Boyutları ölç', 'Measure sizes')}<//>
      <${Btn} icon="refresh" kind="ghost" onClick=${() => call('apps.list', { refresh: true }).then(list.setData)}>${t('Yenile', 'Refresh')}<//>`}>
    <div class="col" style=${{ gap: '14px' }}>
      <${SimulationNote} />
      <div class="grid c4">
        <div class="card pad"><${Stat} label=${t('Kurulu', 'Installed')} value=${list.data ? num(items.length) : '–'} /></div>
        <div class="card pad"><${Stat} label=${t('Toplam boyut', 'Total size')} value=${list.data ? bytes(list.data.total) : '–'} sub=${t('bildirilen ve ölçülen', 'reported and measured')} /></div>
        <div class="card pad"><${Stat} label=${t('Mağaza uygulaması', 'Store apps')} value=${list.data ? num(list.data.store) : '–'} /></div>
        <div class="card pad"><${Stat} label=${t('Gereksiz olabilir', 'Possibly unneeded')} value=${list.data ? num(list.data.bloat) : '–'} tone=${list.data && list.data.bloat ? 'warn' : null} /></div>
      </div>
      <${JobBar} job=${measure.job} onCancel=${measure.cancel} />
      <${JobBar} job=${uninstall.job} />
      ${results && html`<${Card} title=${t('Kaldırma sonucu', 'Uninstall result')} icon="check" actions=${html`<${Btn} size="sm" kind="ghost" icon="x" onClick=${() => setResults(null)} />`} flush>
        ${results.results.map((r) => html`<div key=${r.id} class="item">
          <div class="grow"><div class="t">${r.name}</div>${r.error && html`<div class="s bad">${r.error}</div>`}</div>
          <${Chip} tone=${{ removed: 'good', failed: 'bad', simulated: 'acc', cancelled: null, unknown: 'warn' }[r.status]}>${{ removed: t('Kaldırıldı', 'Removed'), failed: t('Başarısız', 'Failed'), simulated: t('Simülasyon', 'Simulated'), cancelled: t('İptal edildi', 'Cancelled'), unknown: t('Belirsiz', 'Unclear') }[r.status]}<//>
          ${r.leftoversAvailable && html`<${Btn} size="sm" icon="search" onClick=${() => setLeftovers({ id: r.id, name: r.name })}>${t('Artıkları tara', 'Scan leftovers')}<//>`}
        </div>`)}
      <//>`}
      <div class="row wrap">
        <${Tabs} value=${tab} onChange=${setTab} items=${[
          { id: 'all', label: t('Tümü', 'All'), count: items.length }, { id: 'win32', label: t('Masaüstü', 'Desktop'), count: items.filter((a) => a.kind === 'win32').length },
          { id: 'store', label: t('Mağaza', 'Store'), count: items.filter((a) => a.kind === 'store').length }, { id: 'bloat', label: t('Gereksiz olabilir', 'Possibly unneeded'), icon: 'alert', count: items.filter((a) => a.bloat).length },
          { id: 'portable', label: t('Taşınabilir', 'Portable'), icon: 'package' },
        ]} />
        ${tab !== 'portable' && html`<${Search} class="grow" value=${query} onInput=${setQuery} />`}
      </div>
      ${tab === 'portable' ? html`<${Async} state=${portable}>${(p) => html`<div class="col gap-l">
        <${Banner} tone="info">${t('Kurulum gerektirmeden çalışan ve Windows\'a kayıtlı olmayan programlar (İndirilenler, Masaüstü ve benzeri klasörlerde). Kaldırmak için dosyayı silmek yeterlidir; "kurulum dosyası" olanları büyük ihtimalle artık tutmanıza gerek yok.',
          'Programs that run without installation and are not registered with Windows (in Downloads, Desktop and similar folders). Deleting the file removes them; the ones marked "installer" are probably no longer needed.')}<//>
        <div class="card"><${Table} rows=${p} rowKey="path" initialSort=${{ id: 'bytes', dir: 'desc' }} empty=${html`<${Empty} icon="package" title=${t('Taşınabilir program bulunamadı', 'No portable programs found')} />`} columns=${[
          { id: 'name', label: t('Program', 'Program'), sort: true, render: (x) => html`<div class="row"><${FileIcon} path=${x.path} /><div><div class="b">${x.name}</div><div class="tiny dim mono">${x.path}</div></div></div>` },
          { id: 'installer', label: '', class: 'shrink', render: (x) => x.installer && html`<${Chip} tone="warn">${t('Kurulum dosyası', 'Installer')}<//>` },
          { id: 'bytes', label: t('Boyut', 'Size'), num: true, sort: true, class: 'shrink', render: (x) => bytes(x.bytes) },
          { id: 'modified', label: t('Tarih', 'Date'), sort: true, class: 'shrink', render: (x) => date(x.modified) },
          { id: 'open', label: '', class: 'shrink', render: (x) => html`<${Btn} size="sm" kind="ghost" icon="folder" onClick=${() => act(() => call('shell.reveal', { path: x.path }))} />` },
        ]} /></div></div>`}<//>` : html`
        ${sel.size > 0 && html`<div class="card pad row wrap">
          <b>${t(`${sel.size} uygulama seçili`, `${sel.size} apps selected`)}</b>
          <label class="row gap-s small muted">${t('Mümkünse sessiz kaldır', 'Silent when possible')}<${Switch} on=${silent} onChange=${setSilent} /></label>
          <div class="sp row gap-s"><${Btn} kind="ghost" onClick=${() => setSel(new Set())}>${t('Seçimi kaldır', 'Clear')}<//><${Btn} kind="danger" icon="trash" onClick=${run}>${t('Kaldır', 'Uninstall')}<//></div>
        </div>`}
        <div class="card">${list.data === undefined ? html`<div style=${{ padding: '16px' }}><${Loading} lines=${10} /></div>` : html`<${Table} rows=${rows} rowKey="id" initialSort=${{ id: 'name', dir: 'asc' }} maxHeight="calc(100vh - 420px)" columns=${[
          { id: 'sel', label: '', class: 'shrink', render: (a) => html`<${Check} checked=${sel.has(a.id)} disabled=${!a.removable} onChange=${(on) => { const n = new Set(sel); if (on) n.add(a.id); else n.delete(a.id); setSel(n); }} />` },
          { id: 'name', label: t('Ad', 'Name'), sort: (a) => a.name.toLowerCase(), render: (a) => html`<div class="row"><${FileIcon} path=${a.icon} fallback=${a.kind === 'store' ? 'windows' : 'package'} />
            <div style=${{ minWidth: 0 }}><div class="b ell">${a.name}</div><div class="tiny dim ell">${a.publisher || ''}${a.version ? ' · ' + a.version : ''}</div>
            ${a.bloat && html`<div class="tiny warn">${a.bloatReason}</div>`}</div></div>` },
          { id: 'kind', label: t('Tür', 'Kind'), sort: true, class: 'shrink', render: (a) => html`<${Chip}>${a.kind === 'store' ? t('Mağaza', 'Store') : t('Masaüstü', 'Desktop')}<//>${!a.removable && html` <${Chip}>${t('Sistem', 'System')}<//>`}` },
          { id: 'sizeBytes', label: t('Boyut', 'Size'), num: true, sort: true, class: 'shrink', render: (a) => (a.sizeBytes ? html`${bytes(a.sizeBytes)}${a.sizeSource === 'measured' ? '' : html`<span class="dim tiny"> *</span>`}` : html`<span class="dim">–</span>`) },
          { id: 'installed', label: t('Kurulum', 'Installed'), sort: (a) => (a.installed ? +new Date(a.installed) : 0), class: 'shrink', render: (a) => (a.installed ? date(a.installed) : '–') },
          { id: 'left', label: '', class: 'shrink', render: (a) => a.kind === 'win32' && html`<${Btn} size="sm" kind="ghost" icon="search" title=${t('Artıkları tara', 'Scan leftovers')} onClick=${(e) => { e.stopPropagation(); setLeftovers(a); }} />` },
        ]} />`}</div>
        <div class="tiny dim">${t('* Boyut, programın kurulumda bildirdiği değerdir; "Boyutları ölç" klasörleri gerçekten ölçer.', '* Size as reported by the installer; "Measure sizes" measures the folders for real.')}</div>`}
    </div>
    ${leftovers && html`<${Leftovers} app=${leftovers} onClose=${() => setLeftovers(null)} />`}
  <//>`;
}
