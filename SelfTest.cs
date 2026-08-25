using System.IO;
using System.IO.Compression;
using MaddenSaveConverter.Convert;
using MaddenSaveConverter.Tdb2;

namespace MaddenSaveConverter;

// Headless validation: converts the known-good M26 roster with the same inputs we
// verified in-game via the JS converter, then compares inner payloads.
public static class SelfTest
{
    public static int Run(string[] args = null)
    {
        if (args != null && args.Contains("--reverse")) return RunReverse();
        return RunForward();
    }

    // M27 -> M26: source = the verified M27-format output of the forward conversion,
    // template = the original M26 roster. Validates salaries migrate back into PLAY.
    static int RunReverse()
    {
        Console.WriteLine("== Legion of Loom MFC self-test (reverse: M27 -> M26) ==");
        try
        {
            var m27src = @"C:\Users\Ninja\AppData\Local\Temp\opencode\m26to27\ROSTER-M26CONVERTED";
            var m26tpl = @"C:\Users\Ninja\Documents\Madden NFL 26\saves\ROSTER-2019";
            var outPath = Path.Combine(Path.GetTempPath(), "mfc_selftest_ROSTER-back-to-26");

            var s = RosterFile.Load(m27src);
            Console.WriteLine($"  source: {s.GameTag} ver={s.VersionString}");
            var t26 = RosterFile.Load(m26tpl);
            Console.WriteLine($"  template: {t26.GameTag} ver={t26.VersionString}");

            var res = RosterProfiles.M27ToM26(s, t26, outPath);
            foreach (var l in res.Log) Console.WriteLine("  " + l);

            Console.Write("verify output: ");
            if (!RosterFile.Verify(outPath, m => Console.WriteLine(m))) return 2;

            var reload = RosterFile.Load(outPath);
            Console.WriteLine("reload tables: " + string.Join(" ", reload.Db.Tables.Select(t => $"{t.Name}({t.NumEntries})")));

            // spot-check: salary fields must exist on PLAY again and match known values
            var play = reload.Db.Tables.First(t => t.Name == "PLAY");
            var pgid112 = play.Records.FirstOrDefault(r =>
                r.Fields.TryGetValue("PGID", out var f) && f.IntValue == 112);
            if (pgid112 == null) { Console.WriteLine("SPOT-CHECK FAILED: PGID 112 not found"); return 4; }
            long v(string k) => pgid112.Fields.TryGetValue(k, out var f) ? f.IntValue : -1;
            Console.WriteLine($"  PGID112 spot: PCON={v("PCON")} PSA0={v("PSA0")} PSB0={v("PSB0")} PSBO={v("PSBO")}");
            bool ok = v("PCON") == 4 && v("PSA0") == 615 && v("PSB0") == 75;
            Console.WriteLine(ok ? "REVERSE SELFTEST PASSED" : "REVERSE SPOT-CHECK MISMATCH");
            return ok ? 0 : 4;
        }
        catch (Exception ex)
        {
            Console.WriteLine("REVERSE SELFTEST FAILED: " + ex);
            return 1;
        }
    }

