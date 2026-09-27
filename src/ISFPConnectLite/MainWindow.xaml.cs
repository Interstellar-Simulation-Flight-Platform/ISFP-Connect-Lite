using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ISFPConnectLite.Fsd;
using ISFPConnectLite.Xlink;

namespace ISFPConnectLite;

public partial class MainWindow : Window
{
    private readonly NetworkModel _net = new();
    private readonly DispatcherTimer _rosterTimer = new();
    private readonly DispatcherTimer _toastTimer = new();
    private AppSettings _settings = new();
    private MessagePanel? _msgPanel;
    private readonly System.Collections.ObjectModel.ObservableCollection<MsgItem> _msgHistory = new();
    private bool _busy;
    private int _lastFreqKhz;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTheme();

        _settings = AppSettings.Load();
        CallsignBox.Text = _settings.Callsign;
        EquipBox.Text = _settings.AircraftIcao;
        AirlineBox.Text = _settings.AirlineIcao;
        _net.AircraftIcao = _settings.AircraftIcao;
        _net.AirlineIcao = _settings.AirlineIcao;
        _net.LiveryIcao = "";

        _net.Log += m => System.Diagnostics.Debug.WriteLine(m);
        _net.Xlink.ConnectedEvent += () => Post(() => ShowToast("已连接 X-Plane (xLink)"));
        _net.Xlink.DisconnectedEvent += () => Post(() => ShowToast("与 X-Plane 断开"));
        _net.Xlink.FlightData += OnFlightData;
        _net.LocalFlightData += OnFlightData;
        _net.RosterChanged += () => Post(UpdateRoster);
        _net.TextReceived += OnNetworkTextReceived;
        _net.FsdUnexpectedlyDisconnected += OnFsdUnexpectedlyDisconnected;

        _rosterTimer.Interval = TimeSpan.FromSeconds(3);
        _rosterTimer.Tick += async (_, _) => await _net.PushRosterAsync();

        _toastTimer.Interval = TimeSpan.FromSeconds(4);
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

        // auto-connect xlink at startup (sim may not be running yet, that's fine)
        _ = TryConnectXlinkAsync();

