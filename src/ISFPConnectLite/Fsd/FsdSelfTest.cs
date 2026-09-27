using System.Text;

namespace ISFPConnectLite.Fsd;

/// <summary>Standalone protocol unit checks, run with: ISFP-Connect-Lite.exe --selftest</summary>
public static class FsdSelfTest
{
    public static int RunAll()
    {
        int fails = 0;

        void Check(bool cond, string name)
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
            if (!cond) fails++;
        }

        // PBH encoding: 0deg=0, 360deg=1023, layout pitch<<22|bank<<12|heading<<2
        Check(FsdPacket.EncodePbh(0, 0, 0) == 0u, "PBH zero");
        Check(FsdPacket.EncodePbh(360, 360, 360) == (1023u << 22 | 1023u << 12 | 1023u << 2), "PBH full scale");
        // pitch=10 -> (10+360)=370 * 1023/360 = 1051.6 -> but 370deg > 360 wraps: 370-360=10 -> 28.4 -> 28
        var pbh = FsdPacket.EncodePbh(10, -20, 90);
        Check(((pbh >> 22) & 0x3FF) == 28u, "PBH pitch field (10deg)");
        Check(((pbh >> 12) & 0x3FF) == (uint)Math.Round(340 * 1023 / 360.0), "PBH bank field (-20deg)");
        Check(((pbh >> 2) & 0x3FF) == (uint)Math.Round(90 * 1023 / 360.0), "PBH heading field");

        // on_ground flag: bit1 (服务器解码 onGround = (pbh & 0b10) >> 1)
        Check((FsdPacket.EncodePbh(0, 0, 0, onGround: false) & 0b10) == 0, "PBH on_ground clear (default)");
        Check((FsdPacket.EncodePbh(0, 0, 0, onGround: true) & 0b10) == 0b10, "PBH on_ground set");
        var pbhGround = FsdPacket.EncodePbh(10, -20, 90, onGround: true);
        Check(((pbhGround & 0b10) >> 1) == 1 &&
              ((pbhGround >> 22) & 0x3FF) == 28u, "PBH on_ground coexists with angles");

        // packet builders
        var ap = FsdPacket.AddPilot("DAL625", "1400000", "secret", 1, 9, 16, "Zhang San");
        Check(ap == "#APDAL625:SERVER:1400000:secret:1:9:16:Zhang San", "#AP layout: " + ap);

        var pos = FsdPacket.PilotPosition("DAL625", 'N', "2000", 1, 40.65906, -73.79891, 26000, 400,
            FsdPacket.EncodePbh(0, 0, 90), 359);
        Check(pos.StartsWith("@N:DAL625:2000:1:40.659060:-73.798910:26000:400:", StringComparison.Ordinal), "pilot pos layout: " + pos);

        var dp = FsdPacket.DeletePilot("AAL325", "1400000");
        Check(dp == "#DPAAL325:1400000", "#DP layout");

        var tm = FsdPacket.TextMessage("N7938C", "@22800", "hello");
        Check(tm == "#TMN7938C:@22800:hello", "#TM layout");

        var ax = FsdPacket.MetarRequest("ZGGG_GND", "ZGGG");
        Check(ax == "$AXZGGG_GND:SERVER:METAR:ZGGG", "$AX layout");

        // parser: @ position without To field
        var parsed = FsdParser.Parse("@S:GTI8197:2000:1:40.65906:-73.79891:26:0:4290776072:359");
        Check(parsed.Kind == "@" && parsed.From == "GTI8197", "parse @ from");
        Check(FsdParser.TryGetPilotPosition(parsed, out var pp) && pp.Callsign == "GTI8197" &&
              Math.Abs(pp.Lat - 40.65906) < 1e-5 && pp.AltFt == 26, "parse @ fields");

        // parser: ATC position
        var atc = FsdParser.Parse("%EWR_P_APP:28550:5:150:4:40.67317:-74.18533:0");
        Check(FsdParser.TryGetAtcPosition(atc, out var ap2) && ap2.Callsign == "EWR_P_APP" &&
              ap2.FreqKhz == 28550 && ap2.Facility == 5 && Math.Abs(ap2.Lon - (-74.18533)) < 1e-5, "parse % fields");

        // parser: $ER error
        // $ERSERVER:unknown:006::Invalid CID/password.
        // ParseWithTo: From=SERVER To=unknown Type=006 Fields=["", "Invalid CID/password."]
        var er = FsdParser.Parse("$ERSERVER:unknown:006::Invalid CID/password.");
        Check(er.Kind == "$ER" && er.Type == "006" && er.Fields.Length == 2 &&
              er.Fields[1] == "Invalid CID/password.", "parse $ER");

