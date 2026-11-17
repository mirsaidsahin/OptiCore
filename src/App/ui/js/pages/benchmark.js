// Hardware Benchmark: CPU, memory, disk, GPU (WebGL), internet and a sustained-load stability test, with history and comparison.
import { html, useState, useRef, useRpc, useJob, call, act, t, cx, num, dateTime, ago, toast } from '../lib/core.js';
import { Page, Card, Btn, Chip, Icon, Empty, Banner, Async, Stat, JobBar, Select, Table, KV, openUrl } from '../lib/ui.js';
import { LineChart, BarList } from '../lib/charts.js';

const TESTS = [
  { id: 'cpu', icon: 'cpu', title: ['İşlemci', 'Processor'], desc: ['Tek ve çok çekirdekli tamsayı, şifreleme ve bellek erişimi karışımı. ~20 sn.', 'Single and multi-core mix of integer math, cryptography and memory access. ~20 s.'] },
  { id: 'memory', icon: 'ram', title: ['Bellek', 'Memory'], desc: ['Kopyalama ve okuma bant genişliği, rastgele erişim gecikmesi. ~10 sn.', 'Copy and read bandwidth, random access latency. ~10 s.'] },
  { id: 'disk', icon: 'disk', title: ['Disk', 'Disk'], desc: ['Sıralı ve rastgele 4K okuma/yazma, önbelleksiz. 1 GB geçici dosya yazar, sonra siler. ~30 sn.', 'Sequential and random 4K read/write, uncached. Writes a 1 GB temporary file, then deletes it. ~30 s.'] },
  { id: 'gpu', icon: 'gpu', title: ['Ekran kartı', 'Graphics'], desc: ['WebGL üzerinde ağır bir gölgelendirici çizer. ~10 sn.', 'Draws a heavy shader through WebGL. ~10 s.'] },
  { id: 'network', icon: 'globe', title: ['İnternet', 'Internet'], desc: ['Cloudflare hız testi sunucusuyla indirme, yükleme ve gecikme. ~20 sn.', 'Download, upload and latency against Cloudflare\'s speed test server. ~20 s.'] },
  { id: 'stability', icon: 'flame', title: ['Kararlılık ve ısı', 'Stability & thermals'], desc: ['İşlemciyi dakikalarca tam yükte tutar; hız düşüşü ve ısınmayı ölçer. 98 °C\'de kendini durdurur.', 'Holds the CPU at full load for minutes; measures speed drop and heating. Stops itself at 98 °C.'] },
];

const METRIC = {
  single: ['Tek çekirdek', 'Single-core', ''], multi: ['Çok çekirdek', 'Multi-core', ''], scaling: ['Ölçeklenme', 'Scaling', '×'], avgMhz: ['Ortalama hız', 'Avg clock', ' MHz'],
  copyGBs: ['Kopyalama', 'Copy', ' GB/s'], readGBs: ['Okuma', 'Read', ' GB/s'], latencyNs: ['Gecikme', 'Latency', ' ns'],
  seqReadMBs: ['Sıralı okuma', 'Seq. read', ' MB/s'], seqWriteMBs: ['Sıralı yazma', 'Seq. write', ' MB/s'], rndReadIops: ['4K okuma', '4K read', ' IOPS'], rndWriteIops: ['4K yazma', '4K write', ' IOPS'], rndReadMBs: ['4K okuma', '4K read', ' MB/s'], latencyUs: ['Erişim süresi', 'Access time', ' µs'],
  fps: ['FPS', 'FPS', ''], gflops: ['Hesaplama', 'Compute', ' GFLOPS'],
  downMbps: ['İndirme', 'Download', ' Mbps'], upMbps: ['Yükleme', 'Upload', ' Mbps'], latencyMs: ['Gecikme', 'Latency', ' ms'], jitterMs: ['Titreşim', 'Jitter', ' ms'],
  startMhz: ['Başlangıç hızı', 'Start clock', ' MHz'], endMhz: ['Son hız', 'End clock', ' MHz'], dropPct: ['Hız düşüşü', 'Clock drop', '%'], throttledPct: ['Kısılma süresi', 'Throttled', '%'], maxTemp: ['En yüksek sıcaklık', 'Max temp', ' °C'], seconds: ['Süre', 'Duration', ' s'],
};
const metricText = (k, v) => { const m = METRIC[k] || [k, k, '']; return [t(m[0], m[1]), num(v, v < 10 && v % 1 ? 2 : v < 100 && v % 1 ? 1 : 0) + m[2]]; };

