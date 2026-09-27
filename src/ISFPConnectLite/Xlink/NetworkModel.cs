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
    /// <summary>Any FSD text message received (from, message). Fires regardless of message panel state.</summary>
    public event Action<string, string>? TextReceived;
    /// <summary>FSD 连接意外断开（网络闪断/被踢/服务器故障），非用户主动断开。</summary>
    public event Action? FsdUnexpectedlyDisconnected;

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
        session.PlaneInfoReceived += OnPlaneInfoReceived;
        session.TextReceived += (from, msg) => TextReceived?.Invoke(from, msg);
        session.StateChanged += s => { if (s == FsdState.Disconnected) OnFsdSocketDown(); };
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
            s.PlaneInfoRequested -= OnPlaneInfoRequested;
            s.PlaneInfoReceived -= OnPlaneInfoReceived;
            // StateChanged 订阅保留：StopFsdAsync 主动断开也会触发 OnFsdSocketDown →
            // ClearRoster（幂等），保证 xLink 清空。先置 Fsd=null 防止重复处理。
            await s.DisconnectAsync();
        }
        ClearRoster();
    }

    /// <summary>FSD 断线（含主动与异常掉线）：清理本地状态并通知 UI。</summary>
    private void OnFsdSocketDown()
    {
        // StopFsdAsync 主动路径已清理过；异常掉线路径在此清理
        if (Fsd != null)
        {
            ClearRoster();
            FsdUnexpectedlyDisconnected?.Invoke();
        }
    }

    private void ClearRoster()
    {
        lock (_gate)
        {
            _atc.Clear();
            _players.Clear();
            _atcLastSeen.Clear();
            _plLastSeen.Clear();
        }
        RosterChanged?.Invoke();
        // 关键：向 xLink 推送空列表，清掉残留的 CSL 玩家与管制标注
        _ = PushRosterNowAsync();
    }

    /// <summary>无视 3s 定时器节流，立即推送当前（可能为空的）roster。</summary>
    private async Task PushRosterNowAsync()
    {
        try { await PushRosterAsync(); }
        catch (Exception ex) { Log?.Invoke($"[xlink] roster clear push failed: {ex.Message}"); }
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
            case "^":
            case "#SL":
            case "#ST":
                // @ 是常规位置；^ #SL #ST 是协议101快速位置变体（坐标/姿态同构，
                // 无 squawk/地速字段）——统一走 TryGetPilotPosition
                if (FsdParser.TryGetPilotPosition(p, out var pp) && pp.Callsign != MyCallsign)
                {
                    lock (_gate)
                    {
                        var tr = _players.TryGetValue(pp.Callsign, out var existing)
                            ? existing : new PlayerTrack { Callsign = pp.Callsign };
                        bool isFast = p.Kind != "@";
                        UpdateFromPbh(tr, pp, overwriteGs: !isFast);
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

            case "#DA":
                // ATC 断连广播：立即移除，不等 45s 超时
                lock (_gate)
                {
                    _atc.Remove(p.From);
                    _atcLastSeen.Remove(p.From);
                }
                changed = true;
                break;

            case "#AP":
                // 玩家登录广播：提前建档（首个位置包到来前 xLink 至少知道有此人）
                if (p.Fields.Length > 0 && p.From != MyCallsign)
                {
                    lock (_gate)
                    {
                        if (!_players.ContainsKey(p.From))
                        {
                            _players[p.From] = new PlayerTrack { Callsign = p.From };
                            _plLastSeen[p.From] = DateTime.UtcNow;
                            changed = true;
                        }
                    }
                }
                break;

            case "#AA":
                // 管制登录广播：占位（%包会带来完整坐标），此处仅刷新存活时间
                lock (_gate)
                {
                    _atcLastSeen[p.From] = DateTime.UtcNow;
                }
                break;

            case "$!!":
                // 被管理员踢出：显示原因后按普通断线处理（服务器随后会断开连接）
                string kickReason = p.Fields.Length > 0 ? string.Join(":", p.Fields) : "";
                Log?.Invoke($"[fsd] killed by admin: {kickReason}");
                break;
        }
        if (changed) RosterChanged?.Invoke();
    }

    private readonly Dictionary<string, DateTime> _atcLastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _plLastSeen = new(StringComparer.OrdinalIgnoreCase);

    private static void UpdateFromPbh(PlayerTrack tr, PilotPositionData pp, bool overwriteGs = true)
    {
        tr.Lat = pp.Lat;
        tr.Lon = pp.Lon;
        tr.AltFt = pp.AltFt;
        // 快速位置包（^ #SL #ST）不携带地速，保留上一次 @ 包的值
        if (overwriteGs) tr.GsKt = pp.GsKt;
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

    /// <summary>
    /// 解析对方机型响应写入 PlayerTrack：
    /// PI:GEN  → Fields=[KEY=VALUE...]（EQUIPMENT=A320 形式）
    /// FSIPI   → Fields=[0, airline, equipment, "", "", "", "", "", model串]
    /// </summary>
    private void OnPlaneInfoReceived(string from, string type, string[] fields, string raw)
    {
        string? equipment = null, family = null;
        try
        {
            if (type == "PI" && fields.Length > 0)
            {
                foreach (var kv in fields)
                {
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = kv[..eq].Trim().ToUpperInvariant();
                    string val = kv[(eq + 1)..].Trim();
                    if (key == "EQUIPMENT" && val.Length >= 3)
                        equipment = val.ToUpperInvariant();
                    else if (key == "CSL")
                        family = val;
                }
            }
            else if (type == "FSIPI" && fields.Length >= 9)
            {
                // #SB<from>:<to>:FSIPI:0:<airline>:<equipment>::...::<model>
                // ParseWithTo 消耗了 from:to:type → Fields=[0, airline, equipment, ... , model串]
                string eq = fields[2].Trim();
                if (eq.Length >= 3) equipment = eq.ToUpperInvariant();
                string model = fields[^1].Trim();
                if (model.Length > 0) family = model;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[fsd] plane info parse error from {from}: {ex.Message}");
        }

        if (string.IsNullOrEmpty(equipment)) return;

        lock (_gate)
        {
            if (_players.TryGetValue(from, out var tr))
            {
                tr.Aircraft = equipment!;
                if (!string.IsNullOrEmpty(family)) tr.Family = family;
                _plLastSeen[from] = DateTime.UtcNow;
            }
        }
        Log?.Invoke($"[fsd] plane info from {from}: {equipment} ({family ?? "-"})");
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
            // on_ground：xLink 无直接字段，用起落架放下近似（地面滑行/停放时起落架为放下状态）
            bool onGround = fd.GearDeploy > 0;
            Fsd.UpdateFlightState(fd.Latitude, fd.Longitude, (int)Math.Round(fd.AltitudeMsl),
                (int)Math.Round(fd.Groundspeed), fd.Pitch, fd.Bank, fd.TrueHeading,
                corrFt, mode, squawk, onGround);
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
