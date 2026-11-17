// Startup Manager: apps, scheduled tasks and services that start with Windows, their measured cost, and boot history.
import { html, useState, useMemo, useRpc, call, act, t, cx, num, bytes, dateTime, date, matches } from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Search, Switch, Drawer, KV, FileIcon, Icon, Empty, Loading, Select, Banner, Async, ElevationHint, Stat, SimulationNote, Table } from '../lib/ui.js';
import { LineChart } from '../lib/charts.js';

const IMPACT = {
  high: ['Yüksek', 'High', 'bad'], medium: ['Orta', 'Medium', 'warn'], low: ['Düşük', 'Low', 'good'], unknown: ['Ölçülmedi', 'Not measured', null],
};
const KINDS = { registry: ['Kayıt defteri', 'Registry'], folder: ['Başlangıç klasörü', 'Startup folder'], uwp: ['Mağaza uygulaması', 'Store app'], task: ['Zamanlanmış görev', 'Scheduled task'], service: ['Hizmet', 'Service'] };
const MODES = [['auto', 'Otomatik', 'Automatic'], ['delayed-auto', 'Otomatik (gecikmeli)', 'Automatic (delayed)'], ['demand', 'El ile', 'Manual'], ['disabled', 'Devre dışı', 'Disabled']];

const ImpactChip = ({ e }) => {
  const [tr, en, tone] = IMPACT[e.impact] || IMPACT.unknown;
  const tip = e.impactSource === 'windows' ? t('Windows\'un açılış izinden', 'From Windows\' own startup trace')
    : e.impactSource === 'opticore' ? t('OptiCore\'un açılıştan sonra ölçtüğü işlemci süresi', 'Processor time measured by OptiCore after startup') : t('Henüz ölçüm yok', 'No measurement yet');
  return html`<${Chip} tone=${tone} title=${tip}>${t(tr, en)}${e.cpuMs != null ? ' · ' + num(e.cpuMs / 1000, 1) + ' s' : ''}<//>`;
};

function Details({ e, onClose, onToggle }) {
  return html`<${Drawer} wide onClose=${onClose} title=${html`<span class="row"><${FileIcon} path=${e.path} />${e.name}</span>`}>
    <div class="col gap-l">
      ${e.flags.length > 0 && html`<${Banner} tone="warn" icon="alert"><b>${t('Dikkat edilmesi gerekenler', 'Worth a closer look')}</b>
        <ul style=${{ margin: '4px 0 0', paddingLeft: '18px' }}>${e.flags.map((f, i) => html`<li key=${i}>${f}</li>`)}</ul>
        <div class="tiny muted mt-s">${t('Bunlar birer işarettir, hüküm değil. Programı tanıyorsanız sorun yoktur.', 'These are signals, not a verdict. If you know the program, all is well.')}</div><//>`}
      ${e.fileMissing && html`<${Banner} tone="info">${t('Bu girdinin gösterdiği dosya artık yok; muhtemelen kaldırılmış bir programdan kaldı.', 'The file this entry points to no longer exists; it was probably left behind by an uninstalled program.')}<//>`}
      <div class="card flat pad row">
        <div class="grow"><div class="b">${e.enabled ? t('Windows ile birlikte başlıyor', 'Starts with Windows') : t('Başlangıçta çalışmıyor', 'Does not run at startup')}</div>
          <div class="tiny dim">${t('Devre dışı bırakmak programı kaldırmaz; siz açana kadar bekletir. İstediğiniz an geri açabilirsiniz.', 'Disabling does not uninstall the program; it just waits until you open it. You can turn it back on at any time.')}</div></div>
        <${Switch} on=${e.enabled} onChange=${(on) => onToggle(e, on)} />
      </div>
      <${KV} rows=${[
        [t('Tür', 'Kind'), t(...KINDS[e.kind])],
        [t('Yayıncı', 'Publisher'), e.publisher],
        [t('Dijital imza', 'Digital signature'), e.kind === 'uwp' ? null : e.signed ? html`<${Chip} tone="good" icon="shieldCheck">${e.signer}<//>` : html`<${Chip}>${t('Gömülü imza yok', 'No embedded signature')}<//>`],
        [t('Açılış etkisi', 'Startup impact'), html`<${ImpactChip} e=${e} />`],
        [t('Komut', 'Command'), e.command && html`<span class="mono wrap-any">${e.command}</span>`],
        [t('Dosya', 'File'), e.path && html`<span class="mono wrap-any">${e.path}</span>`],
        [t('Kayıtlı olduğu yer', 'Registered in'), html`<span class="mono wrap-any">${e.location}</span>`],
        [t('Kapsam', 'Scope'), e.scope === 'machine' ? t('Tüm kullanıcılar (yönetici gerekir)', 'All users (needs administrator)') : t('Yalnızca siz', 'Only you')],
        e.kind === 'task' && [t('Tetikleyici', 'Trigger'), e.description === 'boot' ? t('Bilgisayar açıldığında', 'When the PC starts') : t('Oturum açıldığında', 'At sign-in')],
        e.delayed && [t('Gecikme', 'Delay'), e.delayed],
        e.kind === 'service' && [t('Açıklama', 'Description'), e.description],
      ]} />
      ${e.path && html`<div><${Btn} size="sm" icon="folder" onClick=${() => act(() => call('shell.reveal', { path: e.path }))}>${t('Dosya konumunu aç', 'Open file location')}<//></div>`}
    </div>
  <//>`;
}