/** GPU test: a fragment shader with a fixed amount of math per pixel, drawn at 1920x1080 for a fixed time. */
async function runGpu(canvas, onProgress) {
  const W = 1920, H = 1080, ITER = 96;
  canvas.width = W; canvas.height = H;
  const gl = canvas.getContext('webgl2', { antialias: false, preserveDrawingBuffer: false, powerPreference: 'high-performance' });
  if (!gl) throw new Error(t('Bu sistemde WebGL 2 kullanılamıyor.', 'WebGL 2 is not available on this system.'));
  const vs = `#version 300 es
in vec2 p; void main(){ gl_Position = vec4(p, 0.0, 1.0); }`;
  const fs = `#version 300 es
precision highp float; uniform float t; out vec4 o;
void main(){
  vec2 uv = gl_FragCoord.xy / vec2(${W}.0, ${H}.0) * 3.0 - 1.5;
  vec2 z = uv; vec3 c = vec3(0.0);
  for (int i = 0; i < ${ITER}; i++) {
    z = vec2(z.x*z.x - z.y*z.y, 2.0*z.x*z.y) + vec2(-0.745 + 0.02*sin(t), 0.186);
    c += 0.5 + 0.5*cos(vec3(0.0,0.6,1.2) + dot(z,z) + t);
    z /= max(1.0, dot(z,z)*0.25);
  }
  o = vec4(c / float(${ITER}), 1.0);
}`;
  const sh = (type, src) => { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s); if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s)); return s; };
  const prog = gl.createProgram();
  gl.attachShader(prog, sh(gl.VERTEX_SHADER, vs)); gl.attachShader(prog, sh(gl.FRAGMENT_SHADER, fs)); gl.linkProgram(prog); gl.useProgram(prog);
  const buf = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, buf);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
  const loc = gl.getAttribLocation(prog, 'p'); gl.enableVertexAttribArray(loc); gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
  const ut = gl.getUniformLocation(prog, 't');
  gl.viewport(0, 0, W, H);
  const px = new Uint8Array(4);
  const frame = (i) => { gl.uniform1f(ut, i * 0.01); gl.drawArrays(gl.TRIANGLES, 0, 3); gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, px); };
  for (let i = 0; i < 10; i++) frame(i); // warm-up, shader compile
  let frames = 0;
  const start = performance.now(), dur = 10000;
  while (performance.now() - start < dur) {
    frame(frames++);
    if (frames % 5 === 0) { onProgress((performance.now() - start) / dur); await new Promise((r) => setTimeout(r, 0)); }
  }
  const secs = (performance.now() - start) / 1000;
  const fps = frames / secs;
  const flopsPerPixel = ITER * 40; // rough count of floating point operations per loop iteration
  const gflops = (fps * W * H * flopsPerPixel) / 1e9;
  const debug = gl.getExtension('WEBGL_debug_renderer_info');
  const label = debug ? gl.getParameter(debug.UNMASKED_RENDERER_WEBGL) : 'GPU';
  gl.getExtension('WEBGL_lose_context')?.loseContext();
  // Reference: Intel Iris Xe (80 EU, i5-1235U) runs this at 33.8 FPS = 1000 points.
  const name = String(label).replace(/^ANGLE \((.*)\)$/, '$1').split(',').slice(1, 2).join('').replace(/\s*\(0x[0-9a-f]+\).*$/i, '').replace(/\s+Direct3D.*$/i, '').trim();
  return { fps, gflops, score: Math.round((fps / 33.8) * 1000), label: name || label };
}

