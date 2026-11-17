using OptiCore.Engine.Core;

namespace OptiCore.Engine.Modules;

/// <summary>
/// Background alerts for overheating, memory pressure and low disk space. Conditions must hold for a
/// while before a notice is shown, and each notice has a cool-down, so short spikes never nag.
/// </summary>
public sealed class AlertService
{
    private readonly SettingsService _settings;
    private readonly NotificationCenter _notices;
    private readonly SystemInfoService _system;
    private double _hotSeconds, _ramSeconds;
    private long _lastT;
    private DateTime _lastDiskCheck = DateTime.MinValue;

    public AlertService(MetricsSampler metrics, SettingsService settings, NotificationCenter notices, SystemInfoService system)
    {
        _settings = settings; _notices = notices; _system = system;
        metrics.Sampled += OnSample;
    }

    private void OnSample(LiveSample s)
    {
        var a = _settings.Current.Alerts;
        double dt = _lastT == 0 ? 0 : Math.Clamp((s.T - _lastT) / 1000.0, 0, 30);
        _lastT = s.T;
        if (!a.Enabled) { _hotSeconds = _ramSeconds = 0; return; }

        // Overheating: the hottest sensor stayed at or above the threshold for a minute.
        _hotSeconds = s.Temp is double temp && temp >= a.TemperatureC ? _hotSeconds + dt : 0;
        if (_hotSeconds >= 60)
        {
            _notices.Add(NoticeLevel.Critical,
                Loc.T("Bilgisayar çok ısındı: {0:0} °C", "The PC is running hot: {0:0} °C").F(s.Temp ?? 0),
                s.Throttled
                    ? Loc.T("İşlemci ısı yüzünden yavaşlatılıyor. Hava girişlerini kontrol edin; dizüstünü sert ve düz bir zemine alın.", "The processor is being slowed down by heat. Check the air vents; put the laptop on a hard, flat surface.")
                    : Loc.T("Sıcaklık bir dakikadır eşiğin üzerinde. Hava girişlerini kontrol edin ve ağır işleri kısa bir süre durdurun.", "The temperature has been above the threshold for a minute. Check the air vents and pause heavy work for a moment."),
                "system/thermal", "alert.temp", coolDownHours: 1);
            _hotSeconds = 0;
        }

        // Memory pressure: two minutes at or above the threshold.
        _ramSeconds = s.MemPercent >= a.RamPercent ? _ramSeconds + dt : 0;
        if (_ramSeconds >= 120)
        {
            _notices.Add(NoticeLevel.Warn,
                Loc.T("Bellek neredeyse dolu: %{0:0}", "Memory is almost full: {0:0}%").F(s.MemPercent),
                Loc.T("Windows diske sayfalamaya başladığında her şey yavaşlar. En çok bellek tutan uygulamaları İşlemler sayfasında görebilirsiniz.", "Once Windows starts paging to disk everything slows down. The Processes page shows which apps hold the most memory."),
                "processes/memory", "alert.ram", coolDownHours: 2);
            _ramSeconds = 0;
        }

        // Low disk space: checked every ten minutes.
        if (DateTime.UtcNow - _lastDiskCheck > TimeSpan.FromMinutes(10))
        {
            _lastDiskCheck = DateTime.UtcNow;
            try
            {
                foreach (var v in _system.Volumes().Where(v => v.Kind == "fixed" && v.TotalBytes > 8L << 30))
                {
                    double free = 100.0 * v.FreeBytes / v.TotalBytes;
                    if (free >= a.DiskFreePercent) continue;
                    _notices.Add(NoticeLevel.Warn,
                        Loc.T("{0}: sürücüsünde yer azaldı (%{1:0} boş)", "Drive {0}: is low on space ({1:0}% free)").F(v.Letter, free),
                        Loc.T("{0} boş alan kaldı. Akıllı Temizlik genellikle birkaç gigabayt açar.", "{0} free. Smart Cleanup usually frees several gigabytes.").F(new ByteSize(v.FreeBytes)),
                        "cleanup", "alert.disk." + v.Letter, coolDownHours: 24);
                }
            }
            catch (Exception e) { Log.Warn("alerts: disk", e); }
        }
    }
}
