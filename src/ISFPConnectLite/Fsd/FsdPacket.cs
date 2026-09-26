namespace ISFPConnectLite.Fsd;

public static class FsdPacket
{
    public const int NetworkRating = 1;
    public const int ProtocolRevision = 9;
    public const int SimTypeXp11 = 15;
    public const int SimTypeXp12 = 16;

    public static uint EncodePbh(double pitchDeg, double bankDeg, double headingDeg)
    {
        uint p = Quantize(pitchDeg);
        uint b = Quantize(bankDeg);
        uint h = Quantize(headingDeg);
        return (p << 22) | (b << 12) | (h << 2);
    }

    private static uint Quantize(double deg)
    {
        if (deg < 0) deg += 360;
        if (deg > 360) deg %= 360;
        long v = (long)Math.Round(deg * 1023.0 / 360.0);
        if (v < 0) v = 0;
        if (v > 1023) v = 1023;
        return (uint)v;
    }

    public static string ClientIdentification(string callsign, string cid, ushort clientId,
        string clientName, int major, int minor, long sysUid, string hexKey)
        => $"$ID{callsign}:SERVER:{clientId:x4}:{clientName}:{major}:{minor}:{cid}:{sysUid}:{hexKey}";

    public static string AddPilot(string callsign, string cid, string password,
        int networkRating, int protocolRevision, int simulatorType, string realName)
        => $"#AP{callsign}:SERVER:{cid}:{password}:{networkRating}:{protocolRevision}:{simulatorType}:{realName}";

    public static string DeletePilot(string callsign, string cid)
        => $"#DP{callsign}:{cid}";

    public static string PilotPosition(string callsign, char transponderMode, string squawk,
        int networkRating, double lat, double lon, int altTrueFt, int gsKt, uint pbh, int altCorrectionFt)
        => $"@{transponderMode}:{callsign}:{squawk}:{networkRating}:{lat:F6}:{lon:F6}:{altTrueFt}:{gsKt}:{pbh}:{altCorrectionFt}";

    public static string PlaneInfoRequest(string from, string to)
        => $"#SB{from}:{to}:PIR";

    public static string PlaneInfoRequestFsi(string from, string to)
        => $"#SB{from}:{to}:FSIPIR:0";

    public static string PlaneInfo(string from, string to, string equipment, string airline, string livery)
    {
        var parts = new List<string> { $"#SB{from}:{to}:PI:GEN", $"EQUIPMENT={equipment}" };
        if (!string.IsNullOrEmpty(airline)) parts.Add($"AIRLINE={airline}");
        if (!string.IsNullOrEmpty(livery)) parts.Add($"LIVERY={livery}");
        return string.Join(":", parts);
    }

    public static string PlaneInfoFsi(string from, string to, string equipment, string airline, string model)
        => $"#SB{from}:{to}:FSIPI:0:{airline}:{equipment}::::::{model}";

    public static string TextMessage(string from, string to, string message)
        => $"#TM{from}:{to}:{message}";

    public static string MetarRequest(string from, string station)
        => $"$AX{from}:SERVER:METAR:{station}";

    public static string Pong(string from, string to, string timestamp)
        => $"$PO{from}:{to}:{timestamp}";

    public static string ClientQueryResponse(string from, string to, string queryType, params string[] payload)
        => $"$CR{from}:{to}:{queryType}" + (payload.Length > 0 ? ":" + string.Join(":", payload) : "");

    public static string AuthResponse(string from, string to, string hexResponse)
        => $"$ZR{from}:{to}:{hexResponse}";

    public static string FreqToTarget(double mhz)
    {
        long khz = (long)Math.Round(mhz * 1000) - 100000;
        if (khz < 0) khz = 0;
        return $"@{khz:D5}";
    }

    public static string KhzToDisplay(int khz)
    {
        long full = 100000 + khz;
        return $"{full / 1000}.{full % 1000:D3}";
    }

    /// <summary>Display an xLink "MHz*1000" frequency value (118050 -> "118.050").</summary>
    public static string MillihertzToDisplay(int milli)
    {
        if (milli < 100000) milli += 100000; // guard: raw XP value missing leading 1
        return $"{milli / 1000}.{milli % 1000:D3}";
    }

    /// <summary>Display a FSD-encoded frequency (22800 = 122.800 MHz -> "122.800"). Same as KhzToDisplay.</summary>
    public static string FsdFreqToDisplay(int fsdFreq) => KhzToDisplay(fsdFreq);
}
