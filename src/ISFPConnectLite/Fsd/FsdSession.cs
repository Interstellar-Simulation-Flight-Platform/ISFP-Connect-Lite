using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ISFPConnectLite.Fsd;

public enum FsdState { Disconnected, Connecting, WaitServerId, WaitLogin, Online }

/// <summary>
/// High-level FSD pilot session: connect handshake, login, position timer,
/// METAR, text messages, plane info, keep-alive, disconnect.
/// </summary>
public sealed class FsdSession : IAsyncDisposable
{
    private readonly FsdLink _link;
    private readonly FsdAuthState _auth = new();
    private Timer? _posTimer;
    private Timer? _keepAliveTimer;
    private readonly ConcurrentQueue<string> _txQueue = new();
    private readonly SemaphoreSlim _drain = new(0);

    public string Callsign { get; }
    public string Cid { get; }
    public string Credential { get; }
    public string RealName { get; }
    public int SimulatorType { get; }
    public FsdState State { get; private set; } = FsdState.Disconnected;

    // latest flight state to report
    private double _lat, _lon;
    private int _altFt, _gsKt, _corrFt;
    private uint _pbh;
    private char _xpdrMode = 'S';
    private string _squawk = "2000";
    private volatile bool _hasFix;

    public event Action<string>? Log;
    public event Action<FsdState>? StateChanged;
    public event Action<string>? ErrorText;          // human-readable login/protocol error
    public event Action<string, string>? TextReceived;    // from, message
    public event Action<string>? MetarReceived;           // raw metar text
    public event Action<string, string>? PlaneInfoRequested; // from, type (PIR/FSIPIR)

    private const ushort ClientId = 55538;             // xPilot-assigned id (unofficial use)
    private const string ClientName = "ISFP-Connect-Lite";
    private const string AuthKey = "ImuL1WbbhVuD8d3MuKpWn2rrLZRa9iVP";

    public FsdSession(string host, int port, string callsign, string cid, string credential,
        string realName, int simulatorType)
    {
        _link = new FsdLink(host, port);
        Callsign = callsign;
        Cid = cid;
        Credential = credential;
        RealName = realName;
        SimulatorType = simulatorType;
        _link.LineReceived += OnLine;
        _link.Disconnected += OnSocketDown;
        _link.Error += ex => Log?.Invoke($"[fsd] {ex.Message}");
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(FsdState.Connecting);
        await _link.ConnectAsync(TimeSpan.FromSeconds(5), ct);
        Log?.Invoke($"[fsd] connected {_link.Host}:{_link.Port}");
        SetState(FsdState.WaitLogin);
        _drainFed = Task.Run(DrainLoopAsync, ct);

        // ISFP FSD: no $DI handshake, no $ID — send #AP directly after TCP connect.
        // (If a $DI shows up anyway, the OnLine handler answers it; harmless.)
        Send(FsdPacket.AddPilot(Callsign, Cid, Credential, FsdPacket.NetworkRating,
            FsdPacket.ProtocolRevision, SimulatorType, RealName));
    }

    private Task? _drainFed;

    private async Task DrainLoopAsync()
    {
        while (State != FsdState.Disconnected)
        {
            await _drain.WaitAsync();
            while (_txQueue.TryDequeue(out var line))
            {
                try { await _link.SendAsync(line); Log?.Invoke($"> {line}"); }
                catch (Exception ex) { Log?.Invoke($"[fsd] send failed: {ex.Message}"); return; }
            }
        }
    }

    private void Send(string line)
    {
        if (!_link.Connected) return;
        _txQueue.Enqueue(line);
        try { _drain.Release(); } catch { }
    }

