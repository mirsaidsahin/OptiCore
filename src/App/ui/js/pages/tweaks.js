// Windows Tweaks: every setting states its evidence, risk, trade-off and the exact change it makes.
import { html, useState, useEffect, useMemo, useRpc, call, act, t, cx, num, matches, confirmDialog, toast, go, app, useStore } from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Search, Switch, Icon, Empty, Loading, Banner, Async, SimulationNote, AdminChip, openUrl } from '../lib/ui.js';

const CATEGORIES = [
  ['privacy', 'Gizlilik', 'Privacy', 'eyeOff'], ['telemetry', 'Telemetri', 'Telemetry', 'activity'], ['performance', 'Performans', 'Performance', 'bolt'],
  ['visual', 'Görünüm', 'Visuals', 'sun'], ['gaming', 'Oyun', 'Gaming', 'gamepad'], ['explorer', 'Gezgin ve görev çubuğu', 'Explorer and taskbar', 'folder'],
  ['updates', 'Güncellemeler', 'Updates', 'refresh'],
];
const EVIDENCE = {
  proven: ['Kanıtlı', 'Proven', 'good', 'Yaptığını söylediği şeyi yapar; etkisi doğrudan görülür ya da ölçülür.', 'Does what it says; the effect is directly visible or measurable.'],
  situational: ['Duruma bağlı', 'Situational', 'info', 'Yalnızca belirli donanım ya da kullanım biçimlerinde fayda sağlar.', 'Helps only on certain hardware or usage patterns.'],
  cosmetic: ['Yalnızca his', 'Feel only', null, 'Gerçek performansı değiştirmez; arayüzün verdiği hissi değiştirir.', 'Does not change real performance; changes how the interface feels.'],
  minimal: ['Etkisi tartışmalı', 'Disputed effect', 'warn', 'İnternette çok önerilir ama ölçülebilir bir kazancı gösterilememiştir. Dürüst olmak için listede.', 'Widely recommended online, yet no measurable gain has been shown. Listed for the sake of honesty.'],
};
const RISK = { safe: ['Güvenli', 'Safe', 'good'], moderate: ['Dikkat', 'Moderate', 'warn'], advanced: ['İleri düzey', 'Advanced', 'bad'] };
const RESTART = { explorer: ['Gezgin yeniden başlatılmalı', 'Explorer restart'], logoff: ['Oturum kapatıp açın', 'Sign out and in'], reboot: ['Yeniden başlatma gerekir', 'Needs a reboot'] };

function restartNotice(restart) {
  if (restart === 'explorer') {
    confirmDialog({
      title: t('Dosya Gezgini yeniden başlatılsın mı?', 'Restart File Explorer?'),
      body: t('Değişikliğin görünmesi için Gezgin\'in yeniden başlaması gerekiyor. Görev çubuğu bir an kaybolup geri gelir; açık klasör pencereleri kapanır.', 'Explorer has to restart for the change to show. The taskbar disappears for a moment; open folder windows close.'),
      confirm: t('Şimdi yeniden başlat', 'Restart now'), cancel: t('Sonra', 'Later'),
    }).then((ok) => ok && act(() => call('tweaks.restartExplorer')));
  } else if (restart === 'logoff') toast('info', t('Oturumu kapatıp açtığınızda geçerli olur', 'Takes effect after you sign out and back in'));
  else if (restart === 'reboot') toast('info', t('Bilgisayar yeniden başlatıldığında geçerli olur', 'Takes effect after the PC restarts'));
}

