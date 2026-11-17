// Security & Privacy: an audit that only reads and explains; every fix opens the Windows page where you make it.
import { html, useState, useEffect, useRpc, call, act, t, cx, num, dateTime, ago, matches, go } from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Search, Icon, Empty, Loading, Banner, Async, Stat, FileIcon, Switch, Table, openUrl, KV } from '../lib/ui.js';
import { Ring } from '../lib/charts.js';

const STATUS = { pass: ['check', 'good'], info: ['info', 'info'], warn: ['alert', 'warning'], fail: ['alert', 'critical'], unknown: ['info', ''] };
const GROUPS = [['protection', 'Koruma', 'Protection'], ['system', 'Sistem', 'System'], ['accounts', 'Hesaplar', 'Accounts'], ['network', 'Ağ', 'Network']];
const RISK = { high: ['Yüksek', 'High', 'bad'], medium: ['Orta', 'Medium', 'warn'], low: ['Düşük', 'Low', null], unknown: ['Bilinmiyor', 'Unknown', 'info'], none: ['Yalnızca yerel', 'Local only', 'good'] };

function Overview() {
  const d = useRpc('security.checks');
  return html`<${Async} state=${d} lines=${8}>${(r) => html`<div class="col gap-l">
    <div class="card pad row gap-l wrap">
      <${Ring} value=${r.score} size=${110} stroke=${10} color=${r.failed ? 'var(--bad)' : r.warnings ? 'var(--warn)' : 'var(--good)'}>
        <b class="num" style=${{ fontSize: '28px', fontFamily: 'var(--display)' }}>${r.score}</b>
      <//>
      <div class="grow">
        <div style=${{ font: '650 19px var(--display)' }}>${r.failed ? t('Acil ilgilenilmesi gereken sorunlar var', 'There are problems that need attention now')
          : r.warnings ? t('Koruma çalışıyor, birkaç zayıf nokta var', 'Protection works, with a few weak spots') : t('Bu bilgisayar iyi korunuyor', 'This PC is well protected')}</div>
        <div class="row gap-s mt-s"><${Chip} tone="good">${r.passed} ${t('geçti', 'passed')}<//><${Chip} tone=${r.warnings ? 'warn' : null}>${r.warnings} ${t('uyarı', 'warnings')}<//><${Chip} tone=${r.failed ? 'bad' : null}>${r.failed} ${t('sorun', 'problems')}<//></div>
        <div class="tiny dim mt-s">${t('OptiCore güvenlik ayarlarını kendisi değiştirmez: her öneri sizi değişikliği yapacağınız Windows sayfasına götürür.', 'OptiCore never changes security settings itself: every suggestion takes you to the Windows page where you make the change.')}</div>
      </div>
      <${Btn} icon="refresh" onClick=${() => call('security.checks', { refresh: true }).then(d.setData)}>${t('Yeniden denetle', 'Check again')}<//>
    </div>
    ${GROUPS.map(([id, tr, en]) => {
      const items = r.checks.filter((c) => c.group === id);
      if (!items.length) return null;
      return html`<${Card} key=${id} title=${t(tr, en)} flush>
        ${items.map((c) => html`<div key=${c.id} class=${cx('finding', STATUS[c.status][1])}>
          <div class="finding-ico"><${Icon} name=${STATUS[c.status][0]} /></div>
          <div class="grow"><h4>${c.title}</h4><p>${c.detail}</p>${c.advice && html`<p class="acc">${c.advice}</p>`}</div>
          ${c.link && c.status !== 'pass' && html`<${Btn} size="sm" icon="external" onClick=${() => openUrl(c.link)}>${t('Windows\'ta aç', 'Open in Windows')}<//>`}
        </div>`)}
      <//>`;
    })}
  </div>`}<//>`;
}

