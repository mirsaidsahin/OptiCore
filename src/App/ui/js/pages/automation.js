// Automatic maintenance: scheduled tasks, profiles, alert rules and run history.
import { html, useState, useRpc, call, act, t, num, dateTime, ago, app, useStore, patchSettings } from '../lib/core.js';
import { Page, Card, Btn, Chip, Icon, Empty, Banner, Async, Switch, Select, Modal, Check } from '../lib/ui.js';

const ACTIONS = [
  ['analyze', 'Tam sistem analizi', 'Full system analysis'], ['cleanup', 'Temizlik', 'Cleanup'], ['startup-check', 'Başlangıç denetimi', 'Startup check'],
  ['tweak-drift', 'Geri alınmış ayarları yeniden uygula', 'Re-apply reset tweaks'], ['restore-point', 'Geri yükleme noktası (yönetici)', 'Restore point (admin)'],
];
const DAYS = [['Pazar', 'Sunday'], ['Pazartesi', 'Monday'], ['Salı', 'Tuesday'], ['Çarşamba', 'Wednesday'], ['Perşembe', 'Thursday'], ['Cuma', 'Friday'], ['Cumartesi', 'Saturday']];
const CATS = [['temp.user', 'Geçici dosyalar', 'Temporary files'], ['temp.windows', 'Windows geçici dosyaları', 'Windows temp files'], ['thumbs', 'Küçük resim önbelleği', 'Thumbnail cache'], ['wer', 'Hata raporları', 'Error reports'],
  ['do.cache', 'Teslim İyileştirme', 'Delivery Optimization'], ['logs.windows', 'Eski günlükler', 'Old logs'], ['browser.chrome', 'Chrome', 'Chrome'], ['browser.edge', 'Edge', 'Edge'], ['browser.firefox', 'Firefox', 'Firefox'],
  ['apps.electron', 'Uygulama önbellekleri', 'App caches'], ['apps.launchers', 'Oyun başlatıcıları', 'Game launchers'], ['recyclebin', 'Geri Dönüşüm Kutusu', 'Recycle Bin']];

function scheduleText(task) {
  const h = String(task.hour).padStart(2, '0') + ':00';
  switch (task.schedule) {
    case 'daily': return t(`Her gün ${h}`, `Every day at ${h}`);
    case 'weekly': return t(`Her ${DAYS[task.day][0]} ${h}`, `Every ${DAYS[task.day][1]} at ${h}`);
    case 'monthly': return t(`Her ayın ${task.day}. günü ${h}`, `Day ${task.day} of every month at ${h}`);
    case 'idle': return t('Günde bir, bilgisayar boştayken', 'Once a day, when the PC is idle');
    default: return t('Her oturum açılışında', 'At every sign-in');
  }
}

function Editor({ task, onClose, onSaved }) {
  const [x, setX] = useState(task);
  const toggle = (list, id, on) => (on ? [...list, id] : list.filter((v) => v !== id));
  const save = () => act(() => call('auto.save', { task: x }), t('Görev kaydedildi', 'Task saved')).then((r) => r && onSaved());
  return html`<${Modal} wide title=${t('Bakım görevi', 'Maintenance task')} onClose=${onClose} footer=${html`<${Btn} kind="ghost" onClick=${onClose}>${t('Vazgeç', 'Cancel')}<//><${Btn} kind="primary" disabled=${!x.name.trim() || !x.actions.length} onClick=${save}>${t('Kaydet', 'Save')}<//>`}>
    <div class="col">
      <div class="field"><label>${t('Ad', 'Name')}</label><input class="input" value=${x.name} onInput=${(e) => setX({ ...x, name: e.target.value })} /></div>
      <div class="row wrap">
        <${Select} value=${x.schedule} onChange=${(schedule) => setX({ ...x, schedule })} options=${[{ value: 'daily', label: t('Her gün', 'Daily') }, { value: 'weekly', label: t('Her hafta', 'Weekly') }, { value: 'monthly', label: t('Her ay', 'Monthly') }, { value: 'idle', label: t('Boştayken (günde bir)', 'When idle (daily)') }, { value: 'logon', label: t('Oturum açılışında', 'At sign-in') }]} />
        ${x.schedule === 'weekly' && html`<${Select} value=${String(x.day)} onChange=${(d) => setX({ ...x, day: +d })} options=${DAYS.map((d, i) => ({ value: String(i), label: t(d[0], d[1]) }))} />`}
        ${x.schedule === 'monthly' && html`<${Select} value=${String(x.day)} onChange=${(d) => setX({ ...x, day: +d })} options=${Array.from({ length: 28 }, (_, i) => ({ value: String(i + 1), label: String(i + 1) }))} />`}
        ${['daily', 'weekly', 'monthly'].includes(x.schedule) && html`<${Select} value=${String(x.hour)} onChange=${(h) => setX({ ...x, hour: +h })} options=${Array.from({ length: 24 }, (_, i) => ({ value: String(i), label: String(i).padStart(2, '0') + ':00' }))} />`}
      </div>
      <div class="up">${t('Yapılacaklar', 'Actions')}</div>
      ${ACTIONS.map(([id, tr, en]) => html`<label key=${id} class="row" style=${{ cursor: 'pointer' }}><${Check} checked=${x.actions.includes(id)} onChange=${(on) => setX({ ...x, actions: toggle(x.actions, id, on) })} />${t(tr, en)}</label>`)}
      ${x.actions.includes('cleanup') && html`<div class="card flat pad"><div class="up mb">${t('Temizlenecekler', 'What to clean')}</div><div class="grid c3" style=${{ gap: '6px' }}>
        ${CATS.map(([id, tr, en]) => html`<label key=${id} class="row small" style=${{ cursor: 'pointer' }}><${Check} checked=${x.cleanupCategories.includes(id)} onChange=${(on) => setX({ ...x, cleanupCategories: toggle(x.cleanupCategories, id, on) })} />${t(tr, en)}</label>`)}</div></div>`}
      <div class="row"><span class="grow">${t('Yalnızca bilgisayar boştayken (oyun ya da tam ekran yokken)', 'Only when idle (no game or full-screen app)')}</span><${Switch} on=${x.onlyIdle} onChange=${(onlyIdle) => setX({ ...x, onlyIdle })} /></div>
      <div class="row"><span class="grow">${t('Yalnızca prize takılıyken', 'Only when plugged in')}</span><${Switch} on=${x.onlyOnAc} onChange=${(onlyOnAc) => setX({ ...x, onlyOnAc })} /></div>
    </div>
  <//>`;
}

