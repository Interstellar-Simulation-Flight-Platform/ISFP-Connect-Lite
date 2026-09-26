using System.Collections.Concurrent;
using System.Globalization;
using ISFPConnectLite.Fsd;

namespace ISFPConnectLite.Xlink;

/// <summary>
/// NetworkModel glues FSD session + xLink client together:
/// builds ATC list from % packets, player list from @ packets,
/// answers PIR with our aircraft ICAO, forwards flight data to FSD reporting.
/// </summary>
public sealed class NetworkModel : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AtcPositionData> _atc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlayerTrack> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _pendingPir = new(StringComparer.OrdinalIgnoreCase);

    public FsdSession? Fsd { get; private set; }
    public XlinkClient Xlink { get; } = new();

    public event Action<string>? Log;
    public event Action? RosterChanged;
    public event Action<FlightData>? LocalFlightData;

    public string MyCallsign { get; private set; } = "";
    public string MyCid { get; private set; } = "";
    public string AircraftIcao { get; set; } = "A320";
    public string AirlineIcao { get; set; } = "";
    public string LiveryIcao { get; set; } = "";

    // roaming ATC stale timeout: ATC re-broadcasts % every 15s
    private static readonly TimeSpan AtcTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PlayerTimeout = TimeSpan.FromSeconds(30);

    private readonly Timer _gcTimer;

    public NetworkModel()
    {
        _gcTimer = new Timer(_ => GcStale(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        Xlink.FlightData += OnXlinkFlightData;
        Xlink.EfbQuery += OnEfbQuery;
    }

    // ---------- lifecycle ----------

    public async Task StartFsdAsync(string host, int port, string callsign, string cid,
        string password, string realName, int simType)
    {
        await StopFsdAsync();
        MyCallsign = callsign;
        MyCid = cid;

        var session = new FsdSession(host, port, callsign, cid, password, realName, simType)
        {
            AircraftIcao = AircraftIcao,
            AirlineIcao = AirlineIcao,
            LiveryIcao = LiveryIcao,
        };
        session.Log += m => Log?.Invoke(m);
        session.PositionPacketReceived += OnFsdPacket;
        session.PlaneInfoRequested += OnPlaneInfoRequested;
        Fsd = session;
        await session.ConnectAsync();
        session.StartReporting();
    }

    public async Task StopFsdAsync()
    {
        if (Fsd != null)
        {
            var s = Fsd;
            Fsd = null;
            s.PositionPacketReceived -= OnFsdPacket;
            await s.DisconnectAsync();
        }
        lock (_gate)
        {
            _atc.Clear();
            _players.Clear();
        }
        RosterChanged?.Invoke();
    }

    // ---------- fsd packet handling ----------

    private void OnFsdPacket(ParsedPacket p)
    {
        bool changed = false;
        switch (p.Kind)
        {
            case "%":
                if (FsdParser.TryGetAtcPosition(p, out var atc))
                {
                    lock (_gate)
                    {
                        _atc[atc.Callsign] = atc;
                        _atcLastSeen[atc.Callsign] = DateTime.UtcNow;
                    }
                    changed = true;
                }
                break;

            case "@":
                if (FsdParser.TryGetPilotPosition(p, out var pp) && pp.Callsign != MyCallsign)
                {
                    lock (_gate)
                    {
                        var tr = _players.TryGetValue(pp.Callsign, out var existing)
                            ? existing : new PlayerTrack { Callsign = pp.Callsign };
                        UpdateFromPbh(tr, pp);
                        _players[pp.Callsign] = tr;
                        _plLastSeen[pp.Callsign] = DateTime.UtcNow;

                        // request plane info once per player
                        if (!tr.ModelRequested)
                        {
                            tr.ModelRequested = true;
                            _ = Task.Run(() => Fsd?.SendPlaneInfoRequest(pp.Callsign));
                        }
                    }
                    changed = true;
                }
                break;

            case "#DP":
                lock (_gate)
                {
                    _players.Remove(p.From);
                    _plLastSeen.Remove(p.From);
                }
                changed = true;
                break;
        }
        if (changed) RosterChanged?.Invoke();
    }

    private readonly Dictionary<string, DateTime> _atcLastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _plLastSeen = new(StringComparer.OrdinalIgnoreCase);

    private static void UpdateFromPbh(PlayerTrack tr, PilotPositionData pp)
    {
        tr.Lat = pp.Lat;
        tr.Lon = pp.Lon;
        tr.AltFt = pp.AltFt;
        tr.GsKt = pp.GsKt;
        uint h = (pp.Pbh >> 2) & 0x3FF;
        uint b = (pp.Pbh >> 12) & 0x3FF;
        uint pi = (pp.Pbh >> 22) & 0x3FF;
        tr.Heading = h * 360.0 / 1023.0;
        tr.Bank = b * 360.0 / 1023.0;
        tr.Pitch = pi * 360.0 / 1023.0;
    }

    private void OnPlaneInfoRequested(string from, string type)
    {
        if (Fsd == null) return;
        if (type == "FSIPIR")
        {
            Fsd.SendPlaneInfoFsi(from);
        }
        else
        {
            Fsd.SendPlaneInfo(from);
        }
        Log?.Invoke($"[fsd] plane info request from {from} ({type}) -> answered");
    }

    private void GcStale()
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (var (cs, t) in _atcLastSeen)
                if (DateTime.UtcNow - t > AtcTimeout) { _atc.Remove(cs); changed = true; }
            foreach (var (cs, t) in _plLastSeen)
                if (DateTime.UtcNow - t > PlayerTimeout) { _players.Remove(cs); changed = true; }
        }
        if (changed) RosterChanged?.Invoke();
    }

    // ---------- xlink flight data -> fsd reporting ----------

    private FlightData? _lastFd;

    private void OnXlinkFlightData(FlightData fd)
    {
        _lastFd = fd;
        LocalFlightData?.Invoke(fd);

        if (Fsd is { State: FsdState.Online })
        {
            // xLink reports imperial units already (ft / kt)
            int corrFt = (int)Math.Round(fd.AltitudeMsl - fd.Altitude);
            char mode = fd.Squark == "N" ? 'N' : 'S';
            string squawk = fd.Transponder.ToString("D4");
            Fsd.UpdateFlightState(fd.Latitude, fd.Longitude, (int)Math.Round(fd.AltitudeMsl),
                (int)Math.Round(fd.Groundspeed), fd.Pitch, fd.Bank, fd.TrueHeading,
                corrFt, mode, squawk);
        }
    }

    // ---------- roster push to xlink ----------

    public async Task PushRosterAsync()
    {
        if (!Xlink.Connected) return;

        List<AtcEntry> atcList;
        List<PlayerEntry> players;
        lock (_gate)
        {
            // 双保险去重：呼号大小写不敏感（服务器重广播可能改变大小写），
            // 同一呼号保留最近更新的条目
            atcList = _atc.Values
                .GroupBy(a => a.Callsign, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(a => _atcLastSeen.TryGetValue(g.Key, out var t) ? t.Ticks : 0).First())
                .Select(a => new AtcEntry
                {
                    Callsign = a.Callsign,
                    Frequency = FsdPacket.FsdFreqToDisplay(a.FreqKhz),
                    Type = FacilityName(a.Facility),
                    Latitude = a.Lat,
                    Longitude = a.Lon,
                    Rating = a.Rating,
                })
                .DistinctBy(a => a.Callsign, StringComparer.OrdinalIgnoreCase)
                .ToList();

            players = _players.Values
                .GroupBy(pl => pl.Callsign, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(pl => new PlayerEntry
                {
                    Callsign = pl.Callsign,
                    Latitude = pl.Lat,
                    Longitude = pl.Lon,
                    Altitude = pl.AltFt,
                    Pitch = pl.Pitch,
                    Bank = pl.Bank,
                    Heading = pl.Heading,
                    GroundSpeed = pl.GsKt,
                    Aircraft = pl.Aircraft,
                    AircraftFamily = pl.Family,
                })
                .DistinctBy(pl => pl.Callsign, StringComparer.OrdinalIgnoreCase)
                // 玩家列表中剔除与管制员同名的条目，防止 xLink 双重渲染
                .Where(pl => !atcList.Any(a => string.Equals(a.Callsign, pl.Callsign, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        await Xlink.SendAtcAsync(atcList);
        await Xlink.SendPlayersFsdAsync(MyCid, players);
    }

    private static string FacilityName(int facility) => facility switch
    {
        1 => "FSS",
        2 => "DEL",
        3 => "GND",
        4 => "TWR",
        5 => "APP",
        6 => "CTR",
        _ => "OBS",
    };

    // ---------- EFB query stubs ----------

    private void OnEfbQuery(string type, string raw)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (type == "Query:Weather")
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    string apt = doc.RootElement.TryGetProperty("apt", out var a)
                        ? (a.GetString() ?? "").ToUpperInvariant() : "";
                    if (Fsd != null && apt.Length == 4)
                    {
                        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                        void Handler(string metar) { if (metar.StartsWith(apt)) tcs.TrySetResult(metar); }
                        Fsd.MetarReceived += Handler;
                        Fsd.RequestMetar(apt);
                        var done = await Task.WhenAny(tcs.Task, Task.Delay(8000));
                        Fsd.MetarReceived -= Handler;
                        string metar = done == tcs.Task ? tcs.Task.Result : $"{apt} METAR request timeout";
                        await Xlink.SendEfbWeatherAsync(new { metar, taf = "" });
                    }
                    else
                    {
                        await Xlink.SendEfbWeatherAsync(new { metar = "not connected to network", taf = "" });
                    }
                }
                else if (type == "Query:Route")
                {
                    await Xlink.SendEfbRouteAsync("route query not supported in ISFP-Connect-Lite");
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[efb] {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        _gcTimer.Dispose();
        Xlink.Dispose();
    }
}

public sealed class PlayerTrack
{
    public string Callsign { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public int AltFt { get; set; }
    public int GsKt { get; set; }
    public double Pitch { get; set; }
    public double Bank { get; set; }
    public double Heading { get; set; }
    public bool ModelRequested { get; set; }
    public string Aircraft { get; set; } = "A320";
    public string Family { get; set; } = "";
}
