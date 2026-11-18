// Settings: appearance, language, behaviour, data and about.
import { html, useState, useRpc, useJob, call, act, t, app, useStore, patchSettings, askElevation, installUpdate } from '../lib/core.js';
import { Page, Card, Btn, Chip, Switch, Select, KV, Banner, JobBar, openUrl } from '../lib/ui.js';
import { Logo } from '../lib/icons.js';

const ACCENTS = [['violet', '#7c6cff'], ['cyan', '#06b6d4'], ['emerald', '#10b981'], ['amber', '#f59e0b'], ['rose', '#f43f5e']];

function Updates({ s, info }) {
  const [check, setCheck] = useState(null);
  const job = useJob();
  const run = () => act(async () => setCheck(await call('update.check')));
  const install = () => act(() => installUpdate(job.run));
  return html`<${Card} title=${t('Güncellemeler', 'Updates')} icon="download">
    <div class="item" style=${{ padding: '8px 0' }}><div class="grow"><div class="t">${t('Yüklü sürüm', 'Installed version')}: ${info.version}</div>
      <div class="s">${check ? (check.newer ? t(`Yeni sürüm var: ${check.latest}`, `A new version is available: ${check.latest}`) : t('OptiCore güncel.', 'OptiCore is up to date.')) : t('Yeni sürümler GitHub\'daki proje sayfasından denetlenir.', 'New versions are checked on the project\'s GitHub page.')}</div></div>
      <${Btn} size="sm" icon="refresh" onClick=${run}>${t('Denetle', 'Check')}<//></div>
    ${check && check.newer && html`<div class="col mt-s">
      ${check.notes && html`<div class="code" style=${{ maxHeight: '160px' }}>${check.notes}</div>`}
      <div class="row gap-s">
        ${check.canInstall ? html`<${Btn} kind="primary" icon="download" busy=${job.running} onClick=${install}>${t('Güncelle', 'Update')}<//>`
          : html`<${Btn} icon="external" onClick=${() => openUrl(check.url)}>${t('Sürüm sayfasını aç', 'Open the release page')}<//>`}
      </div>
      <${JobBar} job=${job.job} />
    </div>`}
    <div class="item" style=${{ padding: '8px 0' }}><div class="grow"><div class="t">${t('Güncellemeleri otomatik denetle', 'Check for updates automatically')}</div>
      <div class="s">${t('Her açılışta bir kez; yeni sürüm varsa bir pencere açılır. Yalnızca herkese açık sürüm listesi okunur, bilgisayarınızla ilgili hiçbir bilgi gönderilmez.', 'Once at every launch; a window opens when a new version exists. Only the public release list is read, nothing about your PC is sent.')}</div></div>
      <${Switch} on=${s.checkUpdates} onChange=${(checkUpdates) => patchSettings({ checkUpdates })} /></div>
  <//>`;
}