const TIER_TONE = ['', 'bad', 'warn', 'info', 'good', 'acc'];
const PART = { cpu: ['İşlemci', 'Processor', 'cpu'], gpu: ['Ekran kartı', 'Graphics', 'gpu'], memory: ['Bellek', 'Memory', 'ram'], storage: ['Depolama', 'Storage', 'disk'] };
const SOURCE = { measured: ['OptiCore ölçümü', 'Measured by OptiCore'], table: ['Yaklaşık değer (gömülü tablo)', 'Approximate (built-in table)'], type: ['Disk türüne göre', 'By disk type'], system: ['Sistemden', 'From the system'] };

function Tiers({ tier }) {
  return html`<div class="row gap-s" style=${{ gap: '3px' }}>${[1, 2, 3, 4, 5].map((i) => html`<i key=${i} style=${{ width: '22px', height: '7px', borderRadius: '4px', background: i <= tier ? `var(--${TIER_TONE[tier] === 'acc' ? 'accent' : TIER_TONE[tier]})` : 'var(--line)' }}></i>`)}</div>`;
}

function RankPart({ p }) {
  const [open, setOpen] = useState(false);
  const [tr, en, icon] = PART[p.id];
  const fmt = (v) => (p.unit === 'GB' ? num(v) + ' GB' : p.unit === 'MB/s' ? num(v) + ' MB/s' : num(v));
  const items = p.refs ? [...p.refs.map((r) => ({ key: r.name, label: r.name + (r.mobile ? ' ' + t('(dizüstü)', '(laptop)') : ''), value: r.value, right: fmt(r.value), color: 'var(--line-2)' })),
    { key: '__me', label: t('Bu bilgisayar', 'This PC') + ' · ' + p.name, value: p.value, right: fmt(p.value), color: 'var(--accent)' }].sort((a, b) => b.value - a.value) : [];
  return html`<div class="item" style=${{ alignItems: 'flex-start', flexDirection: 'column', gap: '6px' }}>
    <div class="row" style=${{ width: '100%' }}>
      <${Icon} name=${icon} size="17" class="acc" />
      <div class="grow" style=${{ minWidth: 0 }}><div class="t">${t(tr, en)}</div><div class="s ell">${p.name}</div></div>
      ${p.unknown ? html`<${Chip}>${t('Tabloda yok', 'Not in table')}<//>` : html`<${Tiers} tier=${p.tier} /><${Chip} tone=${TIER_TONE[p.tier]}>${p.tierLabel}<//>`}
    </div>
    <div class="small muted">${p.verdict}</div>
    <div class="row wrap gap-s">
      ${p.refs && html`<${Btn} size="sm" kind="ghost" icon=${open ? 'chevU' : 'chevD'} onClick=${() => setOpen(!open)}>${t('Diğerleriyle karşılaştır', 'Compare with others')}<//>`}
      ${p.search && html`<${Btn} size="sm" kind="ghost" icon="external" onClick=${() => openUrl(p.search)}>${t('İnternetteki test sonuçları', 'Test results online')}<//>`}
      ${p.source && html`<span class="tiny dim">${t(...SOURCE[p.source])}</span>`}
    </div>
    ${open && html`<div style=${{ width: '100%', maxWidth: '640px' }}><${BarList} items=${items} /></div>`}
  </div>`;
}

function Rank({ reloadKey }) {
  const d = useRpc('bench.rank', {}, { deps: [reloadKey] });
  return html`<${Async} state=${d}>${(r) => html`<${Card} title=${t('Bu bilgisayar ne kadar iyi?', 'How good is this PC?')} icon="star" flush
    actions=${html`<${Chip} tone=${TIER_TONE[r.overallTier]}>${t('Genel', 'Overall')}: ${r.overallLabel}<//>`}>
    <div style=${{ padding: '4px 16px 10px' }} class="small">
      <b>${t('Oyunlar', 'Games')}:</b> <span class="muted">${r.gaming}</span>
      ${r.bottleneck && html`<div class="warn mt-s">${r.bottleneck}</div>`}
    </div>
    ${r.parts.map((p) => html`<${RankPart} key=${p.id} p=${p} />`)}
    <div class="tiny dim" style=${{ padding: '8px 16px 12px' }}>${t('Karşılaştırma, yayımlanmış test ortalamalarından derlenmiş yaklaşık değerlere dayanır (±%15). İşlemci ve disk için kendi ölçümünüz varsa o kullanılır; daha kesin sonuç için aşağıdaki testleri çalıştırın.',
      'Comparisons rely on approximate figures compiled from published test averages (±15%). Your own measurement is used for the processor and disk when available; run the tests below for a more precise result.')}</div>
  <//>`}<//>`;
}

function ResultCard({ r, compare }) {
  const test = TESTS.find((x) => x.id === r.kind) || TESTS[0];
  let timeline = null;
  try { timeline = r.info && r.info.timeline ? JSON.parse(r.info.timeline) : null; } catch { /* ignore */ }
  return html`<${Card} title=${t(...test.title)} icon=${test.icon} sub=${r.label + ' · ' + dateTime(r.time)}
    actions=${html`<span class="big acc">${num(r.score)}</span>${compare && compare.score && html`<${Chip} tone=${r.score >= compare.score ? 'good' : 'bad'}>${r.score >= compare.score ? '+' : ''}${num(((r.score - compare.score) / compare.score) * 100, 1)}%<//>`}`}>
    <div class="grid c4">${Object.entries(r.metrics).map(([k, v]) => { const [l, val] = metricText(k, v); return html`<${Stat} key=${k} label=${l} value=${val} />`; })}</div>
    ${timeline && html`<div class="mt"><${LineChart} height=${150} area=${false} labels=${timeline.map((p) => p[0] + ' s')} series=${[{ label: 'MHz', data: timeline.map((p) => p[1]), color: '#7c6cff' }, ...(timeline.some((p) => p[2] > 0) ? [{ label: '°C ×10', data: timeline.map((p) => p[2] * 10), color: '#fb7185' }] : [])]} /></div>`}
    ${r.note && html`<div class="small muted mt">${r.note}</div>`}
  <//>`;
}

export default function Benchmark({ sub }) {
  const history = useRpc('bench.history');
  const volumes = useRpc('system.volumes');
  const job = useJob();
  const canvas = useRef(null);
  const [gpuProgress, setGpuProgress] = useState(null);
  const [drive, setDrive] = useState(null);
  const [minutes, setMinutes] = useState('5');
  const [last, setLast] = useState(null);
  const list = history.data || [];
  const busy = job.running || gpuProgress != null;

  const run = (kind) => act(async () => {
    let r;
    if (kind === 'gpu') {
      setGpuProgress(0);
      try { const g = await runGpu(canvas.current, setGpuProgress); r = await call('bench.saveGpu', g); } finally { setGpuProgress(null); }
    } else {
      r = await job.run('bench.run', { kind, drive: drive || undefined, minutes: +minutes });
    }
    setLast(r);
    history.reload();
  });

  const previous = (r) => list.find((x) => x.kind === r.kind && x.id !== r.id && new Date(x.time) < new Date(r.time));

  return html`<${Page} title=${t('Benchmark', 'Benchmark')} desc=${t('Sabit iş yükleriyle ölçülen donanım puanları. 1000 puan, 2022 yapımı orta sınıf bir dizüstüne (Core i5-1235U, DDR4-3200, NVMe) karşılık gelir. Puanlar yalnızca OptiCore sonuçlarıyla karşılaştırılabilir.',
    'Hardware scores measured with fixed workloads. 1000 points equals a 2022 mid-range laptop (Core i5-1235U, DDR4-3200, NVMe). Scores are only comparable with other OptiCore results.')}>
    <div class="col" style=${{ gap: '14px' }}>
      <${Rank} reloadKey=${list.length} />
      <${Banner} tone="info" icon="info">${t('En doğru sonuç için testi bilgisayar prizdeyken, diğer programlar kapalıyken çalıştırın. Bir optimizasyonun etkisini görmek için önce ve sonra aynı testi tekrarlayın.', 'For accurate results run tests on AC power with other programs closed. To see the effect of an optimization, repeat the same test before and after.')}<//>
      <div class="grid auto-lg">${TESTS.map((x) => html`<div key=${x.id} class="card pad col">
        <div class="row"><${Icon} name=${x.icon} size="18" class="acc" /><b class="grow">${t(...x.title)}</b>
          ${list.find((r) => r.kind === x.id) && html`<span class="num b" title=${t('Son sonuç', 'Latest result')}>${num([...list].filter((r) => r.kind === x.id).sort((a, b) => new Date(b.time) - new Date(a.time))[0].score)}</span>`}</div>
        <div class="small muted grow">${t(...x.desc)}</div>
        <div class="row gap-s">
          ${x.id === 'disk' && html`<${Select} class="sm" value=${drive || ''} onChange=${setDrive} options=${(volumes.data || []).filter((v) => v.kind === 'fixed').map((v) => ({ value: v.letter + ':\\', label: v.letter + ':' }))} />`}
          ${x.id === 'stability' && html`<${Select} class="sm" value=${minutes} onChange=${setMinutes} options=${[{ value: '2', label: '2 dk' }, { value: '5', label: '5 dk' }, { value: '10', label: '10 dk' }, { value: '20', label: '20 dk' }]} />`}
          <${Btn} size="sm" kind="primary" icon="play" disabled=${busy} onClick=${() => run(x.id)}>${t('Çalıştır', 'Run')}<//>
        </div>
      </div>`)}</div>
      <${JobBar} job=${job.job} onCancel=${job.cancel} />
      ${gpuProgress != null && html`<div class="card pad"><div class="b">${t('Ekran kartı testi çalışıyor…', 'Graphics test running…')}</div><div class="mt-s"><div class="bar"><i style=${{ width: gpuProgress * 100 + '%' }}></i></div></div></div>`}
      <canvas ref=${canvas} style=${{ width: '320px', height: '180px', borderRadius: '10px', display: gpuProgress != null ? 'block' : 'none' }}></canvas>
      ${last && html`<${ResultCard} r=${last} compare=${previous(last)} />`}
      <${Card} title=${t('Geçmiş', 'History')} icon="history" flush>
        ${!list.length ? html`<${Empty} icon="gauge" title=${t('Henüz test yok', 'No tests yet')} />` : html`<${Table} rows=${list} rowKey="id" initialSort=${{ id: 'time', dir: 'desc' }} columns=${[
          { id: 'kind', label: t('Test', 'Test'), sort: true, render: (r) => { const x = TESTS.find((y) => y.id === r.kind); return html`<div class="row"><${Icon} name=${x ? x.icon : 'gauge'} size="15" class="dim" />${x ? t(...x.title) : r.kind}</div>`; } },
          { id: 'label', label: t('Donanım', 'Hardware'), render: (r) => html`<span class="small muted">${r.label}</span>` },
          { id: 'score', label: t('Puan', 'Score'), num: true, sort: true, class: 'shrink', render: (r) => html`<b>${num(r.score)}</b>` },
          { id: 'time', label: t('Tarih', 'Date'), sort: (r) => +new Date(r.time), class: 'shrink', render: (r) => ago(r.time) },
          { id: 'x', label: '', class: 'shrink', render: (r) => html`<div class="row gap-s"><${Btn} size="sm" kind="ghost" icon="eye" onClick=${() => setLast(r)} /><${Btn} size="sm" kind="ghost" icon="trash" onClick=${() => call('bench.delete', { id: r.id }).then(history.reload)} /></div>` },
        ]} />`}
      <//>
    </div>
  <//>`;
}
