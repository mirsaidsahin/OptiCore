// Dashboard: health score, what to do next, and the live state of the machine.
import {
  html, useState, useRpc, t, pct, bytes, rate, bits, ghz, temp, num, tone, go, cx, duration, ago, uiState, setUiState, act, runJob,
} from '../lib/core.js';
import { Page, Card, Bar, Btn, Chip, FileIcon, Icon, Loading, Modal, Switch, Tabs, Empty, SimulationNote } from '../lib/ui.js';
import { Spark, Ring, BarList } from '../lib/charts.js';

const GRADES = {
  excellent: ['Mükemmel', 'Excellent', 'var(--good)'],
  good: ['İyi', 'Good', 'var(--accent)'],
  fair: ['Orta', 'Fair', 'var(--warn)'],
  poor: ['Zayıf', 'Poor', 'var(--bad)'],
};
export const SEVERITY_ICON = { critical: 'alert', warning: 'alert', info: 'info', good: 'check' };

const WIDGETS = [
  ['tiles', 'Canlı göstergeler', 'Live gauges'],
  ['cores', 'İşlemci çekirdekleri', 'Processor cores'],
  ['top', 'En çok kaynak tüketenler', 'Top consumers'],
  ['storage', 'Depolama', 'Storage'],
  ['power', 'Pil ve sıcaklık', 'Battery and temperature'],
  ['activity', 'Son işlemler', 'Recent activity'],
];

function Hero({ report, onAnalyze }) {
  if (report.data === undefined) return html`<div class="card pad"><${Loading} lines=${5} height=${20} /></div>`;
  const r = report.data;
  const [tr, en, color] = GRADES[r.grade] || GRADES.good;
  const open = r.findings.filter((f) => !f.ignored && (f.severity === 'critical' || f.severity === 'warning'));
  const top = (open.length ? open : r.findings.filter((f) => !f.ignored)).slice(0, 3);
  return html`<div class="card hero">
    <${Ring} value=${r.score} size=${176} stroke=${13} color=${r.grade === 'poor' ? 'var(--bad)' : r.grade === 'fair' ? 'var(--warn)' : null}>
      <div class="ring-score">${r.score}</div>
      <div class="ring-label" style=${{ color }}>${t(tr, en)}</div>
    <//>
    <div class="col" style=${{ gap: '9px' }}>
      <div>
        <div class="up">${t('Sistem sağlık puanı', 'System health score')}</div>
        <div class="small muted" style=${{ marginTop: '3px' }}>
          ${open.length
            ? t(`${r.critical} kritik, ${r.warnings} uyarı puanı düşürüyor.`, `${r.critical} critical and ${r.warnings} warnings are lowering the score.`)
            : t('Puanı düşüren bir sorun yok.', 'Nothing is lowering the score.')}
          ${r.quick && html` <span class="dim">${t('(hızlı ölçüm)', '(quick check)')}</span>`}
        </div>
      </div>
      ${r.parts.map((p) => html`<div key=${p.id} class="subscore">
        <span>${p.label}</span><${Bar} value=${p.score} tone=${p.score >= 85 ? 'good' : p.score >= 60 ? 'warn' : 'bad'} /><b>${p.score}</b>
      </div>`)}
      <div class="row gap-s mt-s">
        <${Btn} kind="primary" icon="bolt" onClick=${() => go('insights/live')}>${t('Şu an neden yavaş?', 'Why is it slow right now?')}<//>
        <${Btn} icon="refresh" onClick=${onAnalyze}>${t('Tam analiz', 'Full analysis')}<//>
      </div>
    </div>
    <div class="hero-recs col" style=${{ gap: 0 }}>
      <div class="row"><div class="up">${t('Önerilen işlemler', 'Recommended next')}</div><a class="sp small" href="#/insights">${t('Tümü', 'All')} →</a></div>
      ${top.length === 0 && html`<${Empty} icon="check" title=${t('Yapılacak bir şey yok', 'Nothing to do')} text=${t('OptiCore ölçtüğü hiçbir şeyde sorun görmedi.', 'OptiCore found no problem in anything it measured.')} />`}
      ${top.map((f) => html`<div key=${f.id} class=${cx('finding', f.severity)} style=${{ padding: '11px 0', cursor: 'pointer' }} onClick=${() => go('insights')}>
        <div class="finding-ico"><${Icon} name=${SEVERITY_ICON[f.severity]} /></div>
        <div class="grow">
          <h4>${f.title}</h4>
          <div class="evidence">${f.evidence.slice(0, 2).map((e) => html`<span key=${e.label}>${e.label}: <b>${e.value}</b></span>`)}</div>
        </div>
        <${Icon} name="chevR" size="15" class="dim" />
      </div>`)}
    </div>
  </div>`;
}

