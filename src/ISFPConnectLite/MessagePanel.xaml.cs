using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ISFPConnectLite.Fsd;
using ISFPConnectLite.Xlink;

namespace ISFPConnectLite;

public partial class MessagePanel : Window
{
    private readonly NetworkModel _net;
    private readonly ObservableCollection<MsgItem> _items = new();
    private string _lastSender = "";

    public MessagePanel(NetworkModel net)
    {
        InitializeComponent();
        _net = net;
        MsgList.ItemsSource = _items;

        if (net.Fsd != null)
        {
            net.Fsd.TextReceived += OnTextReceived;
            TargetText.Text = $"  ·  {net.MyCallsign}";
            Closed += (_, _) => net.Fsd.TextReceived -= OnTextReceived;
        }
    }

    private void OnTextReceived(string from, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _items.Add(new MsgItem { Sender = from, Body = message, TimeStr = DateTime.Now.ToString("HH:mm:ss") });
            _lastSender = from;
            TargetBox.Text = from;
            MsgList.ScrollIntoView(MsgList.Items[^1]);
        });
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Send();
    }

    private void UseFreq_Click(object sender, RoutedEventArgs e)
    {
        // current COM1 frequency comes from the last flight data cached in main window's model
        if (Application.Current.MainWindow is MainWindow mw)
        {
            string? f = mw.CurrentCom1ForMessaging();
            if (f != null) TargetBox.Text = f;
        }
    }

    private void Send()
    {
        string text = InputBox.Text.Trim();
        if (text.Length == 0) return;
        string target = TargetBox.Text.Trim();
        if (target.Length == 0) { TargetBox.Focus(); return; }

        var fsd = _net.Fsd;
        if (fsd == null)
        {
            _items.Add(new MsgItem { Sender = "系统", Body = "未连接网络，无法发送", TimeStr = DateTime.Now.ToString("HH:mm:ss") });
            MsgList.ScrollIntoView(MsgList.Items[^1]);
            return;
        }

        // frequency target: "118.1" or "@118100"
        if (target.StartsWith('@'))
        {
            fsd.SendText(target, text);
        }
        else if (target.Contains('.') && double.TryParse(target, System.Globalization.CultureInfo.InvariantCulture, out double mhz))
        {
            fsd.SendFrequencyText(mhz, text);
        }
        else
        {
            fsd.SendText(target.ToUpperInvariant(), text);
        }

        _items.Add(new MsgItem { Sender = $"{_net.MyCallsign} (我)", Body = text, TimeStr = DateTime.Now.ToString("HH:mm:ss") });
        InputBox.Clear();
        MsgList.ScrollIntoView(MsgList.Items[^1]);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Window_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed
            && e.OriginalSource is DependencyObject d
            && IsInteractive(d))
            return; // let TextBox/Button handle their own clicks
        DragMove();
    }

    private static bool IsInteractive(DependencyObject d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.TextBox
                or System.Windows.Controls.Button
                or System.Windows.Controls.Primitives.ToggleButton
                or System.Windows.Controls.ListBox
                or System.Windows.Controls.Primitives.ScrollBar)
                return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public class MsgItem
{
    public string Sender { get; set; } = "";
    public string Body { get; set; } = "";
    public string TimeStr { get; set; } = "";
}
