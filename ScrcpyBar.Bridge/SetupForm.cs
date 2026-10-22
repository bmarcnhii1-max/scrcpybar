using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Windows.Management.Deployment;

namespace ScrcpyBar.Bridge;

/// <summary>
/// Extracts the first-run payload (widget msix/cer, companion apk) from the exe into
/// %LOCALAPPDATA%\ScrcpyBar\setup so a fresh PC can be configured without any other files.
/// </summary>
public static class SetupResources
{
    public const string WidgetFamilyName = "ScrcpyBar.Widget_hbtnae4dbeyjp";

    private static string SetupDir => AppPaths.DataSubDir("setup");

    public static string WidgetMsixPath => Path.Combine(SetupDir, "widget.msix");
    public static string WidgetCerPath => Path.Combine(SetupDir, "widget.cer");

    public static string MarkerPath => Path.Combine(SetupDir, "setup.done");

    public static bool HasPayload(string logicalName)
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains(logicalName);
    }

    public static bool Extract(string logicalName, string targetPath)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName);
        if (stream == null)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var file = File.Create(targetPath);
        stream.CopyTo(file);
        return true;
    }

    /// <summary>Unpacks everything the setup window may need. Cheap enough to run on every launch.</summary>
    public static void ExtractAll()
    {
        try
        {
            foreach (var (resource, target) in new[]
                     {
                         ("widget.msix", WidgetMsixPath),
                         ("widget.cer", WidgetCerPath),
                         ("companion.apk", Path.Combine(AppPaths.DataDir, "companion.apk"))
                     })
            {
                if (HasPayload(resource) && !File.Exists(target))
                {
                    Extract(resource, target);
                }
            }
        }
        catch
        {
            // A missing unpacked payload only disables the corresponding setup button.
        }
    }

    public static bool IsSetupDone()
    {
        return File.Exists(MarkerPath);
    }

    public static void MarkSetupDone()
    {
        try
        {
            Directory.CreateDirectory(SetupDir);
            File.WriteAllText(MarkerPath, DateTime.Now.ToString("s"));
        }
        catch
        {
        }
    }

    public static bool IsWidgetInstalled()
    {
        try
        {
            var manager = new PackageManager();
            var packages = manager.FindPackagesForUser("", "ScrcpyBar.Widget");
            return packages != null && packages.Any();
        }
        catch
        {
            // Can't tell → don't nag the user with the setup window.
            return true;
        }
    }
}

/// <summary>
/// First-run setup: installs the Game Bar widget from the embedded msix (certificate +
/// registration + loopback in one elevated script) and the companion app on the phone.
/// </summary>
public sealed class SetupForm : Form
{
    private readonly IServiceProvider _services;

    private readonly Label _widgetStatus;
    private readonly Label _companionStatus;
    private readonly Button _btnWidget;
    private readonly Button _btnCompanion;
    private readonly Button _btnDone;

    public SetupForm(IServiceProvider services)
    {
        _services = services;

        Text = "Первоначальная настройка — ScrcpyBar";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 300);
        Font = new Font("Segoe UI", 9.5f);

        var title = new Label
        {
            Text = "Всё, что нужно для работы, уже внутри этого exe.",
            AutoSize = false,
            Location = new Point(24, 16),
            Size = new Size(472, 28),
            Font = new Font("Segoe UI Semibold", 12f)
        };
        Controls.Add(title);

        var widgetHint = new Label
        {
            Text = "1. Виджет в Game Bar (Win+↑) — ставится из встроенного пакета, в процессе будет запрос UAC.",
            AutoSize = false,
            Location = new Point(24, 52),
            Size = new Size(472, 24)
        };
        Controls.Add(widgetHint);

        _widgetStatus = new Label
        {
            AutoSize = false,
            Location = new Point(40, 76),
            Size = new Size(400, 22),
            ForeColor = Color.DimGray
        };
        Controls.Add(_widgetStatus);

        _btnWidget = new Button
        {
            Text = "Установить виджет",
            Location = new Point(40, 100),
            Size = new Size(180, 32)
        };
        Controls.Add(_btnWidget);

        var companionHint = new Label
        {
            Text = "2. Приложение-компаньон на телефоне (USB-отладка должна быть включена).",
            AutoSize = false,
            Location = new Point(24, 148),
            Size = new Size(472, 24)
        };
        Controls.Add(companionHint);

        _companionStatus = new Label
        {
            AutoSize = false,
            Location = new Point(40, 172),
            Size = new Size(436, 22),
            ForeColor = Color.DimGray
        };
        Controls.Add(_companionStatus);

