using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using OptiCore.Engine;

namespace OptiCore.App;

/// <summary>
/// In-game overlay: a small click-through, always-on-top panel with FPS, frame time, CPU, GPU and temperature.
/// It draws over windowed and borderless games; exclusive full-screen games take over the display and hide it.
/// </summary>
internal sealed class OverlayForm : Form
{
    private readonly OptiCoreEngine _engine;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly Font _big = new("Segoe UI Semibold", 20f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _small = new("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private string[] _lines = [];
    private string _fps = "–";

    public OverlayForm(OptiCoreEngine engine)
    {
        _engine = engine;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(16, 16);
        Size = new Size(190, 118);
        BackColor = Color.FromArgb(10, 12, 18);
        TransparencyKey = Color.Magenta;
        Opacity = 0.88;
        DoubleBuffered = true;
        _timer.Tick += (_, _) => Refresh_();
        _timer.Start();
        Refresh_();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000; // layered, transparent (click-through), tool window, no-activate
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    private void Refresh_()
    {
        try
        {
            _engine.Metrics.Touch();
            var s = _engine.Metrics.Latest;
            var (active, fps, stats) = _engine.Gaming.OverlayInfo();
            _fps = fps is double f ? $"{f:0} FPS" : active ? "— FPS" : "OptiCore";
            var lines = new List<string>();
            if (stats is { } st) lines.Add($"1% low {st.Low1:0}  ·  {st.MedianMs:0.0} ms");
            if (s is not null)
            {
                double core = s.Cores.Length > 0 ? s.Cores.Max() : s.Cpu;
                lines.Add($"CPU {s.Cpu:0}%  (max {core:0}%)");
                lines.Add($"GPU {s.Gpu:0}%  ·  RAM {s.MemPercent:0}%");
                if (s.Temp is double t) lines.Add($"{t:0} °C{(s.Throttled ? "  · THROTTLE" : "")}");
            }
            _lines = [.. lines];
            Invalidate();
        }
        catch { /* engine busy */ }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var border = new Pen(Color.FromArgb(124, 108, 255), 2)) g.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
        using var accent = new SolidBrush(Color.FromArgb(34, 211, 238));
        using var text = new SolidBrush(Color.FromArgb(233, 237, 246));
        g.DrawString(_fps, _big, accent, 10, 6);
        float y = 36;
        foreach (var line in _lines) { g.DrawString(line, _small, text, 11, y); y += 18; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _big.Dispose(); _small.Dispose(); }
        base.Dispose(disposing);
    }
}