        // wait for SizeToContent to measure, then position
        Loaded += (_, _) => PositionBottomRight();
    }

    private void ApplyTheme()
    {
        // hook: light/dark theme switch can be added here later
    }

    private void PositionBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 24;
        Top = wa.Bottom - Height - 24;
    }

    private static void Post(Action a) =>
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, a);

    // ---------- xlink ----------

    private async Task TryConnectXlinkAsync()
    {
        bool ok = await _net.Xlink.ConnectAsync();
        if (!ok) ShowToast("未检测到 X-Plane (xLink 插件未运行)");
    }

    private void OnFlightData(FlightData fd)
    {
        _lastFreqKhz = fd.Com1Freq;
        Post(() =>
        {
            FreqText.Text = fd.Com1Freq > 0 ? FsdPacket.MillihertzToDisplay(fd.Com1Freq) : "---.---";
            SquawkText.Text = fd.Transponder > 0 ? fd.Transponder.ToString("D4") : "----";
            GsText.Text = $"{Math.Round(fd.Groundspeed)}";
            AltText.Text = $"{Math.Round(fd.AltitudeMsl / 100.0) * 100:00000}";
        });
    }

    private void UpdateRoster()
    {
        // counts only; xLink receives the full lists via PushRosterAsync
    }

    // ---------- connect / disconnect ----------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string callsign = CallsignBox.Text.Trim().ToUpperInvariant();
        string equip = EquipBox.Text.Trim().ToUpperInvariant();
        string airline = AirlineBox.Text.Trim().ToUpperInvariant();
        string cid = _settings.Cid.Trim();
        string pwd = _settings.Password;
        string realName = _settings.RealName.Trim();

        // 强制校验：呼号 / 机型ICAO / 航司ICAO(涂装) / CID / 密码 / RealName
        if (callsign.Length < 2) { ShowToast("请输入有效呼号"); return; }
        if (equip.Length != 4 || !equip.All(char.IsLetterOrDigit)) { ShowToast("机型 ICAO 无效，请填写 4 位代码（如 A320）"); return; }
        if (airline.Length < 2) { ShowToast("航司 ICAO（涂装）未填写"); return; }
        if (!cid.All(char.IsDigit) || cid.Length < 4) { ShowToast("CID 未填写，请在设置中填写"); return; }
        if (pwd.Length == 0) { ShowToast("密码未填写，请在设置中填写"); return; }
        if (realName.Length < 2) { ShowToast("真实姓名未填写，请在设置中填写"); return; }

        _busy = true;
        ConnectBtn.IsEnabled = false;
        try
        {
            // remember settings
            _settings.Callsign = callsign;
            _settings.AircraftIcao = equip;
            _settings.AirlineIcao = airline;
            _settings.Save();

            if (!_net.Xlink.Connected)
            {
                ShowToast("正在连接 X-Plane...");
                await _net.Xlink.ConnectAsync();
                if (!_net.Xlink.Connected)
                {
                    ShowToast("无法连接 X-Plane，请先启动模拟器");
                    return;
                }
            }

            ShowToast("正在登录 ISFP 网络...");
            int simType = await DetectSimTypeAsync();
            await _net.StartFsdAsync("fsd.flyisfp.com", FsdLink.DefaultPort, callsign, cid, pwd, realName, simType);

            SwitchToOnline(callsign);
            _rosterTimer.Start();
            ShowToast($"已连线 {callsign}，祝飞行愉快！");
        }
        catch (Exception ex)
        {
            ShowToast($"连线失败: {ex.Message}");
        }
        finally
        {
            _busy = false;
            ConnectBtn.IsEnabled = true;
        }
    }

    private static async Task<int> DetectSimTypeAsync()
    {
        // X-Plane only for now; xp11=15, xp12=16 — assume 12 by default
        return await Task.FromResult(FsdPacket.SimTypeXp12);
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _rosterTimer.Stop();
        // close message panel first (it subscribes to Fsd events being torn down below)
        MsgBtn.IsChecked = false;
        _msgPanel?.Close();
        _msgPanel = null;
        try { await _net.StopFsdAsync(); } catch { }
        SwitchToLogin();
        ShowToast("已断开连接");
    }

    /// <summary>FSD 异常掉线（非用户主动断开）：停定时器、关消息面板、回登录态并提示。</summary>
    private void OnFsdUnexpectedlyDisconnected()
    {
        Post(() =>
        {
            _rosterTimer.Stop();
            MsgBtn.IsChecked = false;
            _msgPanel?.Close();
            _msgPanel = null;
            SwitchToLogin();
            ShowToast("与 ISFP 网络断开连接");
        });
    }

    private void SwitchToOnline(string callsign)
    {
        CallsignText.Text = callsign;
        LoginPanel.Visibility = Visibility.Collapsed;
        OnlinePanel.Visibility = Visibility.Visible;
        MsgBtn.Visibility = Visibility.Visible;
        DiscBtn.Visibility = Visibility.Visible;
        // width changed -> re-anchor to bottom-right
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, PositionBottomRight);
    }

    private void SwitchToLogin()
    {
        LoginPanel.Visibility = Visibility.Visible;
        OnlinePanel.Visibility = Visibility.Collapsed;
        MsgBtn.Visibility = Visibility.Collapsed;
        DiscBtn.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, PositionBottomRight);
    }

    // ---------- messages ----------

    private void Msg_Checked(object sender, RoutedEventArgs e)
    {
        if (_msgPanel == null || !_msgPanel.IsLoaded)
        {
            _msgPanel = new MessagePanel(_net, _msgHistory) { Owner = this };
            _msgPanel.Closed += (_, _) => MsgBtn.IsChecked = false;
            _msgPanel.Show();
        }
        _msgPanel.Activate();
    }

    private void Msg_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_msgPanel != null && _msgPanel.IsLoaded)
            _msgPanel.Close();
        _msgPanel = null;
    }

    // ---------- new message notification ----------

    private System.Media.SoundPlayer? _notifySound;

    /// <summary>Lazy-load embedded notify.wav; falls back to system sound if missing.</summary>
    private System.Media.SoundPlayer NotifySound
    {
        get
        {
            if (_notifySound == null)
            {
                try
                {
                    var uri = new Uri("pack://application:,,,/assets/notify.wav", UriKind.Absolute);
                    var sri = Application.GetResourceStream(uri);
                    if (sri?.Stream != null)
                    {
                        // 复制到独立 MemoryStream：资源流可能延迟加载/被回收，SoundPlayer 需要
                        // 一个始终可读的流
                        var ms = new MemoryStream();
                        sri.Stream.CopyTo(ms);
                        ms.Position = 0;
                        _notifySound = new System.Media.SoundPlayer(ms);
                    }
                }
                catch { }
                _notifySound ??= new System.Media.SoundPlayer();
            }
            return _notifySound;
        }
    }

    /// <summary>
    /// 唯一的消息历史写入点：所有 FSD 文本消息先进历史，再按面板状态决定是否响铃提醒。
    /// MessagePanel 通过绑定 _msgHistory 自动显示新消息（包括面板关闭期间收到的）。
    /// </summary>
    private void OnNetworkTextReceived(string from, string message)
    {
        Post(() =>
        {
            var item = new MsgItem
            {
                Sender = from,
                Body = message,
                TimeStr = DateTime.Now.ToString("HH:mm:ss")
            };

            bool panelOpen = _msgPanel != null && _msgPanel.IsLoaded;

            _msgHistory.Add(item);
            if (_msgHistory.Count > 500) _msgHistory.RemoveAt(0);

            if (panelOpen)
            {
                _msgPanel?.NotifyAppended();
                return; // user can see it live via binding
            }

            bool played = false;
            try
            {
                NotifySound.Play();
                played = true;
            }
            catch { }

            if (!played)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            }
            string preview = message.Length > 60 ? message[..60] + "…" : message;
            ShowToast($"💬 {from}: {preview}");
        });
    }

    // ---------- always on top ----------

    private void Pin_Checked(object sender, RoutedEventArgs e)
    {
        Topmost = true;
        ShowToast("窗口已置顶");
    }

    private void Pin_Unchecked(object sender, RoutedEventArgs e)
    {
        Topmost = false;
    }

    // ---------- flight plan ----------

    private void FlightPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.flyisfp.com/flight-plan",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowToast($"无法打开浏览器: {ex.Message}");
        }
    }

    // ---------- settings ----------

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Result;
            _settings.Save();
            if (FsdSessionReady())
            {
                ShowToast("设置已保存，部分项将在下次连线时生效");
            }
        }
    }

    private bool FsdSessionReady() => _net.Fsd != null;

    /// <summary>COM1 freq in MHz string for messaging, or null.</summary>
    public string? CurrentCom1ForMessaging()
    {
        if (_lastFreqKhz <= 0) return null;
        return FsdPacket.MillihertzToDisplay(_lastFreqKhz);
    }

    // ---------- chrome ----------

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _rosterTimer.Stop();
        _ = _net.StopFsdAsync();
        Application.Current.Shutdown();
    }

    // ---------- toast ----------

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
