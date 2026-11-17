// Storage Intelligence: disk map, file types, largest files, disk health and space trends.
import { html, useState, useEffect, useRpc, useJob, call, act, t, num, bytes, date, ago, tone, go } from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Bar, Icon, Empty, Loading, Banner, Async, Stat, JobBar, Table, KV, ElevationHint } from '../lib/ui.js';
import { Treemap, Donut, Legend, LineChart, PALETTE } from '../lib/charts.js';

const TYPES = {
  video: ['Video', 'Video', '#fb7185'], image: ['Resim', 'Images', '#fbbf24'], audio: ['Ses', 'Audio', '#c084fc'], document: ['Belge', 'Documents', '#60a5fa'],
  archive: ['Arşiv ve kalıp', 'Archives & images', '#fb923c'], program: ['Program', 'Programs', '#7c6cff'], game: ['Oyun verisi', 'Game data', '#34d399'],
  code: ['Kod', 'Code', '#22d3ee'], data: ['Veri ve günlük', 'Data & logs', '#94a3b8'], other: ['Diğer', 'Other', '#475569'],
};

function DiskMap() {
  const volumes = useRpc('system.volumes');
  const job = useJob();
  const [root, setRoot] = useState(null);
  const [result, setResult] = useState(null);
  const [node, setNode] = useState(null);
  const [trail, setTrail] = useState([]);

  const scan = (r) => act(async () => {
    setResult(null); setNode(null); setTrail([]);
    const res = await job.run('storage.scan', { root: r });
    setResult(res); setNode(res.node); setTrail([res.node]);
  });
  const open = (child) => call('storage.node', { path: child.path }).then((n) => { setNode(n); setTrail((tr) => [...tr, n]); }).catch(() => {});
  const back = (i) => { setNode(trail[i]); setTrail(trail.slice(0, i + 1)); };

  return html`<div class="col gap-l">
    <div class="card pad row wrap">
      <span class="muted">${t('Taranacak yer', 'Scan')}</span>
      ${(volumes.data || []).map((v) => html`<${Btn} key=${v.letter} kind=${root === v.letter + ':\\' ? 'primary' : ''} onClick=${() => setRoot(v.letter + ':\\')}>${v.letter}: <span class="dim">${bytes(v.totalBytes - v.freeBytes)}</span><//>`)}
      <input class="input grow" placeholder=${t('ya da bir klasör yolu', 'or a folder path')} value=${root && root.length > 3 ? root : ''} onInput=${(e) => setRoot(e.target.value)} />
      <${Btn} kind="primary" icon="search" disabled=${!root} busy=${job.running} onClick=${() => scan(root)}>${t('Haritayı çıkar', 'Map it')}<//>
    </div>
    <${JobBar} job=${job.job} onCancel=${job.cancel} />
    ${!result && !job.running && html`<div class="card"><${Empty} icon="disk" title=${t('Disk kullanım haritası', 'Disk usage map')} text=${t('Bir sürücü ya da klasör seçin; OptiCore her klasörün gerçek boyutunu ölçer ve neyin yer kapladığını kutu haritası olarak gösterir. Yönetici olmadan bazı sistem klasörleri okunamaz.', 'Pick a drive or folder; OptiCore measures the real size of every folder and shows what takes space as a treemap. Without admin rights some system folders cannot be read.')} /></div>`}
    ${result && node && html`
      <div class="grid c4">
        <div class="card pad"><${Stat} label=${t('Taranan', 'Scanned')} value=${bytes(result.size)} /></div>
        <div class="card pad"><${Stat} label=${t('Dosya / klasör', 'Files / folders')} value=${num(result.files)} sub=${num(result.folders) + t(' klasör', ' folders')} /></div>
        <div class="card pad"><${Stat} label=${t('Bulut dosyası', 'Cloud-only files')} value=${bytes(result.cloudBytes)} sub=${t('diskte yer kaplamaz', 'take no disk space')} /></div>
        <div class="card pad"><${Stat} label=${t('Süre', 'Took')} value=${num(result.seconds, 1)} unit="s" sub=${result.denied ? num(result.denied) + t(' klasör okunamadı', ' folders unreadable') : null} /></div>
      </div>
      <${Card} title=${html`<span class="row wrap gap-s">${trail.map((n, i) => html`<a key=${n.path} href="#" onClick=${(e) => { e.preventDefault(); back(i); }}>${i === 0 ? n.name : n.name}</a>${i < trail.length - 1 ? ' › ' : ''}`)}</span>`}
        sub=${bytes(node.size)} actions=${html`<${Btn} size="sm" kind="ghost" icon="folder" onClick=${() => act(() => call('shell.reveal', { path: node.path }))} />`}>
        <${Treemap} height=${400} items=${[...node.children.map((c) => ({ ...c, key: c.path })), node.ownBytes > 0 && { name: t('(bu klasördeki dosyalar)', '(files in this folder)'), size: node.ownBytes, key: '__own' }].filter(Boolean)}
          sub=${(i) => bytes(i.size)} onPick=${(i) => i.folder && i.hasChildren && open(i)} />
        <div class="tiny dim mt-s">${t('Bir kutuya tıklayınca o klasörün içine girersiniz.', 'Click a box to step into that folder.')}</div>
      <//>
      <div class="grid c2">
        <${Card} title=${t('Dosya türleri', 'File types')} icon="layers">
          <div class="row gap-l wrap">
            <${Donut} size=${140} segments=${result.types.map((x) => ({ value: x.bytes, color: TYPES[x.type][2] }))}><b class="num">${bytes(result.size)}</b><//>
            <div class="grow col" style=${{ gap: '6px' }}>${result.types.sort((a, b) => b.bytes - a.bytes).map((x) => html`<div key=${x.type} class="row small"><i style=${{ width: '9px', height: '9px', borderRadius: '3px', background: TYPES[x.type][2] }}></i><span class="grow">${t(TYPES[x.type][0], TYPES[x.type][1])}</span><b class="num">${bytes(x.bytes)}</b></div>`)}</div>
          </div>
        <//>
        <${Card} title=${t('En büyük dosyalar', 'Largest files')} icon="file" flush>
          <div style=${{ maxHeight: '300px', overflow: 'auto' }}>${result.largest.slice(0, 40).map((f) => html`<div key=${f.path} class="item">
            <div class="grow" style=${{ minWidth: 0 }}><div class="t ell">${f.path.split('\\').pop()}</div><div class="tiny dim mono ell">${f.path}</div></div>
            <b class="num">${bytes(f.bytes)}</b><${Btn} size="sm" kind="ghost" icon="folder" onClick=${() => act(() => call('shell.reveal', { path: f.path }))} />
          </div>`)}</div>
        <//>
      </div>`}
  </div>`;
}

function Health() {
  const hw = useRpc('system.hardware');
  const live = useRpc('metrics.live', { history: 60 }, { interval: 2000 });
  return html`<${Async} state=${hw}>${(h) => html`<div class="col gap-l">
    ${h.disks.some((d) => !d.smartAvailable) && html`<${ElevationHint}>${t('SMART verileri (sıcaklık, yıpranma, çalışma saati, hata sayaçları) yalnızca yönetici olarak okunabilir.', 'SMART data (temperature, wear, power-on hours, error counters) can only be read as administrator.')}<//>`}
    ${h.disks.map((d) => {
      const io = live.data && live.data.now && live.data.now.disks.find((x) => x.name.startsWith(d.index + ' '));
      return html`<${Card} key=${d.index} title=${d.model} icon="disk" sub=${[d.media, d.bus, bytes(d.sizeBytes)].filter(Boolean).join(' · ')}
        actions=${html`<${Chip} tone=${{ healthy: 'good', warning: 'warn', unhealthy: 'bad' }[d.health]}>${{ healthy: t('Sağlıklı', 'Healthy'), warning: t('Uyarı', 'Warning'), unhealthy: t('Sorunlu', 'Unhealthy') }[d.health] || t('Bilinmiyor', 'Unknown')}<//>`}>
        <div class="grid c4">
          <${Stat} label=${t('Sıcaklık', 'Temperature')} value=${d.temperatureC != null ? num(d.temperatureC) : '–'} unit=${d.temperatureC != null ? '°C' : ''} tone=${d.temperatureC != null && tone(d.temperatureC, 55, 68) !== 'good' ? tone(d.temperatureC, 55, 68) : null} />
          <${Stat} label=${t('Kullanılan ömür', 'Life used')} value=${d.wearPercent != null ? num(d.wearPercent) : '–'} unit=${d.wearPercent != null ? '%' : ''} />
          <${Stat} label=${t('Çalışma saati', 'Power-on hours')} value=${d.powerOnHours != null ? num(d.powerOnHours) : '–'} />
          <${Stat} label=${t('Şu anki etkinlik', 'Activity now')} value=${io ? num(io.active) : '–'} unit="%" sub=${io ? t('yanıt ', 'response ') + num(io.latencyMs, 1) + ' ms' : null} />
        </div>
        ${d.media === 'HDD' && d.isSystem && html`<div class="mt"><${Banner} tone="warn" icon="bulb">${t('Windows mekanik bir diskte çalışıyor. Bir SSD\'ye geçmek bu bilgisayar için yapılabilecek en etkili yükseltmedir.', 'Windows runs from a mechanical disk. Moving to an SSD is the most effective upgrade for this PC.')}<//></div>`}
      <//>`;
    })}
    <div><${Btn} icon="gauge" onClick=${() => go('benchmark/disk')}>${t('Disk hız testi', 'Disk speed test')}<//></div>
  </div>`}<//>`;
}

function Trends() {
  const trends = useRpc('storage.trends');
  const watched = useRpc('storage.watched');
  const [path, setPath] = useState('');
  const watch = (p, remove) => act(() => call('storage.watch', { path: p, remove })).then((r) => r && watched.reload());
  return html`<div class="col gap-l">
    <${Async} state=${trends}>${(list) => (list.length === 0
      ? html`<div class="card"><${Empty} icon="trend" title=${t('Eğilim verisi birikiyor', 'Collecting trend data')} text=${t('OptiCore boş alanı 6 saatte bir kaydeder. Birkaç gün sonra burada büyüme hızı ve dolma tahmini görünür.', 'OptiCore records free space every 6 hours. After a few days the growth rate and a fill-up forecast appear here.')} /></div>`
      : list.map((d) => html`<${Card} key=${d.letter} title=${d.letter + ':'} icon="disk" sub=${bytes(d.free) + t(' boş', ' free')}
          actions=${d.daysUntilFull != null ? html`<${Chip} tone=${d.daysUntilFull < 30 ? 'bad' : 'warn'}>${t(`~${num(d.daysUntilFull)} günde dolar`, `full in ~${num(d.daysUntilFull)} days`)}<//>`
            : d.bytesPerDay != null ? html`<${Chip} tone="good">${d.bytesPerDay > 0 ? '+' : ''}${bytes(d.bytesPerDay)}/${t('gün', 'day')}<//>` : null}>
          ${d.points.length > 1 ? html`<${LineChart} height=${170} labels=${d.points.map((p) => date(p.t))} format=${(v) => bytes(v)} min=${0} max=${d.total}
            series=${[{ label: t('Kullanılan', 'Used'), data: d.points.map((p) => p.used), color: '#7c6cff' }]} />` : html`<div class="muted small">${t('İlk kayıt alındı; ikinci kayıttan sonra grafik çizilir.', 'First record taken; the chart is drawn after the second one.')}</div>`}
        <//>`))}<//>
    <${Card} title=${t('İzlenen klasörler', 'Watched folders')} icon="eye">
      <div class="small muted">${t('Büyüdüğünü düşündüğünüz klasörleri (İndirilenler, oyun kayıtları, video kayıtları…) ekleyin; OptiCore boyutlarını düzenli olarak kaydeder.', 'Add folders you suspect are growing (Downloads, game captures, recordings…); OptiCore records their size regularly.')}</div>
      <div class="row mt"><input class="input grow" placeholder="C:\\Users\\…\\Downloads" value=${path} onInput=${(e) => setPath(e.target.value)} />
        <${Btn} icon="plus" disabled=${!path.trim()} onClick=${() => watch(path.trim(), false).then(() => { setPath(''); call('storage.record').catch(() => {}); })}>${t('İzle', 'Watch')}<//></div>
      ${(watched.data || []).map((w) => html`<div key=${w.path} class="item" style=${{ padding: '10px 0' }}>
        <div class="grow"><div class="mono">${w.path}</div><div class="tiny dim">${w.points.length ? bytes(w.points[w.points.length - 1].size) + ' · ' + ago(w.points[w.points.length - 1].t) : t('henüz ölçülmedi', 'not measured yet')}${w.points.length > 1 ? ' · ' + t('ilk ölçümden bu yana ', 'since first measurement ') + (w.points[w.points.length - 1].size >= w.points[0].size ? '+' : '') + bytes(w.points[w.points.length - 1].size - w.points[0].size) : ''}</div></div>
        <${Btn} size="sm" kind="ghost" icon="trash" onClick=${() => watch(w.path, true)} />
      </div>`)}
    <//>
  </div>`;
}

export default function Storage({ sub }) {
  const tabs = ['map', 'health', 'trends'];
  const [tab, setTab] = useState(tabs.includes(sub) ? sub : 'map');
  return html`<${Page} title=${t('Depolama', 'Storage')} desc=${t('Diskte neyin yer kapladığı, disklerin sağlığı ve boş alanın zaman içindeki seyri.', 'What takes space on disk, how healthy the disks are and how free space changes over time.')}
    actions=${html`<${Btn} icon="sparkles" onClick=${() => go('cleanup')}>${t('Temizliğe git', 'Go to cleanup')}<//>`}>
    <div class="mb"><${Tabs} value=${tab} onChange=${setTab} items=${[
      { id: 'map', label: t('Kullanım haritası', 'Usage map'), icon: 'layers' },
      { id: 'health', label: t('Disk sağlığı', 'Disk health'), icon: 'heart' },
      { id: 'trends', label: t('Eğilimler', 'Trends'), icon: 'trend' },
    ]} /></div>
    ${tab === 'map' ? html`<${DiskMap} />` : tab === 'health' ? html`<${Health} />` : html`<${Trends} />`}
  <//>`;
}
