using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISFPConnectLite.Xlink;

public sealed class XlinkClient : IDisposable
{
    public const string Host = "127.0.0.1";
    public const int Port = 51001;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _txLock = new(1, 1);
    // xLink JSON 字段为 snake_case（com1_freq / altitude_msl / ground_speed ...），
    // SnakeCaseLower 策略保证收发两侧 C# 属性与文档字段名一致
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public bool Connected { get; private set; }
    public int PluginVersion { get; private set; }

    public event Action? ConnectedEvent;
    public event Action? DisconnectedEvent;
    public event Action<FlightData>? FlightData;
    public event Action<string, string>? EfbQuery;   // type, raw json
    public event Action<string>? Log;

    public async Task<bool> ConnectAsync(int retries = 3, CancellationToken ct = default)
    {
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(Host, Port, cts.Token);
                _tcp = tcp;
                _stream = tcp.GetStream();
                _cts = new CancellationTokenSource();
                Connected = true;
                ConnectedEvent?.Invoke();
                Log?.Invoke($"[xlink] connected (attempt {attempt})");
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log?.Invoke($"[xlink] attempt {attempt}: timeout");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[xlink] attempt {attempt}: {ex.Message}");
            }
            if (attempt < retries) await Task.Delay(1000, ct);
        }
        return false;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[16384];
        var sb = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream!.ReadAsync(buf, ct);
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                int idx;
                while ((idx = sb.ToString().IndexOf('\n')) >= 0)
                {
                    string line = sb.ToString()[..idx].Trim();
                    sb.Remove(0, idx + 1);
                    if (line.Length > 0) HandleMessage(line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log?.Invoke($"[xlink] rx error: {ex.Message}");
        }
        finally
        {
            Connected = false;
            Cleanup();
            DisconnectedEvent?.Invoke();
            Log?.Invoke("[xlink] disconnected");
        }
    }

    private void HandleMessage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            string type = typeEl.GetString() ?? "";

            switch (type)
            {
                case "connected":
                    if (root.TryGetProperty("version", out var v))
                        PluginVersion = v.GetInt32();
                    Log?.Invoke($"[xlink] plugin version {PluginVersion}");
                    break;

                case "flight_data":
                    var fd = root.Deserialize<FlightData>(JsonOpts);
                    if (fd != null) FlightData?.Invoke(fd);
                    break;

                case "Query:Route":
                case "Query:Weather":
                    EfbQuery?.Invoke(type, line);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[xlink] bad frame: {ex.Message}");
        }
    }

    public async Task SendAsync(object message, CancellationToken ct = default)
    {
        if (!Connected || _stream is null) return;
        string json = JsonSerializer.Serialize(message, message.GetType(), JsonOpts);
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _txLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _txLock.Release();
        }
    }

    public async Task SendAtcAsync(IEnumerable<AtcEntry> atcList, CancellationToken ct = default) =>
        await SendAsync(new { type = "Send:ATC", data = atcList }, ct);

    public async Task SendPlayersFsdAsync(string myCid, IEnumerable<PlayerEntry> players, CancellationToken ct = default) =>
        await SendAsync(new { type = "Send:OPD:FSD9", mycid = myCid, data = players }, ct);

    public async Task SendEfbRouteAsync(string data, CancellationToken ct = default) =>
        await SendAsync(new { type = "Send:EFB:Route", data }, ct);

    public async Task SendEfbWeatherAsync(object data, CancellationToken ct = default) =>
        await SendAsync(new { type = "Send:EFB:Weather", data }, ct);

    private void Cleanup()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        Cleanup();
    }
}

public class FlightData
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public double Elevation { get; set; }
    public double Pitch { get; set; }
    public double Bank { get; set; }
    public double Heading { get; set; }
    public double IndicatedAirspeed { get; set; }
    public double TrueAirspeed { get; set; }
    public double Groundspeed { get; set; }
    public double VerticalSpeed { get; set; }
    [JsonPropertyName("altitude_msl")]
    public double AltitudeMsl { get; set; }
    [JsonPropertyName("altitude_agl")]
    public double AltitudeAgl { get; set; }
    [JsonPropertyName("mag_heading")]
    public double MagHeading { get; set; }
    [JsonPropertyName("true_heading")]
    public double TrueHeading { get; set; }
    [JsonPropertyName("com1_freq")]
    public int Com1Freq { get; set; }
    [JsonPropertyName("com2_freq")]
    public int Com2Freq { get; set; }
    public int Transponder { get; set; }
    public string Squark { get; set; } = "S";
    [JsonPropertyName("gear_deploy")]
    public int GearDeploy { get; set; }
    [JsonPropertyName("flaps_ratio")]
    public double FlapsRatio { get; set; }
    [JsonPropertyName("throttle_ratio")]
    public double ThrottleRatio { get; set; }
}

public class AtcEntry
{
    public string Callsign { get; set; } = "";
    public string Frequency { get; set; } = "";
    public string Type { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int Rating { get; set; }
}

public class PlayerEntry
{
    public string Callsign { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public double Pitch { get; set; }
    public double Bank { get; set; }
    public double Heading { get; set; }
    [JsonPropertyName("ground_speed")]
    public double GroundSpeed { get; set; }
    public string Aircraft { get; set; } = "A320";
    [JsonPropertyName("aircraft_family")]
    public string AircraftFamily { get; set; } = "";
}
