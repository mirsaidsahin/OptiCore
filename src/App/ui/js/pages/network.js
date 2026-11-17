// Network Optimizer: live traffic, latency and loss, DNS benchmark and switch, Wi-Fi, per-app connections, diagnosis and repair.
import { html, useState, useRpc, useJob, call, act, t, cx, num, bits, ms, pct, confirmDialog, go } from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Icon, Empty, Loading, Banner, Async, Stat, JobBar, Select, FileIcon, KV } from '../lib/ui.js';
import { LineChart, Ring, BarList } from '../lib/charts.js';

function Live() {
  const live = useRpc('metrics.live', { history: 120 }, { interval: 1000 });
  const wifi = useRpc('network.wifi', {}, { interval: 5000 });
  const adapters = useRpc('system.adapters');
  if (!live.data || !live.data.now) return html`<${Loading} lines=${6} />`;
  const n = live.data.now, h = live.data.history;
  const w = wifi.data;
  const up = (adapters.data || []).find((a) => a.status === 'up' && a.gateway);
  return html`<div class="col gap-l">
    <div class="grid c4">
      <div class="card pad"><${Stat} label=${t('İndirme', 'Download')} value=${bits(n.netDown)} sub=${t('en yüksek ', 'peak ') + bits(Math.max(...h.down))} /></div>
      <div class="card pad"><${Stat} label=${t('Yükleme', 'Upload')} value=${bits(n.netUp)} sub=${t('en yüksek ', 'peak ') + bits(Math.max(...h.up))} /></div>
      <div class="card pad"><${Stat} label=${t('Bağlantı', 'Connection')} value=${up ? up.name : t('Yok', 'None')} sub=${up ? up.ipv4 : null} /></div>
      <div class="card pad"><${Stat} label=${t('Bağlantı hızı', 'Link speed')} value=${up && up.speedBps ? bits(up.speedBps / 8) : '–'} sub=${t('bağdaştırıcının anlaştığı hız', 'negotiated by the adapter')} /></div>
    </div>
    <${Card} title=${t('Trafik', 'Traffic')} icon="activity" sub=${t('son 2 dakika', 'last 2 minutes')}>
      <${LineChart} height=${200} format=${(v) => bits(v)} series=${[{ label: t('İndirme', 'Download'), data: h.down, color: '#60a5fa' }, { label: t('Yükleme', 'Upload'), data: h.up, color: '#34d399' }]} />
    <//>
    ${w && html`<${Card} title=${'Wi-Fi: ' + w.ssid} icon="wifi">
      <div class="row gap-l wrap">
        <${Ring} value=${w.quality} size=${100} stroke=${9} color=${w.quality >= 70 ? 'var(--good)' : w.quality >= 45 ? 'var(--warn)' : 'var(--bad)'}><b class="num" style=${{ fontSize: '20px' }}>${w.quality}</b><span class="tiny dim">${w.rssi} dBm</span><//>
        <div class="grow"><${KV} rows=${[[t('Standart', 'Standard'), w.phy], [t('Kanal / bant', 'Channel / band'), w.channel ? `${w.channel} · ${w.band}` : null], [t('Bağlantı hızı', 'Link rate'), `↓ ${num(w.rxMbps)} / ↑ ${num(w.txMbps)} Mbps`], [t('Güvenlik', 'Security'), html`<${Chip} tone=${w.open ? 'bad' : 'good'}>${w.security}<//>`]]} /></div>
      </div>
      ${w.quality < 50 && html`<div class="mt"><${Banner} tone="warn">${t('Sinyal zayıf. Modeme yaklaşmak, araya giren duvarları azaltmak ya da 5 GHz yerine 2,4 GHz ağı kullanmak (uzak mesafede) bağlantıyı iyileştirir.', 'The signal is weak. Moving closer to the router, reducing walls in between or using the 2.4 GHz network instead of 5 GHz (at long range) improves the connection.')}<//></div>`}
      ${w.open && html`<div class="mt"><${Banner} tone="bad">${t('Bu ağ şifrelenmemiş: aynı ağdaki herkes trafiğinizi görebilir. Hassas işler için VPN kullanın.', 'This network is not encrypted: anyone on it can see your traffic. Use a VPN for sensitive work.')}<//></div>`}
    <//>`}
  </div>`;
}

function Latency() {
  const job = useJob();
  const [res, setRes] = useState(null);
  const [target, setTarget] = useState('');
  const run = () => act(async () => setRes(await job.run('network.ping', { target: target.trim() || null, count: 25 })));
  const verdict = (r) => (r.avg == null ? ['bad', t('Ulaşılamıyor', 'Unreachable')] : r.loss > 2 ? ['bad', t('Paket kaybı', 'Packet loss')] : r.avg > 80 || r.jitter > 20 ? ['warn', t('Oyun için yüksek', 'High for gaming')] : ['good', t('İyi', 'Good')]);
  return html`<div class="col gap-l">
    <div class="card pad row wrap">
      <input class="input grow" placeholder=${t('Hedef (boş: modem, Cloudflare, Google, Quad9) — ör. bir oyun sunucusu adresi', 'Target (empty: router, Cloudflare, Google, Quad9) — e.g. a game server address')} value=${target} onInput=${(e) => setTarget(e.target.value)} />
      <${Btn} kind="primary" icon="activity" busy=${job.running} onClick=${run}>${t('Ölç (25 paket)', 'Measure (25 packets)')}<//>
    </div>
    <${JobBar} job=${job.job} onCancel=${job.cancel} />
    ${!res && !job.running && html`<div class="card"><${Empty} icon="activity" title=${t('Gecikme, titreşim ve paket kaybı', 'Latency, jitter and packet loss')} text=${t('Modeme olan gecikme yüksekse sorun evinizin içindedir (Wi-Fi); modem iyi ama internet kötüyse sorun sağlayıcıdadır.', 'If latency to the router is high the problem is inside your home (Wi-Fi); if the router is fine but the internet is bad, it is your provider.')} /></div>`}
    ${res && html`<div class="grid c2">${res.results.map((r) => {
      const [tn, label] = verdict(r);
      return html`<${Card} key=${r.host} title=${r.label} sub=${r.host} actions=${html`<${Chip} tone=${tn}>${label}<//>`}>
        <div class="grid c4"><${Stat} label=${t('Ortalama', 'Average')} value=${ms(r.avg)} /><${Stat} label=${t('En iyi / kötü', 'Best / worst')} value=${r.min != null ? `${num(r.min)}/${num(r.max)}` : '–'} /><${Stat} label=${t('Titreşim', 'Jitter')} value=${ms(r.jitter, 1)} /><${Stat} label=${t('Kayıp', 'Loss')} value=${pct(r.loss)} tone=${r.loss > 2 ? 'bad' : null} /></div>
        <div class="mt"><${LineChart} height=${110} ticks=${2} area=${false} format=${(v) => num(v) + ' ms'} series=${[{ label: 'ms', data: r.samples.map((s) => (s == null ? 0 : s)), color: '#22d3ee' }]} /></div>
      <//>`;
    })}</div>`}
  </div>`;
}

