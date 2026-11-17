// Performance Intelligence: findings with evidence, live diagnosis, history and before/after.
import {
  html, useState, useEffect, useRpc, useJob, call, act, t, cx, go, num, pct, bytes, ghz, temp, ms, dateTime, clock, duration, toast,
} from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Bar, Banner, Empty, Loading, JobBar, Icon, Stat, Async, Switch, Check } from '../lib/ui.js';
import { Ring, LineChart, BarList } from '../lib/charts.js';

const TABS = ['findings', 'live', 'history', 'compare'];
const SEV_ICON = { critical: 'alert', warning: 'alert', info: 'info', good: 'check' };
const SEV_LABEL = { critical: ['Kritik', 'Critical'], warning: ['Uyarı', 'Warning'], info: ['Bilgi', 'Info'], good: ['İyi', 'Good'] };
const SEV_TONE = { critical: 'bad', warning: 'warn', info: 'info', good: 'good' };

export function FindingCard({ f, onChanged }) {
  const run = (a) => {
    if (a.route) return go(a.route);
    return act(() => call(a.method, a.params || {}), a.label).then((ok) => { if (ok !== undefined && onChanged) onChanged(); });
  };
  return html`<div class=${cx('finding', f.severity)} style=${f.ignored ? { opacity: 0.55 } : null}>
    <div class="finding-ico"><${Icon} name=${SEV_ICON[f.severity]} /></div>
    <div class="grow">
      <div class="row wrap gap-s">
        <h4>${f.title}</h4>
        <${Chip} tone=${SEV_TONE[f.severity]}>${t(...SEV_LABEL[f.severity])}<//>
        ${f.penalty > 0 && !f.ignored && html`<span class="tiny dim">−${f.penalty} ${t('puan', 'pts')}</span>`}
      </div>
      <p class="sel">${f.detail}</p>
      ${f.evidence.length > 0 && html`<div class="evidence">${f.evidence.map((e) => html`<span key=${e.label}>${e.label}: <b>${e.value}</b></span>`)}</div>`}
      ${f.impact && html`<div class="small mt-s"><span class="acc b">${t('Beklenen etki', 'Expected effect')}:</span> <span class="muted">${f.impact}</span></div>`}
      <div class="row wrap gap-s mt">
        ${f.actions.map((a, i) => html`<${Btn} key=${i} size="sm" kind=${i === 0 ? 'primary' : ''} onClick=${() => run(a)}>${a.label}<//>`)}
        ${onChanged && html`<${Btn} size="sm" kind="ghost" icon=${f.ignored ? 'eye' : 'eyeOff'}
          onClick=${() => call('insights.ignore', { id: f.id, ignore: !f.ignored }).then(onChanged)}>${f.ignored ? t('Yoksaymayı kaldır', 'Stop ignoring') : t('Yoksay', 'Ignore')}<//>`}
      </div>
    </div>
  </div>`;
}

function Findings() {
  const report = useRpc('insights.summary');
  const job = useJob();
  const [group, setGroup] = useState('all');
  const [showIgnored, setShowIgnored] = useState(false);
  const [explain, setExplain] = useState(false);

  const analyze = () => act(async () => { await job.run('insights.analyze'); report.reload(); }, t('Analiz tamamlandı', 'Analysis finished'));
  useEffect(() => { if (report.data && report.data.quick && !job.running) analyze(); }, [report.data && report.data.quick]);

  if (report.data === undefined) return html`<${Loading} lines=${8} height=${20} />`;
  const r = report.data;
  const visible = r.findings.filter((f) => (group === 'all' || f.group === group) && (showIgnored || !f.ignored));
  const count = (g) => r.findings.filter((f) => f.group === g && !f.ignored && f.severity !== 'info' && f.severity !== 'good').length;

  return html`<div class="col gap-l">
    <div class="card pad row gap-l wrap">
      <${Ring} value=${r.score} size=${112} stroke=${10} color=${r.grade === 'poor' ? 'var(--bad)' : r.grade === 'fair' ? 'var(--warn)' : null}>
        <b class="num" style=${{ fontSize: '30px', fontFamily: 'var(--display)' }}>${r.score}</b>
      <//>
      <div class="grow col" style=${{ gap: '7px', minWidth: '280px' }}>
        ${r.parts.map((p) => html`<div key=${p.id} class="subscore"><span>${p.label}</span><${Bar} value=${p.score} tone=${p.score >= 85 ? 'good' : p.score >= 60 ? 'warn' : 'bad'} /><b>${p.score}</b></div>`)}
      </div>
      <div class="col" style=${{ alignItems: 'flex-end' }}>
        <div class="row gap-s">
          <${Chip} tone=${r.critical ? 'bad' : null}>${r.critical} ${t('kritik', 'critical')}<//>
          <${Chip} tone=${r.warnings ? 'warn' : null}>${r.warnings} ${t('uyarı', 'warnings')}<//>
        </div>
        <div class="tiny dim">${t('Son ölçüm', 'Measured')}: ${clock(r.time)}</div>
        <div class="row gap-s">
          <${Btn} size="sm" kind="ghost" icon="info" onClick=${() => setExplain(!explain)}>${t('Puan nasıl hesaplanır?', 'How is it scored?')}<//>
          <${Btn} kind="primary" icon="refresh" busy=${job.running} onClick=${analyze}>${t('Tam analiz', 'Full analysis')}<//>
        </div>
      </div>
    </div>
    <${JobBar} job=${job.job} onCancel=${job.cancel} />
    ${explain && html`<${Banner} tone="info" icon="scale">
      <div class="small">${t('Her grup 100 puanla başlar; o gruptaki her uyarı ve kritik bulgu, kartında yazan puanı düşürür. Genel puan grupların ağırlıklı ortalamasıdır',
        'Every group starts at 100; each warning or critical finding in it subtracts the points shown on its card. The overall score is the weighted average of the groups')}
        (${r.parts.map((p) => `${p.label} %${p.weight}`).join(', ')}). ${t('Herhangi bir kritik bulgu varken genel puan 74\'ü geçemez. Yoksaydığınız bulgular puanı etkilemez.',
        'While any critical finding is open the overall score cannot exceed 74. Findings you ignore do not affect the score.')}</div>
    <//>`}
    <div class="row wrap">
      <${Tabs} value=${group} onChange=${setGroup} items=${[{ id: 'all', label: t('Tümü', 'All'), count: r.findings.filter((f) => !f.ignored).length },
        ...r.parts.map((p) => ({ id: p.id, label: p.label, count: count(p.id) || null }))]} />
      <label class="row gap-s small muted sp" style=${{ cursor: 'pointer' }}><${Check} checked=${showIgnored} onChange=${setShowIgnored} />${t('Yoksayılanları göster', 'Show ignored')}</label>
    </div>
    <div class="card">
      ${visible.length === 0
        ? html`<${Empty} icon="check" title=${t('Bu grupta bulgu yok', 'No findings in this group')} text=${t('Ölçülen her şey beklenen aralıkta.', 'Everything measured is within the expected range.')} />`
        : visible.map((f) => html`<${FindingCard} key=${f.id} f=${f} onChanged=${report.reload} />`)}
    </div>
  </div>`;
}

function Live({ autostart }) {
  const job = useJob();
  const [result, setResult] = useState(null);
  const start = () => act(async () => { setResult(null); setResult(await job.run('insights.live')); });
  useEffect(() => { if (autostart) start(); }, []);
  const tones = { thermal: 'flame', memory: 'ram', cpu: 'cpu', disk: 'disk', gpu: 'gpu', power: 'battery', none: 'check' };

  return html`<div class="col gap-l">
    ${!result && !job.running && html`<div class="card"><${Empty} icon="bolt" title=${t('Şu an neden yavaş?', 'Why is it slow right now?')}
      text=${t('OptiCore sistemi 8 saniye dinler; işlemci, bellek, disk, ekran kartı ve ısıyı aynı anda ölçer ve yavaşlığın kaynağını, sorumlu uygulamalarla birlikte gösterir.',
        'OptiCore listens to the system for 8 seconds, measuring processor, memory, disk, graphics and heat at once, then names the source of the slowdown and the apps behind it.')}
      action=${html`<${Btn} kind="primary" size="lg" icon="bolt" onClick=${start}>${t('Teşhisi başlat', 'Start diagnosis')}<//>`} /></div>`}
    <${JobBar} job=${job.job} onCancel=${job.cancel} />
    ${result && html`
      <div class="card pad row gap-l">
        <div class="finding-ico" style=${{ width: '54px', height: '54px', borderRadius: '15px', background: result.bottleneck === 'none' ? 'var(--good-soft)' : 'var(--warn-soft)', color: result.bottleneck === 'none' ? 'var(--good)' : 'var(--warn)' }}>
          <${Icon} name=${tones[result.bottleneck] || 'bolt'} size="26" />
        </div>
        <div class="grow">
          <div class="up">${t('Teşhis', 'Diagnosis')} · ${t(`${result.seconds} sn ölçüm`, `${result.seconds}s measured`)}</div>
          <div style=${{ font: '650 21px var(--display)', marginTop: '3px' }}>${result.verdict}</div>
        </div>
        <${Btn} icon="refresh" onClick=${start}>${t('Yeniden ölç', 'Measure again')}<//>
      </div>
      <div class="grid c5">
        <div class="card pad"><${Stat} label=${t('İşlemci', 'Processor')} value=${num(result.averages.cpu)} unit="%" sub=${t('en yoğun çekirdek ', 'busiest core ') + pct(result.averages.maxCore)} /></div>
        <div class="card pad"><${Stat} label=${t('Bellek', 'Memory')} value=${num(result.averages.mem)} unit="%" sub=${num(result.averages.faults) + t(' sayfa/sn', ' pages/s')} /></div>
        <div class="card pad"><${Stat} label=${t('Disk', 'Disk')} value=${num(result.averages.disk)} unit="%" sub=${ms(result.averages.latency, 1) + t(' yanıt', ' response')} /></div>
        <div class="card pad"><${Stat} label=${t('Ekran kartı', 'Graphics')} value=${num(result.averages.gpu)} unit="%" /></div>
        <div class="card pad"><${Stat} label=${t('Hız', 'Speed')} value=${num(result.averages.mhz / 1000, 2)} unit="GHz" sub=${result.averages.temp != null ? temp(result.averages.temp) : null} /></div>
      </div>
      ${result.factors.length > 0 && html`<div class="card">
        ${result.factors.map((f, i) => html`<div key=${i} class=${cx('finding', f.severity)}>
          <div class="finding-ico"><${Icon} name=${tones[f.kind] || 'info'} /></div>
          <div class="grow">
            <h4>${f.title}</h4><p>${f.detail}</p>
            ${f.culprits.length > 0 && html`<div class="mt" style=${{ maxWidth: '520px' }}><${BarList} items=${f.culprits.map((c) => ({ label: c.name, value: c.value, right: c.text }))} /></div>`}
          </div>
        </div>`)}
      </div>`}
      <div class="grid c2">
        <${Card} title=${t('Ölçüm sırasında en çok işlemci kullananlar', 'Top processor users during the measurement')} icon="cpu">
          <${BarList} items=${result.topCpu.map((c) => ({ label: c.name, value: c.value, right: c.text }))} />
        <//>
        <${Card} title=${t('En çok bellek tutanlar', 'Top memory holders')} icon="ram">
          <${BarList} items=${result.topMemory.map((c) => ({ label: c.name, value: c.value, right: c.text, color: '#22d3ee' }))} />
        <//>
      </div>
      <div><${Btn} icon="activity" onClick=${() => go('processes')}>${t('İşlem yöneticisini aç', 'Open the process manager')}<//></div>`}
  </div>`;
}

function History() {
  const [hours, setHours] = useState(24);
  const health = useRpc('insights.history');
  const metrics = useRpc('metrics.history', { hours }, { deps: [hours] });
  const points = metrics.data || [];
  const labels = points.map((p) => (hours <= 24 ? clock(p.t) : dateTime(p.t)));
  return html`<div class="col gap-l">
    <${Card} title=${t('Sağlık puanı geçmişi', 'Health score history')} icon="trend" sub=${t('her tam analizde bir nokta', 'one point per full analysis')}>
      ${health.data === undefined ? html`<${Loading} />` : health.data.length < 2
        ? html`<${Empty} icon="trend" title=${t('Henüz yeterli veri yok', 'Not enough data yet')} text=${t('Tam analiz çalıştırdıkça puanın zaman içindeki seyri burada birikir.', 'As you run full analyses, the score\'s course over time builds up here.')} />`
        : html`<${LineChart} series=${[{ label: t('Sağlık puanı', 'Health score'), data: health.data.map((p) => p.score), color: '#7c6cff' }]} labels=${health.data.map((p) => dateTime(p.t))} max=${100} min=${0} height=${190} />`}
    <//>
    <${Card} title=${t('Kaynak kullanımı', 'Resource usage')} icon="chart"
      actions=${html`<${Tabs} value=${hours} onChange=${setHours} items=${[{ id: 1, label: t('1 saat', '1 hour') }, { id: 24, label: t('24 saat', '24 hours') }, { id: 168, label: t('7 gün', '7 days') }]} />`}>
      ${metrics.data === undefined ? html`<${Loading} />` : points.length < 3
        ? html`<${Empty} icon="chart" title=${t('Kayıt birikiyor', 'Recording')} text=${t('OptiCore çalışırken her dakika bir özet kaydeder. Birkaç dakika sonra grafik burada belirir.', 'While OptiCore runs it stores one summary per minute. The chart appears here after a few minutes.')} />`
        : html`<${LineChart} series=${[
            { label: t('İşlemci (ort.)', 'CPU (avg)'), data: points.map((p) => p.cpu), color: '#7c6cff' },
            { label: t('İşlemci (en yüksek)', 'CPU (peak)'), data: points.map((p) => p.cpuMax), color: '#fb7185' },
            { label: t('Bellek', 'Memory'), data: points.map((p) => p.mem), color: '#22d3ee' },
            { label: t('Disk', 'Disk'), data: points.map((p) => p.disk), color: '#fbbf24' },
          ]} labels=${labels} max=${100} format=${(v) => pct(v)} height=${230} area=${false} />`}
    <//>
  </div>`;
}

const BASE_ROWS = [
  ['cpuIdle', 'Boşta işlemci kullanımı', 'Idle processor usage', (v) => pct(v, 1), -1],
  ['memoryUsed', 'Kullanılan bellek', 'Memory in use', (v) => bytes(v), -1],
  ['processes', 'Çalışan işlem', 'Running processes', (v) => num(v), -1],
  ['threads', 'İş parçacığı', 'Threads', (v) => num(v), -1],
  ['systemFree', 'Sistem diskinde boş alan', 'Free space on the system drive', (v) => bytes(v), 1],
  ['healthScore', 'Sağlık puanı', 'Health score', (v) => num(v), 1],
];
const EXTRA_LABELS = {
  startupEnabled: ['Etkin başlangıç öğesi', 'Enabled startup items'],
  servicesRunning: ['Çalışan hizmet', 'Running services'],
  junkBytes: ['Temizlenebilir alan', 'Reclaimable space'],
};

function Compare() {
  const list = useRpc('insights.baselines');
  const job = useJob();
  const [label, setLabel] = useState('');
  const [a, setA] = useState(null);
  const [b, setB] = useState(null);
  const items = list.data || [];
  useEffect(() => {
    if (items.length >= 2 && (!a || !b)) { setA(items[items.length - 2].id); setB(items[items.length - 1].id); }
  }, [items.length]);

  const capture = () => act(async () => {
    await job.run('insights.baseline.capture', { label: label || t('Ölçüm', 'Measurement') + ' ' + (items.length + 1) });
    setLabel('');
    list.reload();
  }, t('Ölçüm kaydedildi', 'Measurement saved'));

  const A = items.find((x) => x.id === a), B = items.find((x) => x.id === b);
  const delta = (va, vb, fmt, better) => {
    if (va == null || vb == null) return '–';
    const d = vb - va;
    if (Math.abs(d) < 1e-9) return html`<span class="dim">=</span>`;
    const good = d * better > 0;
    return html`<span class=${good ? 'good' : 'bad'}>${d > 0 ? '+' : '−'}${fmt(Math.abs(d))}</span>`;
  };

  return html`<div class="col gap-l">
    <${Banner} tone="info" icon="scale">${t('Bir optimizasyondan önce ve sonra birer ölçüm alın; farkı tahminle değil sayılarla görün. En tutarlı sonuç için ölçümü bilgisayar boştayken ve benzer koşullarda (aynı açık uygulamalar) alın.',
      'Take one measurement before and one after an optimization and see the difference in numbers, not guesses. For consistent results measure while the PC is idle and under similar conditions (same apps open).')}<//>
    <div class="card pad row wrap">
      <input class="input grow" placeholder=${t('Ölçüm adı (ör. "Temizlikten önce")', 'Name (e.g. "Before cleanup")')} value=${label} onInput=${(e) => setLabel(e.target.value)} />
      <${Btn} kind="primary" icon="target" busy=${job.running} onClick=${capture}>${t('Yeni ölçüm al (10 sn)', 'Take a measurement (10s)')}<//>
    </div>
    <${JobBar} job=${job.job} />
    ${items.length < 2 ? html`<div class="card"><${Empty} icon="scale" title=${t('Karşılaştırma için iki ölçüm gerekir', 'Two measurements are needed to compare')} text=${t(`Şu an ${items.length} ölçüm var.`, `There ${items.length === 1 ? 'is 1 measurement' : 'are ' + items.length + ' measurements'} so far.`)} /></div>` : html`
      <${Card} title=${t('Karşılaştırma', 'Comparison')} icon="scale" flush>
        <div class="tbl-wrap"><table class="tbl">
          <thead><tr>
            <th>${t('Ölçüt', 'Metric')}</th>
            ${[[a, setA], [b, setB]].map(([v, set], i) => html`<th key=${i} class="r"><select class="select sm" value=${v} onChange=${(e) => set(e.target.value)}>
              ${items.map((x) => html`<option key=${x.id} value=${x.id}>${x.label} · ${dateTime(x.time)}</option>`)}</select></th>`)}
            <th class="r">${t('Fark', 'Change')}</th>
          </tr></thead>
          <tbody>
            ${A && B && BASE_ROWS.map(([key, tr, en, fmt, better]) => html`<tr key=${key}><td>${t(tr, en)}</td><td class="r">${fmt(A[key])}</td><td class="r">${fmt(B[key])}</td><td class="r b">${delta(A[key], B[key], fmt, better)}</td></tr>`)}
            ${A && B && Object.keys({ ...A.extra, ...B.extra }).map((key) => {
              const [tr, en] = EXTRA_LABELS[key] || [key, key];
              const fmt = key.endsWith('Bytes') ? bytes : (v) => num(v);
              return html`<tr key=${key}><td>${t(tr, en)}</td><td class="r">${A.extra[key] != null ? fmt(A.extra[key]) : '–'}</td><td class="r">${B.extra[key] != null ? fmt(B.extra[key]) : '–'}</td><td class="r b">${delta(A.extra[key], B.extra[key], fmt, -1)}</td></tr>`;
            })}
          </tbody>
        </table></div>
      <//>`}
    ${items.length > 0 && html`<${Card} title=${t('Kayıtlı ölçümler', 'Saved measurements')} icon="list" flush>
      ${[...items].reverse().map((x) => html`<div key=${x.id} class="item">
        <${Icon} name="target" size="16" class="dim" />
        <div class="grow"><div class="t">${x.label}</div><div class="s">${dateTime(x.time)} · ${t('çalışma süresi', 'uptime')} ${duration(x.uptimeSeconds)}</div></div>
        <span class="num muted small">${pct(x.cpuIdle, 1)} CPU · ${bytes(x.memoryUsed)}</span>
        <${Btn} size="sm" kind="ghost" icon="trash" onClick=${() => call('insights.baseline.delete', { id: x.id }).then(list.reload)} />
      </div>`)}
    <//>`}
  </div>`;
}

export default function Insights({ sub }) {
  const [tab, setTab] = useState(TABS.includes(sub) ? sub : 'findings');
  useEffect(() => { if (TABS.includes(sub)) setTab(sub); }, [sub]);
  return html`<${Page} title=${t('Performans Zekâsı', 'Performance Intelligence')}
    desc=${t('OptiCore\'un merkezi: ölçer, sorunu kanıtıyla gösterir, ne yapılacağını ve bunun neyi değiştireceğini söyler.', 'The core of OptiCore: it measures, shows each problem with its evidence, and says what to do and what that will change.')}>
    <div class="mb"><${Tabs} value=${tab} onChange=${(id) => { setTab(id); go('insights/' + id); }} items=${[
      { id: 'findings', label: t('Bulgular', 'Findings'), icon: 'bulb' },
      { id: 'live', label: t('Canlı teşhis', 'Live diagnosis'), icon: 'bolt' },
      { id: 'history', label: t('Geçmiş', 'History'), icon: 'trend' },
      { id: 'compare', label: t('Önce / Sonra', 'Before / After'), icon: 'scale' },
    ]} /></div>
    ${tab === 'findings' ? html`<${Findings} />` : tab === 'live' ? html`<${Live} autostart=${sub === 'live'} />` : tab === 'history' ? html`<${History} />` : html`<${Compare} />`}
  <//>`;
}
