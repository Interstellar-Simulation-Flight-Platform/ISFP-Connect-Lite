namespace ISFPConnectLite.Fsd;

public sealed class ParsedPacket
{
    public string Kind = "";
    public string From = "";
    public string To = "";
    public string Type = "";
    public string[] Fields = Array.Empty<string>();
    public string Raw = "";

    public override string ToString() => Raw;
}

public sealed class PilotPositionData
{
    public string Callsign { get; set; } = "";
    public char Mode { get; set; } = 'S';
    public string Squawk { get; set; } = "";
    public int Rating { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public int AltFt { get; set; }
    public int GsKt { get; set; }
    public uint Pbh { get; set; }
}

public sealed class AtcPositionData
{
    public string Callsign { get; set; } = "";
    public int FreqKhz { get; set; }
    public int Facility { get; set; }
    public int Rating { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
}

public static class FsdParser
{
    /// <summary>
    /// Split a raw FSD line. Layout depends on packet kind:
    /// @mode:FROM:... (no To) | #TMfrom:to:... | $DIserver:to:... | %from:to:...
    /// </summary>
    public static ParsedPacket Parse(string line)
    {
        var p = new ParsedPacket { Raw = line };
        if (line.Length < 3) return p;

        string kind = GetKind(line);

        switch (kind)
        {
            case "@":
            case "^":
            case "#ST":
            case "#SL":
            case "#DP":
            case "#DA":
            case "%":
                ParseNoTo(p, kind, line);
                break;
            case "#AP":
            case "#AA":
            case "#TM":
            case "#SB":
            case "$DI":
            case "$ID":
            case "$ER":
            case "$AX":
            case "$AR":
            case "$CQ":
            case "$CR":
            case "$PI":
            case "$PO":
            case "$ZC":
            case "$ZR":
            case "$FP":
            case "$!!":
                ParseWithTo(p, kind, line);
                break;
            default:
                p.Kind = kind;
                p.Type = "RAW";
                break;
        }
        return p;
    }

    private static string GetKind(string line)
    {
        if (line[0] == '@') return "@";
        if (line[0] == '^') return "^";
        if (line[0] == '%') return "%";
        if (line.StartsWith("#SB")) return "#SB";
        if (line.StartsWith("#ST")) return "#ST";
        if (line.StartsWith("#SL")) return "#SL";
        if (line.StartsWith("#DP")) return "#DP";
        if (line.StartsWith("#DA")) return "#DA";
        if (line.StartsWith("#AP")) return "#AP";
        if (line.StartsWith("#AA")) return "#AA";
        if (line.StartsWith("#TM")) return "#TM";
        if (line.StartsWith("$DI")) return "$DI";
        if (line.StartsWith("$ID")) return "$ID";
        if (line.StartsWith("$ER")) return "$ER";
        if (line.StartsWith("$AX")) return "$AX";
        if (line.StartsWith("$AR")) return "$AR";
        if (line.StartsWith("$CQ")) return "$CQ";
        if (line.StartsWith("$CR")) return "$CR";
        if (line.StartsWith("$PI")) return "$PI";
        if (line.StartsWith("$PO")) return "$PO";
        if (line.StartsWith("$ZC")) return "$ZC";
        if (line.StartsWith("$ZR")) return "$ZR";
        if (line.StartsWith("$!!")) return "$!!";
        return line[..2];
    }

    private static void ParseNoTo(ParsedPacket p, string kind, string line)
    {
        p.Kind = kind;
        string rest = line[kind.Length..];
        var f = rest.Split(':');
        switch (kind)
        {
            case "@":
                // @mode:from:squawk:rating:lat:lon:alt:gs:pbh:corr
                p.From = f.Length > 1 ? f[1] : "";
                p.Fields = f;
                break;
            case "%":
                // %from:freq:facility:vis:rating:lat:lon:0
                p.From = f.Length > 0 ? f[0] : "";
                p.Fields = f;
                break;
            default:
                // #DPfrom:cid, fast positions from:lat:lon:...
                p.From = f.Length > 0 ? f[0] : "";
                p.Fields = f;
                break;
        }
    }

    private static void ParseWithTo(ParsedPacket p, string kind, string line)
    {
        p.Kind = kind;
        string rest = line[kind.Length..];
        var f = rest.Split(':');
        p.From = f.Length > 0 ? f[0] : "";
        if (f.Length > 1) p.To = f[1];
        if (f.Length > 2) p.Type = f[2];
        if (f.Length > 3)
        {
            p.Fields = new string[f.Length - 3];
            Array.Copy(f, 3, p.Fields, 0, f.Length - 3);
        }
        else
        {
            p.Fields = Array.Empty<string>();
        }
    }

    public static bool TryGetAtcPosition(ParsedPacket p, out AtcPositionData data)
    {
        data = new AtcPositionData();
        if (p.Kind != "%") return false;
        var f = p.Fields;
        // Fields = [from, freq, facility, vis, rating, lat, lon, 0]
        if (f.Length < 7) return false;
        try
        {
            data = new AtcPositionData
            {
                Callsign = f[0],
                FreqKhz = ParseFreq(f[1]),
                Facility = int.Parse(f[2]),
                Rating = int.Parse(f[4]),
                Lat = double.Parse(f[5], System.Globalization.CultureInfo.InvariantCulture),
                Lon = double.Parse(f[6], System.Globalization.CultureInfo.InvariantCulture),
            };
            return true;
        }
        catch { return false; }
    }

    /// <summary>Parse "22800" or "22800&19980" style frequency field, return first freq in kHz offset (MHz*1000).</summary>
    public static int ParseFreq(string raw)
    {
        string first = raw.Split('&')[0];
        if (long.TryParse(first, out long v)) return (int)v;
        return 0;
    }

    public static bool TryGetPilotPosition(ParsedPacket p, out PilotPositionData data)
    {
        data = new PilotPositionData();
        if (p.Kind != "@") return false;
        var f = p.Fields;
        // @mode:from:squawk:rating:lat:lon:alt:gs:pbh:corr
        if (f.Length < 10) return false;
        try
        {
            data = new PilotPositionData
            {
                Callsign = f[1],
                Mode = f[0] is { Length: > 0 } m ? m[0] : 'S',
                Squawk = f[2],
                Rating = int.Parse(f[3]),
                Lat = double.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture),
                Lon = double.Parse(f[5], System.Globalization.CultureInfo.InvariantCulture),
                AltFt = (int)double.Parse(f[6], System.Globalization.CultureInfo.InvariantCulture),
                GsKt = (int)double.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture),
                Pbh = uint.Parse(f[8], System.Globalization.CultureInfo.InvariantCulture),
            };
            return true;
        }
        catch { return false; }
    }
}