    private void OnLine(string line)
    {
        Log?.Invoke($"< {line}");
        var p = FsdParser.Parse(line);

        switch (p.Kind)
        {
            case "$ZC":
                if (p.Fields.Length > 0)
                {
                    string resp = _auth.RespondToChallenge(p.Fields[0]);
                    Send(FsdPacket.AuthResponse(Callsign, FsdLink.ServerCallsign, resp));
                }
                break;

            case "$ER":
                // $ER + "server:to:code:causing:description"
                // ParseWithTo: From=server To=to Type=code Fields=[causing, description...]
                string code = p.Type is { Length: > 0 } t ? t : (p.Fields.Length > 0 ? p.Fields[0] : "");
                string desc = p.Fields.Length > 1 ? string.Join(":", p.Fields[1..]) : "";
                ErrorText?.Invoke($"服务器错误 {code}: {desc}");
                _link.Close();
                break;

            case "%":
                break; // ATC positions handled by NetworkModel via PositionReceived

            case "$PI":
                if (p.From.Length > 0)
                    Send(FsdPacket.Pong(Callsign, p.From, p.Fields.Length > 0 ? p.Fields[0] : "0"));
                break;

            case "#TM":
                // #TM<from>:<to>:<message...>  message may contain colons -> reconstruct from raw
                TextReceived?.Invoke(p.From, JoinRest(p.Raw, 2));
                break;

            case "$AR":
                // $ARSERVER:<me>:<METAR text ...>  where text may start with "METAR:" (openfsd)
                // ParseWithTo: From=SERVER To=me Type=<first token of metar> Fields=[rest...]
                string arMetar = JoinRest(p.Raw, 2);
                if (arMetar.StartsWith("METAR:", StringComparison.Ordinal))
                    arMetar = arMetar[6..];
                MetarReceived?.Invoke(arMetar);
                break;

            case "#SB":
                if (p.Type == "PIR" || p.Type == "FSIPIR")
                    PlaneInfoRequested?.Invoke(p.From, p.Type);
                break;

            case "#DP":
                break;
        }

        PositionPacketReceived?.Invoke(p);
    }

    public event Action<ParsedPacket>? PositionPacketReceived;

    /// <summary>Everything after the 2nd colon (kind+from:to:MESSAGE...), colon-safe.</summary>
    private static string JoinRest(string raw, int colonsToSkip)
    {
        int idx = raw.IndexOf(':');
        for (int i = 1; i < colonsToSkip && idx >= 0; i++)
            idx = raw.IndexOf(':', idx + 1);
        return idx < 0 ? "" : raw[(idx + 1)..];
    }

    private static string ExtractNthField(string raw, int n)
    {
        int start = raw.StartsWith("$!!", StringComparison.Ordinal) ? 3 : 3;
        int fieldIdx = 0;
        int i = start;
        while (fieldIdx < n)
        {
            int c = raw.IndexOf(':', i);
            if (c < 0) return "";
            i = c + 1;
            fieldIdx++;
        }
        int end = raw.IndexOf(':', i);
        return end < 0 ? raw[i..] : raw[i..end];
    }

    private static string ExtractLastField(string raw)
    {
        int idx = raw.LastIndexOf(':');
        return idx >= 0 ? raw[(idx + 1)..] : raw;
    }

    public void UpdateFlightState(double lat, double lon, int altTrueFt, int gsKt,
        double pitchDeg, double bankDeg, double headingDeg, int corrFt, char xpdrMode, string squawk)
    {
        _lat = lat; _lon = lon; _altFt = altTrueFt; _gsKt = gsKt;
        _pbh = FsdPacket.EncodePbh(pitchDeg, bankDeg, headingDeg);
        _corrFt = corrFt;
        _xpdrMode = xpdrMode;
        _squawk = squawk;
        _hasFix = true;
    }

