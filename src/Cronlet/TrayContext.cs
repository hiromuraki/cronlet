using System.Diagnostics;
using System.Text;

namespace HiroMuraki.Cronlet;

/// <summary>
/// 托盘外壳：打开配置 TOML / 重载配置 / 退出。
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly string _configPath;
    private readonly Action _onExit;

    public TrayContext(string configPath, Action onExit, Func<Task<string?>> reload)
    {
        _configPath = configPath;
        _onExit = onExit;

        _menu = new ContextMenuStrip();
        _menu.Items.Add("打开配置 TOML", null, (_, _) => OpenConfig());
        _menu.Items.Add("打开脚本目录", null, (_, _) => OpenFolder(ScriptResolver.ScriptsDirectory));
        _menu.Items.Add("打开日志目录", null, (_, _) => OpenFolder(Log.LogDirectory));

        var reloadItem = new ToolStripMenuItem("重载配置");
        reloadItem.Click += async (_, _) =>
        {
            reloadItem.Enabled = false;
            try
            {
                string? error = await reload();
                if (error is not null)
                {
                    MessageBox.Show(error, "Cronlet", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("重载失败：" + ex.Message, "Cronlet",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                reloadItem.Enabled = true;
            }
        };
        _menu.Items.Add(reloadItem);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Cronlet",
            Visible = true,
            ContextMenuStrip = _menu,
        };
    }

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("HiroMuraki.Cronlet.runner.ico");
        if (stream is not null)
        {
            return new Icon(stream, SystemInformation.SmallIconSize);
        }
        return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    }

    private void OpenConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                File.WriteAllText(_configPath, ConfigLoader.DefaultTemplate, new UTF8Encoding(false));
            }
            Process.Start(new ProcessStartInfo(_configPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开配置失败：{ex.Message}", "Cronlet",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开目录失败：{ex.Message}", "Cronlet",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExitApp()
    {
        _onExit();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _menu.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
