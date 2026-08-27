using System.Text;

namespace MaddenSaveConverter.Convert;

public static class DraftConverter
{
    // Draft files: FBCHUNKS + file-level JSON + players
    // M26: firstName 17, lastName 21, hometown 28, stats 92, asset 32, gap 12
    // M27: firstName 21, lastName 49, stats 90, asset 32, gap 54
    // TESTCONVERT5 logic: names/asset from source, stats first64 from source, last26 portrait from name-matched template

    public static ConvertResult M26ToM27(string srcPath, string tplPath, string outPath)
    {
        var res = new ConvertResult { OutputPath = outPath };
        void log(string s) => res.Log.Add(s);
        var src = File.ReadAllBytes(srcPath);
        var tpl = File.ReadAllBytes(tplPath);
        if (Encoding.ASCII.GetString(src, 0, 8) != "FBCHUNKS" || Encoding.ASCII.GetString(tpl, 0, 8) != "FBCHUNKS")
            throw new InvalidDataException("not FBCHUNKS");

        var srcPlayers = Parse(src, true);
        var tplPlayers = Parse(tpl, false);
        log($"M26 draft players: {srcPlayers.Count}");
        log($"M27 template players: {tplPlayers.Count}");

        // name -> tpl player (for portrait) - keep first on duplicate names
        var tplByName = new Dictionary<string, DraftPlayer>();
        foreach (var p in tplPlayers) { var k = p.Fn + "|" + p.Ln; if (!tplByName.ContainsKey(k)) tplByName[k] = p; }
        var outBuf = (byte[])tpl.Clone();

        int n = Math.Min(srcPlayers.Count, tplPlayers.Count);
        for (int i = 0; i < n; i++)
        {
            var s = srcPlayers[i];
            var d = tplPlayers[i];
            // firstName 21
            var fn = Encoding.ASCII.GetString(src, s.Off, src[s.Off..].TakeWhile(b => b != 0).Count());
            // Use src player's fn/ln directly
            var fnBytes = new byte[21]; Encoding.ASCII.GetBytes(s.Fn).CopyTo(fnBytes, 0); Array.Copy(fnBytes, 0, outBuf, d.Off, 21);
            var lnBytes = new byte[49]; Encoding.ASCII.GetBytes(s.Ln).CopyTo(lnBytes, 0); Array.Copy(lnBytes, 0, outBuf, d.Off + 21, 49);
            // asset 32
            Array.Copy(src, s.AssetStart, outBuf, d.AssetStart, 32);
            // stats: 0-64 from src, 64-90 from name-matched tpl
            var m26Stats = src.AsSpan(s.StatsStart, 90).ToArray(); // M26 has 92, take first 90
            byte[] portraitSrc;
            if (tplByName.TryGetValue(s.Fn + "|" + s.Ln, out var match))
                portraitSrc = tpl.AsSpan(match.StatsStart + 64, 26).ToArray();
            else
                portraitSrc = tpl.AsSpan(d.StatsStart + 64, 26).ToArray();
            var newStats = new byte[90];
            Array.Copy(m26Stats, 0, newStats, 0, 64);
            Array.Copy(portraitSrc, 0, newStats, 64, 26);
            Array.Copy(newStats, 0, outBuf, d.StatsStart, 90);
        }
        File.WriteAllBytes(outPath, outBuf);
        log($"transplanted {n} players (names/asset/stats with name-matched portraits)");
        res.Success = true;
        return res;
    }