function Tile({ icon, label, value, unit, sub, data, color, max, route }) {
  return html`<div class="card tile click" onClick=${() => go(route)}>
    <div class="tile-top"><${Icon} name=${icon} />${label}</div>
    <div class="tile-val">${value}${unit && html`<small>${unit}</small>`}</div>
    <div class="tile-sub">${sub}</div>
    <div class="tile-chart"><${Spark} data=${data} color=${color} max=${max} height=${40} /></div>
  </div>`;
}

function Tiles({ live }) {
  const n = live.now, h = live.history;
  const gpu = n.gpus[0];
  const net = n.nets.find((x) => x.physical && (x.down > 0 || x.up > 0)) || n.nets.find((x) => x.physical) || n.nets[0];
  return html`<div class="grid c5">
    <${Tile} icon="cpu" label=${t('İşlemci', 'Processor')} value=${num(n.cpu)} unit="%" sub=${ghz(n.cpuMhz) + (n.throttled ? ' · ' + t('kısılıyor', 'throttled') : '')}
      data=${h.cpu} max=${100} color=${n.cpu > 85 ? 'var(--bad)' : 'var(--accent)'} route="processes/cpu" />
    <${Tile} icon="ram" label=${t('Bellek', 'Memory')} value=${num(n.memPercent)} unit="%" sub=${bytes(n.memUsed) + ' / ' + bytes(n.memTotal)}
      data=${h.mem} max=${100} color=${n.memPercent > 88 ? 'var(--bad)' : '#22d3ee'} route="processes/memory" />
    <${Tile} icon="gpu" label=${t('Ekran kartı', 'Graphics')} value=${num(n.gpu)} unit="%"
      sub=${gpu ? (gpu.memTotal > (768 << 20) ? bytes(gpu.memUsed) + ' / ' + bytes(gpu.memTotal) : bytes(gpu.sharedUsed) + ' ' + t('paylaşılan', 'shared')) + (gpu.temp ? ' · ' + temp(gpu.temp) : '') : '–'}
      data=${h.gpu} max=${100} color="#34d399" route="system/gpu" />
    <${Tile} icon="disk" label=${t('Disk', 'Disk')} value=${num(n.diskActive)} unit="%" sub=${'↓ ' + rate(n.diskRead) + '  ↑ ' + rate(n.diskWrite)}
      data=${h.disk} max=${100} color="#fbbf24" route="storage" />
    <${Tile} icon=${net && net.type === 'wifi' ? 'wifi' : 'ethernet'} label=${t('Ağ', 'Network')} value=${bits(n.netDown).split(' ')[0]} unit=${bits(n.netDown).split(' ')[1]}
      sub=${'↑ ' + bits(n.netUp) + (net ? ' · ' + net.name : '')} data=${h.down} color="#60a5fa" route="network" />
  </div>`;
}

function Cores({ live, hardware }) {
  const n = live.now;
  const pCores = hardware && hardware.cpu.performanceCores ? hardware.cpu.performanceCores : 0;
  // On hybrid CPUs the performance cores come first and usually carry two threads each.
  const pThreads = pCores && hardware.cpu.threads > hardware.cpu.cores ? Math.min(n.cores.length, pCores * 2) : pCores;
  return html`<${Card} title=${t('İşlemci çekirdekleri', 'Processor cores')} icon="cpu" sub=${hardware ? hardware.cpu.name : ''}>
    <div class="cores">${n.cores.map((v, i) => html`<div key=${i} class=${cx('core', pThreads && i >= pThreads && 'e')} title=${`#${i}: ${pct(v)}`}><i style=${{ height: Math.max(3, v) + '%' }}></i></div>`)}</div>
    <div class="row wrap small muted mt" style=${{ gap: '6px 18px' }}>
      ${pThreads > 0 && html`<span><i class="dot" style=${{ background: 'var(--accent)', display: 'inline-block', marginRight: '6px' }}></i>${t(`${pCores} performans`, `${pCores} performance`)}</span>
        <span><i class="dot" style=${{ background: 'var(--accent-2)', display: 'inline-block', marginRight: '6px' }}></i>${t(`${hardware.cpu.efficiencyCores} verimlilik`, `${hardware.cpu.efficiencyCores} efficiency`)}</span>`}
      <span>${t('Hız', 'Speed')}: <b class="num" style=${{ color: 'var(--text)' }}>${ghz(n.cpuMhz)}</b></span>
      <span>${t('İşlem', 'Processes')}: <b class="num" style=${{ color: 'var(--text)' }}>${num(n.processes)}</b></span>
      <span>${t('İş parçacığı', 'Threads')}: <b class="num" style=${{ color: 'var(--text)' }}>${num(n.threads)}</b></span>
    </div>
  <//>`;
}