function Alerts() {
  const settings = useStore(app, (s) => s.settings);
  const a = settings.alerts;
  const set = (patch) => patchSettings({ alerts: patch });
  const row = (label, desc, control) => html`<div class="item" style=${{ padding: '10px 0' }}><div class="grow"><div class="t">${label}</div><div class="s">${desc}</div></div>${control}</div>`;
  return html`<${Card} title=${t('Uyarı kuralları', 'Alert rules')} icon="bell">
    ${row(t('Uyarılar', 'Alerts'), t('Arka planda izleme ve bildirimler', 'Background monitoring and notifications'), html`<${Switch} on=${a.enabled} onChange=${(enabled) => set({ enabled })} />`)}
    ${row(t('İşlemciyi uzun süre yoran uygulama', 'App hogging the CPU'), t('Bir uygulama bu süre boyunca yoğun işlemci kullanırsa bildir', 'Notify when an app uses heavy CPU for this long'),
      html`<${Select} class="sm" value=${String(a.cpuSeconds)} onChange=${(v) => set({ cpuSeconds: +v })} options=${[60, 90, 180, 300, 600].map((s) => ({ value: String(s), label: s < 120 ? s + ' sn' : s / 60 + ' dk' }))} />`)}
    ${row(t('Aşırı ısınma', 'Overheating'), t('En sıcak sensör bir dakika boyunca bu değerin üzerinde kalırsa bildir', 'Notify when the hottest sensor stays above this for a minute'),
      html`<${Select} class="sm" value=${String(a.temperatureC)} onChange=${(v) => set({ temperatureC: +v })} options=${[80, 85, 90, 95].map((v) => ({ value: String(v), label: v + ' °C' }))} />`)}
    ${row(t('Bellek baskısı', 'Memory pressure'), t('Bellek kullanımı iki dakika boyunca bu değerin üzerinde kalırsa bildir', 'Notify when memory use stays above this for two minutes'),
      html`<${Select} class="sm" value=${String(a.ramPercent)} onChange=${(v) => set({ ramPercent: +v })} options=${[85, 90, 92, 95].map((v) => ({ value: String(v), label: '%' + v }))} />`)}
    ${row(t('Düşük disk alanı', 'Low disk space'), t('Bir sürücüde boş alan bu oranın altına düşerse bildir', 'Notify when a drive\'s free space drops below this'),
      html`<${Select} class="sm" value=${String(a.diskFreePercent)} onChange=${(v) => set({ diskFreePercent: +v })} options=${[5, 10, 15, 20].map((v) => ({ value: String(v), label: '%' + v }))} />`)}
    ${row(t('Bellek sızıntısı tespiti', 'Memory leak detection'), t('Belleği sürekli büyüyen uygulamaları bildir', 'Notify about apps whose memory keeps growing'), html`<${Switch} on=${a.leakDetection} onChange=${(leakDetection) => set({ leakDetection })} />`)}
    ${row(t('Başlangıç bekçisi', 'Startup guard'), t('Başlangıca yeni bir program eklendiğinde bildir', 'Notify when a program adds itself to startup'), html`<${Switch} on=${a.startupGuard} onChange=${(startupGuard) => set({ startupGuard })} />`)}
  <//>`;
}