function DnsTab() {
  const job = useJob();
  const adapters = useRpc('system.adapters');
  const providers = useRpc('network.dnsProviders');
  const [res, setRes] = useState(null);
  const [adapter, setAdapter] = useState(null);
  const [provider, setProvider] = useState('cloudflare');
  const ups = (adapters.data || []).filter((a) => a.status === 'up' && a.physical);
  const chosen = adapter || (ups[0] && ups[0].id);
  const apply = () => confirmDialog({
    title: t('DNS sunucusu değiştirilsin mi?', 'Change the DNS server?'),
    body: t('Değişiklik Geri Alma Merkezi\'ne kaydedilir. Okul, iş yeri ya da otel ağlarında özel DNS gerekebilir; o durumda "Otomatik (DHCP)" seçin.', 'The change is recorded in the Undo Center. School, work or hotel networks may require their own DNS; in that case choose "Automatic (DHCP)".'),
    confirm: t('Uygula', 'Apply'),
  }).then((ok) => ok && act(() => call('network.setDns', { adapter: chosen, provider }), (r) => (r.simulated ? t('Simülasyon: DNS değiştirilmedi', 'Simulation: DNS was not changed') : t('DNS değiştirildi', 'DNS changed'))).then((r) => r && adapters.reload()));
  return html`<div class="col gap-l">
    <div class="card pad row wrap"><div class="grow"><div class="b">${t('DNS karşılaştırması', 'DNS benchmark')}</div><div class="small muted">${t('Her sunucuya 10 popüler alan adı ikişer kez doğrudan sorulur; işletim sistemi önbelleği devre dışıdır. Ortanca süre, günlük kullanımda hissedeceğiniz hızdır.', 'Each server is asked for 10 popular domains twice, directly, bypassing the OS cache. The median is the speed you will feel day to day.')}</div></div>
      <${Btn} kind="primary" icon="gauge" busy=${job.running} onClick=${() => act(async () => setRes(await job.run('network.dnsBenchmark')))}>${t('Karşılaştır', 'Benchmark')}<//></div>
    <${JobBar} job=${job.job} onCancel=${job.cancel} />
    ${res && html`<${Card} title=${t('Sonuçlar', 'Results')} icon="chart">
      <${BarList} items=${res.results.filter((r) => r.median != null).map((r, i) => ({ key: r.ip, label: r.name, sub: r.note, value: r.median, right: ms(r.median, 1) + (r.failed ? ' · ' + r.failed + ' ' + t('hata', 'errors') : ''), color: i === 0 ? 'var(--good)' : r.id === 'current' ? 'var(--warn)' : 'var(--accent)' }))} />
      <div class="tiny dim mt">${t('Fark 10 ms\'nin altındaysa pratikte hissedilmez; DNS değiştirmenin asıl faydası gizlilik ve filtrelemedir.', 'Differences under 10 ms are not noticeable in practice; the real benefit of switching DNS is privacy and filtering.')}</div>
    <//>`}
    <${Card} title=${t('DNS sunucusunu değiştir', 'Change the DNS server')} icon="globe">
      ${ups.length === 0 ? html`<div class="muted">${t('Bağlı bir bağdaştırıcı yok.', 'No connected adapter.')}</div>` : html`<div class="row wrap">
        <${Select} value=${chosen} onChange=${setAdapter} options=${ups.map((a) => ({ value: a.id, label: `${a.name} (${a.dns.join(', ') || 'DHCP'})` }))} />
        <${Select} value=${provider} onChange=${setProvider} options=${[{ value: 'auto', label: t('Otomatik (DHCP)', 'Automatic (DHCP)') }, ...(providers.data || []).map((p) => ({ value: p.id, label: `${p.name} — ${p.primary}` }))]} />
        <${Btn} kind="primary" onClick=${apply}>${t('Uygula', 'Apply')}<//>
      </div>`}
    <//>
  </div>`;
}

function AppsTab() {
  const apps = useRpc('network.apps', {}, { interval: 5000 });
  return html`<${Async} state=${apps}>${(list) => html`<div class="col gap-l">
    <${Banner} tone="info">${t('İnternete açık bağlantısı olan uygulamalar ve kaç farklı uzak sunucuyla konuştukları. Ayrıntılı bağlantı listesi İşlemler > Bağlantılar sekmesindedir.', 'Apps with open internet connections and how many distinct remote servers they talk to. The full connection list is under Processes > Connections.')}<//>
    <div class="card">${list.map((a) => html`<div key=${a.name} class="item"><${FileIcon} path=${a.path} /><div class="grow"><div class="t">${a.name}</div><div class="s">${a.pids.length} ${t('işlem', 'processes')}</div></div>
      <${Chip}>${a.connections} ${t('bağlantı', 'connections')}<//><${Chip} tone="info">${a.remotes} ${t('sunucu', 'servers')}<//></div>`)}</div>
    <div><${Btn} icon="globe" onClick=${() => go('processes/connections')}>${t('Tüm bağlantılar', 'All connections')}<//></div>
  </div>`}<//>`;
}

function Diagnose() {
  const job = useJob();
  const [res, setRes] = useState(null);
  const repair = (action, title, body, admin) => confirmDialog({ title, body, confirm: t('Çalıştır', 'Run') })
    .then((ok) => ok && act(() => call('network.repair', { action }), (r) => (r.simulated ? t('Simülasyon: araç çalıştırılmadı', 'Simulation: the tool did not run') : title)));
  return html`<div class="col gap-l">
    <div class="card pad row"><div class="grow"><div class="b">${t('İnternet bağlantısı teşhisi', 'Internet connection diagnosis')}</div><div class="small muted">${t('Bağdaştırıcıdan web\'e kadar her halkayı sırayla dener ve kopukluğun nerede olduğunu söyler.', 'Tests every link from the adapter to the web in turn and tells you where the chain breaks.')}</div></div>
      <${Btn} kind="primary" icon="search" busy=${job.running} onClick=${() => act(async () => setRes(await job.run('network.diagnose')))}>${t('Teşhis et', 'Diagnose')}<//></div>
    <${JobBar} job=${job.job} />
    ${res && html`<div class="card">${res.steps.map((s, i) => html`<div key=${s.id} class=${cx('finding', s.ok ? 'good' : 'critical')}>
      <div class="finding-ico"><${Icon} name=${s.ok ? 'check' : 'x'} /></div><div class="grow"><h4>${i + 1}. ${s.title}</h4><p>${s.detail}</p>${s.fix && html`<p class="acc">${s.fix}</p>`}</div></div>`)}</div>`}
    <${Card} title=${t('Onarım araçları', 'Repair tools')} icon="wrench" flush>
      ${[
        ['flushdns', t('DNS önbelleğini temizle', 'Flush the DNS cache'), t('Siteler açılmıyor ya da eski adrese gidiyorsa.', 'When sites do not open or go to an old address.'), false],
        ['renew', t('IP adresini yenile', 'Renew the IP address'), t('Modemden yeni bir adres ister. Bağlantı birkaç saniye kopar.', 'Asks the router for a new address. The connection drops for a few seconds.'), false],
        ['winsock', t('Winsock\'u sıfırla', 'Reset Winsock'), t('Ağ yığınını bozan VPN ya da güvenlik yazılımlarından sonra. Yeniden başlatma gerekir.', 'After VPN or security software that broke the network stack. Needs a restart.'), true],
        ['tcpip', t('TCP/IP\'yi sıfırla', 'Reset TCP/IP'), t('Son çare: IP ayarlarını varsayılana döndürür; elle girilmiş sabit IP\'ler silinir. Yeniden başlatma gerekir.', 'Last resort: returns IP settings to defaults; manually set static IPs are lost. Needs a restart.'), true],
      ].map(([id, title, desc, admin]) => html`<div key=${id} class="item"><div class="grow"><div class="t">${title} ${admin && html`<${Chip} tone="warn" icon="shield">${t('Yönetici', 'Admin')}<//>`}</div><div class="s">${desc}</div></div>
        <${Btn} size="sm" onClick=${() => repair(id, title, desc, admin)}>${t('Çalıştır', 'Run')}<//></div>`)}
    <//>
  </div>`;
}

export default function Network({ sub }) {
  const tabs = ['live', 'latency', 'dns', 'apps', 'diagnose'];
  const [tab, setTab] = useState(tabs.includes(sub) ? sub : 'live');
  return html`<${Page} title=${t('Ağ', 'Network')} desc=${t('Canlı trafik, gecikme ve paket kaybı, DNS, Wi-Fi sinyali ve bağlantı sorunlarının teşhisi.', 'Live traffic, latency and packet loss, DNS, Wi-Fi signal and diagnosis of connection problems.')}>
    <div class="mb"><${Tabs} value=${tab} onChange=${setTab} items=${[
      { id: 'live', label: t('Canlı', 'Live'), icon: 'activity' }, { id: 'latency', label: t('Gecikme testi', 'Latency test'), icon: 'clock' },
      { id: 'dns', label: 'DNS', icon: 'globe' }, { id: 'apps', label: t('Uygulamalar', 'Apps'), icon: 'package' }, { id: 'diagnose', label: t('Teşhis ve onarım', 'Diagnose & repair'), icon: 'wrench' },
    ]} /></div>
    ${tab === 'live' ? html`<${Live} />` : tab === 'latency' ? html`<${Latency} />` : tab === 'dns' ? html`<${DnsTab} />` : tab === 'apps' ? html`<${AppsTab} />` : html`<${Diagnose} />`}
  <//>`;
}
