// System Intelligence: the complete hardware and Windows inventory, battery and thermals.
import {
  html, useState, useEffect, useRpc, t, bytes, num, pct, ghz, temp, date, dateTime, duration, bits, tone, cx, go, act, saveText, call,
} from '../lib/core.js';
import { Page, Card, Tabs, KV, Chip, Bar, Btn, Async, Banner, Empty, Stat, ElevationHint, Icon, Loading } from '../lib/ui.js';
import { LineChart, Ring } from '../lib/charts.js';

const TABS = ['overview', 'memory', 'gpu', 'storage', 'network', 'battery', 'thermal'];
const yes = (v) => (v == null ? null : v ? t('Evet', 'Yes') : t('Hayır', 'No'));

function Secret({ value }) {
  const [shown, setShown] = useState(false);
  if (!value) return null;
  return html`<span class="row gap-s" style=${{ justifyContent: 'flex-end' }}>
    <span class="mono">${shown ? value : value.slice(0, 3) + '•'.repeat(Math.max(4, value.length - 3))}</span>
    <${Btn} size="sm" kind="ghost" icon=${shown ? 'eyeOff' : 'eye'} onClick=${() => setShown(!shown)} title=${t('Göster / gizle', 'Show / hide')} />
  </span>`;
}

function Overview({ hw }) {
  const { computer, os, cpu, board, memory, gpus } = hw;
  const types = { laptop: t('Dizüstü', 'Laptop'), desktop: t('Masaüstü', 'Desktop'), tablet: 'Tablet', 'all-in-one': t('Hepsi bir arada', 'All-in-one'), 'mini-pc': 'Mini PC' };
  return html`<div class="grid c2">
    <${Card} title=${t('Bilgisayar', 'Computer')} icon="monitor">
      <${KV} rows=${[
        [t('Üretici', 'Manufacturer'), computer.manufacturer],
        [t('Model', 'Model'), computer.model],
        [t('Tür', 'Type'), types[computer.type] || computer.type],
        [t('Seri numarası', 'Serial number'), computer.serial && html`<${Secret} value=${computer.serial} />`],
        [t('Anakart', 'Motherboard'), [board.manufacturer, board.product].filter(Boolean).join(' ')],
        ['BIOS', `${board.biosVendor} ${board.biosVersion}`],
        [t('BIOS tarihi', 'BIOS date'), board.biosDate && date(board.biosDate)],
        [t('Donanım yazılımı', 'Firmware'), os.firmware],
      ]} />
    <//>
    <${Card} title="Windows" icon="windows">
      <${KV} rows=${[
        [t('Sürüm', 'Edition'), os.name],
        [t('Güncelleme', 'Version'), os.version],
        [t('Yapı', 'Build'), os.build],
        [t('Mimari', 'Architecture'), os.arch],
        [t('Kurulum', 'Installed'), os.installDate && date(os.installDate)],
        [t('Son açılış', 'Last boot'), dateTime(os.lastBoot)],
        [t('Çalışma süresi', 'Uptime'), duration(os.uptimeSeconds)],
        [t('Güvenli Önyükleme', 'Secure Boot'), os.secureBoot == null ? null : html`<${Chip} tone=${os.secureBoot ? 'good' : 'warn'}>${os.secureBoot ? t('Açık', 'On') : t('Kapalı', 'Off')}<//>`],
        ['TPM', os.tpm ? html`<${Chip} tone="good">${os.tpm}<//>` : html`<${Chip}>${t('Bulunamadı', 'Not found')}<//>`],
        [t('Hızlı Başlangıç', 'Fast Startup'), yes(os.fastStartup)],
      ]} />
    <//>
    <${Card} title=${t('İşlemci', 'Processor')} icon="cpu">
      <${KV} rows=${[
        [t('Model', 'Model'), cpu.name],
        [t('Çekirdek / iş parçacığı', 'Cores / threads'), `${cpu.cores} / ${cpu.threads}`],
        cpu.performanceCores > 0 && [t('Hibrit yapı', 'Hybrid layout'), t(`${cpu.performanceCores} performans + ${cpu.efficiencyCores} verimlilik`, `${cpu.performanceCores} performance + ${cpu.efficiencyCores} efficiency`)],
        [t('Temel hız', 'Base speed'), ghz(cpu.baseMhz)],
        [t('Soket', 'Socket'), cpu.socket],
        ['L2', cpu.l2Kb ? bytes(cpu.l2Kb * 1024) : null],
        ['L3', cpu.l3Kb ? bytes(cpu.l3Kb * 1024) : null],
        [t('Sanallaştırma', 'Virtualization'), yes(cpu.virtualization)],
      ]} />
    <//>
    <${Card} title=${t('Bellek ve grafik', 'Memory and graphics')} icon="ram"
      actions=${html`<${Btn} size="sm" kind="ghost" onClick=${() => go('system/memory')}>${t('Modüller', 'Modules')}<//>`}>
      <${KV} rows=${[
        [t('Takılı bellek', 'Installed memory'), bytes(memory.installedBytes)],
        [t('Kullanılabilir', 'Usable'), bytes(memory.totalBytes)],
        [t('Yuvalar', 'Slots'), memory.slotsTotal ? t(`${memory.slotsUsed} / ${memory.slotsTotal} dolu`, `${memory.slotsUsed} of ${memory.slotsTotal} used`) : null],
        [t('Tür', 'Type'), memory.modules[0] && [memory.modules[0].type, memory.modules[0].speedMhz && memory.modules[0].speedMhz + ' MHz'].filter(Boolean).join(' · ')],
        ...gpus.map((g, i) => [gpus.length > 1 ? t(`Ekran kartı ${i + 1}`, `Graphics ${i + 1}`) : t('Ekran kartı', 'Graphics'), g.name]),
      ]} />
    <//>
  </div>`;
}

function Memory({ hw }) {
  const m = hw.memory;
  const free = m.slotsTotal - m.slotsUsed;
  return html`<div class="col gap-l">
    <div class="grid c4">
      <div class="card pad"><${Stat} label=${t('Takılı', 'Installed')} value=${bytes(m.installedBytes)} /></div>
      <div class="card pad"><${Stat} label=${t('Yuvalar', 'Slots')} value=${`${m.slotsUsed} / ${m.slotsTotal || '?'}`} sub=${free > 0 ? t(`${free} boş yuva`, `${free} free`) : t('Boş yuva yok', 'No free slot')} /></div>
      <div class="card pad"><${Stat} label=${t('En fazla', 'Maximum')} value=${m.maxCapacityBytes ? bytes(m.maxCapacityBytes) : '–'} sub=${t('anakartın desteklediği', 'supported by the board')} /></div>
      <div class="card pad"><${Stat} label=${t('Hız', 'Speed')} value=${m.modules[0] && m.modules[0].configuredMhz ? num(m.modules[0].configuredMhz) : '–'} unit="MHz" /></div>
    </div>
    ${m.maxCapacityBytes > m.installedBytes && html`<${Banner} tone="info" icon="bulb">
      ${free > 0
        ? t(`Yükseltme yolu açık: ${free} boş yuva var ve anakart ${bytes(m.maxCapacityBytes)} belleğe kadar destekliyor.`, `There is room to upgrade: ${free} free slot(s) and the board supports up to ${bytes(m.maxCapacityBytes)}.`)
        : t(`Tüm yuvalar dolu. Belleği artırmak için mevcut modülleri daha büyükleriyle değiştirmek gerekir (en fazla ${bytes(m.maxCapacityBytes)}).`, `All slots are in use. To add memory the existing modules must be replaced by larger ones (up to ${bytes(m.maxCapacityBytes)}).`)}
    <//>`}
    <${Card} title=${t('Bellek modülleri', 'Memory modules')} icon="ram" flush>
      <div class="tbl-wrap"><table class="tbl">
        <thead><tr><th>${t('Yuva', 'Slot')}</th><th class="r">${t('Kapasite', 'Capacity')}</th><th>${t('Tür', 'Type')}</th><th class="r">${t('Hız', 'Speed')}</th><th>${t('Üretici', 'Manufacturer')}</th><th>${t('Parça no', 'Part number')}</th></tr></thead>
        <tbody>${m.modules.map((x) => html`<tr key=${x.slot}>
          <td>${x.slot}</td><td class="r">${bytes(x.capacityBytes)}</td><td>${[x.type, x.formFactor].filter(Boolean).join(' ')}</td>
          <td class="r">${x.configuredMhz || x.speedMhz || '–'} MHz</td><td>${x.manufacturer}</td><td class="mono sel">${x.partNumber}</td>
        </tr>`)}</tbody>
      </table></div>
    <//>
  </div>`;
}

function Graphics({ hw }) {
  const live = useRpc('metrics.live', { history: 90 }, { interval: 1000 });
  const n = live.data && live.data.now;
  return html`<div class="col gap-l">
    ${hw.gpus.map((g, i) => {
      const l = n && n.gpus.find((x) => x.name === g.name);
      const months = g.driverDate ? (Date.now() - new Date(g.driverDate)) / (30.4 * 86400000) : 0;
      return html`<${Card} key=${g.name + i} title=${g.name} icon="gpu" sub=${g.integrated ? t('Tümleşik', 'Integrated') : t('Ayrık', 'Discrete')}>
        <div class="grid c2">
          <${KV} rows=${[
            [t('Üretici', 'Vendor'), g.vendor],
            [t('Sürücü', 'Driver'), g.driverVersion],
            [t('Sürücü tarihi', 'Driver date'), g.driverDate && html`<span>${date(g.driverDate)} ${months > 18 && html`<${Chip} tone="warn">${t(`${num(months)} aylık`, `${num(months)} months old`)}<//>`}</span>`],
            [t('Ayrılmış bellek', 'Dedicated memory'), bytes(g.dedicatedBytes)],
            [t('Paylaşılan bellek', 'Shared memory'), bytes(g.sharedBytes)],
            [t('Çözünürlük', 'Resolution'), g.resolution && `${g.resolution} @ ${g.refreshHz} Hz`],
          ]} />
          <div class="col">
            ${l ? html`
              <div class="row"><span class="muted grow">${t('Kullanım', 'Utilization')}</span><b class="num">${pct(l.usage)}</b></div><${Bar} value=${l.usage} />
              <div class="row"><span class="muted grow">${g.integrated ? t('Paylaşılan bellek', 'Shared memory') : t('Ayrılmış bellek', 'Dedicated memory')}</span>
                <b class="num">${g.integrated ? bytes(l.sharedUsed) + ' / ' + bytes(l.sharedTotal) : bytes(l.memUsed) + ' / ' + bytes(l.memTotal)}</b></div>
              <${Bar} value=${g.integrated ? l.sharedUsed : l.memUsed} max=${g.integrated ? l.sharedTotal || 1 : l.memTotal || 1} tone="info" />
              <div class="row wrap small muted" style=${{ gap: '6px 14px' }}>
                ${l.temp != null && html`<span>${t('Sıcaklık', 'Temperature')}: <b>${temp(l.temp)}</b></span>`}
                ${l.fanRpm != null && html`<span>${t('Fan', 'Fan')}: <b>${num(l.fanRpm)} RPM</b></span>`}
                ${Object.entries(l.engines).map(([k, v]) => html`<span key=${k}>${k}: <b>${pct(v)}</b></span>`)}
              </div>` : html`<${Loading} lines=${3} />`}
          </div>
        </div>
      <//>`;
    })}
    ${live.data && html`<${Card} title=${t('Ekran kartı kullanımı', 'Graphics utilization')} icon="chart" sub=${t('son 90 saniye', 'last 90 seconds')}>
      <${LineChart} series=${[{ label: 'GPU', data: live.data.history.gpu, color: '#34d399' }]} max=${100} format=${(v) => pct(v)} height=${170} />
    <//>`}
  </div>`;
}

function Storage({ hw }) {
  const volumes = useRpc('system.volumes');
  const healthTone = { healthy: 'good', warning: 'warn', unhealthy: 'bad', unknown: null };
  const healthText = { healthy: t('Sağlıklı', 'Healthy'), warning: t('Uyarı', 'Warning'), unhealthy: t('Sorunlu', 'Unhealthy'), unknown: t('Bilinmiyor', 'Unknown') };
  return html`<div class="col gap-l">
    ${hw.disks.some((d) => !d.smartAvailable) && html`<${ElevationHint}>${t('Disk sıcaklığı, yıpranma ve çalışma saati gibi SMART verilerini Windows yalnızca yöneticilere verir.', 'Windows hands SMART data such as disk temperature, wear and power-on hours only to administrators.')}<//>`}
    ${hw.disks.map((d) => html`<${Card} key=${d.index} title=${d.model} icon="disk" sub=${[d.media, d.bus].filter(Boolean).join(' · ')}
        actions=${html`<${Chip} tone=${healthTone[d.health]}>${healthText[d.health]}<//>`}>
      <div class="grid c2">
        <${KV} rows=${[
          [t('Kapasite', 'Capacity'), bytes(d.sizeBytes)],
          [t('Sürücü harfleri', 'Drive letters'), d.letters.map((l) => l + ':').join(' ')],
          [t('Donanım yazılımı', 'Firmware'), d.firmware],
          [t('Seri numarası', 'Serial number'), d.serial && html`<${Secret} value=${d.serial} />`],
          [t('Sistem diski', 'System disk'), yes(d.isSystem)],
        ]} />
        ${d.smartAvailable ? html`<${KV} rows=${[
          [t('Sıcaklık', 'Temperature'), d.temperatureC != null && html`<span class=${tone(d.temperatureC, 55, 68)}>${temp(d.temperatureC)}</span>`],
          [t('Kullanılan ömür', 'Life used'), d.wearPercent != null && pct(d.wearPercent)],
          [t('Çalışma saati', 'Power-on hours'), d.powerOnHours != null && num(d.powerOnHours)],
          [t('Okuma hatası', 'Read errors'), d.readErrors != null && num(d.readErrors)],
          [t('Yazma hatası', 'Write errors'), d.writeErrors != null && num(d.writeErrors)],
          [t('Açma/kapama', 'Start/stop cycles'), d.startStopCycles != null && num(d.startStopCycles)],
        ]} />` : html`<${Empty} icon="lock" title=${t('SMART verisi kilitli', 'SMART data is locked')} text=${t('Yönetici olarak çalıştırıldığında burada görünür.', 'It appears here when OptiCore runs as administrator.')} />`}
      </div>
    <//>`)}
    <${Card} title=${t('Birimler', 'Volumes')} icon="folder">
      <${Async} state=${volumes}>${(list) => html`<div class="col" style=${{ gap: '14px' }}>
        ${list.map((v) => {
          const used = v.totalBytes - v.freeBytes, p = (used / v.totalBytes) * 100;
          return html`<div key=${v.letter}>
            <div class="row"><b>${v.letter}:</b><span class="muted">${v.label}</span><${Chip}>${v.fileSystem}<//><span class="sp num muted">${bytes(v.freeBytes)} ${t('boş', 'free')} / ${bytes(v.totalBytes)}</span></div>
            <div class="mt-s"><${Bar} value=${p} thick tone=${tone(p, 85, 93)} /></div>
          </div>`;
        })}
      </div>`}<//>
    <//>
    <div><${Btn} icon="disk" onClick=${() => go('storage')}>${t('Depolama analizine git', 'Open storage analysis')}<//></div>
  </div>`;
}

function Network() {
  const adapters = useRpc('system.adapters');
  return html`<${Async} state=${adapters}>${(list) => html`<div class="grid auto-lg">
    ${list.map((a) => html`<${Card} key=${a.id} title=${a.name} icon=${a.type === 'wifi' ? 'wifi' : 'ethernet'}
        actions=${html`<${Chip} tone=${a.status === 'up' ? 'good' : null}>${a.status === 'up' ? t('Bağlı', 'Connected') : t('Bağlı değil', 'Disconnected')}<//>`}>
      <div class="small muted ell" style=${{ marginBottom: '6px' }}>${a.description}</div>
      <${KV} rows=${[
        [t('Bağlantı hızı', 'Link speed'), a.speedBps ? bits(a.speedBps / 8) : null],
        ['IPv4', a.ipv4], ['IPv6', a.ipv6],
        [t('Ağ geçidi', 'Gateway'), a.gateway],
        ['DNS', a.dns.join(', ')],
        ['DHCP', a.ipv4 ? yes(a.dhcp) : null],
        ['MAC', a.mac && html`<${Secret} value=${a.mac} />`],
      ]} />
    <//>`)}
  </div>`}<//>`;
}

function Battery() {
  const battery = useRpc('system.battery', {}, { interval: 15000 });
  const history = useRpc('system.batteryHistory');
  return html`<${Async} state=${battery}>${(b) => !b.present
    ? html`<div class="card"><${Empty} icon="battery" title=${t('Bu bilgisayarda pil yok', 'This PC has no battery')} text=${t('Pil analizi dizüstü bilgisayarlarda kullanılabilir.', 'Battery analysis is available on laptops.')} /></div>`
    : html`<div class="col gap-l">
      <div class="grid c4">
        <div class="card pad row gap-l">
          <${Ring} value=${b.healthPercent || 0} size=${96} stroke=${9} color=${b.healthPercent >= 80 ? 'var(--good)' : b.healthPercent >= 60 ? 'var(--warn)' : 'var(--bad)'}>
            <b class="num" style=${{ fontSize: '20px' }}>${b.healthPercent != null ? num(b.healthPercent) : '–'}</b>
          <//>
          <${Stat} label=${t('Pil sağlığı', 'Battery health')} value=${b.healthPercent != null ? pct(b.healthPercent) : '–'} sub=${t('ilk kapasiteye göre', 'of original capacity')} />
        </div>
        <div class="card pad"><${Stat} label=${t('Şarj', 'Charge')} value=${num(b.chargePercent)} unit="%" sub=${b.charging ? t('Şarj oluyor', 'Charging') : b.onAc ? t('Prizde', 'Plugged in') : t('Pilde', 'On battery')} /></div>
        <div class="card pad"><${Stat} label=${b.rateMw >= 0 ? t('Şarj hızı', 'Charge rate') : t('Tüketim', 'Drain')} value=${num(Math.abs(b.rateMw) / 1000, 1)} unit="W"
          sub=${b.estimatedMinutes ? t('kalan ', 'left: ') + duration(b.estimatedMinutes * 60) : null} /></div>
        <div class="card pad"><${Stat} label=${t('Döngü sayısı', 'Cycle count')} value=${b.cycleCount != null ? num(b.cycleCount) : '–'} sub=${b.cycleCount == null ? t('donanım bildirmiyor', 'not reported by firmware') : null} /></div>
      </div>
      <div class="grid c2">
        <${Card} title=${t('Pil bilgileri', 'Battery details')} icon="battery">
          <${KV} rows=${[
            [t('Model', 'Model'), b.name], [t('Üretici', 'Manufacturer'), b.manufacturer], [t('Kimya', 'Chemistry'), b.chemistry],
            [t('Tasarım kapasitesi', 'Design capacity'), b.designMwh ? num(b.designMwh / 1000, 1) + ' Wh' : null],
            [t('Tam şarj kapasitesi', 'Full charge capacity'), b.fullMwh ? num(b.fullMwh / 1000, 1) + ' Wh' : null],
            [t('Kaybedilen kapasite', 'Capacity lost'), b.designMwh && b.fullMwh ? num((b.designMwh - b.fullMwh) / 1000, 1) + ' Wh' : null],
            [t('Kalan enerji', 'Energy left'), b.remainingMwh ? num(b.remainingMwh / 1000, 1) + ' Wh' : null],
            [t('Gerilim', 'Voltage'), b.voltageMv ? num(b.voltageMv / 1000, 2) + ' V' : null],
          ]} />
        <//>
        <${Card} title=${t('Pil ömrünü uzatmak için', 'To make the battery last')} icon="leaf">
          <ol class="steps small">
            <li>${t('Sürekli prizde kullanıyorsanız üreticinin şarj sınırı özelliğini (%60-80) açın; tam dolu beklemek pili en çok yıpratan durumdur.', 'If the laptop is mostly plugged in, enable the manufacturer\'s charge limit (60-80%); sitting at 100% wears a battery the most.')}</li>
            <li>${t('Isı ikinci büyük düşmandır: oyun ve ağır işlerde havalandırmayı kapatmayın.', 'Heat is the second enemy: keep the vents clear during games and heavy work.')}</li>
            <li>${t('Pili düzenli olarak %0\'a kadar boşaltmayın; %20 civarında şarja takmak yeterlidir.', 'Avoid draining to 0% regularly; plugging in around 20% is enough.')}</li>
          </ol>
          <div class="mt"><${Btn} size="sm" icon="sliders" onClick=${() => go('tweaks/power')}>${t('Güç ayarları', 'Power settings')}<//></div>
        <//>
      </div>
      <${Card} title=${t('Kapasite geçmişi', 'Capacity history')} icon="trend" sub=${t('Windows pil raporundan', 'from the Windows battery report')}>
        ${history.data === undefined ? html`<${Loading} lines=${4} />`
          : history.data.length < 2 ? html`<${Empty} icon="trend" title=${t('Henüz yeterli geçmiş yok', 'Not enough history yet')} text=${t('Windows kapasite değişimini haftalık olarak kaydeder; birkaç hafta sonra burada bir eğilim görünür.', 'Windows records capacity changes weekly; a trend appears here after a few weeks.')} />`
          : html`<${LineChart} series=${[{ label: t('Tam şarj kapasitesi', 'Full charge capacity'), data: history.data.map((x) => x.fullMwh / 1000), color: '#34d399' }]}
              labels=${history.data.map((x) => date(x.date))} format=${(v) => num(v, 1) + ' Wh'} height=${200} min=${0} max=${Math.max(...history.data.map((x) => x.designMwh)) / 1000} />`}
      <//>
    </div>`}<//>`;
}

function Thermal() {
  const live = useRpc('metrics.live', { history: 180 }, { interval: 1000 });
  if (!live.data || !live.data.now) return html`<${Loading} lines=${5} />`;
  const n = live.data.now, h = live.data.history;
  const hasTemp = n.temp != null;
  return html`<div class="col gap-l">
    <div class="grid c4">
      <div class="card pad"><${Stat} label=${t('Sıcaklık', 'Temperature')} value=${hasTemp ? num(n.temp) : '–'} unit=${hasTemp ? '°C' : ''} tone=${hasTemp && tone(n.temp, 80, 92) !== 'good' ? tone(n.temp, 80, 92) : null}
        sub=${hasTemp ? (n.zones.length ? t('ACPI termal bölgesi', 'ACPI thermal zone') : t('ekran kartı', 'graphics card')) : t('sensör yok', 'no sensor')} /></div>
      <div class="card pad"><${Stat} label=${t('Isıl kısılma', 'Thermal throttling')} value=${n.throttled ? t('Var', 'Active') : t('Yok', 'None')} tone=${n.throttled ? 'bad' : 'good'}
        sub=${n.zones[0] ? t('pasif sınır ', 'passive limit ') + pct(Math.min(...n.zones.map((z) => z.passiveLimit))) : null} /></div>
      <div class="card pad"><${Stat} label=${t('İşlemci hızı', 'Processor speed')} value=${num(n.cpuMhz / 1000, 2)} unit="GHz" sub=${t('temel hızın ', '') + pct(n.cpuPerf) + t('', ' of base speed')} /></div>
      <div class="card pad"><${Stat} label=${t('İşlemci yükü', 'Processor load')} value=${num(n.cpu)} unit="%" /></div>
    </div>
    ${n.throttled && html`<${Banner} tone="bad" icon="flame">${t('Donanım yazılımı şu an sıcaklığı düşürmek için işlemciyi kısıyor. Hava girişlerini açın; sorun sürüyorsa fan ve soğutucu temizliği gerekir.', 'The firmware is throttling the processor right now to shed heat. Clear the air intakes; if it persists the fan and heatsink need cleaning.')}<//>`}
    <${Card} title=${t('Yük ve hız', 'Load and speed')} icon="chart" sub=${t('son 3 dakika', 'last 3 minutes')}>
      <${LineChart} series=${[{ label: t('İşlemci yükü %', 'CPU load %'), data: h.cpu, color: '#7c6cff' }, { label: t('Hız (temelin %)', 'Speed (% of base)'), data: h.mhz.map((m) => (n.cpuMhz && n.cpuPerf ? (m / (n.cpuMhz / n.cpuPerf * 100)) * 100 : 0)), color: '#22d3ee' }]}
        format=${(v) => num(v)} height=${200} area=${false} />
    <//>
    ${hasTemp && html`<${Card} title=${t('Sıcaklık', 'Temperature')} icon="thermo" sub=${t('son 3 dakika', 'last 3 minutes')}>
      <${LineChart} series=${[{ label: '°C', data: h.temp, color: '#fb7185' }]} format=${(v) => num(v) + ' °C'} height=${170} max=${Math.max(60, ...h.temp) + 10} />
    <//>`}
    <${Card} title=${t('Sensörler', 'Sensors')} icon="thermo">
      ${n.zones.length || n.gpus.some((g) => g.temp != null) ? html`<${KV} rows=${[
        ...n.zones.map((z) => [z.name, html`<span>${temp(z.tempC)} · ${t('pasif sınır', 'passive limit')} ${pct(z.passiveLimit)}</span>`]),
        ...n.gpus.filter((g) => g.temp != null).map((g) => [g.name, temp(g.temp)]),
      ]} />` : html`<div class="muted small">${t('Windows bu donanımda sıcaklık bildirmiyor.', 'Windows reports no temperatures on this hardware.')}</div>`}
      <div class="tiny dim mt">${t('OptiCore çekirdek sürücüsü yüklemez. Bu yüzden yalnızca Windows\'un kendi sunduğu sensörleri (ACPI termal bölgeleri, ekran kartı, yönetici modunda diskler) okur; çekirdek başına işlemci sıcaklığı bu yolla alınamaz. Kısılma tespiti ise sıcaklık değerine değil, donanım yazılımının bildirdiği hız sınırına dayanır ve her sistemde çalışır.',
        'OptiCore installs no kernel driver. It therefore reads only the sensors Windows itself exposes (ACPI thermal zones, the graphics card, disks when elevated); per-core CPU temperature is not available this way. Throttle detection does not depend on a temperature reading: it uses the speed limit reported by the firmware, which works on every system.')}</div>
    <//>
  </div>`;
}

export default function System({ sub }) {
  const [tab, setTab] = useState(TABS.includes(sub) ? sub : 'overview');
  useEffect(() => { if (TABS.includes(sub)) setTab(sub); }, [sub]);
  const hardware = useRpc('system.hardware');

  const exportReport = () => act(async () => {
    const [hw, volumes, adapters, battery] = await Promise.all([call('system.hardware', { refresh: true }), call('system.volumes'), call('system.adapters'), call('system.battery')]);
    const clean = JSON.parse(JSON.stringify({ generated: new Date().toISOString(), hardware: hw, volumes, adapters, battery }));
    // Identifiers stay out of exported reports.
    if (clean.hardware.computer) delete clean.hardware.computer.serial;
    clean.hardware.disks.forEach((d) => delete d.serial);
    clean.adapters.forEach((a) => delete a.mac);
    return saveText(`opticore-system-${new Date().toISOString().slice(0, 10)}.json`, JSON.stringify(clean, null, 2));
  }, t('Sistem raporu kaydedildi', 'System report saved'));

  return html`<${Page} title=${t('Sistem', 'System')}
    desc=${t('Bu bilgisayarın donanım ve Windows envanteri. Tüm değerler doğrudan sistemden okunur.', 'Hardware and Windows inventory of this PC. Every value is read straight from the system.')}
    actions=${html`
      <${Btn} icon="refresh" kind="ghost" onClick=${() => call('system.hardware', { refresh: true }).then(() => hardware.reload())}>${t('Yenile', 'Refresh')}<//>
      <${Btn} icon="download" onClick=${exportReport}>${t('Rapor dışa aktar', 'Export report')}<//>`}>
    <div class="mb"><${Tabs} value=${tab} onChange=${(id) => { setTab(id); go('system/' + id); }} items=${[
      { id: 'overview', label: t('Genel', 'Overview'), icon: 'monitor' },
      { id: 'memory', label: t('Bellek', 'Memory'), icon: 'ram' },
      { id: 'gpu', label: t('Ekran kartı', 'Graphics'), icon: 'gpu' },
      { id: 'storage', label: t('Diskler', 'Disks'), icon: 'disk' },
      { id: 'network', label: t('Ağ', 'Network'), icon: 'wifi' },
      { id: 'battery', label: t('Pil', 'Battery'), icon: 'battery' },
      { id: 'thermal', label: t('Termal', 'Thermal'), icon: 'thermo' },
    ]} /></div>
    <${Async} state=${hardware} lines=${8}>${(hw) =>
      tab === 'overview' ? html`<${Overview} hw=${hw} />`
      : tab === 'memory' ? html`<${Memory} hw=${hw} />`
      : tab === 'gpu' ? html`<${Graphics} hw=${hw} />`
      : tab === 'storage' ? html`<${Storage} hw=${hw} />`
      : tab === 'network' ? html`<${Network} />`
      : tab === 'battery' ? html`<${Battery} />`
      : html`<${Thermal} />`}<//>
  <//>`;
}
