// Overclocking assistant: what this PC can be overclocked with, official tools, XMP/EXPO, Windows boost policy, safe procedure.
import { html, useRpc, call, act, t, num, go } from '../lib/core.js';
import { Page, Card, Btn, Chip, Icon, Banner, Async, KV, Select, openUrl, SimulationNote } from '../lib/ui.js';

const STATUS = { unlocked: ['Açık', 'Unlocked', 'good'], limited: ['Kısmen açık', 'Partly unlocked', 'warn'], locked: ['Kilitli', 'Locked', null], unknown: ['Belirsiz', 'Unknown', null] };
const BOOST = [
  ['0', 'Kapalı (turbo yok)', 'Disabled (no turbo)'], ['3', 'Verimli', 'Efficient enabled'], ['1', 'Etkin (Windows varsayılanı)', 'Enabled (Windows default)'],
  ['4', 'Verimli agresif', 'Efficient aggressive'], ['2', 'Agresif', 'Aggressive'], ['5', 'Agresif (garanti hızda)', 'Aggressive at guaranteed'], ['6', 'Verimli agresif (garanti hızda)', 'Efficient aggressive at guaranteed'],
];

function Tools({ tools }) {
  if (!tools || !tools.length) return null;
  return html`<div class="row wrap gap-s mt">${tools.map((x) => html`<${Btn} key=${x.id} size="sm" icon=${x.installed ? 'check' : 'external'} kind=${x.installed ? '' : 'primary'}
    onClick=${() => openUrl(x.url)}>${x.name}${x.installed ? ' · ' + t('kurulu', 'installed') : ''}<//>`)}</div>`;
}

function Policy({ p, reload }) {
  const set = (setting, power, value) => act(() => call('oc.setPolicy', { setting, power, value: +value }), (r) => (r.simulated ? t('Simülasyon: değiştirilmedi', 'Simulation: not changed') : t('Ayar uygulandı', 'Setting applied'))).then((r) => r && reload());
  const row = (label, desc, setting, options) => html`<div class="item" style=${{ padding: '12px 0', alignItems: 'flex-start' }}>
    <div class="grow"><div class="t">${label}</div><div class="s">${desc}</div></div>
    ${['ac', 'dc'].map((pw) => html`<div key=${pw} class="field" style=${{ minWidth: '170px' }}><label>${pw === 'ac' ? t('Prizde', 'Plugged in') : t('Pilde', 'On battery')}</label>
      <${Select} class="sm" value=${String(p[setting][pw] ?? '')} onChange=${(v) => set(setting, pw, v)} options=${options} /></div>`)}
  </div>`;
  const percents = [100, 99, 95, 90, 80, 70, 50].map((v) => ({ value: String(v), label: '%' + v }));
  return html`<${Card} title=${t('Windows işlemci güç politikası', 'Windows processor power policy')} icon="sliders">
    <div class="small muted">${t('Bu ayarlar her bilgisayarda çalışır ve kilitli işlemcilerde de performansı etkiler. Hız aşırtma değildir: işlemciyi üreticinin sınırları içinde, turbo hızına ne kadar istekli çıkacağını belirler. Her değişiklik Geri Alma Merkezi\'ne kaydedilir.',
      'These settings work on every PC and affect performance on locked CPUs too. It is not overclocking: within the manufacturer\'s limits it sets how eagerly the CPU goes to its turbo speed. Every change is recorded in the Undo Center.')}</div>
    ${row(t('Turbo modu', 'Boost mode'), t('"Agresif" işlemciyi turbo hıza daha hızlı ve daha uzun çıkarır: oyunlarda ve ağır işlerde daha hızlı, ama daha sıcak ve pil tüketimi daha yüksek. "Kapalı" turboyu tamamen kapatır: serin ve sessiz, ama belirgin şekilde yavaş.',
      '"Aggressive" sends the CPU to turbo sooner and keeps it there longer: faster in games and heavy work, but hotter with more battery drain. "Disabled" turns turbo off: cool and quiet, but noticeably slower.'),
      'boost', BOOST.map(([v, tr, en]) => ({ value: v, label: t(tr, en) })))}
    ${row(t('En yüksek işlemci durumu', 'Maximum processor state'), t('%100\'ün altı turboyu ve en yüksek hızı sınırlar. Dizüstünde aşırı ısınma ve fan gürültüsüne karşı %99 sık kullanılan bir çözümdür (turbo kapanır).',
      'Below 100% caps turbo and the top speed. On laptops 99% is a common fix for overheating and fan noise (it turns turbo off).'), 'max', percents)}
    <div class="tiny dim">${t('Değişikliğin etkisini Benchmark > Kararlılık ve ısı testiyle önce ve sonra ölçün.', 'Measure the effect before and after with Benchmark > Stability & thermals.')}</div>
  <//>`;
}