    public static ConvertResult M27ToM26(string srcPath, string tplPath, string outPath)
    {
        var res = new ConvertResult { OutputPath = outPath };
        void log(string s) => res.Log.Add(s);
        var src = File.ReadAllBytes(srcPath);
        var tpl = File.ReadAllBytes(tplPath);
        var srcPlayers = Parse(src, false);
        var tplPlayers = Parse(tpl, true);
        log($"M27 draft players: {srcPlayers.Count}");
        log($"M26 template players: {tplPlayers.Count}");
        var tplByName = new Dictionary<string, DraftPlayer>();
        foreach (var p in tplPlayers) { var k = p.Fn + "|" + p.Ln; if (!tplByName.ContainsKey(k)) tplByName[k] = p; }
        var outBuf = (byte[])tpl.Clone();
        int n = Math.Min(srcPlayers.Count, tplPlayers.Count);
        for (int i = 0; i < n; i++)
        {
            var s = srcPlayers[i];
            var d = tplPlayers[i];
            var fnBytes = new byte[17]; Encoding.ASCII.GetBytes(s.Fn).CopyTo(fnBytes, 0); Array.Copy(fnBytes, 0, outBuf, d.Off, 17);
            var lnBytes = new byte[21]; Encoding.ASCII.GetBytes(s.Ln).CopyTo(lnBytes, 0); Array.Copy(lnBytes, 0, outBuf, d.Off + 17, 21);
            // hometown 28 bytes - keep from name-matched tpl if available
            if (tplByName.TryGetValue(s.Fn + "|" + s.Ln, out var match))
                Array.Copy(tpl, match.Off + 38, outBuf, d.Off + 38, 28);
            else
                Array.Clear(outBuf, d.Off + 38, 28);
            Array.Copy(src, s.AssetStart, outBuf, d.AssetStart, 32);
            var m27Stats = src.AsSpan(s.StatsStart, 90).ToArray();
            byte[] portraitSrc;
            if (tplByName.TryGetValue(s.Fn + "|" + s.Ln, out var m))
                portraitSrc = tpl.AsSpan(m.StatsStart + 64, 26).ToArray();
            else
                portraitSrc = tpl.AsSpan(d.StatsStart + 64, 28).ToArray(); // M26 last 28
            var newStats = new byte[92];
            Array.Copy(m27Stats, 0, newStats, 0, 64);
            Array.Copy(portraitSrc, 0, newStats, 64, Math.Min(portraitSrc.Length, 28));
            Array.Copy(newStats, 0, outBuf, d.StatsStart, 92);
        }
        File.WriteAllBytes(outPath, outBuf);
        log($"transplanted {n} players M27->M26 (name-matched portraits/hometowns)");
        res.Success = true;
        return res;
    }

    class DraftPlayer
    {
        public int Off, StatsStart, AssetStart, JsonStart, JsonEnd;
        public string Fn, Ln;
    }

    static List<DraftPlayer> Parse(byte[] buf, bool isM26)
    {
        int off = isM26 ? 0x1046 : 0x1646;
        var list = new List<DraftPlayer>();
        while (off < buf.Length)
        {
            int fnEnd = Array.IndexOf(buf, (byte)0, off);
            if (fnEnd < 0) break;
            string fn = Encoding.ASCII.GetString(buf, off, fnEnd - off);
            if (string.IsNullOrEmpty(fn) || fn.Length > 20) break;
            int lnStart = off + (isM26 ? 17 : 21);
            int lnEnd = Array.IndexOf(buf, (byte)0, lnStart);
            if (lnEnd < 0) break;
            string ln = Encoding.ASCII.GetString(buf, lnStart, lnEnd - lnStart);
            int statsStart = isM26 ? off + 17 + 21 + 28 : off + 21 + 49;
            int assetStart = statsStart + (isM26 ? 92 : 90);
            if (assetStart + 32 > buf.Length) break;
            int jsonStart = Array.IndexOf(buf, (byte)'{', assetStart);
            if (jsonStart < 0) break;
            // find json end by brace counting
            int depth = 0, end = -1;
            for (int i = jsonStart; i < buf.Length; i++)
            {
                if (buf[i] == '{') depth++;
                else if (buf[i] == '}') depth--;
                if (depth == 0) { end = i + 1; break; }
                if (i - jsonStart > 8192) break;
            }
            if (end < 0) break;
            list.Add(new DraftPlayer { Off = off, Fn = fn, Ln = ln, StatsStart = statsStart, AssetStart = assetStart, JsonStart = jsonStart, JsonEnd = end });
            int next = end;
            while (next < buf.Length && buf[next] == 0) next++;
            off = next;
            if (list.Count > 500) break;
        }
        return list;
    }
}