function Ports() {
  const d = useRpc('security.ports', {}, { interval: 10000 });
  const [all, setAll] = useState(false);
  return html`<${Async} state=${d}>${(r) => html`<div class="col gap-l">
    <div class="grid c4">
      <div class="card pad"><${Stat} label=${t('Dinleyen port', 'Listening ports')} value=${num(r.total)} /></div>
      <div class="card pad"><${Stat} label=${t('Ağa açık', 'Reachable from network')} value=${num(r.exposed)} /></div>
      <div class="card pad"><${Stat} label=${t('Yüksek riskli', 'High risk')} value=${num(r.high)} tone=${r.high ? 'bad' : 'good'} /></div>
      <div class="card pad"><${Stat} label=${t('Güvenlik duvarı', 'Firewall')} value=${r.firewall ? t('Açık', 'On') : t('Kapalı', 'Off')} tone=${r.firewall ? 'good' : 'bad'} /></div>
    </div>
    <${Banner} tone="info">${t('"Ağa açık" bir port, güvenlik duvarı izin verirse yerel ağdaki cihazlardan ulaşılabilir demektir. Windows\'un kendi keşif ve paylaşım portlarının açık olması normaldir; dikkat edilmesi gerekenler veritabanları, uzak masaüstü ve tanımadığınız programlardır.',
      '"Reachable" means devices on the local network can connect if the firewall allows it. Windows\' own discovery and sharing ports being open is normal; watch for databases, remote desktop and programs you do not recognize.')}<//>
    <div class="row"><span class="grow"></span><label class="row gap-s small muted">${t('Yalnızca yerel olanları da göster', 'Also show local-only')}<${Switch} on=${all} onChange=${setAll} /></label></div>
    <div class="card"><${Table} rows=${r.items.filter((p) => all || p.exposed)} rowKey=${(p) => p.protocol + p.port + p.pid} columns=${[
      { id: 'port', label: 'Port', num: true, sort: true, class: 'shrink', render: (p) => html`<b class="mono">${p.port}</b> <span class="dim tiny">${p.protocol}</span>` },
      { id: 'name', label: t('Ne', 'What'), render: (p) => p.name || html`<span class="dim">${t('Bilinmeyen hizmet', 'Unknown service')}</span>` },
      { id: 'process', label: t('Program', 'Program'), sort: true, render: (p) => html`<div class="row"><${FileIcon} path=${p.path} /><div><div class="b">${p.description || p.process}</div><div class="tiny dim">${p.services ? p.services.join(', ') : p.process} · PID ${p.pid}</div></div></div>` },
      { id: 'risk', label: t('Risk', 'Risk'), sort: (p) => ({ high: 4, medium: 3, unknown: 2, low: 1, none: 0 })[p.risk], class: 'shrink', render: (p) => html`<${Chip} tone=${RISK[p.risk][2]}>${t(RISK[p.risk][0], RISK[p.risk][1])}<//>` },
    ]} /></div>
  </div>`}<//>`;
}

function Suspicious() {
  const d = useRpc('security.suspicious');
  return html`<${Async} state=${d}>${(r) => html`<div class="col gap-l">
    <${Banner} tone="info">${t('Bu liste bir virüs taraması değildir. Alışılmadık konumdan çalışan, imzasız ya da gizli komut kullanan öğeleri gösterir; tanıdığınız programlar burada görünebilir. Emin olamadığınız bir dosyanın SHA-256 özetini VirusTotal\'da aratabilirsiniz.',
      'This list is not a virus scan. It shows items that run from unusual places, are unsigned or use hidden commands; programs you know may appear here. You can look up the SHA-256 of a file you are unsure about on VirusTotal.')}<//>
    ${!r.processes.length && !r.startup.length && html`<div class="card"><${Empty} icon="shieldCheck" title=${t('Dikkat çeken bir şey yok', 'Nothing stands out')} /></div>`}
    ${r.processes.length > 0 && html`<${Card} title=${t('Çalışan işlemler', 'Running processes')} icon="activity" flush>${r.processes.map((p) => html`<${SuspectRow} key=${p.path} name=${p.description || p.name} path=${p.path} flags=${p.flags} signer=${p.signer} extra=${p.online ? t('internete bağlı', 'connected to the internet') : null} />`)}<//>`}
    ${r.startup.length > 0 && html`<${Card} title=${t('Başlangıç öğeleri', 'Startup items')} icon="power" flush>${r.startup.map((e) => html`<${SuspectRow} key=${e.id} name=${e.name} path=${e.path} flags=${e.flags} signer=${e.signer} extra=${e.command} />`)}<//>`}
  </div>`}<//>`;
}