    static int RunForward()
    {
        var src = @"C:\Users\Ninja\Documents\Madden NFL 26\saves\ROSTER-2019";
        var tpl = @"C:\Users\Ninja\Documents\Madden NFL 27\Saves\ROSTER-Official";
        var jsOut = @"C:\Users\Ninja\AppData\Local\Temp\opencode\m26to27\ROSTER-M26CONVERTED";
        var outPath = Path.Combine(Path.GetTempPath(), "mfc_selftest_ROSTER-2019");

        Console.WriteLine("== Legion of Loom MFC self-test ==");
        try
        {
            Console.WriteLine("loading source (M26 ROSTER-2019)...");
            var s = RosterFile.Load(src);
            Console.WriteLine($"  {s.GameTag} ver={s.VersionString} tables={s.Db.Tables.Count}");

            Console.WriteLine("loading template (M27 Official)...");
            var t27 = RosterFile.Load(tpl);
            Console.WriteLine($"  {t27.GameTag} ver={t27.VersionString} tables={t27.Db.Tables.Count}");
            foreach (var t in t27.Db.Tables)
                Console.WriteLine($"   {t.Name} type={t.Type} entries={t.NumEntries}");

            Console.WriteLine("converting M26 -> M27...");
            var res = RosterProfiles.M26ToM27(s, t27, outPath);
            foreach (var l in res.Log) Console.WriteLine("  " + l);

            Console.Write("verify output: ");
            if (!RosterFile.Verify(outPath, m => Console.WriteLine(m))) return 2;

            // roundtrip reload
            var reload = RosterFile.Load(outPath);
            Console.WriteLine("reload tables: " + string.Join(" ", reload.Db.Tables.Select(t => $"{t.Name}({t.NumEntries})")));

            if (File.Exists(jsOut))
            {
                // Byte-identity is not the bar (different deflate impls produce valid-but-different
                // streams). The bar: identical structure and identical field values.
                bool same = SemanticallyEqual(jsOut, outPath);
                Console.WriteLine($"semantic match vs verified JS converter: {same}");
                return same ? 0 : 3;
            }
            Console.WriteLine("(JS reference output not found - skipped comparison)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SELFTEST FAILED: " + ex);
            return 1;
        }
    }

    static bool SemanticallyEqual(string jsPath, string csPath)
    {
        var a = RosterFile.Load(jsPath);
        var b = RosterFile.Load(csPath);
        bool ok = true;

        if (a.Db.Tables.Count != b.Db.Tables.Count)
        {
            Console.WriteLine($"  table count differs: JS={a.Db.Tables.Count} C#={b.Db.Tables.Count}");
            return false;
        }

        for (int ti = 0; ti < a.Db.Tables.Count; ti++)
        {
            var ta = a.Db.Tables[ti];
            var tb = b.Db.Tables[ti];
            if (ta.Name != tb.Name || ta.Records.Count != tb.Records.Count)
            {
                Console.WriteLine($"  table[{ti}] differs: JS {ta.Name}({ta.Records.Count}) vs C# {tb.Name}({tb.Records.Count})");
                ok = false;
                continue;
            }
            int diffs = 0;
            for (int ri = 0; ri < ta.Records.Count && diffs < 5; ri++)
            {
                var ra = ta.Records[ri];
                var rb = tb.Records[ri];
                foreach (var kv in ra.Fields)
                {
                    if (!rb.Fields.TryGetValue(kv.Key, out var fb2)) { Report(ta.Name, ri, kv.Key, "missing in C#"); diffs++; break; }
                    if (!FieldsEqual(kv.Value, fb2)) { Report(ta.Name, ri, kv.Key, "value differs"); diffs++; break; }
                }
            }
            if (diffs == 0) Console.WriteLine($"  {ta.Name}: {ta.Records.Count} records, all fields equal");
            else ok = false;
        }
        return ok;

        static void Report(string tbl, int rec, string key, string msg) =>
            Console.WriteLine($"  DIFF {tbl}[{rec}].{key}: {msg}");
    }

    static bool FieldsEqual(Tdb2Field x, Tdb2Field y)
    {
        if (x.Type != y.Type) return false;
        if (x.SubTable != null || y.SubTable != null)
        {
            if (x.SubTable == null || y.SubTable == null) return false;
            if (x.SubTable.Name != y.SubTable.Name || x.SubTable.Records.Count != y.SubTable.Records.Count) return false;
            for (int i = 0; i < x.SubTable.Records.Count; i++)
            {
                var rx = x.SubTable.Records[i]; var ry = y.SubTable.Records[i];
                foreach (var kv in rx.Fields)
                    if (!ry.Fields.TryGetValue(kv.Key, out var fy) || !FieldsEqual(kv.Value, fy)) return false;
            }
            return true;
        }
        if (x.Raw == null || y.Raw == null) return x.Raw == null && y.Raw == null;
        if (x.Type == Tdb2Field.TYPE_INT) return x.IntValue == y.IntValue;   // LEB encodings may differ legitimately
        return x.Raw.SequenceEqual(y.Raw);
    }
}
