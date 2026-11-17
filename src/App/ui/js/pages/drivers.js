// Driver Manager: inventory, problem devices, old drivers, backup and restore, official vendor pages.
import { html, useState, useMemo, useRpc, useJob, call, act, t, num, bytes, date, dateTime, matches, confirmDialog } from '../lib/core.js';
import { Page, Card, Tabs, Btn, Chip, Search, Empty, Loading, Banner, Async, Stat, JobBar, Table, ElevationHint, openUrl } from '../lib/ui.js';

const CLASSES = { DISPLAY: 'Ekran kartı|Display', NET: 'Ağ|Network', MEDIA: 'Ses|Audio', BLUETOOTH: 'Bluetooth|Bluetooth', USB: 'USB|USB', SYSTEM: 'Sistem|System', HDC: 'Depolama denetleyicisi|Storage controller',
  SCSIADAPTER: 'Depolama|Storage', HIDCLASS: 'Giriş aygıtı|Input device', MOUSE: 'Fare|Mouse', KEYBOARD: 'Klavye|Keyboard', CAMERA: 'Kamera|Camera', BIOMETRIC: 'Biyometrik|Biometric',
  MONITOR: 'Monitör|Monitor', PRINTER: 'Yazıcı|Printer', BATTERY: 'Pil|Battery', FIRMWARE: 'Donanım yazılımı|Firmware', DISKDRIVE: 'Disk|Disk', PROCESSOR: 'İşlemci|Processor' };
const cls = (c) => { const v = CLASSES[(c || '').toUpperCase()]; return v ? t(...v.split('|')) : c || '–'; };