export default function Automation() {
  const d = useRpc('auto.tasks');
  const history = useRpc('auto.history');
  const [edit, setEdit] = useState(null);
  const blank = { id: Math.random().toString(16).slice(2, 10), name: '', enabled: true, schedule: 'weekly', day: 0, hour: 12, actions: ['analyze'], cleanupCategories: ['temp.user', 'thumbs', 'wer'], onlyIdle: true, onlyOnAc: true };
  return html`<${Page} title=${t('Otomatik Bakım', 'Automation')} desc=${t('Bakımı unutun: belirlediğiniz takvimde, bilgisayar boştayken çalışır ve her adımı kayda geçirir.', 'Forget about maintenance: it runs on your schedule, while the PC is idle, and records every step.')}
    actions=${html`<${Btn} kind="primary" icon="plus" onClick=${() => setEdit(blank)}>${t('Yeni görev', 'New task')}<//>`}>
    <${Async} state=${d}>${(r) => html`<div class="col" style=${{ gap: '14px' }}>
      ${r.canSchedule ? html`<div class="card pad row"><div class="grow"><div class="b">${t('OptiCore\'u oturum açılışında arka planda başlat', 'Start OptiCore in the background at sign-in')}</div>
          <div class="small muted">${t('Görevler yalnızca OptiCore çalışırken yürür. Bu seçenek, Windows Görev Zamanlayıcısı\'na OptiCore\'u simge durumunda başlatan bir görev ekler.', 'Tasks only run while OptiCore runs. This adds a Windows Task Scheduler entry that starts OptiCore minimized.')}</div></div>
          <${Switch} on=${r.windowsTask} onChange=${(on) => act(() => call('auto.windowsTask', { on })).then(d.reload)} /></div>`
        : html`<${Banner} tone="info">${t('Görevler OptiCore açıkken çalışır. Masaüstü uygulamasında oturum açılışında otomatik başlatma seçeneği bulunur.', 'Tasks run while OptiCore is open. The desktop app can also start itself at sign-in.')}<//>`}
      ${r.tasks.length === 0 && html`<div class="grid c3">${r.profiles.map((p) => html`<div key=${p.id} class="card pad col"><b>${p.title}</b><div class="small muted grow">${p.description}</div>
        <${Btn} size="sm" kind="primary" onClick=${() => act(() => call('auto.addProfile', { id: p.id }), t('Bakım profili eklendi', 'Maintenance profile added')).then(d.reload)}>${t('Bu profili kullan', 'Use this profile')}<//></div>`)}</div>`}
      ${r.tasks.length > 0 && html`<${Card} title=${t('Görevler', 'Tasks')} icon="repeat" flush>${r.tasks.map(({ task, next }) => html`<div key=${task.id} class="item">
        <${Switch} on=${task.enabled} onChange=${(enabled) => call('auto.save', { task: { ...task, enabled } }).then(d.reload)} />
        <div class="grow"><div class="t">${task.name}</div><div class="s">${scheduleText(task)} · ${task.actions.map((a) => t(...(ACTIONS.find((x) => x[0] === a) || [a, a, a]).slice(1))).join(', ')}</div>
          <div class="tiny dim">${task.lastRun ? t('Son: ', 'Last: ') + ago(task.lastRun) : ''}${next ? ' · ' + t('Sonraki: ', 'Next: ') + dateTime(next) : ''}</div></div>
        <${Btn} size="sm" icon="play" onClick=${() => act(() => call('auto.runNow', { id: task.id }), t('Bakım tamamlandı', 'Maintenance finished')).then(() => { d.reload(); history.reload(); })}>${t('Şimdi çalıştır', 'Run now')}<//>
        <${Btn} size="sm" kind="ghost" icon="sliders" onClick=${() => setEdit(task)} />
        <${Btn} size="sm" kind="ghost" icon="trash" onClick=${() => call('auto.delete', { id: task.id }).then(d.reload)} />
      </div>`)}<//>`}
      <${Alerts} />
      <${Card} title=${t('Bakım geçmişi', 'Maintenance history')} icon="history" flush>
        <${Async} state=${history}>${(h) => (!h.length ? html`<${Empty} icon="history" title=${t('Henüz bakım çalışmadı', 'No maintenance has run yet')} />` : h.map((x, i) => html`<div key=${i} class="item" style=${{ alignItems: 'flex-start' }}>
          <${Icon} name=${x.ok ? 'check' : 'alert'} size="16" class=${x.ok ? 'good' : 'warn'} />
          <div class="grow"><div class="t">${x.task}${x.simulated ? ' · ' + t('simülasyon', 'simulated') : ''}</div><div class="s">${dateTime(x.time)} · ${num(x.seconds)} ${t('sn', 's')}</div>
            <ul class="small muted" style=${{ margin: '4px 0 0', paddingLeft: '18px' }}>${x.lines.map((l, j) => html`<li key=${j}>${l}</li>`)}</ul></div>
        </div>`))}<//>
      <//>
    </div>`}<//>
    ${edit && html`<${Editor} task=${edit} onClose=${() => setEdit(null)} onSaved=${() => { setEdit(null); d.reload(); }} />`}
  <//>`;
}