export default function Overclock() {
  const d = useRpc('oc.status');
  return html`<${Page} title=${t('Hız Aşırtma', 'Overclocking')} desc=${t('Bu bilgisayarın neyle hız aşırtılabileceği, hangi resmî aracın kullanılacağı ve sonucun nasıl doğrulanacağı.', 'What this PC can be overclocked with, which official tool to use and how to verify the result.')}
    actions=${html`<${Btn} icon="flame" onClick=${() => go('benchmark')}>${t('Kararlılık testi', 'Stability test')}<//>`}>
    <${Async} state=${d} lines=${8}>${(s) => html`<div class="col" style=${{ gap: '14px' }}>
      <${SimulationNote} />
      <${Banner} tone="warn" icon="alert">${t('OptiCore saat hızı ya da voltaj değerlerini kendisi yazmaz: bunun için çekirdek sürücüsü gerekir ve yanlış bir değer donanıma zarar verebilir, veri bozabilir ya da garantiyi geçersiz kılabilir. Bunun yerine bilgisayarınız için doğru resmî aracı gösterir ve sonucu ölçer.',
        'OptiCore does not write clock or voltage values itself: that needs a kernel driver, and a wrong value can damage hardware, corrupt data or void the warranty. Instead it shows the right official tool for your PC and measures the result.')}<//>

      <${Card} title=${t('İşlemci', 'Processor')} icon="cpu" actions=${html`<${Chip} tone=${STATUS[s.cpu.status][2]}>${t(STATUS[s.cpu.status][0], STATUS[s.cpu.status][1])}<//>`}>
        <div class="b">${s.cpu.name}</div>
        <div class="small muted mt-s">${s.cpu.reason}</div>
        ${s.cpu.tool && html`<${Tools} tools=${[s.cpu.tool]} />`}
        ${s.laptop && s.cpu.status !== 'unlocked' && html`<div class="tiny dim mt">${t('Dizüstü bilgisayarlarda performansı asıl belirleyen soğutmadır: hava girişlerini temiz tutmak ve sert bir zemin kullanmak, çoğu ayardan daha çok kazandırır.', 'On laptops cooling is what really sets performance: keeping the vents clean and using a hard surface gains more than most settings.')}</div>`}
      <//>

      <div class="grid c2">
        ${s.gpus.map((g) => html`<${Card} key=${g.name} title=${t('Ekran kartı', 'Graphics')} icon="gpu" actions=${html`<${Chip} tone=${STATUS[g.status][2]}>${t(STATUS[g.status][0], STATUS[g.status][1])}<//>`}>
          <div class="b">${g.name}</div><div class="small muted mt-s">${g.reason}</div><${Tools} tools=${g.tools} />
        <//>`)}
        <${Card} title=${t('Bellek', 'Memory')} icon="ram" actions=${s.memory.below ? html`<${Chip} tone="warn">${t('XMP/EXPO kapalı olabilir', 'XMP/EXPO may be off')}<//>` : html`<${Chip} tone="good">${t('Nominal hızda', 'At rated speed')}<//>`}>
          <${KV} rows=${[[t('Tür', 'Type'), s.memory.type], [t('Nominal hız', 'Rated speed'), s.memory.rated ? s.memory.rated + ' MHz' : null], [t('Çalışan hız', 'Running at'), s.memory.configured ? s.memory.configured + ' MHz' : null], [t('Modül / yuva', 'Modules / slots'), `${s.memory.modules} / ${s.memory.slots}`]]} />
          <div class="small muted mt-s">${s.memory.below
            ? t('Modülleriniz daha yüksek bir hız için üretilmiş. BIOS\'ta XMP (Intel) ya da EXPO (AMD) profilini açın; bu, hız aşırtmanın en güvenli ve en kolay adımıdır.', 'Your modules are rated for a higher speed. Enable the XMP (Intel) or EXPO (AMD) profile in the BIOS; it is the safest and easiest overclocking step.')
            : s.laptop ? t('Dizüstü bilgisayarlarda bellek hızı BIOS\'ta sabittir.', 'On laptops the memory speed is fixed in the BIOS.')
            : t('Bellek nominal hızında çalışıyor.', 'Memory is running at its rated speed.')}
            ${s.memory.modules === 1 && s.memory.slots > 1 ? ' ' + t('Tek modül takılı: ikinci bir eş modül eklemek (çift kanal) özellikle tümleşik grafikte büyük fark yaratır.', 'One module installed: adding a matching second module (dual channel) makes a big difference, especially on integrated graphics.') : ''}</div>
        <//>
      </div>

      <${Policy} p=${s.policy} reload=${d.reload} />

      ${(s.cpu.status !== 'locked' || s.gpus.some((g) => g.status === 'unlocked')) && html`<${Card} title=${t('Güvenli hız aşırtma adımları', 'Safe overclocking steps')} icon="list">
        <ol class="steps small">
          <li>${t('Başlamadan önce Benchmark\'ta işlemci, ekran kartı ve kararlılık testlerini çalıştırın: bunlar karşılaştırma için başlangıç noktanız olur.', 'Before you start, run the CPU, graphics and stability tests in Benchmark: they are your baseline for comparison.')}</li>
          <li>${t('Bir seferde tek bir değeri küçük adımlarla değiştirin (ör. +25-50 MHz). Voltajı artırmadan önce otomatik ayar seçeneklerini (PBO, NVIDIA otomatik ayarlama, Adrenalin otomatik hız aşırtma) deneyin.', 'Change one value at a time in small steps (e.g. +25-50 MHz). Before raising voltage, try the automatic options (PBO, NVIDIA automatic tuning, Adrenalin auto overclock).')}</li>
          <li>${t('Her adımdan sonra en az 10 dakika kararlılık testi çalıştırın. İşlemci 90 °C\'yi, ekran kartı 85 °C\'yi sürekli geçiyorsa geri adım atın.', 'After each step run the stability test for at least 10 minutes. Step back if the CPU stays above 90 °C or the GPU above 85 °C.')}</li>
          <li>${t('Donma, mavi ekran ya da oyunda görüntü bozulması hız aşırtmanın kararsız olduğunu gösterir; Tanılama sayfası mavi ekran kodunu gösterir. Son kararlı değere dönün.', 'Freezes, blue screens or graphical artifacts mean the overclock is unstable; the Diagnostics page shows the blue screen code. Go back to the last stable value.')}</li>
          <li>${t('Bitirince Benchmark testlerini tekrarlayın: OptiCore önceki sonuca göre yüzde farkı gösterir. %3\'ün altındaki kazanç ısı ve gürültüye genellikle değmez.', 'When done, repeat the Benchmark tests: OptiCore shows the percentage difference to the previous result. Gains under 3% are usually not worth the heat and noise.')}</li>
        </ol>
      <//>`}
    </div>`}<//>
  <//>`;
}