export default function Settings() {
  const state = useStore(app);
  const s = state.settings, info = state.info;
  const autostart = useRpc('app.autostart', {}, { enabled: info.host === 'desktop' });
  const row = (label, desc, control) => html`<div class="item" style=${{ padding: '12px 0' }}><div class="grow"><div class="t">${label}</div>${desc && html`<div class="s">${desc}</div>`}</div>${control}</div>`;
  return html`<${Page} title=${t('Ayarlar', 'Settings')}>
    <div class="grid c2">
      <${Card} title=${t('Görünüm', 'Appearance')} icon="sun">
        ${row(t('Dil', 'Language'), null, html`<${Select} value=${s.language} onChange=${(language) => patchSettings({ language })} options=${[{ value: 'tr', label: 'Türkçe' }, { value: 'en', label: 'English' }]} />`)}
        ${row(t('Tema', 'Theme'), null, html`<${Select} value=${s.theme} onChange=${(theme) => patchSettings({ theme })} options=${[{ value: 'dark', label: t('Koyu', 'Dark') }, { value: 'light', label: t('Açık', 'Light') }, { value: 'system', label: t('Sistemle aynı', 'Same as system') }]} />`)}
        ${row(t('Vurgu rengi', 'Accent color'), null, html`<div class="row gap-s">${ACCENTS.map(([id, color]) => html`<button key=${id} title=${id} onClick=${() => patchSettings({ accent: id })}
          style=${{ width: '24px', height: '24px', borderRadius: '50%', background: color, border: s.accent === id ? '3px solid var(--text)' : '2px solid transparent', cursor: 'pointer' }}></button>`)}</div>`)}
        ${row(t('Animasyonları azalt', 'Reduce motion'), null, html`<${Switch} on=${s.reduceMotion} onChange=${(reduceMotion) => patchSettings({ reduceMotion })} />`)}
      <//>
      <${Card} title=${t('Davranış', 'Behaviour')} icon="cog">
        ${info.host === 'desktop' && row(t('Windows ile başlat', 'Start with Windows'), t('Oturum açıldığında simge durumunda başlar', 'Starts minimized when you sign in'),
          html`<${Switch} on=${!!autostart.data} onChange=${(on) => act(() => call('app.setAutostart', { on })).then(autostart.reload)} />`)}
        ${row(t('Kapatınca bildirim alanında çalışmaya devam et', 'Keep running in the tray when closed'), t('İzleme, uyarılar ve otomatik bakım arka planda sürer', 'Monitoring, alerts and maintenance continue in the background'),
          html`<${Switch} on=${s.closeToTray} onChange=${(closeToTray) => patchSettings({ closeToTray })} />`)}
        ${row(t('Simge durumunda başlat', 'Start minimized'), null, html`<${Switch} on=${s.startMinimized} onChange=${(startMinimized) => patchSettings({ startMinimized })} />`)}
        ${row(t('Yoksayılan bulgular', 'Ignored findings'), t(`${s.ignoredFindings.length} bulgu yoksayılıyor`, `${s.ignoredFindings.length} findings ignored`),
          html`<${Btn} size="sm" disabled=${!s.ignoredFindings.length} onClick=${() => patchSettings({ ignoredFindings: [] })}>${t('Sıfırla', 'Reset')}<//>`)}
      <//>
      <${Updates} s=${s} info=${info} />
      <${Card} title=${t('Yetki ve güvenlik', 'Rights and safety')} icon="shield">
        <${KV} rows=${[
          [t('Çalışma kipi', 'Running as'), info.elevated ? html`<${Chip} tone="good">${t('Yönetici', 'Administrator')}<//>` : html`<${Chip} tone="warn">${t('Standart kullanıcı', 'Standard user')}<//>`],
          [t('Simülasyon modu', 'Simulation mode'), info.dryRun ? html`<${Chip} tone="acc">${t('Açık', 'On')}<//>` : t('Kapalı', 'Off')],
        ]} />
        ${!info.elevated && html`<div class="mt"><${Btn} icon="shield" onClick=${askElevation}>${t('Yönetici olarak yeniden başlat', 'Restart as administrator')}<//></div>`}
        <div class="small muted mt">${t('OptiCore varsayılan olarak standart kullanıcı olarak çalışır ve yalnızca gerektiğinde yetki ister. Simülasyon modu (--dry-run) her şeyi tarar ama hiçbir şeyi değiştirmez; denemek için idealdir.',
          'OptiCore runs as a standard user by default and asks for rights only when needed. Simulation mode (--dry-run) scans everything but changes nothing; ideal for trying things out.')}</div>
      <//>
      <${Card} title=${t('Veriler', 'Data')} icon="folder">
        <div class="small muted">${t('OptiCore her şeyi bu bilgisayarda, okunabilir JSON dosyaları olarak saklar. Hiçbir veri bir sunucuya gönderilmez; internete yalnızca sizin başlattığınız hız testi ve DNS testi ile (kapatılabilen) güncelleme denetimi çıkar.',
          'OptiCore keeps everything on this PC as readable JSON files. No data is sent to any server; the only internet traffic is the speed test and DNS test you start yourself and the update check (which can be turned off).')}</div>
        <div class="code mt">${info.dataDir}</div>
        <div class="mt"><${Btn} size="sm" icon="folder" onClick=${() => act(() => call('shell.reveal', { path: info.dataDir }))}>${t('Klasörü aç', 'Open folder')}<//></div>
      <//>
      <div class="card pad row gap-l span2">
        <${Logo} size=${48} />
        <div class="grow"><div style=${{ font: '650 18px var(--display)' }}>OptiCore <span class="dim">${info.version}</span></div>
          <div class="small muted">${t('Windows için ölçerek optimize eden, her değişikliği geri alınabilir tutan sistem platformu.', 'A Windows platform that optimizes by measuring and keeps every change reversible.')}</div>
          <div class="tiny dim mt-s">.NET 10 · WebView2 · Preact · htm · TraceEvent · ${info.methods} ${t('motor yöntemi', 'engine methods')}</div></div>
      </div>
    </div>
  <//>`;
}
