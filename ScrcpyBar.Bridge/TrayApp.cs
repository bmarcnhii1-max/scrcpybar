using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.Win32;

namespace ScrcpyBar.Bridge;

/// <summary>
/// Tray icon for the windowless bridge mode: status, web dashboard shortcut,
/// Windows autostart toggle and exit. Created on the WinForms message-loop thread.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private const string DashboardUrl = "http://127.0.0.1:39281";
    private const string AutoRunName = "ScrcpyBar";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly WebApplication _app;
    private readonly Task _hostStart;
    private readonly NotifyIcon _tray;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public TrayApp(WebApplication app, Task hostStart)
    {
        _app = app;
        _hostStart = hostStart;

        _tray = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "ScrcpyBar",
            Visible = true
        };

        var statusItem = new ToolStripMenuItem("Запуск…") { Enabled = false };
        var openItem = new ToolStripMenuItem("Открыть пульт");
        var setupItem = new ToolStripMenuItem("Первоначальная настройка…");
        var autoRunItem = new ToolStripMenuItem("Запускать при входе в Windows") { Checked = IsAutoRunOn() };
        var exitItem = new ToolStripMenuItem("Выход");

        openItem.Click += (_, _) => OpenDashboard();
        _tray.DoubleClick += (_, _) => OpenDashboard();
        setupItem.Click += (_, _) => ShowSetup();
        autoRunItem.Click += (_, _) =>
        {
            var on = !autoRunItem.Checked;
            SetAutoRun(on);
            autoRunItem.Checked = on;
        };
        exitItem.Click += (_, _) =>
        {
            _tray.Visible = false;
            Application.Exit();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(setupItem);
        menu.Items.Add(autoRunItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        _tray.ContextMenuStrip = menu;

        _hostStart.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                statusItem.Text = "Ошибка запуска";
                _tray.ShowBalloonTip(
                    6000,
                    "ScrcpyBar",
                    "Bridge не запустился: " + (t.Exception?.GetBaseException().Message ?? "неизвестная ошибка"),
                    ToolTipIcon.Error);
                return;
            }

            statusItem.Text = "Работает — " + DashboardUrl;
            _tray.Text = "ScrcpyBar — работает";
            _tray.ShowBalloonTip(
                4000,
                "ScrcpyBar",
                "Bridge работает. Двойной клик по значку открывает пульт.",
                ToolTipIcon.Info);
        }, TaskScheduler.FromCurrentSynchronizationContext());

        var ping = new System.Windows.Forms.Timer { Interval = 4000 };
        ping.Tick += async (_, _) =>
        {
            try
            {
                using var response = await _http.GetAsync(DashboardUrl + "/api/track");
                statusItem.Text = response.IsSuccessStatusCode
                    ? "Работает — " + DashboardUrl
                    : "Офлайн — " + DashboardUrl;
                _tray.Text = response.IsSuccessStatusCode ? "ScrcpyBar — работает" : "ScrcpyBar — офлайн";
            }
            catch
            {
                statusItem.Text = "Офлайн — " + DashboardUrl;
                _tray.Text = "ScrcpyBar — офлайн";
            }
        };
        ping.Start();

        // First run (or the widget is gone): offer the one-window setup.
        if (!SetupResources.IsSetupDone() && !SetupResources.IsWidgetInstalled())
        {
            ShowSetup();
        }
    }

    private void ShowSetup()
    {
        foreach (Form form in Application.OpenForms)
        {
            if (form is SetupForm)
            {
                form.Activate();
                return;
            }
        }

        new SetupForm(_app.Services).Show();
    }

    private static void OpenDashboard()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(DashboardUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    protected override void ExitThreadCore()
    {
        try
        {
            _tray.Visible = false;
            _app.StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        base.ExitThreadCore();
    }

    private static bool IsAutoRunOn()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AutoRunName) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void SetAutoRun(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                return;
            }

            if (on)
            {
                key.SetValue(AutoRunName, "\"" + Application.ExecutablePath + "\"");
            }
            else
            {
                key.DeleteValue(AutoRunName, throwOnMissingValue: false);
            }
        }
        catch
        {
        }
    }

    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = new SolidBrush(Color.FromArgb(255, 11, 15, 25));
        using var fg = new SolidBrush(Color.FromArgb(255, 48, 209, 88));
        g.FillEllipse(bg, 1, 1, 30, 30);
        using var font = new Font("Segoe UI Symbol", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("♪", font, fg, new RectangleF(0, 0, 32, 32), format);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