function Boots() {
  const boots = useRpc('startup.boots');
  return html`<${Async} state=${boots}>${(r) => html`<div class="col gap-l">
    ${r.denied && html`<${ElevationHint}>${t('Windows açılış süresi ölçümlerini yalnızca yöneticilere gösterir.', 'Windows shows its boot time measurements only to administrators.')}<//>`}
    ${r.boots.length === 0 && !r.denied && html`<div class="card"><${Empty} icon="clock" title=${t('Kayıtlı açılış ölçümü yok', 'No boot measurements recorded')} /></div>`}
    ${r.boots.length > 0 && html`
      <div class="grid c3">
        <div class="card pad"><${Stat} label=${t('Son açılış', 'Last boot')} value=${num(r.boots[r.boots.length - 1].totalMs / 1000, 1)} unit="s" sub=${dateTime(r.boots[r.boots.length - 1].time)} /></div>
        <div class="card pad"><${Stat} label=${t('Ortalama', 'Average')} value=${num(r.boots.reduce((s, b) => s + b.totalMs, 0) / r.boots.length / 1000, 1)} unit="s" sub=${t(`${r.boots.length} açılış`, `${r.boots.length} boots`)} /></div>
        <div class="card pad"><${Stat} label=${t('En hızlı / en yavaş', 'Fastest / slowest')} value=${num(Math.min(...r.boots.map((b) => b.totalMs)) / 1000, 1) + ' / ' + num(Math.max(...r.boots.map((b) => b.totalMs)) / 1000, 1)} unit="s" /></div>
      </div>
      <${Card} title=${t('Açılış süresi geçmişi', 'Boot time history')} icon="trend" sub=${t('Windows\'un kendi ölçümü', 'measured by Windows itself')}>
        <${LineChart} height=${220} labels=${r.boots.map((b) => date(b.time))} format=${(v) => num(v, 1) + ' s'} series=${[
          { label: t('Toplam', 'Total'), data: r.boots.map((b) => b.totalMs / 1000), color: '#7c6cff' },
          { label: t('Masaüstüne kadar', 'Until the desktop'), data: r.boots.map((b) => b.mainPathMs / 1000), color: '#22d3ee' },
          { label: t('Masaüstünden sonra', 'After the desktop'), data: r.boots.map((b) => b.postBootMs / 1000), color: '#fbbf24' },
        ]} />
        <div class="tiny dim mt">${t('"Masaüstünden sonra" kısmı başlangıç uygulamalarının yüküdür; başlangıç öğelerini azalttığınızda düşen değer budur.', 'The "after the desktop" part is the load of startup apps; it is the value that drops when you trim startup items.')}</div>
      <//>`}
  </div>`}<//>`;
}

