// Gaming: library, per-game profiles, live FPS and frame times, session history and bottleneck analysis.
import { html, useState, useEffect, useRpc, call, act, t, cx, num, pct, bytes, ago, dateTime, duration, temp, go, app, useStore } from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Icon, Empty, Loading, Banner, Async, Stat, FileIcon, Switch, Select, Modal, ElevationHint, KV } from '../lib/ui.js';
import { LineChart, BarList } from '../lib/charts.js';

const BOTTLENECK = {
  gpu: ['Ekran kartı sınırında', 'GPU bound', 'gpu', 'info'], cpu: ['İşlemci sınırında', 'CPU bound', 'cpu', 'warn'], 'cpu-thread': ['Tek çekirdek sınırında', 'Single-thread bound', 'cpu', 'warn'],
  memory: ['Bellek yetersiz', 'Out of memory', 'ram', 'bad'], thermal: ['Isıl kısılma', 'Thermal throttling', 'flame', 'bad'], capped: ['Kare hızı sınırlı', 'Frame rate capped', 'target', 'good'],
  balanced: ['Dengeli', 'Balanced', 'scale', 'good'], unknown: ['Belirsiz', 'Unknown', 'info', null],
};

function FrameGraph({ frames, height = 150 }) {
  if (!frames || frames.length < 2) return html`<div class="muted small">${t('Kare verisi bekleniyor…', 'Waiting for frame data…')}</div>`;
  const max = Math.max(33.4, ...frames) * 1.1;
  return html`<${LineChart} height=${height} area=${false} ticks=${3} max=${max} format=${(v) => num(v, 1) + ' ms'} series=${[{ label: t('Kare süresi', 'Frame time'), data: frames, color: '#22d3ee' }]} />`;
}

