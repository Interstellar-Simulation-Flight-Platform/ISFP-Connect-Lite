using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ISFPConnectLite;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 统一使用 PNG：窗口/任务栏图标从 png 加载
        try
        {
            var uri = new Uri("pack://application:,,,/assets/logo.png", UriKind.Absolute);
            var bmp = new BitmapImage(uri);
            Icon = bmp; // BitmapImage IS an ImageSource, can be used directly as window icon
        }
        catch { /* logo missing — fall back to default icon */ }

        if (e.Args.Length > 0 && e.Args[0] == "--selftest")
        {
            Shutdown(Fsd.FsdSelfTest.RunAll());
            return;
        }
    }

    public static ImageSource? Icon { get; private set; }
}