        _btnCompanion = new Button
        {
            Text = "Установить companion на телефон",
            Location = new Point(40, 196),
            Size = new Size(280, 32)
        };
        Controls.Add(_btnCompanion);

        _btnDone = new Button
        {
            Text = "Готово",
            Location = new Point(40, 248),
            Size = new Size(120, 32)
        };
        Controls.Add(_btnDone);

        _btnWidget.Click += (_, _) => _ = InstallWidgetAsync();
        _btnCompanion.Click += (_, _) => _ = InstallCompanionAsync();
        _btnDone.Click += (_, _) => Close();

        RefreshState();
    }

    private void RefreshState()
    {
        if (!SetupResources.HasPayload("widget.msix"))
        {
            _widgetStatus.Text = "В этом exe нет встроенного пакета — собери виджет (rebuild_and_deploy.ps1).";
            _btnWidget.Enabled = false;
        }
        else if (SetupResources.IsWidgetInstalled())
        {
            _widgetStatus.Text = "Виджет установлен.";
            _btnWidget.Text = "Переустановить";
        }
        else
        {
            _widgetStatus.Text = "Виджет не найден.";
        }

        var apk = Path.Combine(AppPaths.DataDir, "companion.apk");
        if (!SetupResources.HasPayload("companion.apk") && !File.Exists(apk))
        {
            _companionStatus.Text = "APK-файл companion не встроен — собери ScrcpyBar.Android (gradlew assembleDebug).";
            _btnCompanion.Enabled = false;
        }
        else
        {
            _companionStatus.Text = "Понадобится включённая USB-отладка и кабель.";
        }
    }

    private async System.Threading.Tasks.Task InstallWidgetAsync()
    {
        _btnWidget.Enabled = false;
        _widgetStatus.ForeColor = Color.DimGray;
        _widgetStatus.Text = "Установка… подтверди запрос UAC.";

        try
        {
            if (!SetupResources.HasPayload("widget.msix") || !SetupResources.HasPayload("widget.cer"))
            {
                _widgetStatus.ForeColor = Color.Firebrick;
                _widgetStatus.Text = "Пакет виджета не встроен в exe.";
                return;
            }

            SetupResources.Extract("widget.msix", SetupResources.WidgetMsixPath);
            SetupResources.Extract("widget.cer", SetupResources.WidgetCerPath);

            string scriptPath = Path.Combine(AppPaths.DataSubDir("setup"), "install-widget.ps1");
            string script =
                "$ErrorActionPreference = 'Stop'\n" +
                $"certutil.exe -addstore -f TrustedPeople \"{SetupResources.WidgetCerPath}\"\n" +
                $"Add-AppxPackage -Path \"{SetupResources.WidgetMsixPath}\"\n" +
                $"CheckNetIsolation.exe LoopbackExempt -a \"-n={SetupResources.WidgetFamilyName}\"\n" +
                "exit 0\n";
            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                Verb = "runas",
                UseShellExecute = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                throw new InvalidOperationException("не удалось запустить установщик");
            }

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"установщик вышел с кодом {process.ExitCode}");
            }

            if (!SetupResources.IsWidgetInstalled())
            {
                throw new InvalidOperationException("пакет не появился в системе");
            }

            SetupResources.MarkSetupDone();
            _widgetStatus.ForeColor = Color.ForestGreen;
            _widgetStatus.Text = "Виджет установлен — открой Win+↑ и закрепи его в Game Bar.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _widgetStatus.ForeColor = Color.Firebrick;
            _widgetStatus.Text = ex.Message.StartsWith("UAC", StringComparison.OrdinalIgnoreCase)
                ? ex.Message
                : "Установка не удалась: " + ex.Message;
        }
        finally
        {
            _btnWidget.Enabled = true;
        }
    }

    private async System.Threading.Tasks.Task InstallCompanionAsync()
    {
        _btnCompanion.Enabled = false;
        _companionStatus.ForeColor = Color.DimGray;
        _companionStatus.Text = "Установка на телефон… не отключай кабель.";

        try
        {
            var adb = _services.GetRequiredService<AdbService>();
            string result = await adb.SetupCompanionNowAsync();

            if (result == "OK")
            {
                SetupResources.MarkSetupDone();
                _companionStatus.ForeColor = Color.ForestGreen;
                _companionStatus.Text = "Companion установлен — запусти музыку на телефоне.";
            }
            else
            {
                _companionStatus.ForeColor = Color.Firebrick;
                _companionStatus.Text = result;
            }
        }
        catch (Exception ex)
        {
            _companionStatus.ForeColor = Color.Firebrick;
            _companionStatus.Text = "Не получилось: " + ex.Message;
        }
        finally
        {
            _btnCompanion.Enabled = true;
        }
    }
}