export default function Drivers() {
  const list = useRpc('drivers.list');
  const backups = useRpc('drivers.backups');
  const job = useJob();
  const [tab, setTab] = useState('important');
  const [query, setQuery] = useState('');
  const items = list.data ? list.data.items : [];
  const important = ['DISPLAY', 'NET', 'MEDIA', 'BLUETOOTH', 'HDC', 'SCSIADAPTER', 'CAMERA', 'BIOMETRIC', 'SYSTEM', 'USB'];
  const rows = useMemo(() => items.filter((d) => (tab === 'all' || (tab === 'problems' && d.errorCode) || (tab === 'old' && !d.microsoft && d.ageMonths > 36)
    || (tab === 'important' && !d.microsoft && important.includes((d.class || '').toUpperCase()))) && matches(d.device + ' ' + (d.provider || '') + ' ' + d.class, query)), [items, tab, query]);

  const backup = () => act(async () => { const r = await job.run('drivers.backup'); backups.reload(); return r; }, (r) => t(`${r.drivers} sürücü yedeklendi (${bytes(r.bytes)})`, `${r.drivers} drivers backed up (${bytes(r.bytes)})`));
  const restore = (b) => confirmDialog({
    title: t('Yedekteki sürücüler yüklensin mi?', 'Install the drivers from this backup?'),
    body: t('Yedekteki her sürücü Windows sürücü deposuna eklenir ve eşleşen aygıtlara kurulur. Windows daha yeni bir sürüm kuruluysa onu korur.', 'Every driver in the backup is added to the Windows driver store and installed on matching devices. Windows keeps a newer version if one is installed.'),
    confirm: t('Yükle', 'Install'),
  }).then((ok) => ok && act(() => job.run('drivers.restore', { path: b.path }), (r) => (r.reboot ? t('Sürücüler yüklendi; yeniden başlatma gerekiyor', 'Drivers installed; a restart is needed') : t('Sürücüler yüklendi', 'Drivers installed'))));

  return html`<${Page} title=${t('Sürücüler', 'Drivers')} desc=${t('Yüklü sürücüler, sorunlu aygıtlar ve yedekler. OptiCore sürücü indirmez: yanlış bir sürücü bilgisayarı açılmaz hâle getirebilir, bu yüzden sizi üreticinin resmî sayfasına yönlendirir.',
    'Installed drivers, problem devices and backups. OptiCore does not download drivers: a wrong driver can stop a PC from booting, so it points you to the manufacturer\'s official page.')}
    actions=${html`<${Btn} icon="external" kind="ghost" onClick=${() => act(() => call('drivers.deviceManager'))}>${t('Aygıt Yöneticisi', 'Device Manager')}<//>`}>
    <div class="col" style=${{ gap: '14px' }}>
      <div class="grid c4">
        <div class="card pad"><${Stat} label=${t('Sürücü', 'Drivers')} value=${list.data ? num(items.length) : '–'} /></div>
        <div class="card pad"><${Stat} label=${t('Sorunlu aygıt', 'Problem devices')} value=${list.data ? num(list.data.problems) : '–'} tone=${list.data && list.data.problems ? 'bad' : 'good'} /></div>
        <div class="card pad"><${Stat} label=${t('3 yıldan eski', 'Older than 3 years')} value=${list.data ? num(list.data.old) : '–'} sub=${t('önemli sınıflarda', 'in key classes')} /></div>
        <div class="card pad"><${Stat} label=${t('İmzasız', 'Unsigned')} value=${list.data ? num(list.data.unsigned) : '–'} /></div>
      </div>
      ${list.data && list.data.vendors.length > 0 && html`<${Card} title=${t('Resmî sürücü sayfaları', 'Official driver pages')} icon="external">
        <div class="row wrap gap-s">${list.data.vendors.filter((v, i, a) => a.findIndex((x) => x.url === v.url) === i).map((v) => html`<${Btn} key=${v.url} size="sm" icon="external" onClick=${() => openUrl(v.url)}>${v.provider}<//>`)}</div>
        <div class="tiny dim mt-s">${t('Dizüstü bilgisayarlarda önce bilgisayar üreticisinin (Acer, Lenovo…) sayfasını kullanın: oradaki sürücüler o modele göre ayarlanmıştır.', 'On laptops, use the PC maker\'s page (Acer, Lenovo…) first: its drivers are tuned for that model.')}</div>
      <//>`}
      <div class="row wrap">
        <${Tabs} value=${tab} onChange=${setTab} items=${[
          { id: 'important', label: t('Önemli', 'Key drivers') }, { id: 'problems', label: t('Sorunlu', 'Problems'), count: list.data ? list.data.problems : null },
          { id: 'old', label: t('Eski', 'Old'), count: list.data ? list.data.old : null }, { id: 'all', label: t('Tümü', 'All'), count: items.length }, { id: 'backup', label: t('Yedekleme', 'Backup'), icon: 'download' },
        ]} />
        ${tab !== 'backup' && html`<${Search} class="grow" value=${query} onInput=${setQuery} />`}
      </div>
      <${JobBar} job=${job.job} />
      ${tab === 'backup' ? html`<div class="col gap-l">
        <${ElevationHint}>${t('Sürücü yedekleme ve geri yükleme yönetici yetkisi ister.', 'Driver backup and restore need administrator rights.')}<//>
        <div class="card pad row"><div class="grow"><div class="b">${t('Üçüncü taraf sürücüleri yedekle', 'Back up third-party drivers')}</div>
          <div class="small muted">${t('Windows ile gelmeyen tüm sürücüler (ekran kartı, ses, Wi-Fi, yonga seti…) Windows\'un kendi pnputil aracıyla dışa aktarılır. Windows\'u yeniden kurduğunuzda tek tıkla geri yüklenir.', 'Every driver not shipped with Windows (graphics, audio, Wi-Fi, chipset…) is exported with Windows\' own pnputil tool. After reinstalling Windows it is restored in one click.')}</div></div>
          <${Btn} kind="primary" icon="download" busy=${job.running} onClick=${backup}>${t('Şimdi yedekle', 'Back up now')}<//></div>
        <${Async} state=${backups}>${(b) => (!b.length ? html`<div class="card"><${Empty} icon="download" title=${t('Henüz yedek yok', 'No backups yet')} /></div>` : html`<div class="card">${b.map((x) => html`<div key=${x.path} class="item">
          <div class="grow"><div class="t">${dateTime(x.time)}</div><div class="s mono">${x.path}</div></div><${Chip}>${x.drivers} ${t('sürücü', 'drivers')} · ${bytes(x.bytes)}<//>
          <${Btn} size="sm" icon="folder" onClick=${() => act(() => call('shell.reveal', { path: x.path }))} /><${Btn} size="sm" icon="upload" onClick=${() => restore(x)}>${t('Geri yükle', 'Restore')}<//>
        </div>`)}</div>`)}<//>
      </div>` : html`<div class="card">${list.data === undefined ? html`<div style=${{ padding: '16px' }}><${Loading} lines=${10} /></div>` : html`<${Table} rows=${rows} rowKey=${(d) => d.deviceId + d.inf} maxHeight="calc(100vh - 470px)"
        empty=${html`<${Empty} icon="check" title=${tab === 'problems' ? t('Sorunlu aygıt yok', 'No problem devices') : t('Burada bir şey yok', 'Nothing here')} />`}
        initialSort=${{ id: 'class', dir: 'asc' }} columns=${[
          { id: 'device', label: t('Aygıt', 'Device'), sort: true, render: (d) => html`<div><div class="b">${d.device}</div>${d.error ? html`<div class="tiny bad">${d.error}</div>` : html`<div class="tiny dim">${d.provider || ''}</div>`}</div>` },
          { id: 'class', label: t('Sınıf', 'Class'), sort: true, class: 'shrink', render: (d) => cls(d.class) },
          { id: 'version', label: t('Sürüm', 'Version'), class: 'shrink', render: (d) => html`<span class="mono">${d.version || '–'}</span>` },
          { id: 'date', label: t('Tarih', 'Date'), sort: (d) => (d.date ? +new Date(d.date) : 0), class: 'shrink', render: (d) => (d.date ? html`${date(d.date)}${!d.microsoft && d.ageMonths > 36 ? html` <${Chip} tone="warn">${t(`${Math.floor(d.ageMonths / 12)} yıl`, `${Math.floor(d.ageMonths / 12)} y`)}<//>` : ''}` : '–') },
          { id: 'signed', label: t('İmza', 'Signed'), class: 'shrink', render: (d) => (d.version ? html`<${Chip} tone=${d.signed ? 'good' : 'warn'}>${d.signed ? t('İmzalı', 'Signed') : t('İmzasız', 'Unsigned')}<//>` : '') },
        ]} />`}</div>`}
    </div>
  <//>`;
}