function LivePanel() {
  const live = useRpc('gaming.live', {}, { interval: 1000 });
  const info = useStore(app, (s) => s.info);
  if (live.data === undefined) return html`<${Loading} />`;
  const l = live.data;
  if (!l.active) return html`<div class="col gap-l">
    <div class="card"><${Empty} icon="gamepad" title=${t('Şu an oyun çalışmıyor', 'No game is running')} text=${t('Kütüphanedeki bir oyun ön plana geldiğinde OptiCore oturumu otomatik başlatır: FPS, kare süreleri, işlemci/ekran kartı yükü ve sıcaklık kaydedilir; oyun kapanınca özet ve darboğaz analizi hazırlanır.',
      'When a game from the library comes to the foreground, OptiCore starts a session automatically: FPS, frame times, CPU/GPU load and temperature are recorded; when the game closes a summary and bottleneck analysis are prepared.')} /></div>
    ${!info.elevated && html`<${ElevationHint}>${t('FPS ve kare süresi ölçümü Windows\'un olay izleme altyapısını (ETW) kullanır ve yönetici yetkisi ister. Yetki olmadan da oturumlar işlemci, ekran kartı ve sıcaklık verileriyle kaydedilir.',
      'FPS and frame time measurement uses Windows\' event tracing (ETW) and needs administrator rights. Without it sessions are still recorded with CPU, GPU and temperature data.')}<//>`}
  </div>`;
  const b = BOTTLENECK[l.bottleneck] || BOTTLENECK.unknown;
  const tl = l.timeline || [];
  return html`<div class="col gap-l">
    <div class="card pad row gap-l wrap">
      <div class="finding-ico" style=${{ width: '50px', height: '50px', borderRadius: '14px', background: 'var(--accent-soft)', color: 'var(--accent)' }}><${Icon} name="gamepad" size="24" /></div>
      <div class="grow"><div class="up">${t('Şu an oynanıyor', 'Now playing')} · ${duration((Date.now() - new Date(l.start)) / 1000)}</div><div style=${{ font: '650 21px var(--display)' }}>${l.game}</div>
        ${l.profileApplied && html`<${Chip} tone="acc" icon="sliders">${t('Profil uygulandı', 'Profile applied')}<//>`}</div>
      <div class="row gap-l">
        <${Stat} label="FPS" value=${l.fpsMeasured ? num(l.fps || 0) : '–'} sub=${l.fpsMeasured ? null : t('yönetici gerekir', 'needs admin')} />
        <${Stat} label=${t('%1 düşük', '1% low')} value=${l.stats ? num(l.stats.low1) : '–'} />
        <${Stat} label=${t('Takılma', 'Stutters')} value=${l.stats ? num(l.stats.stutters) : '–'} />
      </div>
    </div>
    <${Banner} tone=${b[3] || 'info'} icon=${b[2]}><b>${t(b[0], b[1])}</b>${l.verdict ? ' — ' + l.verdict : ''}<//>
    ${l.fpsMeasured && html`<${Card} title=${t('Kare süreleri', 'Frame times')} icon="activity" sub=${t('son kareler; düz ve düşük çizgi akıcı demektir', 'latest frames; flat and low means smooth')}><${FrameGraph} frames=${l.frametimes} /><//>`}
    <${Card} title=${t('İşlemci, ekran kartı ve FPS', 'CPU, GPU and FPS')} icon="chart" sub=${t('son 4 dakika', 'last 4 minutes')}>
      <${LineChart} height=${190} area=${false} series=${[
        { label: 'GPU %', data: tl.map((p) => p.gpu), color: '#34d399' }, { label: t('CPU (en yoğun çekirdek) %', 'CPU (busiest core) %'), data: tl.map((p) => p.maxCore), color: '#fbbf24' },
        { label: 'CPU %', data: tl.map((p) => p.cpu), color: '#7c6cff' }, ...(l.fpsMeasured ? [{ label: 'FPS', data: tl.map((p) => p.fps), color: '#22d3ee' }] : []),
      ]} />
    <//>
    <div><${Btn} kind="ghost" icon="stop" onClick=${() => call('gaming.endSession').then(live.reload)}>${t('Oturumu şimdi bitir', 'End the session now')}<//></div>
  </div>`;
}

function ProfileEditor({ game, profile, onClose, onSaved }) {
  const plans = useRpc('power.plans');
  const [p, setP] = useState(profile || { gameId: game.id, enabled: true, powerPlan: 'keep', priority: 'above', throttle: [], throttleBrowsers: true });
  const [extra, setExtra] = useState((profile?.throttle || []).join(', '));
  const save = () => act(() => call('gaming.saveProfile', { profile: { ...p, throttle: extra.split(',').map((x) => x.trim()).filter(Boolean) } }), t('Profil kaydedildi', 'Profile saved')).then((r) => r && onSaved());
  return html`<${Modal} title=${t(`Oyun profili: ${game.name}`, `Game profile: ${game.name}`)} onClose=${onClose}
    footer=${html`<${Btn} kind="ghost" onClick=${onClose}>${t('Vazgeç', 'Cancel')}<//><${Btn} kind="primary" onClick=${save}>${t('Kaydet', 'Save')}<//>`}>
    <div class="col">
      <div class="small muted">${t('Oyun açıldığında uygulanır, kapandığında her şey otomatik olarak eski hâline döner.', 'Applied when the game starts; everything returns to how it was when the game closes.')}</div>
      <div class="row"><span class="grow">${t('Profil etkin', 'Profile enabled')}</span><${Switch} on=${p.enabled} onChange=${(enabled) => setP({ ...p, enabled })} /></div>
      <div class="field"><label>${t('Güç planı', 'Power plan')}</label><${Select} value=${p.powerPlan} onChange=${(powerPlan) => setP({ ...p, powerPlan })}
        options=${[{ value: 'keep', label: t('Değiştirme', 'Do not change') }, ...((plans.data && plans.data.plans) || []).map((x) => ({ value: x.id, label: x.name }))]} /></div>
      <div class="field"><label>${t('Oyunun işlemci önceliği', 'Game CPU priority')}</label><${Select} value=${p.priority} onChange=${(priority) => setP({ ...p, priority })}
        options=${[{ value: 'normal', label: t('Normal', 'Normal') }, { value: 'above', label: t('Normalin üstü (önerilen)', 'Above normal (recommended)') }, { value: 'high', label: t('Yüksek', 'High') }]} /></div>
      <div class="row"><div class="grow"><div>${t('Tarayıcıları verimlilik moduna al', 'Put browsers into efficiency mode')}</div><div class="tiny dim">${t('Arka plandaki tarayıcı sekmeleri oyundan işlemci çalmaz.', 'Background browser tabs stop stealing CPU from the game.')}</div></div><${Switch} on=${p.throttleBrowsers} onChange=${(throttleBrowsers) => setP({ ...p, throttleBrowsers })} /></div>
      <div class="field"><label>${t('Ayrıca kısılacak programlar (virgülle)', 'Also throttle these programs (comma separated)')}</label><input class="input" placeholder="Discord.exe, OneDrive.exe" value=${extra} onInput=${(e) => setExtra(e.target.value)} /></div>
    </div>
  <//>`;
}