    public void StartReporting()
    {
        SetState(FsdState.Online);
        _posTimer?.Dispose();
        _posTimer = new Timer(_ =>
        {
            if (!_hasFix || !_link.Connected) return;
            Send(FsdPacket.PilotPosition(Callsign, _xpdrMode, _squawk, FsdPacket.NetworkRating,
                _lat, _lon, _altFt, _gsKt, _pbh, _corrFt));
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        _keepAliveTimer?.Dispose();
        _keepAliveTimer = new Timer(_ =>
        {
            if (!_link.Connected) return;
            // $PI ping to SERVER keeps NAT alive; server has no ping handler so use silent $PO echo target
            Send(FsdPacket.TextMessage(Callsign, FsdLink.ServerCallsign, "ping"));
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public void SendText(string to, string message) => Send(FsdPacket.TextMessage(Callsign, to, message));

    public void SendFrequencyText(double mhz, string message) =>
        Send(FsdPacket.TextMessage(Callsign, FsdPacket.FreqToTarget(mhz), message));

    public void RequestMetar(string station) => Send(FsdPacket.MetarRequest(Callsign, station));

    public void SendPlaneInfo(string to)
        => Send(FsdPacket.PlaneInfo(Callsign, to, AircraftIcao, AirlineIcao, LiveryIcao));

    public void SendPlaneInfoFsi(string to)
        => Send(FsdPacket.PlaneInfoFsi(Callsign, to, AircraftIcao, AirlineIcao, $"{AirlineIcao} {AircraftIcao}".Trim()));

    public void SendPlaneInfoRequest(string to)
        => Send(FsdPacket.PlaneInfoRequest(Callsign, to));

    public string AircraftIcao { get; set; } = "A320";
    public string AirlineIcao { get; set; } = "";
    public string LiveryIcao { get; set; } = "";

    private void OnSocketDown()
    {
        StopTimers();
        if (State != FsdState.Disconnected)
        {
            SetState(FsdState.Disconnected);
            Log?.Invoke("[fsd] disconnected");
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_link.Connected)
            {
                Send(FsdPacket.DeletePilot(Callsign, Cid));
                await Task.Delay(200);
            }
        }
        catch { }
        StopTimers();
        _link.Close();
        SetState(FsdState.Disconnected);
    }

    private void StopTimers()
    {
        _posTimer?.Dispose(); _posTimer = null;
        _keepAliveTimer?.Dispose(); _keepAliveTimer = null;
    }

    private void SetState(FsdState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}

/// <summary>VATSIM-auth style challenge/response state (xPilot client id, non-official).</summary>
public sealed class FsdAuthState
{
    private ushort _clientId;
    private string _initState = "";
    private string _currState = "";

    public string ClientChallengeKey { get; private set; } = "";

    public void Init(ushort clientId, string privateKey)
    {
        _clientId = clientId;
        _currState = privateKey;
    }

    /// <summary>Compute the client challenge key sent in $ID using the server's $DI challenge.</summary>
    public void RoundReceived(string serverChallenge)
    {
        ClientChallengeKey = ObfuscationScheme(serverChallenge);
        _initState = ClientChallengeKey;
        _currState = ClientChallengeKey;
    }

    public string RespondToChallenge(string challenge)
    {
        string resp = ObfuscationScheme(challenge);

        // advance state: md5(initState + resp)
        string combined = _initState + resp;
        byte[] hash = MD5.HashData(Encoding.ASCII.GetBytes(combined));
        _currState = Convert.ToHexString(hash).ToLowerInvariant();
        return resp;
    }

    private string ObfuscationScheme(string challenge)
    {
        string c1, c2;
        int half = challenge.Length / 2;
        c1 = challenge[..half];
        c2 = challenge[half..];
        if ((_clientId & 1) == 1) (c1, c2) = (c2, c1);

        string s1 = _currState[0..12];
        string s2 = _currState[12..22];
        string s3 = _currState[22..32];

        string h = (_clientId % 3) switch
        {
            0 => s1 + c1 + s2 + c2 + s3,
            1 => s2 + c1 + s3 + c2 + s1,
            _ => s3 + c1 + s1 + c2 + s2,
        };

        byte[] hash = MD5.HashData(Encoding.ASCII.GetBytes(h));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
