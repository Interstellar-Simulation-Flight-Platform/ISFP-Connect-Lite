using System.IO;
using System.Reflection;
using System.Windows;
using ISFPConnectLite.Xlink;

namespace ISFPConnectLite;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        Result = current;
        RealNameBox.Text = current.RealName;
        CidBox.Text = current.Cid;
        PwdBox.Password = current.Password;
        RememberPwd.IsChecked = current.RememberPassword;
        XpDirBox.Text = current.XplaneDir;
        RefreshPluginStatus();
    }

    private void RefreshPluginStatus()
    {
        string dir = XpDirBox.Text.Trim();
        if (dir.Length == 0)
        {
            PluginStatusText.Text = "";
            return;
        }
        bool installed = PluginInstaller.IsInstalled(dir);
        bool valid = PluginInstaller.IsValidXplaneDir(dir);
        PluginStatusText.Text = installed ? "已安装 ISFP-xLink 插件"
            : valid ? "未安装插件"
            : "目录无效（需含 X-Plane.exe）";
    }

    private void BrowseXpDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 X-Plane 安装根目录",
            ValidateNames = false
        };
        if (dlg.ShowDialog(this) == true)
        {
            XpDirBox.Text = dlg.FolderName;
            RefreshPluginStatus();
        }
    }

    private void InstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        string dir = XpDirBox.Text.Trim();
        if (dir.Length == 0)
        {
            MessageBox.Show(this, "请先填写 X-Plane 目录", "ISFP-Connect-Lite",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!PluginInstaller.IsValidXplaneDir(dir))
        {
            MessageBox.Show(this, "该目录不是有效的 X-Plane 根目录（未找到 X-Plane.exe）",
                "ISFP-Connect-Lite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        InstallPluginBtn.IsEnabled = false;
        try
        {
            (int files, bool already) = PluginInstaller.Install(dir);
            PluginStatusText.Text = "已安装 ISFP-xLink 插件";
            ShowToastMsg(already ? $"插件已更新（覆盖 {files} 个文件）" : $"插件安装完成（{files} 个文件）");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"插件安装失败: {ex.Message}", "ISFP-Connect-Lite",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            InstallPluginBtn.IsEnabled = true;
        }
    }

    private void ShowToastMsg(string _) { /* status shown via PluginStatusText */ }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string realName = RealNameBox.Text.Trim();
        string cid = CidBox.Text.Trim();
        string pwd = PwdBox.Password;

        if (realName.Length < 2)
        {
            MessageBox.Show(this, "请填写真实姓名", "ISFP-Connect-Lite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!cid.All(char.IsDigit) || cid.Length < 4)
        {
            MessageBox.Show(this, "CID 必须为至少 4 位数字", "ISFP-Connect-Lite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (pwd.Length == 0)
        {
            MessageBox.Show(this, "请填写密码", "ISFP-Connect-Lite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result.RealName = realName;
        Result.Cid = cid;
        Result.Password = pwd;
        Result.RememberPassword = RememberPwd.IsChecked == true;
        Result.XplaneDir = XpDirBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_DragMove(object sender, System.Windows.Input.MouseButtonEventArgs e) => DragMove();
}