function TopConsumers() {
  const [by, setBy] = useState('cpu');
  const top = useRpc('processes.top', { n: 6 }, { interval: 2500 });
  const items = top.data ? top.data[by] : [];
  return html`<${Card} title=${t('En çok kaynak tüketenler', 'Top consumers')} icon="activity"
    actions=${html`<${Tabs} value=${by} onChange=${setBy} items=${[{ id: 'cpu', label: 'CPU' }, { id: 'memory', label: t('Bellek', 'Memory') }]} />`}>
    ${top.data === undefined ? html`<${Loading} />` : html`<${BarList}
      items=${items.map((p) => ({
        key: p.name, label: p.name, sub: p.count > 1 ? '×' + p.count : null, value: by === 'cpu' ? p.cpu : p.memory,
        right: by === 'cpu' ? pct(p.cpu, 1) : bytes(p.memory), icon: html`<${FileIcon} path=${p.path} />`,
        color: by === 'cpu' ? 'var(--accent)' : '#22d3ee',
      }))}
      max=${by === 'cpu' ? Math.max(25, ...items.map((p) => p.cpu)) : undefined} onPick=${() => go('processes')} />`}
  <//>`;
}

function StorageCard() {
  const volumes = useRpc('system.volumes', {}, { interval: 30000 });
  return html`<${Card} title=${t('Depolama', 'Storage')} icon="disk" actions=${html`<${Btn} size="sm" kind="ghost" onClick=${() => go('storage')}>${t('Ayrıntı', 'Details')}<//>`}>
    ${volumes.data === undefined ? html`<${Loading} lines=${2} />` : html`<div class="col" style=${{ gap: '14px' }}>
      ${volumes.data.map((v) => {
        const used = v.totalBytes - v.freeBytes, p = (used / v.totalBytes) * 100;
        return html`<div key=${v.letter}>
          <div class="row small"><b>${v.letter}:</b><span class="muted ell">${v.label || (v.isSystem ? 'Windows' : '')}</span>
            ${v.media && html`<${Chip}>${v.media}<//>`}<span class="sp num muted">${bytes(v.freeBytes)} ${t('boş', 'free')}</span></div>
          <div class="mt-s"><${Bar} value=${p} thick tone=${tone(p, 85, 93)} /></div>
          <div class="tiny dim mt-s num">${bytes(used)} / ${bytes(v.totalBytes)} · ${pct(p)}</div>
        </div>`;
      })}
    </div>`}
  <//>`;
}

function PowerCard({ live }) {
  const n = live.now;
  const battery = useRpc('system.battery', {}, { interval: 60000 });
  const b = battery.data;
  return html`<${Card} title=${t('Pil ve sıcaklık', 'Battery and temperature')} icon="battery" actions=${html`<${Btn} size="sm" kind="ghost" onClick=${() => go('system/battery')}>${t('Ayrıntı', 'Details')}<//>`}>
    <div class="grid c2" style=${{ gap: '16px' }}>
      <div>
        <div class="up">${t('Pil', 'Battery')}</div>
        ${b && b.present ? html`
          <div class="big mt-s">${num(n.batteryPercent >= 0 ? n.batteryPercent : b.chargePercent)}<small>%</small></div>
          <div class="small muted">${n.onBattery ? t('Pilde', 'On battery') : b.charging ? t('Şarj oluyor', 'Charging') : t('Prizde', 'Plugged in')}${b.estimatedMinutes && n.onBattery ? ' · ' + duration(b.estimatedMinutes * 60) : ''}</div>
          ${b.healthPercent != null && html`<div class="mt"><div class="row tiny muted"><span>${t('Pil sağlığı', 'Battery health')}</span><span class="sp num b">${pct(b.healthPercent)}</span></div>
            <div class="mt-s"><${Bar} value=${b.healthPercent} tone=${b.healthPercent >= 80 ? 'good' : b.healthPercent >= 60 ? 'warn' : 'bad'} /></div></div>`}`
        : html`<div class="small muted mt-s">${b ? t('Bu bilgisayarda pil yok.', 'This PC has no battery.') : '…'}</div>`}
      </div>
      <div>
        <div class="up">${t('Sıcaklık', 'Temperature')}</div>
        ${n.temp != null ? html`
          <div class=${cx('big mt-s', tone(n.temp, 80, 92) !== 'good' && tone(n.temp, 80, 92))}>${num(n.temp)}<small>°C</small></div>
          <div class="small muted">${n.throttled ? t('İşlemci kısılıyor', 'CPU is throttled') : t('Kısılma yok', 'No throttling')}</div>
          <div class="tiny dim mt">${n.zones.length ? t('ACPI termal bölgesi', 'ACPI thermal zone') : t('Ekran kartı sensörü', 'GPU sensor')}</div>`
        : html`<div class="small muted mt-s">${t('Bu donanım, sürücü yüklemeden okunabilen bir sıcaklık sensörü sunmuyor.', 'This hardware exposes no temperature sensor readable without a driver.')}</div>`}
      </div>
    </div>
  <//>`;
}