function SuspectRow({ name, path, flags, signer, extra }) {
  const [hash, setHash] = useState(null);
  return html`<div class="finding warning">
    <div class="finding-ico"><${Icon} name="alert" /></div>
    <div class="grow" style=${{ minWidth: 0 }}>
      <h4>${name}</h4>
      <div class="mono tiny dim wrap-any">${path}</div>
      <ul class="small" style=${{ margin: '6px 0 0', paddingLeft: '18px' }}>${flags.map((f, i) => html`<li key=${i}>${f}</li>`)}</ul>
      ${extra && html`<div class="tiny dim mt-s mono wrap-any">${extra}</div>`}
      <div class="tiny mt-s">${signer ? t('İmzalayan: ', 'Signed by: ') + signer : t('İmzasız', 'Unsigned')}</div>
      ${hash && html`<div class="code mt-s">SHA-256 ${hash}</div>`}
      <div class="row gap-s mt">
        ${path && html`<${Btn} size="sm" icon="folder" onClick=${() => act(() => call('shell.reveal', { path }))}>${t('Konumu aç', 'Open location')}<//>`}
        ${path && !hash && html`<${Btn} size="sm" icon="key" onClick=${() => act(() => call('security.hash', { path })).then((r) => r && setHash(r.sha256))}>${t('Özet hesapla', 'Compute hash')}<//>`}
        ${hash && html`<${Btn} size="sm" icon="external" onClick=${() => openUrl('https://www.virustotal.com/gui/file/' + hash)}>VirusTotal<//>`}
      </div>
    </div>
  </div>`;
}

function Permissions() {
  const d = useRpc('privacy.permissions');
  const set = (cap, app, allow) => act(() => call('privacy.setPermission', { capability: cap, app, allow }), allow ? t('İzin verildi', 'Allowed') : t('İzin kaldırıldı', 'Permission removed')).then((r) => r && d.reload());
  return html`<${Async} state=${d}>${(list) => html`<div class="col gap-l">
    <${Banner} tone="info" icon="eye">${t('Hangi uygulamanın kamera, mikrofon, konum gibi hassas yeteneklere erişebildiği ve en son ne zaman kullandığı. 90 günden uzun süredir kullanılmayan izinler "kullanılmıyor" olarak işaretlenir: bunları kaldırmak hiçbir şeyi bozmaz, uygulama gerekirse yeniden sorar.',
      'Which apps may use sensitive capabilities such as the camera, microphone and location, and when they last did. Permissions unused for over 90 days are marked "unused": removing them breaks nothing, the app asks again if it needs to.')}<//>
    ${list.map((c) => html`<${Card} key=${c.id} title=${c.title} icon=${c.icon} flush sub=${c.globalAllowed ? '' : t('Windows genelinde kapalı', 'Off for all of Windows')}
      actions=${html`${c.stale > 0 && html`<${Chip} tone="warn">${c.stale} ${t('kullanılmıyor', 'unused')}<//>`}${c.inUse > 0 && html`<${Chip} tone="bad" icon="dot">${t('Şu an kullanımda', 'In use now')}<//>`}<${Btn} size="sm" kind="ghost" icon="external" onClick=${() => openUrl(c.settings)} />`}>
      ${c.apps.map((a) => html`<div key=${a.id} class="item">
        <div class="grow" style=${{ minWidth: 0 }}><div class="t ell">${a.name}${!a.packaged && html` <span class="tiny dim">${t('masaüstü programı', 'desktop program')}</span>`}</div>
          <div class="s">${a.inUse ? t('Şu an kullanıyor', 'Using it right now') : a.lastUsed ? t('Son kullanım: ', 'Last used: ') + ago(a.lastUsed) : t('Hiç kullanmadı', 'Never used it')}</div></div>
        ${a.stale && html`<${Chip} tone="warn">${t('Kullanılmıyor', 'Unused')}<//>`}
        ${a.packaged ? html`<${Switch} on=${a.allowed} onChange=${(on) => set(c.id, a.id, on)} />` : html`<${Chip}>${a.allowed ? t('İzinli', 'Allowed') : t('Engelli', 'Blocked')}<//>`}
      </div>`)}
    <//>`)}
  </div>`}<//>`;
}

function Extensions() {
  const d = useRpc('privacy.extensions');
  const tones = { high: 'bad', medium: 'warn', low: 'good' };
  return html`<${Async} state=${d}>${(list) => html`<div class="col gap-l">
    <${Banner} tone="info">${t('Tarayıcı eklentileri ziyaret ettiğiniz sayfaları görebilir. "Tüm siteler" yetkisi olan, mağaza dışından kurulmuş ve çerez/istek yetkisi isteyen eklentiler en yüksek riski taşır.',
      'Browser extensions can see the pages you visit. Extensions with access to all sites, installed from outside the store and asking for cookie or request permissions carry the highest risk.')}<//>
    ${!list.length ? html`<div class="card"><${Empty} icon="puzzle" title=${t('Eklenti bulunamadı', 'No extensions found')} /></div>` : html`<div class="card"><${Table} rows=${list} rowKey=${(e) => e.browser + e.profile + e.id} columns=${[
      { id: 'name', label: t('Eklenti', 'Extension'), sort: true, render: (e) => html`<div><div class="b">${e.name} <span class="dim tiny">${e.version}</span></div><div class="tiny dim ell" style=${{ maxWidth: '520px' }}>${e.description}</div></div>` },
      { id: 'browser', label: t('Tarayıcı', 'Browser'), sort: true, class: 'shrink', render: (e) => html`${e.browser} <span class="tiny dim">${e.profile}</span>` },
      { id: 'access', label: t('Erişim', 'Access'), render: (e) => html`<div class="row wrap gap-s">${e.allSites && html`<${Chip} tone="warn">${t('Tüm siteler', 'All sites')}<//>`}${e.sensitive.slice(0, 4).map((p) => html`<${Chip} key=${p}>${p}<//>`)}${!e.fromStore && html`<${Chip} tone="bad">${t('Mağaza dışı', 'Off-store')}<//>`}</div>` },
      { id: 'enabled', label: t('Durum', 'State'), class: 'shrink', render: (e) => (e.enabled === false ? html`<${Chip}>${t('Kapalı', 'Disabled')}<//>` : html`<${Chip} tone="good">${t('Açık', 'On')}<//>`) },
      { id: 'risk', label: t('Risk', 'Risk'), class: 'shrink', sort: (e) => ({ high: 2, medium: 1, low: 0 })[e.risk], render: (e) => html`<${Chip} tone=${tones[e.risk]}>${t(...{ high: ['Yüksek', 'High'], medium: ['Orta', 'Medium'], low: ['Düşük', 'Low'] }[e.risk])}<//>` },
      { id: 'store', label: '', class: 'shrink', render: (e) => e.storeUrl && html`<${Btn} size="sm" kind="ghost" icon="external" onClick=${() => openUrl(e.storeUrl)} />` },
    ]} /></div>`}
  </div>`}<//>`;
}