        // parser: $AR metar response
        // $ARSERVER:ZGGG_GND:ZGGG 041600Z ... -> From=SERVER To=ZGGG_GND Type=ZGGG Fields=[rest...]
        var ar = FsdParser.Parse("$ARSERVER:ZGGG_GND:ZGGG 041600Z 05003MPS CAVOK 22/20 Q1009 NOSIG $");
        Check(ar.Kind == "$AR" && ar.From == "SERVER" && ar.To == "ZGGG_GND" &&
              ar.Type.StartsWith("ZGGG", StringComparison.Ordinal), "parse $AR");

        // parser: $AR with METAR: prefix (openfsd style)
        var ar2 = FsdParser.Parse("$ARSERVER:ZGGG_GND:METAR:ZGGG 041600Z 05003MPS CAVOK");
        Check(ar2.Kind == "$AR" && ar2.Type == "METAR" && ar2.Fields[0].StartsWith("ZGGG", StringComparison.Ordinal), "parse $AR METAR: prefix");

        // parser: TM from freq broadcast
        // #TMZGGG_TWR:CSN3081:contact delivery 121.7 -> message has no more colons so it lands in Type
        var tmIn = FsdParser.Parse("#TMZGGG_TWR:CSN3081:contact delivery 121.7");
        Check(tmIn.Kind == "#TM" && tmIn.From == "ZGGG_TWR" && tmIn.To == "CSN3081" &&
              tmIn.Type == "contact delivery 121.7", "parse #TM");

        // parser: fast position variants (protocol 101)
        // ^from:lat:lon:altTrue:altAgl:pbh:vel... (7+ fields)
        var fast = FsdParser.Parse("^CSA1234:22.7:114.3:32060.5:1500.0:1712:0.001:0.0002:0.0005:0:0:0:-0.4");
        Check(FsdParser.TryGetPilotPosition(fast, out var fp) && fp.Callsign == "CSA1234" &&
              Math.Abs(fp.Lat - 22.7) < 1e-6 && fp.AltFt == 32060 && fp.Pbh == 1712, "parse ^ fast pos");
        // #SL slow variant (same layout as ^)
        var slow = FsdParser.Parse("#SLCCA987:40.0:-73.0:10000:500:4290776072");
        Check(FsdParser.TryGetPilotPosition(slow, out var sp) && sp.Callsign == "CCA987" && sp.Pbh == 4290776072u, "parse #SL");
        // #ST stopped variant (6 fields exactly)
        var stopped = FsdParser.Parse("#STCSN3081:23.0:113.0:35.2:34.9:1024");
        Check(FsdParser.TryGetPilotPosition(stopped, out var stp) && stp.Callsign == "CSN3081" &&
              Math.Abs(stp.Lat - 23.0) < 1e-6 && stp.AltFt == 35, "parse #ST");

        // parser: PI:GEN response (EQUIPMENT extraction happens in NetworkModel; here verify Fields layout)
        // ParseWithTo: From=CSN3081 To=CCA987 Type=PI Fields=[GEN, EQUIPMENT=..., AIRLINE=...]
        var pig = FsdParser.Parse("#SBCSN3081:CCA987:PI:GEN:EQUIPMENT=B738:AIRLINE=CSN");
        Check(pig.Kind == "#SB" && pig.Type == "PI" && pig.From == "CSN3081" &&
              pig.Fields.Length == 3 && pig.Fields[0] == "GEN" && pig.Fields[1] == "EQUIPMENT=B738", "parse PI:GEN fields");
        // parser: FSIPI response
        // #SB<from>:<to>:FSIPI:0:<airline>:<equipment>::...:<model> -> Fields=[0,airline,equipment,'','','','','',model] = 9
        var fsi = FsdParser.Parse("#SBCSN3081:CCA987:FSIPI:0:CSN:B738::::::CSN B738 B-1234");
        Check(fsi.Kind == "#SB" && fsi.Type == "FSIPI" && fsi.Fields.Length == 9 &&
              fsi.Fields[2] == "B738" && fsi.Fields[8] == "CSN B738 B-1234", "parse FSIPI fields");

        // freq helpers
        Check(FsdPacket.FreqToTarget(122.800) == "@22800", "freq target 122.800");
        Check(FsdPacket.FreqToTarget(121.500) == "@21500", "freq target 121.500");
        Check(FsdPacket.KhzToDisplay(22800) == "122.800", "freq display");

        // auth scheme round-trip determinism
        var a1 = new FsdAuthState(); a1.Init(55538, "ImuL1WbbhVuD8d3MuKpWn2rrLZRa9iVP"); a1.RoundReceived("76617473696d");
        var a2 = new FsdAuthState(); a2.Init(55538, "ImuL1WbbhVuD8d3MuKpWn2rrLZRa9iVP"); a2.RoundReceived("76617473696d");
        Check(a1.ClientChallengeKey == a2.ClientChallengeKey && a1.ClientChallengeKey.Length == 32, "auth deterministic");
        Check(a1.RespondToChallenge("0123456789abcdef") == a2.RespondToChallenge("0123456789abcdef"), "auth challenge same");

        Console.WriteLine(fails == 0 ? "ALL TESTS PASSED" : $"{fails} TEST(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