export default function Startup() {
  const list = useRpc('startup.list');
  const [tab, setTab] = useState('apps');
  const [filter, setFilter] = useState('all');
  const [query, setQuery] = useState('');
  const [showMs, setShowMs] = useState(false);
  const [open, setOpen] = useState(null);
  const items = list.data ? list.data.items : [];

  const toggle = (e, enabled) => act(() => call('startup.toggle', { ids: [e.id], enabled }),
    (r) => (r.simulated ? t('Simülasyon: değişiklik uygulanmadı', 'Simulation: nothing was changed') : enabled ? t(`${e.name} başlangıçta etkin`, `${e.name} enabled at startup`) : t(`${e.name} başlangıçtan çıkarıldı`, `${e.name} removed from startup`)))
    .then((r) => { if (r && !r.simulated) list.reload(); });
  const setMode = (e, mode) => act(() => call('startup.serviceMode', { id: e.id, mode }), t('Hizmet başlatma türü değiştirildi', 'Service start type changed')).then((r) => r && list.reload());

  const groups = {
    apps: items.filter((e) => e.kind === 'registry' || e.kind === 'folder' || e.kind === 'uwp'),
    tasks: items.filter((e) => e.kind === 'task'),
    services: items.filter((e) => e.kind === 'service' && (showMs || !e.microsoft)),
  };
  const rows = useMemo(() => (groups[tab] || []).filter((e) => (filter === 'all' || (filter === 'on') === e.enabled) && matches(e.name + ' ' + (e.publisher || '') + ' ' + e.command, query)), [items, tab, filter, query, showMs]);
  const apps = groups.apps;
  const high = apps.filter((e) => e.enabled && e.impact === 'high').length;
  const flagged = items.filter((e) => e.flags.length > 0 && e.enabled).length;
  const current = open && items.find((e) => e.id === open);

  const nameCol = { id: 'name', label: t('Ad', 'Name'), sort: (e) => e.name.toLowerCase(), render: (e) => html`<div class="row">
    <${FileIcon} path=${e.path} fallback=${e.kind === 'uwp' ? 'windows' : e.kind === 'service' ? 'cog' : 'package'} />
    <div class="grow" style=${{ minWidth: 0 }}><div class="b ell">${e.name}</div><div class="tiny dim ell">${e.publisher || (e.kind === 'service' ? e.location : '')}</div></div>
    ${e.flags.length > 0 && html`<${Chip} tone="warn" icon="alert" title=${e.flags.join(' · ')}>${e.flags.length}<//>`}
    ${e.fileMissing && html`<${Chip} title=${t('Gösterdiği dosya yok', 'Its file is missing')}>${t('Artık girdi', 'Orphan')}<//>`}
  </div>` };

  return html`<${Page} title=${t('Başlangıç', 'Startup')} desc=${t('Windows ile birlikte başlayan her şey. Etki değerleri tahmin değil, ölçümdür; ölçüm yoksa öyle yazar.', 'Everything that starts with Windows. Impact values are measurements, not guesses; where there is none, it says so.')}
    actions=${html`<${Btn} icon="refresh" kind="ghost" onClick=${() => call('startup.list', { refresh: true }).then(list.setData)}>${t('Yenile', 'Refresh')}<//>`}>
    <div class="col" style=${{ gap: '14px' }}>
      <${SimulationNote} />
      <div class="grid c4">
        <div class="card pad"><${Stat} label=${t('Etkin uygulama', 'Enabled apps')} value=${list.data ? num(apps.filter((e) => e.enabled).length) : '–'} sub=${t(`${apps.length} kayıtlıdan`, `of ${apps.length} registered`)} /></div>
        <div class="card pad"><${Stat} label=${t('Yüksek etkili', 'High impact')} value=${list.data ? num(high) : '–'} tone=${high >= 3 ? 'warn' : null} /></div>
        <div class="card pad"><${Stat} label=${t('Zamanlanmış görev', 'Scheduled tasks')} value=${list.data ? num(groups.tasks.filter((e) => e.enabled).length) : '–'} sub=${t('açılışta ya da oturumda', 'at boot or sign-in')} /></div>
        <div class="card pad"><${Stat} label=${t('İncelenmesi gereken', 'Worth a look')} value=${list.data ? num(flagged) : '–'} tone=${flagged ? 'warn' : 'good'} /></div>
      </div>
      ${list.data && !list.data.measured && html`<${Banner} tone="info" icon="clock">${t('Açılış etkisi henüz ölçülmedi. OptiCore bir sonraki açılıştan birkaç dakika sonra her uygulamanın harcadığı işlemci süresini kendisi ölçer; yönetici olarak çalıştırıldığında Windows\'un kendi açılış izini de okur.',
        'Startup impact has not been measured yet. A few minutes after the next boot OptiCore measures the processor time each app used; when run as administrator it also reads Windows\' own startup trace.')}<//>`}
      <div class="row wrap">
        <${Tabs} value=${tab} onChange=${setTab} items=${[
          { id: 'apps', label: t('Uygulamalar', 'Apps'), icon: 'package', count: groups.apps.length },
          { id: 'tasks', label: t('Zamanlanmış görevler', 'Scheduled tasks'), icon: 'clock', count: groups.tasks.length },
          { id: 'services', label: t('Hizmetler', 'Services'), icon: 'cog', count: groups.services.length },
          { id: 'boots', label: t('Açılış geçmişi', 'Boot history'), icon: 'trend' },
        ]} />
        ${tab !== 'boots' && html`
          <${Search} class="grow" value=${query} onInput=${setQuery} />
          <${Tabs} value=${filter} onChange=${setFilter} items=${[{ id: 'all', label: t('Tümü', 'All') }, { id: 'on', label: t('Etkin', 'Enabled') }, { id: 'off', label: t('Devre dışı', 'Disabled') }]} />`}
      </div>
      ${tab === 'boots' ? html`<${Boots} />` : html`
        ${tab === 'services' && html`<div class="row small muted"><span class="grow">${t('Windows\'un kendi hizmetleri varsayılan olarak gizlidir ve buradan değiştirilemez; güvenle ayarlanabilenler Windows Ayarları sayfasındadır.', 'Windows\' own services are hidden by default and cannot be changed here; the ones that are safe to tune are on the Windows Tweaks page.')}</span>
          <label class="row gap-s" style=${{ cursor: 'pointer' }}>${t('Windows hizmetlerini göster', 'Show Windows services')}<${Switch} on=${showMs} onChange=${setShowMs} /></label></div>`}
        <div class="card">${list.data === undefined ? html`<div style=${{ padding: '16px' }}><${Loading} lines=${8} /></div>` : html`<${Table} rows=${rows} rowKey="id" onRow=${(e) => setOpen(e.id)} maxHeight="calc(100vh - 400px)"
          empty=${html`<${Empty} icon="power" title=${t('Burada bir şey yok', 'Nothing here')} />`}
          columns=${tab === 'services' ? [
            nameCol,
            { id: 'state', label: t('Durum', 'State'), sort: true, class: 'shrink', render: (e) => html`<${Chip} tone=${e.state === 'running' ? 'good' : null}>${e.state === 'running' ? t('Çalışıyor', 'Running') : t('Durmuş', 'Stopped')}<//>` },
            { id: 'startMode', label: t('Başlatma türü', 'Start type'), class: 'shrink', render: (e) => (e.microsoft
              ? html`<span class="dim">${t(...(MODES.find((m) => m[0] === e.startMode) || MODES[0]).slice(1))}</span>`
              : html`<span onClick=${(ev) => ev.stopPropagation()}><${Select} class="sm" value=${e.startMode} onChange=${(m) => setMode(e, m)} options=${MODES.map(([v, tr, en]) => ({ value: v, label: t(tr, en) }))} /></span>`) },
          ] : [
            nameCol,
            { id: 'kind', label: t('Tür', 'Kind'), sort: true, class: 'shrink', render: (e) => html`<${Chip}>${t(...KINDS[e.kind])}<//>` },
            { id: 'impact', label: t('Açılış etkisi', 'Startup impact'), sort: (e) => e.cpuMs ?? -1, num: true, class: 'shrink', render: (e) => html`<${ImpactChip} e=${e} />` },
            { id: 'enabled', label: t('Etkin', 'Enabled'), sort: (e) => (e.enabled ? 1 : 0), class: 'shrink', render: (e) => html`<${Switch} on=${e.enabled} onChange=${(on) => toggle(e, on)} title=${e.requiresAdmin ? t('Yönetici yetkisi gerektirir', 'Needs administrator rights') : ''} />` },
          ]} />`}</div>`}
    </div>
    ${current && html`<${Details} e=${current} onClose=${() => setOpen(null)} onToggle=${toggle} />`}
  <//>`;
}