function Activity() {
  const journal = useRpc('journal.list', {}, { interval: 30000 });
  const items = (journal.data || []).slice(0, 5);
  return html`<${Card} title=${t('Son işlemler', 'Recent activity')} icon="history" flush
    actions=${html`<${Btn} size="sm" kind="ghost" onClick=${() => go('backup')}>${t('Geri alma merkezi', 'Undo center')}<//>`}>
    ${journal.data === undefined ? html`<div style=${{ padding: '0 16px' }}><${Loading} lines=${3} /></div>`
      : !items.length ? html`<${Empty} icon="history" title=${t('Henüz bir değişiklik yok', 'No changes yet')} text=${t('OptiCore\'un sistemde yaptığı her değişiklik burada listelenir ve geri alınabilir.', 'Every change OptiCore makes to the system is listed here and can be undone.')} />`
      : items.map((s) => html`<div key=${s.id} class="item click" onClick=${() => go('backup')}>
          <${Icon} name=${s.revertedAt ? 'undo' : s.simulated ? 'flask' : 'check'} size="16" class=${s.revertedAt ? 'dim' : s.simulated ? 'acc' : 'good'} />
          <div class="grow"><div class="t ell">${s.title}</div><div class="s">${ago(s.time)} · ${t(`${s.count} değişiklik`, `${s.count} changes`)}${s.simulated ? ' · ' + t('simülasyon', 'simulated') : ''}</div></div>
        </div>`)}
  <//>`;
}

function Customize({ hidden, onClose, onChange }) {
  return html`<${Modal} title=${t('Panoyu özelleştir', 'Customize the dashboard')} onClose=${onClose}>
    <div class="col" style=${{ gap: 0 }}>
      ${WIDGETS.map(([id, tr, en]) => html`<div key=${id} class="row" style=${{ padding: '9px 0', borderBottom: '1px solid var(--line)' }}>
        <span class="grow">${t(tr, en)}</span>
        <${Switch} on=${!hidden.includes(id)} onChange=${(on) => onChange(on ? hidden.filter((x) => x !== id) : [...hidden, id])} />
      </div>`)}
    </div>
  <//>`;
}

export default function Dashboard() {
  const live = useRpc('metrics.live', { history: 60 }, { interval: 1000 });
  const hardware = useRpc('system.hardware');
  const report = useRpc('insights.summary', {}, { interval: 60000 });
  const [hidden, setHidden] = useState(() => uiState('dashHidden', []));
  const [customize, setCustomize] = useState(false);
  const show = (id) => !hidden.includes(id);
  const ready = live.data && live.data.now;
  const hw = hardware.data;

  const analyze = () => act(async () => {
    await runJob('insights.analyze');
    report.reload();
  }, t('Analiz tamamlandı', 'Analysis finished'));

  return html`<${Page}
    title=${hw ? t(`Merhaba, ${hw.os.user}`, `Hello, ${hw.os.user}`) : 'OptiCore'}
    desc=${hw ? `${hw.computer.manufacturer} ${hw.computer.model} · ${hw.os.name} ${hw.os.version} · ${t('çalışma süresi', 'uptime')} ${duration(hw.os.uptimeSeconds)}` : null}
    actions=${html`<${Btn} icon="sliders" kind="ghost" onClick=${() => setCustomize(true)}>${t('Özelleştir', 'Customize')}<//>`}>
    <div class="col" style=${{ gap: '14px' }}>
      <${SimulationNote} />
      <${Hero} report=${report} onAnalyze=${analyze} />
      ${show('tiles') && (ready ? html`<${Tiles} live=${live.data} />` : html`<div class="card pad"><${Loading} lines=${3} /></div>`)}
      <div class="grid c2">
        ${show('cores') && ready && html`<${Cores} live=${live.data} hardware=${hw} />`}
        ${show('top') && html`<${TopConsumers} />`}
        ${show('storage') && html`<${StorageCard} />`}
        ${show('power') && ready && html`<${PowerCard} live=${live.data} />`}
      </div>
      ${show('activity') && html`<${Activity} />`}
    </div>
    ${customize && html`<${Customize} hidden=${hidden} onClose=${() => setCustomize(false)} onChange=${(next) => { setHidden(next); setUiState('dashHidden', next); }} />`}
  <//>`;
}