function TweakRow({ tw, onToggle, highlight, drifted }) {
  const [open, setOpen] = useState(highlight);
  const ev = EVIDENCE[tw.evidence], risk = RISK[tw.risk];
  return html`<div id=${'tw-' + tw.id} style=${highlight ? { background: 'var(--accent-soft)' } : null}>
    <div class="item click" onClick=${() => setOpen(!open)}>
      <div class="grow">
        <div class="row wrap gap-s">
          <span class="t">${tw.title}</span>
          <${Chip} tone=${ev[2]} title=${t(ev[3], ev[4])}>${t(ev[0], ev[1])}<//>
          ${tw.risk !== 'safe' && html`<${Chip} tone=${risk[2]}>${t(risk[0], risk[1])}<//>`}
          ${tw.admin && html`<${AdminChip} />`}
          ${tw.restart !== 'none' && html`<${Chip} icon="refresh">${t(...RESTART[tw.restart])}<//>`}
          ${drifted && html`<${Chip} tone="warn" icon="undo">${t('Geri alınmış', 'Was reset')}<//>`}
        </div>
        <div class="s">${tw.description}</div>
        ${tw.tradeoff && html`<div class="small" style=${{ marginTop: '3px' }}><span class="warn b">${t('Karşılığında', 'Trade-off')}:</span> <span class="muted">${tw.tradeoff}</span></div>`}
      </div>
      <${Switch} on=${tw.state === 'on'} mixed=${tw.state === 'mixed'} onChange=${(on) => onToggle(tw, tw.state === 'mixed' ? true : on)}
        title=${tw.state === 'mixed' ? t('Kısmen uygulanmış: tıklayınca tamamlanır', 'Partly applied: click to complete') : ''} />
    </div>
    ${open && html`<div style=${{ padding: '0 16px 14px', borderBottom: '1px solid var(--line)' }}>
      <div class="tiny dim" style=${{ marginBottom: '4px' }}>${t('Tam olarak ne değişir?', 'What exactly changes?')}</div>
      <div class="code">${tw.changes.join('\n')}</div>
      <div class="tiny dim mt-s">${t('Kapatırsanız Windows varsayılanı geri yüklenir. Önceki değerin birebir aynısına dönmek için Geri Alma Merkezi\'ni kullanın.', 'Turning it off restores the Windows default. To return to the exact previous value, use the Undo Center.')}</div>
    </div>`}
  </div>`;
}

function PowerPlans() {
  const plans = useRpc('power.plans');
  const kinds = { balanced: t('Performans ve güç tüketimini dengeler. Çoğu bilgisayar için doğru seçim.', 'Balances performance and power use. The right choice for most PCs.'),
    high: t('İşlemciyi yüksek hızda tutar; daha fazla ısı ve güç tüketimi.', 'Keeps the processor at high speed; more heat and power.'),
    saver: t('Pil ömrü için işlemci hızını sınırlar.', 'Limits processor speed to save battery.'),
    ultimate: t('İş istasyonları için: güç tasarrufu özelliklerinin çoğunu kapatır.', 'For workstations: turns off most power-saving features.'), custom: t('Üretici ya da kullanıcı tanımlı plan.', 'A manufacturer or user-defined plan.') };
  const activate = (p) => act(() => call('power.setActive', { id: p.id }), (r) => (r.simulated ? t('Simülasyon: plan değiştirilmedi', 'Simulation: the plan was not changed') : t(`Etkin plan: ${p.name}`, `Active plan: ${p.name}`))).then((r) => r && plans.reload());
  return html`<${Async} state=${plans}>${(d) => html`<div class="col gap-l">
    <${Card} title=${t('Güç planları', 'Power plans')} icon="battery" flush>
      ${d.plans.map((p) => html`<div key=${p.id} class="item">
        <${Icon} name=${p.kind === 'saver' ? 'leaf' : p.kind === 'balanced' ? 'scale' : 'bolt'} size="18" class=${p.active ? 'acc' : 'dim'} />
        <div class="grow"><div class="t">${p.name}</div><div class="s">${kinds[p.kind]}</div></div>
        ${p.active ? html`<${Chip} tone="acc" icon="check">${t('Etkin', 'Active')}<//>` : html`<${Btn} size="sm" onClick=${() => activate(p)}>${t('Etkinleştir', 'Activate')}<//>`}
      </div>`)}
    <//>
    <${Banner} tone="info" icon="info">${t('Modern dizüstülerde (Modern Bekleme) Windows yalnızca "Dengeli" planını sunar; performans düzeyi Ayarlar\'daki "Güç modu" ile seçilir. "Nihai Performans" planı masaüstü bilgisayarlar içindir: dizüstünde pil ömrünü ve sıcaklığı olumsuz etkiler, hız kazancı ise ölçülemeyecek kadar küçüktür.',
      'On modern laptops (Modern Standby) Windows offers only the "Balanced" plan; the performance level is chosen with "Power mode" in Settings. The "Ultimate Performance" plan is meant for desktops: on a laptop it hurts battery life and temperature while the speed gain is too small to measure.')}<//>
    <div class="row wrap gap-s">
      ${!d.ultimate && html`<${Btn} icon="plus" onClick=${() => act(() => call('power.unlockUltimate'), (r) => (r.simulated ? t('Simülasyon: plan eklenmedi', 'Simulation: no plan was added') : t('Nihai Performans planı eklendi', 'Ultimate Performance plan added'))).then((r) => r && plans.reload())}>${t('Nihai Performans planını ekle', 'Add the Ultimate Performance plan')}<//>`}
      <${Btn} icon="external" kind="ghost" onClick=${() => openUrl('ms-settings:powersleep')}>${t('Windows güç ayarlarını aç', 'Open Windows power settings')}<//>
    </div>
  </div>`}<//>`;
}

export default function Tweaks({ sub }) {
  const data = useRpc('tweaks.list');
  const info = useStore(app, (s) => s.info);
  const [cat, setCat] = useState(sub === 'power' ? 'power' : 'all');
  const [query, setQuery] = useState('');
  const [only, setOnly] = useState('all');
  const items = data.data ? data.data.items : [];
  const focus = sub && sub !== 'power' ? sub : null;

  useEffect(() => { if (sub === 'power') setCat('power'); }, [sub]);
  useEffect(() => {
    if (focus && data.data) setTimeout(() => { const el = document.getElementById('tw-' + focus); if (el) el.scrollIntoView({ block: 'center' }); }, 60);
  }, [focus, !!data.data]);

  const toggle = (tw, on) => act(() => call('tweaks.apply', { ids: [tw.id], on }), (r) => {
    if (r.simulated) return t('Simülasyon: değişiklik uygulanmadı', 'Simulation: nothing was changed');
    return on ? t('Ayar uygulandı', 'Tweak applied') : t('Windows varsayılanı geri yüklendi', 'Windows default restored');
  }).then((r) => { if (r) { data.reload(); if (!r.simulated && r.applied > 0) restartNotice(r.restart); } });

  const applyPreset = async (p) => {
    const list = items.filter((x) => x.presets.includes(p.id) && x.state !== 'on');
    if (!list.length) { toast('good', t('Bu ön ayarın tüm ayarları zaten uygulanmış', 'Every tweak of this preset is already applied')); return; }
    const needAdmin = list.filter((x) => x.admin).length;
    const ok = await confirmDialog({
      title: t(`"${p.title}" ön ayarı uygulansın mı?`, `Apply the "${p.title}" preset?`),
      body: t(`${list.length} ayar değiştirilecek. Hepsi tek bir kayıt olarak Geri Alma Merkezi'ne yazılır ve tek tıkla geri alınabilir.`, `${list.length} tweaks will be changed. They are recorded as one entry in the Undo Center and can be undone in one click.`)
        + (needAdmin && !info.elevated ? '\n\n' + t(`${needAdmin} tanesi yönetici yetkisi ister ve şimdilik atlanır.`, `${needAdmin} of them need administrator rights and will be skipped for now.`) : ''),
      details: list.map((x) => '• ' + x.title).join('\n'), confirm: t('Uygula', 'Apply'), wide: true,
    });
    if (!ok) return;
    const r = await act(() => call('tweaks.preset', { id: p.id }), (res) => (res.simulated ? t('Simülasyon: değişiklik uygulanmadı', 'Simulation: nothing was changed') : t(`${res.applied} değişiklik uygulandı`, `${res.applied} changes applied`)));
    if (r) { data.reload(); if (!r.simulated) restartNotice(r.restart); }
  };

  const visible = useMemo(() => items.filter((x) => (cat === 'all' || x.category === cat) && (only === 'all' || (only === 'on' ? x.state !== 'off' : x.state === 'off')) && matches(x.title + ' ' + x.description + ' ' + x.id, query)), [items, cat, query, only]);
  const drift = data.data ? data.data.drift : [];

  return html`<${Page} title=${t('Windows Ayarları', 'Windows Tweaks')}
    desc=${t('Hız vaat eden sihirli düğmeler yok. Her ayarın yanında ne kadar kanıtlı olduğu, neye mal olduğu ve tam olarak neyi değiştirdiği yazar; hepsi geri alınabilir.', 'No magic speed buttons. Each tweak states how well-founded it is, what it costs and exactly what it changes; all of them can be undone.')}
    actions=${html`<${Btn} icon="history" kind="ghost" onClick=${() => go('backup')}>${t('Geri Alma Merkezi', 'Undo Center')}<//>`}>
    <div class="col" style=${{ gap: '14px' }}>
      <${SimulationNote} />
      ${drift.length > 0 && html`<${Banner} tone="warn" icon="undo" action=${html`<${Btn} size="sm" kind="primary" onClick=${() => act(() => call('tweaks.apply', { ids: drift, on: true }), t('Ayarlar yeniden uygulandı', 'Tweaks re-applied')).then((r) => r && data.reload())}>${t('Yeniden uygula', 'Re-apply')}<//>`}>
        ${t(`Uyguladığınız ${drift.length} ayar geri alınmış (genellikle bir Windows güncellemesi yapar).`, `${drift.length} of the tweaks you applied were reset (usually by a Windows update).`)}<//>`}
      ${data.data && cat !== 'power' && html`<div class="grid c4">
        ${data.data.presets.map((p) => {
          const left = items.filter((x) => x.presets.includes(p.id) && x.state !== 'on').length;
          return html`<div key=${p.id} class="card pad col">
            <div class="row"><b class="grow">${p.title}</b>${left === 0 ? html`<${Chip} tone="good" icon="check">${t('Tamam', 'Done')}<//>` : html`<${Chip}>${left}/${p.count}<//>`}</div>
            <div class="small muted grow">${p.description}</div>
            <${Btn} size="sm" kind=${left ? 'primary' : ''} disabled=${!left} onClick=${() => applyPreset(p)}>${left ? t('İncele ve uygula', 'Review and apply') : t('Uygulandı', 'Applied')}<//>
          </div>`;
        })}
      </div>`}
      <div class="row wrap">
        <${Tabs} value=${cat} onChange=${setCat} items=${[{ id: 'all', label: t('Tümü', 'All'), count: items.length },
          ...CATEGORIES.map(([id, tr, en, icon]) => ({ id, label: t(tr, en), icon, count: items.filter((x) => x.category === id).length })),
          { id: 'power', label: t('Güç planları', 'Power plans'), icon: 'battery' }]} />
      </div>
      ${cat === 'power' ? html`<${PowerPlans} />` : html`
        <div class="row wrap">
          <${Search} class="grow" value=${query} onInput=${setQuery} placeholder=${t('Ayar ara…', 'Search tweaks…')} />
          <${Tabs} value=${only} onChange=${setOnly} items=${[{ id: 'all', label: t('Tümü', 'All') }, { id: 'on', label: t('Uygulanmış', 'Applied') }, { id: 'off', label: t('Varsayılan', 'Default') }]} />
        </div>
        ${data.data === undefined ? html`<div class="card pad"><${Loading} lines=${8} /></div>` : (cat === 'all' ? CATEGORIES : CATEGORIES.filter((c) => c[0] === cat)).map(([id, tr, en, icon]) => {
          const rows = visible.filter((x) => x.category === id);
          if (!rows.length) return null;
          return html`<${Card} key=${id} title=${t(tr, en)} icon=${icon} flush sub=${t(`${rows.filter((x) => x.state === 'on').length} / ${rows.length} uygulanmış`, `${rows.filter((x) => x.state === 'on').length} of ${rows.length} applied`)}>
            ${rows.map((tw) => html`<${TweakRow} key=${tw.id} tw=${tw} onToggle=${toggle} highlight=${focus === tw.id} drifted=${drift.includes(tw.id)} />`)}
          <//>`;
        })}
        ${data.data && !visible.length && html`<div class="card"><${Empty} icon="search" title=${t('Eşleşen ayar yok', 'No matching tweak')} /></div>`}
        <${Card} title=${t('OptiCore\'un sunmadığı ayarlar', 'Tweaks OptiCore does not offer')} icon="shieldCheck">
          <div class="small muted">${t('Windows Defender\'ı, Güvenlik Duvarı\'nı, Kullanıcı Hesabı Denetimi\'ni ya da güvenlik güncellemelerini kapatan ayarlar burada yoktur ve olmayacaktır. Birkaç kare ya da birkaç megabayt için bilgisayarı korumasız bırakmak iyi bir takas değildir.',
            'Tweaks that switch off Windows Defender, the Firewall, User Account Control or security updates are not here and never will be. Leaving the PC unprotected for a few frames or megabytes is a bad trade.')}</div>
        <//>`}
    </div>
  <//>`;
}
