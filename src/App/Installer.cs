using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using Microsoft.Win32;
using OptiCore.Engine;

namespace OptiCore.App;

/// <summary>
/// Per-user installation without a separate installer: the same OptiCore.exe copies itself to
/// %LOCALAPPDATA%\Programs\OptiCore, adds Start menu and desktop shortcuts and an entry under
/// "Installed apps", and removes all of it again with --uninstall. No administrator rights needed.
/// </summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OptiCore";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OptiCore");
    public static string InstalledExe => Path.Combine(InstallDir, "OptiCore.exe");
    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "OptiCore.lnk");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OptiCore.lnk");

    public static bool Tr => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
    public static string S(string tr, string en) => Tr ? tr : en;

    /// <summary>Started as "OptiCore-Kurulum.exe" / "OptiCore-Setup.exe", or with --install.</summary>
    public static bool IsSetupLaunch(string[] args)
    {
        if (args.Contains("--install")) return true;
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        return name.Contains("Kurulum", StringComparison.OrdinalIgnoreCase) || name.Contains("Setup", StringComparison.OrdinalIgnoreCase);
    }

    private static void Shortcut(string link, string target, string args = "")
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic lnk = shell.CreateShortcut(link);
        lnk.TargetPath = target;
        lnk.Arguments = args;
        lnk.WorkingDirectory = Path.GetDirectoryName(target);
        lnk.IconLocation = target + ",0";
        lnk.Description = "OptiCore";
        lnk.Save();
    }

    private static IEnumerable<Process> OtherInstances() =>
        Process.GetProcessesByName("OptiCore").Where(p => p.Id != Environment.ProcessId);

    private static bool CloseOthers(bool quiet = false)
    {
        var others = OtherInstances().ToList();
        if (others.Count == 0) return true;
        if (quiet) return false;
        if (MessageBox.Show(S("OptiCore şu an açık. Devam etmek için kapatılsın mı?", "OptiCore is running. Close it to continue?"), "OptiCore",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return false;
        foreach (var p in others)
        {
            try { p.Kill(); p.WaitForExit(5000); }
            catch { return false; } // e.g. an elevated instance we may not end
        }
        return true;
    }

    public static bool Install(bool desktop, bool autostart, bool launch, bool quiet = false)
    {
        if (!CloseOthers(quiet)) return false;
        string source = Environment.ProcessPath!;
        Directory.CreateDirectory(InstallDir);
        if (!source.Equals(InstalledExe, StringComparison.OrdinalIgnoreCase)) File.Copy(source, InstalledExe, overwrite: true);

        Directory.CreateDirectory(Path.GetDirectoryName(StartMenuLink)!);
        Shortcut(StartMenuLink, InstalledExe);
        if (desktop) Shortcut(DesktopLink, InstalledExe); else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (autostart) run.SetValue("OptiCore", $"\"{InstalledExe}\" --minimized");
            else run.DeleteValue("OptiCore", false);
        }
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "OptiCore");
            key.SetValue("DisplayVersion", OptiCoreEngine.Version);
            key.SetValue("Publisher", "mirsaidsahin");
            key.SetValue("DisplayIcon", InstalledExe + ",0");
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            key.SetValue("URLInfoAbout", "https://github.com/" + Engine.Modules.UpdateService.Repository);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        if (launch) Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
        return true;
    }

    /// <param name="quiet">No questions: keeps settings and history, fails if OptiCore is running.</param>
    public static bool Uninstall(bool quiet = false)
    {
        if (!quiet && MessageBox.Show(S("OptiCore bu bilgisayardan kaldırılsın mı?\n\nOptiCore'un sistemde yaptığı değişiklikler (ayarlar, başlangıç öğeleri) kendiliğinden geri alınmaz. Geri almak istiyorsanız önce OptiCore'daki Geri Alma Merkezi'ni kullanın.",
                "Remove OptiCore from this PC?\n\nChanges OptiCore made to the system (tweaks, startup items) are not undone automatically. To undo them, use the Undo Center in OptiCore first."),
                "OptiCore", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        if (!CloseOthers(quiet)) return false;
        bool wipe = !quiet && MessageBox.Show(S("OptiCore'un ayarları ve geçmişi (ölçümler, değişiklik günlüğü) de silinsin mi?", "Also delete OptiCore's settings and history (measurements, change journal)?"),
            "OptiCore", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        foreach (var link in new[] { StartMenuLink, DesktopLink }) try { if (File.Exists(link)) File.Delete(link); } catch { /* in use */ }
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) run?.DeleteValue("OptiCore", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        RunHidden(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/Delete /TN \"\\OptiCore\\Maintenance\" /F");
        if (wipe) try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptiCore"), true); } catch { /* partially in use */ }

        // The running exe cannot delete itself: a hidden command removes the folder once this process has exited.
        RunHidden(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"");
        if (!quiet) MessageBox.Show(S("OptiCore kaldırıldı.", "OptiCore was removed."), "OptiCore", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    private static void RunHidden(string file, string args)
    {
        try { Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }); } catch { /* best effort */ }
    }
}

/// <summary>The small setup window shown when OptiCore-Kurulum.exe is opened.</summary>
internal sealed class InstallerForm : Form
{
    public InstallerForm()
    {
        string S(string tr, string en) => Installer.S(tr, en);
        Text = S("OptiCore Kurulumu", "OptiCore Setup");
        using (var stream = typeof(InstallerForm).Assembly.GetManifestResourceStream("opticore.ico")!) Icon = new Icon(stream);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 300);
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;

        var logo = new PictureBox { Image = Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(24, 22, 48, 48) };
        var title = new Label { Text = $"OptiCore {OptiCoreEngine.Version}", Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(84, 22) };
        var sub = new Label { Text = S("Windows için ölçerek optimize eden sistem platformu", "A Windows platform that optimizes by measuring"), AutoSize = true, ForeColor = Color.DimGray, Location = new Point(86, 54) };
        var path = new Label { Text = S("Kurulacağı yer: ", "Install location: ") + Installer.InstallDir, AutoSize = false, ForeColor = Color.DimGray, Bounds = new Rectangle(24, 92, 412, 40) };
        var desktop = new CheckBox { Text = S("Masaüstüne kısayol ekle", "Add a desktop shortcut"), Checked = true, AutoSize = true, Location = new Point(24, 138) };
        var autostart = new CheckBox { Text = S("Windows açılınca arka planda başlat", "Start in the background with Windows"), AutoSize = true, Location = new Point(24, 166) };
        var launch = new CheckBox { Text = S("Kurulumdan sonra OptiCore'u aç", "Open OptiCore after setup"), Checked = true, AutoSize = true, Location = new Point(24, 194) };
        var note = new Label { Text = S("Yönetici yetkisi gerekmez. Ayarlar > Uygulamalar'dan kaldırılabilir.", "No administrator rights needed. Remove it from Settings > Apps."), AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8.5f), Location = new Point(24, 226) };
        var install = new Button { Text = S("Kur", "Install"), Bounds = new Rectangle(254, 252, 90, 32) };
        var cancel = new Button { Text = S("Vazgeç", "Cancel"), Bounds = new Rectangle(350, 252, 90, 32), DialogResult = DialogResult.Cancel };
        install.Click += (_, _) =>
        {
            install.Enabled = false;
            try
            {
                Installer.Install(desktop.Checked, autostart.Checked, launch.Checked);
                if (!launch.Checked) MessageBox.Show(this, S("OptiCore kuruldu.", "OptiCore was installed."), "OptiCore");
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, S("Kurulum tamamlanamadı: ", "Setup could not finish: ") + ex.Message, "OptiCore", MessageBoxButtons.OK, MessageBoxIcon.Error);
                install.Enabled = true;
            }
        };
        AcceptButton = install;
        CancelButton = cancel;
        Controls.AddRange([logo, title, sub, path, desktop, autostart, launch, note, install, cancel]);
    }
}
