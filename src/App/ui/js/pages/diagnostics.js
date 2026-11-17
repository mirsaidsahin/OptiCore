// Diagnostics: app health and crashes, blue screens, event log analysis, reliability history, updates.
import { html, useState, useEffect, useRpc, useJob, call, act, t, cx, num, date, dateTime, ago, go, confirmDialog } from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Icon, Empty, Loading, Banner, Async, Stat, JobBar, Table, Modal, Check, ElevationHint, Select } from '../lib/ui.js';
import { LineChart, BarList } from '../lib/charts.js';

function AppHealth() {
  const [days, setDays] = useState('30');
  const d = useRpc('diag.apps', { days: +days }, { deps: [days] });
  return html`<div class="col gap-l">
    <div class="row"><span class="grow small muted">${t('Windows\'un kaydettiği uygulama çökmeleri (olay 1000) ve donmaları (olay 1002). Bir uygulama hep aynı modülde çöküyorsa suçlu genellikle o modüldür.', 'App crashes (event 1000) and hangs (event 1002) recorded by Windows. When an app always crashes in the same module, that module is usually to blame.')}</span>
      <${Select} class="sm" value=${days} onChange=${setDays} options=${[{ value: '7', label: t('7 gün', '7 days') }, { value: '30', label: t('30 gün', '30 days') }, { value: '90', label: t('90 gün', '90 days') }]} /></div>
    <${Async} state=${d}>${(r) => (r.apps.length === 0
      ? html`<div class="card"><${Empty} icon="check" title=${t('Çöken uygulama yok', 'No crashing apps')} /></div>`
      : html`<div class="card">${r.apps.map((a) => html`<div key=${a.app} class="finding ${a.crashes + a.hangs >= 5 ? 'warning' : 'info'}">
          <div class="finding-ico"><${Icon} name=${a.hangs > a.crashes ? 'hourglass' : 'bug'} /></div>
          <div class="grow" style=${{ minWidth: 0 }}>
            <div class="row wrap gap-s"><h4>${a.app}</h4>${a.version && html`<span class="tiny dim">${a.version}</span>`}
              ${a.crashes > 0 && html`<${Chip} tone="bad">${a.crashes} ${t('çökme', 'crashes')}<//>`}${a.hangs > 0 && html`<${Chip} tone="warn">${a.hangs} ${t('donma', 'hangs')}<//>`}
              <span class="tiny dim">${t('son: ', 'last: ')}${ago(a.last)}</span></div>
            ${a.hint && html`<p>${a.hint}</p>`}
            <div class="evidence">${a.code && html`<span>${t('Hata kodu', 'Error code')}: <b class="mono">0x${a.code}</b></span>`}${a.modules.map((m) => html`<span key=${m.module}>${m.module}: <b>${m.count}</b></span>`)}</div>
            ${a.path && html`<div class="tiny dim mono mt-s wrap-any">${a.path}</div>`}
          </div>
        </div>`)}</div>`)}<//>
  </div>`;
}

function BlueScreens() {
  const d = useRpc('diag.bsod');
  const kinds = { power: [t('Güç kesildi ya da bilgisayar dondu', 'Power was cut or the PC froze'), 'bad'], button: [t('Güç düğmesiyle kapatıldı', 'Turned off with the power button'), 'warn'], bugcheck: [t('Mavi ekran', 'Blue screen'), 'bad'], unexpected: [t('Beklenmedik kapanış kaydı', 'Unexpected shutdown record'), 'warn'] };
  return html`<${Async} state=${d}>${(r) => html`<div class="col gap-l">
    <div class="grid c3">
      <div class="card pad"><${Stat} label=${t('Mavi ekran', 'Blue screens')} value=${num(r.bluescreens.length)} tone=${r.bluescreens.length ? 'bad' : 'good'} /></div>
      <div class="card pad"><${Stat} label=${t('Beklenmedik kapanma (90 gün)', 'Unexpected shutdowns (90 d)')} value=${num(r.unexpected.filter((u) => u.kind !== 'unexpected').length)} /></div>
      <div class="card pad"><${Stat} label=${t('Döküm dosyası', 'Dump files')} value=${r.canReadDumps ? num(r.dumps.length) : '–'} sub=${r.memoryDump ? 'MEMORY.DMP' : null} /></div>
    </div>
    ${r.bluescreens.length === 0 ? html`<div class="card"><${Empty} icon="check" title=${t('Kayıtlı mavi ekran yok', 'No blue screens recorded')} /></div>` : html`<div class="card">${r.bluescreens.map((b, i) => html`<div key=${i} class="finding critical">
      <div class="finding-ico"><${Icon} name="alert" /></div>
      <div class="grow"><div class="row wrap gap-s"><h4>${b.name || t('Bilinmeyen hata', 'Unknown error')}</h4><${Chip} class="mono">${b.code || '?'}<//><span class="tiny dim">${dateTime(b.time)}</span></div>
        ${b.cause && html`<p>${b.cause}</p>`}${b.dump && html`<div class="tiny dim mono">${b.dump}${b.dumpExists ? '' : t(' (silinmiş)', ' (deleted)')}</div>`}</div>
    </div>`)}</div>`}
    ${r.unexpected.length > 0 && html`<${Card} title=${t('Düzgün kapanmayan oturumlar', 'Sessions that did not end cleanly')} icon="power" flush>${r.unexpected.filter((u) => u.kind !== 'unexpected').map((u, i) => html`<div key=${i} class="item">
      <${Icon} name="power" size="15" class="dim" /><div class="grow">${kinds[u.kind][0]}</div><span class="small muted">${dateTime(u.time)}</span></div>`)}<//>`}
    ${!r.canReadDumps && html`<${ElevationHint}>${t('Döküm dosyalarını listelemek için yönetici yetkisi gerekir.', 'Listing dump files needs administrator rights.')}<//>`}
    <${Card} title=${t('Mavi ekran ya da donma yaşıyorsanız', 'If you get blue screens or freezes')} icon="wrench">
      <ol class="steps small">
        <li>${t('Ekran kartı, yonga seti ve Wi-Fi sürücülerini bilgisayar üreticisinin sayfasından güncelleyin.', 'Update graphics, chipset and Wi-Fi drivers from the PC maker\'s page.')}</li>
        <li>${t('Belleği test edin: Windows Bellek Tanılama bir yeniden başlatmayla çalışır.', 'Test the memory: Windows Memory Diagnostic runs with a restart.')} <a href="#" onClick=${(e) => { e.preventDefault(); act(() => call('diag.memoryTest')); }}>${t('Başlat', 'Start')}</a></li>
        <li>${t('Hız aşırtma (XMP dahil) kullanıyorsanız varsayılana dönüp sorunun sürüp sürmediğine bakın.', 'If you overclock (including XMP), return to defaults and see whether the problem persists.')}</li>
        <li>${t('Sıcaklıkları Sistem > Termal sayfasından ve Benchmark\'taki kararlılık testinden izleyin.', 'Watch temperatures on System > Thermal and with the stability test in Benchmark.')}</li>
      </ol>
    <//>
  </div>`}<//>`;
}

function Events() {
  const [days, setDays] = useState('7');
  const [level, setLevel] = useState('error');
  const d = useRpc('diag.events', { days: +days, level }, { deps: [days, level] });
  const [open, setOpen] = useState(null);
  const sample = useRpc('diag.eventSample', open || {}, { enabled: !!open, deps: [open && open.source + open.id] });
  return html`<div class="col gap-l">
    <div class="row wrap"><span class="grow small muted">${t('Sistem ve Uygulama günlüklerindeki hatalar, kaynağa göre gruplanmış. Çoğu kayıt zararsızdır; önemli olan sık tekrarlayanlar ve sorunlarınızla aynı zamanda görünenlerdir.', 'Errors from the System and Application logs, grouped by source. Most entries are harmless; what matters are the frequent ones and those that coincide with your problems.')}</span>
      <${Select} class="sm" value=${level} onChange=${setLevel} options=${[{ value: 'critical', label: t('Kritik', 'Critical') }, { value: 'error', label: t('Hata ve üstü', 'Errors and above') }, { value: 'warning', label: t('Uyarı ve üstü', 'Warnings and above') }]} />
      <${Select} class="sm" value=${days} onChange=${setDays} options=${[{ value: '1', label: t('24 saat', '24 hours') }, { value: '7', label: t('7 gün', '7 days') }, { value: '30', label: t('30 gün', '30 days') }]} /></div>
    <${Async} state=${d}>${(r) => html`
      ${r.perDay.length > 1 && html`<${Card} title=${t('Günlere göre', 'By day')} icon="chart"><${LineChart} height=${150} labels=${r.perDay.map((x) => date(x.day))} series=${[{ label: t('Hata', 'Errors'), data: r.perDay.map((x) => x.errors), color: '#fb7185' }, { label: t('Uyarı', 'Warnings'), data: r.perDay.map((x) => x.warnings), color: '#fbbf24' }]} /><//>`}
      <div class="card"><${Table} rows=${r.groups} rowKey=${(g) => g.log + g.source + g.id} onRow=${(g) => setOpen({ log: g.log, source: g.source, id: g.id })} initialSort=${{ id: 'count', dir: 'desc' }}
        empty=${html`<${Empty} icon="check" title=${t('Kayıt yok', 'No entries')} />`} columns=${[
          { id: 'source', label: t('Kaynak', 'Source'), sort: true, render: (g) => html`<div><div class="b">${g.source}</div><div class="tiny dim">${g.log} · ${t('olay', 'event')} ${g.id}</div></div>` },
          { id: 'level', label: t('Düzey', 'Level'), class: 'shrink', render: (g) => html`<${Chip} tone=${g.level === 'warning' ? 'warn' : 'bad'}>${{ critical: t('Kritik', 'Critical'), error: t('Hata', 'Error'), warning: t('Uyarı', 'Warning') }[g.level]}<//>` },
          { id: 'count', label: t('Sayı', 'Count'), num: true, sort: true, class: 'shrink' },
          { id: 'daily', label: t('Gün', 'Days'), num: true, sort: true, class: 'shrink' },
          { id: 'last', label: t('Son', 'Last'), sort: (g) => +new Date(g.last), class: 'shrink', render: (g) => ago(g.last) },
        ]} /></div>`}<//>
    ${open && html`<${Modal} wide title=${open.source + ' · ' + open.id} onClose=${() => setOpen(null)}>
      <${Async} state=${sample}>${(s) => html`<div class="col">
        ${s.explain && html`<${Banner} tone="info" icon="bulb">${s.explain}<//>`}
        ${s.samples.map((x, i) => html`<div key=${i}><div class="tiny dim">${dateTime(x.time)}</div><div class="code mt-s">${x.message || '—'}</div></div>`)}
      </div>`}<//>
    <//>`}
  </div>`;
}

function Reliability() {
  const d = useRpc('diag.reliability');
  return html`<${Async} state=${d}>${(r) => html`<div class="col gap-l">
    <div class="card pad row gap-l"><${Stat} label=${t('Kararlılık endeksi', 'Stability index')} value=${r.current != null ? num(r.current, 1) : '–'} unit="/ 10" tone=${r.current >= 8 ? 'good' : r.current >= 5 ? 'warn' : 'bad'} />
      <div class="grow small muted">${t('Windows\'un kendi hesapladığı, çökme ve hatalarla düşen 1–10 arası endeks.', 'Windows\' own 1–10 index, which drops with crashes and failures.')}</div>
      <${Btn} icon="external" onClick=${() => act(() => call('diag.reliabilityMonitor'))}>${t('Güvenilirlik İzleyicisi', 'Reliability Monitor')}<//></div>
    ${r.points.length > 1 && html`<${Card} title=${t('Endeks geçmişi', 'Index history')} icon="trend"><${LineChart} height=${170} min=${0} max=${10} labels=${r.points.map((p) => date(p.day))} format=${(v) => num(v, 1)} series=${[{ label: t('Endeks', 'Index'), data: r.points.map((p) => p.index), color: '#34d399' }]} /><//>`}
    <${Card} title=${t('Son olaylar', 'Recent events')} icon="list" flush>${r.records.slice(0, 60).map((x, i) => html`<div key=${i} class="item">
      <div class="grow" style=${{ minWidth: 0 }}><div class="t ell">${x.product || x.source}</div><div class="s ell">${x.message}</div></div><span class="tiny dim">${dateTime(x.time)}</span></div>`)}<//>
  </div>`}<//>`;
}

function Updates() {
  const history = useRpc('diag.updates');
  const job = useJob();
  const [list, setList] = useState(null);
  const [sel, setSel] = useState(new Set());
  const check = () => act(async () => { const r = await job.run('diag.upgrades'); setList(r.items); setSel(new Set()); });
  const upgrade = () => confirmDialog({
    title: t(`${sel.size} uygulama güncellensin mi?`, `Update ${sel.size} apps?`), body: t('Güncellemeler winget ile, her uygulamanın resmî kaynağından sessizce kurulur. Güncellenen uygulama açıksa kapanabilir.', 'Updates are installed silently with winget from each app\'s official source. An app that is open may close.'),
    details: [...sel].join('\n'), confirm: t('Güncelle', 'Update'),
  }).then((ok) => ok && act(async () => { const r = await job.run('diag.upgrade', { ids: [...sel] }); setList((l) => l.filter((x) => !r.results.some((y) => y.id === x.id && y.ok))); return r; },
    (r) => (r.simulated ? t('Simülasyon: hiçbir şey kurulmadı', 'Simulation: nothing was installed') : t(`${r.results.filter((x) => x.ok).length} uygulama güncellendi`, `${r.results.filter((x) => x.ok).length} apps updated`))));
  const res = { 2: [t('Kuruldu', 'Installed'), 'good'], 3: [t('Kısmen', 'Partial'), 'warn'], 4: [t('Başarısız', 'Failed'), 'bad'], 5: [t('İptal', 'Aborted'), null] };
  return html`<div class="col gap-l">
    <${Card} title=${t('Uygulama güncellemeleri', 'App updates')} icon="download" actions=${html`<${Btn} size="sm" kind="primary" icon="refresh" busy=${job.running} onClick=${check}>${t('Denetle', 'Check')}<//>`}>
      <${JobBar} job=${job.job} />
      ${!list ? html`<div class="small muted">${t('Kurulu programların daha yeni sürümlerini Windows Paket Yöneticisi (winget) ile bulur.', 'Finds newer versions of installed programs with Windows Package Manager (winget).')}</div>`
        : !list.length ? html`<${Empty} icon="check" title=${t('Her şey güncel', 'Everything is up to date')} />` : html`
          <div class="row mb"><span class="grow small muted">${t(`${list.length} güncelleme bulundu`, `${list.length} updates found`)}</span>
            <${Btn} size="sm" onClick=${() => setSel(sel.size === list.length ? new Set() : new Set(list.map((x) => x.id)))}>${sel.size === list.length ? t('Seçimi kaldır', 'Clear') : t('Tümünü seç', 'Select all')}<//>
            <${Btn} size="sm" kind="primary" disabled=${!sel.size || job.running} onClick=${upgrade}>${t(`Güncelle (${sel.size})`, `Update (${sel.size})`)}<//></div>
          ${list.map((x) => html`<div key=${x.id} class="item" style=${{ padding: '8px 0' }}><${Check} checked=${sel.has(x.id)} onChange=${(on) => { const n = new Set(sel); if (on) n.add(x.id); else n.delete(x.id); setSel(n); }} />
            <div class="grow"><div class="t">${x.name}</div><div class="s mono">${x.id}</div></div><span class="num small">${x.version} → <b class="good">${x.available}</b></span></div>`)}`}
    <//>
    <${Card} title=${t('Windows Update geçmişi', 'Windows Update history')} icon="windows" flush>
      <${Async} state=${history}>${(h) => (!h.history ? html`<div style=${{ padding: '16px' }} class="muted">${t('Geçmiş okunamadı.', 'History could not be read.')}</div>` : h.history.slice(0, 50).map((u, i) => html`<div key=${i} class="item">
        <div class="grow" style=${{ minWidth: 0 }}><div class="t ell">${u.title}</div><div class="s">${dateTime(u.date)}</div></div><${Chip} tone=${(res[u.result] || [])[1]}>${(res[u.result] || ['?'])[0]}<//></div>`))}<//>
    <//>
  </div>`;
}

export default function Diagnostics({ sub }) {
  const tabs = ['crashes', 'bsod', 'events', 'reliability', 'updates'];
  const [tab, setTab] = useState(tabs.includes(sub) ? sub : 'crashes');
  useEffect(() => { if (tabs.includes(sub)) setTab(sub); }, [sub]);
  return html`<${Page} title=${t('Tanılama', 'Diagnostics')} desc=${t('Çöken uygulamalar, mavi ekranlar, olay günlükleri ve güncellemeler: bir şeyler ters gittiğinde ilk bakılacak yer.', 'Crashing apps, blue screens, event logs and updates: the first place to look when something goes wrong.')}>
    <div class="mb"><${Tabs} value=${tab} onChange=${(id) => { setTab(id); go('diagnostics/' + id); }} items=${[
      { id: 'crashes', label: t('Uygulama sağlığı', 'App health'), icon: 'bug' }, { id: 'bsod', label: t('Mavi ekran ve kapanmalar', 'Blue screens & shutdowns'), icon: 'alert' },
      { id: 'events', label: t('Olay günlüğü', 'Event log'), icon: 'list' }, { id: 'reliability', label: t('Kararlılık', 'Reliability'), icon: 'trend' }, { id: 'updates', label: t('Güncellemeler', 'Updates'), icon: 'download' },
    ]} /></div>
    ${tab === 'crashes' ? html`<${AppHealth} />` : tab === 'bsod' ? html`<${BlueScreens} />` : tab === 'events' ? html`<${Events} />` : tab === 'reliability' ? html`<${Reliability} />` : html`<${Updates} />`}
  <//>`;
}
