// One-click optimization dialog: plan → review → apply → before/after, with a single undo.
import { html, useState, useEffect, useJob, call, act, t, num, bytes, app, useStore } from '../lib/core.js';
import { Modal, Btn, Check, Chip, Icon, JobBar, Banner, Loading } from '../lib/ui.js';

const ICON = { cleanup: 'sparkles', tweaks: 'sliders', fix: 'wrench' };

function Delta({ label, before, after, fmt, better = 1 }) {
  const d = after - before;
  const good = d * better > 0;
  return html`<div class="card flat pad"><div class="up">${label}</div>
    <div class="row" style=${{ alignItems: 'baseline', gap: '8px' }}><span class="dim num">${fmt(before)}</span><${Icon} name="chevR" size="14" class="dim" /><b class="num" style=${{ fontSize: '18px' }}>${fmt(after)}</b>
      ${d !== 0 && html`<${Chip} tone=${good ? 'good' : 'warn'}>${d > 0 ? '+' : '−'}${fmt(Math.abs(d))}<//>`}</div></div>`;
}

export function OptimizeDialog({ onClose, onDone }) {
  const info = useStore(app, (s) => s.info);
  const plan = useJob();
  const run = useJob();
  const [steps, setSteps] = useState(null);
  const [chosen, setChosen] = useState(new Set());
  const [result, setResult] = useState(null);
  const [undone, setUndone] = useState(false);

  useEffect(() => {
    act(async () => {
      const p = await plan.run('optimize.plan');
      setSteps(p.steps);
      setChosen(new Set(p.steps.map((s) => s.id)));
    });
  }, []);

  const apply = () => act(async () => {
    const r = await run.run('optimize.run', { steps: steps.filter((s) => chosen.has(s.id)) });
    setResult(r);
    onDone && onDone();
  });
  const undo = () => act(async () => {
    for (const id of [...result.changeSets].reverse()) await call('journal.revert', { id }).catch(() => {});
    setUndone(true);
    onDone && onDone();
  }, t('Optimizasyon geri alındı (silinen önbellek dosyaları hariç)', 'Optimization undone (except deleted cache files)'));

  const busy = plan.running || run.running;
  return html`<${Modal} wide title=${t('Tek tık optimizasyon', 'One-click optimization')} onClose=${busy ? null : onClose}
    footer=${result
      ? html`${!result.simulated && result.changeSets.length > 0 && !undone && html`<${Btn} kind="ghost" icon="undo" onClick=${undo}>${t('Hepsini geri al', 'Undo all')}<//>`}<${Btn} kind="primary" onClick=${onClose}>${t('Tamam', 'Done')}<//>`
      : html`<${Btn} kind="ghost" disabled=${busy} onClick=${onClose}>${t('Vazgeç', 'Cancel')}<//><${Btn} kind="primary" icon="bolt" disabled=${busy || !steps || !chosen.size} onClick=${apply}>${t('Seçilenleri uygula', 'Apply selected')}<//>`}>
    <div class="col">
      ${!result && html`<div class="small muted">${t('OptiCore yalnızca güvenli ve kanıtlı adımları önerir. Uygulamadan önce ve sonra ölçer; temizlenen önbellek dosyaları dışında her şey tek tıkla geri alınabilir.',
        'OptiCore only suggests safe, proven steps. It measures before and after; except for deleted cache files, everything can be undone in one click.')}</div>`}
      ${info.dryRun && html`<${Banner} tone="acc" icon="flask">${t('Simülasyon modu: hiçbir şey değiştirilmeyecek.', 'Simulation mode: nothing will be changed.')}<//>`}
      <${JobBar} job=${plan.job} />
      <${JobBar} job=${run.job} />
      ${!steps && !plan.running && html`<${Loading} lines=${3} />`}
      ${steps && !result && (steps.length === 0
        ? html`<div class="empty"><${Icon} name="check" /><b>${t('Yapılacak bir şey yok', 'Nothing to do')}</b><div class="small">${t('Bu bilgisayar zaten optimize edilmiş görünüyor.', 'This PC already looks optimized.')}</div></div>`
        : html`<div class="card flat">${steps.map((s) => html`<label key=${s.id} class="item" style=${{ cursor: 'pointer', alignItems: 'flex-start' }}>
            <${Check} checked=${chosen.has(s.id)} disabled=${run.running} onChange=${(on) => { const n = new Set(chosen); if (on) n.add(s.id); else n.delete(s.id); setChosen(n); }} />
            <${Icon} name=${ICON[s.kind] || 'check'} size="16" class="acc" />
            <div class="grow"><div class="t">${s.title}</div><div class="s">${s.detail}</div></div>
            ${!s.reversible && html`<${Chip} title=${t('Silinen önbellek dosyaları geri gelmez, ama programlar onları yeniden oluşturur.', 'Deleted cache files do not come back, but programs rebuild them.')}>${t('Kalıcı', 'Permanent')}<//>`}
          </label>`)}</div>
          ${!info.elevated && html`<div class="tiny dim">${t('Yönetici yetkisi isteyen adımlar listelenmedi. Daha fazlası için OptiCore\'u yönetici olarak çalıştırın.', 'Steps that need administrator rights are not listed. Run OptiCore as administrator for more.')}</div>`}`)}
      ${result && html`
        <${Banner} tone=${result.results.every((r) => r.ok) ? 'good' : 'warn'} icon="check">${result.simulated ? t('Simülasyon tamamlandı.', 'Simulation finished.') : undone ? t('Değişiklikler geri alındı.', 'Changes were undone.') : t(`${result.results.filter((r) => r.ok).length} adım uygulandı.`, `${result.results.filter((r) => r.ok).length} steps applied.`)}<//>
        <div class="grid c2">
          <${Delta} label=${t('Sağlık puanı', 'Health score')} before=${result.before.score} after=${result.after.score} fmt=${(v) => num(v)} />
          <${Delta} label=${t('Sistem diskinde boş alan', 'Free space on the system drive')} before=${result.before.systemFree} after=${result.after.systemFree} fmt=${bytes} />
          <${Delta} label=${t('Temizlenebilir alan', 'Reclaimable space')} before=${result.before.junkBytes} after=${result.after.junkBytes} fmt=${bytes} better=${-1} />
          <${Delta} label=${t('Uygulanmış önerilen ayar', 'Recommended tweaks applied')} before=${result.before.tweaksApplied} after=${result.after.tweaksApplied} fmt=${(v) => num(v)} />
        </div>
        ${result.simulated && html`<div class="tiny dim">${t('Simülasyonda "sonra" değerleri, adımlar gerçekten uygulansaydı ne olacağını gösterir; disk ve ayarlar değişmedi.', 'In simulation the "after" values show what would happen if the steps were really applied; the disk and settings did not change.')}</div>`}
        ${result.results.filter((r) => !r.ok).map((r) => html`<${Banner} key=${r.id} tone="warn">${r.title}: ${r.error}<//>`)}`}
    </div>
  <//>`;
}