function Hosts() {
  const d = useRpc('security.hosts');
  return html`<${Async} state=${d}>${(h) => html`<div class="col gap-l">
    <${Card} title=${t('Hosts dosyası', 'Hosts file')} icon="file">
      <${KV} rows=${[[t('Konum', 'Location'), html`<span class="mono">${h.path}</span>`], [t('Girdi', 'Entries'), num(h.entries)], [t('Engelleme', 'Blocks'), num(h.blocks)], [t('Son değişiklik', 'Last changed'), h.modified && dateTime(h.modified)]]} />
    <//>
    ${h.redirects.length > 0 && html`<${Banner} tone="bad" icon="alert"><b>${t('Bilinen siteler başka adreslere yönlendiriliyor:', 'Well-known sites are redirected elsewhere:')}</b><div class="code mt-s">${h.redirects.map((r) => `${r.ip}  ${r.host}`).join('\n')}</div><//>`}
    ${h.blockedSecurity.length > 0 && html`<${Banner} tone="warn"><b>${t('Güvenlik ya da güncelleme adresleri engelli:', 'Security or update addresses are blocked:')}</b><div class="code mt-s">${h.blockedSecurity.map((r) => `${r.ip}  ${r.host}`).join('\n')}</div><//>`}
    ${!h.redirects.length && !h.blockedSecurity.length && html`<div class="card"><${Empty} icon="shieldCheck" title=${t('Hosts dosyası temiz', 'The hosts file is clean')} /></div>`}
  </div>`}<//>`;
}

export default function Security({ sub }) {
  const tabs = ['overview', 'ports', 'suspicious', 'permissions', 'extensions', 'hosts'];
  const [tab, setTab] = useState(tabs.includes(sub) ? sub : 'overview');
  useEffect(() => { if (tabs.includes(sub)) setTab(sub); }, [sub]);
  return html`<${Page} title=${t('Güvenlik ve Gizlilik', 'Security & Privacy')} desc=${t('Koruma durumunu, ağa açık kapıları, şüpheli öğeleri, uygulama izinlerini ve tarayıcı eklentilerini denetler.', 'Audits protection, open doors to the network, suspicious items, app permissions and browser extensions.')}>
    <div class="mb"><${Tabs} value=${tab} onChange=${(id) => { setTab(id); go('security/' + id); }} items=${[
      { id: 'overview', label: t('Denetim', 'Audit'), icon: 'shield' },
      { id: 'ports', label: t('Açık portlar', 'Open ports'), icon: 'globe' },
      { id: 'suspicious', label: t('Şüpheli öğeler', 'Suspicious items'), icon: 'alert' },
      { id: 'permissions', label: t('Uygulama izinleri', 'App permissions'), icon: 'eye' },
      { id: 'extensions', label: t('Tarayıcı eklentileri', 'Browser extensions'), icon: 'puzzle' },
      { id: 'hosts', label: 'Hosts', icon: 'file' },
    ]} /></div>
    ${tab === 'overview' ? html`<${Overview} />` : tab === 'ports' ? html`<${Ports} />` : tab === 'suspicious' ? html`<${Suspicious} />`
      : tab === 'permissions' ? html`<${Permissions} />` : tab === 'extensions' ? html`<${Extensions} />` : html`<${Hosts} />`}
  <//>`;
}
