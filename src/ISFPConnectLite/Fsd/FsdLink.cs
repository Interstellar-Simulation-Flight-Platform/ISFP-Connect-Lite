using System.Net.Sockets;
using System.Text;

namespace ISFPConnectLite.Fsd;

/// <summary>Low-level FSD TCP link: framing, send, receive loop, reconnect-free connect.</summary>
public sealed class FsdLink
{
    public const string ServerCallsign = "SERVER";
    public const int DefaultPort = 6809;

    // ISFP FSD 文本流量使用 GB18030（兼容中文）。
    // 注意：必须先注册 CodePagesEncodingProvider，否则 GetEncoding 抛异常导致 TypeInitializationException。
    private static readonly Encoding TextEncoding = CreateTextEncoding();

    private static Encoding CreateTextEncoding()
    {
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
        catch { /* already registered */ }
        try { return Encoding.GetEncoding("GB18030"); }
        catch { return Encoding.UTF8; } // fallback: never crash the app over encoding
    }

    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly StringBuilder _rx = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool Connected { get; private set; }
    public string Host { get; }
    public int Port { get; }

    public event Action<string>? LineReceived;
    public event Action? Disconnected;
    public event Action<Exception>? Error;

    public FsdLink(string host, int port = DefaultPort)
    {
        Host = host;
        Port = port;
    }

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(Host, Port, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"connect {Host}:{Port} timeout");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _stream = client.GetStream();
        _rx.Clear();
        Connected = true;
        _ = Task.Run(() => ReceiveLoopAsync(ct), ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested && _stream is { } s)
            {
                int n = await s.ReadAsync(buf, ct);
                if (n <= 0) break;
                _rx.Append(TextEncoding.GetString(buf, 0, n));
                while (true)
                {
                    string acc = _rx.ToString();
                    int idx = acc.IndexOf('\n');
                    if (idx < 0) break;
                    string line = acc[..idx].TrimEnd('\r');
                    _rx.Remove(0, idx + 1);
                    if (line.Length > 0)
                    {
                        try { LineReceived?.Invoke(line); }
                        catch (Exception ex) { Error?.Invoke(ex); }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
        finally
        {
            Connected = false;
            try { _client?.Close(); } catch { }
            _client = null;
            _stream = null;
            Disconnected?.Invoke();
        }
    }

    /// <summary>Send a raw FSD line. A trailing CR-LF is appended if missing.</summary>
    public async Task SendAsync(string line, CancellationToken ct = default)
    {
        if (!Connected || _stream is null) throw new InvalidOperationException("not connected");
        if (!line.EndsWith('\n')) line += "\r\n";
        byte[] payload = TextEncoding.GetBytes(line);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(payload, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Close()
    {
        Connected = false;
        try { _client?.Close(); } catch { }
        _client = null;
        _stream = null;
    }
}