function Library() {
  const lib = useRpc('gaming.library');
  const profiles = useRpc('gaming.profiles');
  const [edit, setEdit] = useState(null);
  const [tips, setTips] = useState(null);
  const [custom, setCustom] = useState('');
  return html`<${Async} state=${lib}>${(games) => html`<div class="col gap-l">
    ${games.length === 0 && html`<div class="card"><${Empty} icon="gamepad" title=${t('Oyun bulunamadı', 'No games found')} text=${t('Steam, Epic, GOG ve Xbox kütüphaneleri tarandı. Başka bir oyunu aşağıdan ekleyebilirsiniz.', 'Steam, Epic, GOG and Xbox libraries were scanned. You can add another game below.')} /></div>`}
    <div class="grid auto-lg">${games.map((g) => {
      const prof = (profiles.data || []).find((p) => p.gameId === g.id);
      return html`<div key=${g.id} class="card pad col">
        <div class="row"><${FileIcon} path=${g.exe} large fallback="gamepad" /><div class="grow" style=${{ minWidth: 0 }}><div class="b ell">${g.name}</div>
          <div class="tiny dim">${g.store.toUpperCase()} · ${bytes(g.sizeBytes)}${g.lastPlayed ? ' · ' + t('son oturum ', 'last session ') + ago(g.lastPlayed) : ''}</div></div></div>
        ${g.known && html`<div class="small muted">${g.known.note}</div>`}
        <div class="row wrap gap-s">
          <${Btn} size="sm" kind="primary" icon="play" onClick=${() => act(() => call('gaming.launch', { id: g.id }))}>${t('Başlat', 'Launch')}<//>
          <${Btn} size="sm" icon="sliders" onClick=${() => setEdit({ game: g, profile: prof })}>${prof && prof.enabled ? t('Profil açık', 'Profile on') : t('Profil', 'Profile')}<//>
          ${g.known && html`<${Btn} size="sm" icon="bulb" onClick=${() => setTips(g)}>${t('İpuçları', 'Tips')}<//>`}
          <${Btn} size="sm" kind="ghost" icon="history" onClick=${() => go('gaming/sessions')} />
        </div>
      </div>`;
    })}</div>
    <div class="card pad row"><input class="input grow" placeholder=${t('Listede olmayan bir oyunun .exe yolu', 'Path to the .exe of a game not listed')} value=${custom} onInput=${(e) => setCustom(e.target.value)} />
      <${Btn} icon="plus" disabled=${!custom.trim()} onClick=${() => act(() => call('gaming.addCustom', { exe: custom.trim() })).then((r) => { if (r) { setCustom(''); lib.setData(r); } })}>${t('Ekle', 'Add')}<//></div>
    ${edit && html`<${ProfileEditor} game=${edit.game} profile=${edit.profile} onClose=${() => setEdit(null)} onSaved=${() => { setEdit(null); profiles.reload(); }} />`}
    ${tips && html`<${Modal} title=${tips.name} onClose=${() => setTips(null)}><div class="col"><div class="muted">${tips.known.note}</div><ol class="steps small">${tips.known.tips.map((x, i) => html`<li key=${i}>${x}</li>`)}</ol></div><//>`}
  </div>`}<//>`;
}

function SessionView({ id, onClose }) {
  const s = useRpc('gaming.session', { id }, { deps: [id] });
  return html`<${Modal} wide title=${s.data ? s.data.game + ' · ' + dateTime(s.data.start) : '…'} onClose=${onClose}>
    <${Async} state=${s}>${(x) => {
      const b = BOTTLENECK[x.bottleneck] || BOTTLENECK.unknown;
      const f = x.frames;
      return html`<div class="col gap-l">
        <div class="grid c4">
          <${Stat} label=${t('Süre', 'Duration')} value=${duration(x.seconds)} />
          <${Stat} label=${t('Ortalama FPS', 'Average FPS')} value=${f ? num(f.avgFps) : '–'} />
          <${Stat} label=${t('%1 / %0,1 düşük', '1% / 0.1% low')} value=${f ? `${num(f.low1)} / ${num(f.low01)}` : '–'} />
          <${Stat} label=${t('En yüksek sıcaklık', 'Max temperature')} value=${temp(x.maxTemp)} />
        </div>
        <${Banner} tone=${b[3] || 'info'} icon=${b[2]}><b>${t(b[0], b[1])}</b>${x.verdict ? ' — ' + x.verdict : ''}<//>
        ${f && html`<div class="grid c2">
          <${KV} rows=${[[t('Ortanca kare süresi', 'Median frame time'), num(f.medianMs, 1) + ' ms'], [t('%99 kare süresi', '99th percentile'), num(f.p99Ms, 1) + ' ms'], [t('En uzun kare', 'Longest frame'), num(f.maxMs, 1) + ' ms'], [t('Takılma sayısı', 'Stutters'), num(f.stutters)], [t('Toplam kare', 'Total frames'), num(f.frames)]]} />
          <div><div class="tiny dim">${t('Kare süresi dağılımı (4 ms aralıklar)', 'Frame time distribution (4 ms buckets)')}</div>
            <${BarList} items=${f.histogram.map((n, i) => ({ key: i, label: i === 11 ? '44+ ms' : `${i * 4}–${i * 4 + 4} ms`, value: n, right: pct((100 * n) / f.frames, 1) })).filter((x) => x.value > 0)} /></div>
        </div>`}
        <${LineChart} height=${190} area=${false} labels=${x.timeline.map((p) => duration(p.t))} series=${[
          { label: 'GPU %', data: x.timeline.map((p) => p.gpu), color: '#34d399' }, { label: t('CPU en yoğun çekirdek %', 'CPU busiest core %'), data: x.timeline.map((p) => p.maxCore), color: '#fbbf24' },
          ...(x.fpsMeasured ? [{ label: 'FPS', data: x.timeline.map((p) => p.fps), color: '#22d3ee' }] : []),
        ]} />
        ${x.background.length > 0 && html`<div><div class="up mb">${t('Oyun sırasında arka planda çalışanlar', 'Running in the background during the game')}</div>
          <${BarList} items=${x.background.map((bg) => ({ label: bg.name, value: bg.cpu, right: pct(bg.cpu, 1) + ' · ' + bytes(bg.memory) }))} /></div>`}
      </div>`;
    }}<//>
  <//>`;
}

function Sessions() {
  const sessions = useRpc('gaming.sessions');
  const [open, setOpen] = useState(null);
  return html`<${Async} state=${sessions}>${(list) => html`<div class="col gap-l">
    ${list.length === 0 ? html`<div class="card"><${Empty} icon="history" title=${t('Henüz oyun oturumu yok', 'No game sessions yet')} text=${t('30 saniyeden uzun her oyun oturumu burada listelenir. Aynı oyunun iki oturumunu karşılaştırarak bir ayarın ya da optimizasyonun etkisini görebilirsiniz.', 'Every game session longer than 30 seconds is listed here. Compare two sessions of the same game to see the effect of a setting or optimization.')} /></div>` : html`<div class="card">
      ${list.map((s) => { const b = BOTTLENECK[s.bottleneck] || BOTTLENECK.unknown; return html`<div key=${s.id} class="item click" onClick=${() => setOpen(s.id)}>
        <${Icon} name="gamepad" size="16" class="dim" />
        <div class="grow"><div class="t">${s.game}</div><div class="s">${dateTime(s.start)} · ${duration(s.seconds)}</div></div>
        ${s.avgFps != null && html`<${Chip} tone="info">${num(s.avgFps)} FPS · ${t('%1 düşük', '1% low')} ${num(s.low1)}<//>`}
        <${Chip} tone=${b[3]}>${t(b[0], b[1])}<//>
        <${Btn} size="sm" kind="ghost" icon="trash" onClick=${(e) => { e.stopPropagation(); call('gaming.deleteSession', { id: s.id }).then(sessions.reload); }} />
      </div>`; })}</div>`}
    ${open && html`<${SessionView} id=${open} onClose=${() => setOpen(null)} />`}
  </div>`}<//>`;
}

export default function Gaming({ sub }) {
  const tabs = ['library', 'live', 'sessions'];
  const [tab, setTab] = useState(tabs.includes(sub) ? sub : 'library');
  useEffect(() => { if (tabs.includes(sub)) setTab(sub); }, [sub]);
  return html`<${Page} title=${t('Oyun', 'Gaming')} desc=${t('Oyunlarınız, oyun başına profiller, canlı FPS ve kare süreleri, oturum geçmişi ve FPS\'i neyin sınırladığının analizi.', 'Your games, per-game profiles, live FPS and frame times, session history and an analysis of what limits your frame rate.')}
    actions=${html`<${Btn} icon="sliders" kind="ghost" onClick=${() => go('tweaks')}>${t('Oyun ayarları', 'Gaming tweaks')}<//>`}>
    <div class="mb"><${Tabs} value=${tab} onChange=${setTab} items=${[{ id: 'library', label: t('Kütüphane', 'Library'), icon: 'gamepad' }, { id: 'live', label: t('Canlı', 'Live'), icon: 'activity' }, { id: 'sessions', label: t('Oturumlar', 'Sessions'), icon: 'history' }]} /></div>
    ${tab === 'library' ? html`<${Library} />` : tab === 'live' ? html`<${LivePanel} />` : html`<${Sessions} />`}
  <//>`;
}
