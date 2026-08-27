using MaddenSaveConverter.Convert;
using MaddenSaveConverter.Tdb2;

namespace MaddenSaveConverter;

// Shared locations + headless CLI: --convert <src> <out> [--zero-personas]
public static class Cli
{
    public static readonly string Docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public static readonly string M26Saves = Path.Combine(Docs, "Madden NFL 26", "saves");
    public static readonly string M27Saves = Path.Combine(Docs, "Madden NFL 27", "Saves");
    public static string TplM26 => Path.Combine(AppContext.BaseDirectory, "Templates", "ROSTER-TemplateM26");
    public static string TplM27 => Path.Combine(AppContext.BaseDirectory, "Templates", "ROSTER-TemplateM27");
    public static string TplDraftM26 => ResolveDraftTpl("M26", "CAREERDRAFT-TemplateM26");
    public static string TplDraftM27 => ResolveDraftTpl("M27", "CAREERDRAFT-TemplateM27");
    public static string TplFranchM26 => ResolveFranchTpl("M26", "CAREER-TemplateM26");
    public static string TplFranchM27 => ResolveFranchTpl("M27", "CAREER-TemplateM27");
    static string ResolveDraftTpl(string tag, string tplName)
    {
        var p = Path.Combine(AppContext.BaseDirectory, "Templates", tplName);
        if (File.Exists(p)) return p;
        var saves = tag == "M27" ? M27Saves : M26Saves;
        if (Directory.Exists(saves))
        {
            var files = Directory.GetFiles(saves, "CAREERDRAFT-*");
            // prefer a file that looks like a full class (2020) over older ones
            var pref = files.FirstOrDefault(f => Path.GetFileName(f).Contains("2020"));
            if (pref != null) return pref;
            var f = files.FirstOrDefault();
            if (f != null) return f;
        }
        return p;
    }
    static string ResolveFranchTpl(string tag, string tplName)
    {
        var p = Path.Combine(AppContext.BaseDirectory, "Templates", tplName);
        if (File.Exists(p)) return p;
        var saves = tag == "M27" ? M27Saves : M26Saves;
        if (Directory.Exists(saves))
        {
            var files = Directory.GetFiles(saves, "CAREER-*").Where(f => !Path.GetFileName(f).StartsWith("CAREERDRAFT")).ToArray();
            var pref = files.FirstOrDefault(f => Path.GetFileName(f).Contains("2011") || Path.GetFileName(f).Contains("2020"));
            if (pref != null) return pref;
            var f = files.FirstOrDefault();
            if (f != null) return f;
        }
        return p;
    }

    // "M26"/"M27"/null based on the FBCHUNKS header version string
    public static string DetectVersion(string path)
    {
        var header = new byte[0x80];
        using var fs = File.OpenRead(path);
        if (fs.Read(header, 0, header.Length) < 8) return null;
        if (System.Text.Encoding.ASCII.GetString(header, 0, 8) != "FBCHUNKS") return null;
        var all = System.Text.Encoding.ASCII.GetString(header, 0, header.Length);
        if (all.Contains("Madden-27")) return "M27";
        if (all.Contains("Madden-26")) return "M26";
        int end = Array.IndexOf(header, (byte)0, 0x2E);
        var ver = System.Text.Encoding.ASCII.GetString(header, 0x2E, end < 0 ? 40 : end - 0x2E);
        if (ver.Contains("Madden-27")) return "M27";
        if (ver.Contains("Madden-26")) return "M26";
        return null;
    }

    public static int RunDumpFields(string[] args)
    {
        int i = Array.IndexOf(args, "--dump-fields");
        if (i + 1 >= args.Length) { Console.WriteLine("usage: --dump-fields <file> [table]"); return 1; }
        string filter = i + 2 < args.Length ? args[i + 2] : null;
        var rf = RosterFile.Load(args[i + 1]);
        foreach (var t in rf.Db.Tables)
        {
            if (filter != null && t.Name != filter) continue;
            DumpTable(t, "");
        }
        return 0;
    }

    static void DumpTable(Tdb2Table t, string indent)
    {
        Console.WriteLine($"{indent}=== {t.Name} type={t.Type} entries={t.NumEntries} defs={t.FieldDefinitions.Count} Unknown1={t.Unknown1} Unknown2={t.Unknown2} RawKey=[{BitConverter.ToString(t.RawKey)}] ===");
        foreach (var fd in t.FieldDefinitions)
            Console.WriteLine($"{indent}  {fd.Name}  type=0x{fd.Type:X2}");
        if (t.Records.Count > 0)
        {
            var first = t.Records[0];
            Console.WriteLine($"{indent}  first record fields ({first.Fields.Count}):");
            foreach (var kv in first.Fields.OrderBy(x => x.Key))
            {
                if (kv.Value.SubTable != null)
                {
                    Console.WriteLine($"{indent}    {kv.Key} = [subtable {kv.Value.SubTable.Name}]");
                    DumpTable(kv.Value.SubTable, indent + "    ");
                }
                else
                    Console.WriteLine($"{indent}    {kv.Key} = {kv.Value.IntValue}");
            }
        }
    }

    public static int RunCompareInner(string[] args)
    {
        int i = Array.IndexOf(args, "--compare-inner");
        if (i + 2 >= args.Length) { Console.WriteLine("usage: --compare-inner <fileA> <fileB>"); return 1; }
        var aInner = Inflate(File.ReadAllBytes(args[i + 1]));
        var bInner = Inflate(File.ReadAllBytes(args[i + 2]));
        var aDb = Tdb2Reader.Parse(aInner, _ => { });
        var bDb = Tdb2Reader.Parse(bInner, _ => { });

        Console.WriteLine($"fileA inner: {aInner.Length} bytes  tables: {string.Join(" ", aDb.Tables.Select(t => $"{t.Name}({t.Type})"))}");
        Console.WriteLine($"fileB inner: {bInner.Length} bytes  tables: {string.Join(" ", bDb.Tables.Select(t => $"{t.Name}({t.Type})"))}");

        // compare serialized inner data
        var aSer = Tdb2Writer.Serialize(aDb);
        var bSer = Tdb2Writer.Serialize(bDb);
        Console.WriteLine($"\nRe-serialized: A={aSer.Length} B={bSer.Length}");

        int minLen = Math.Min(aSer.Length, bSer.Length);
        int firstDiff = -1, diffCount = 0;
        for (int j = 0; j < minLen; j++)
        {
            if (aSer[j] != bSer[j]) { if (firstDiff < 0) firstDiff = j; diffCount++; }
        }
        if (firstDiff >= 0)
        {
            Console.WriteLine($"First diff at re-serialized offset: 0x{firstDiff:X}");
            Console.WriteLine($"A: {BitConverter.ToString(aSer, firstDiff, Math.Min(32, minLen - firstDiff))}");
            Console.WriteLine($"B: {BitConverter.ToString(bSer, firstDiff, Math.Min(32, minLen - firstDiff))}");
            Console.WriteLine($"Total differing bytes in overlap: {diffCount}");
        }
        else
            Console.WriteLine("Re-serialized inner data is IDENTICAL");

        // Also compare each table header to find where divergence starts
        Console.WriteLine("\nPer-table comparison (first record bytes):");
        var aTables = aDb.Tables.ToDictionary(t => t.Name);
        var bTables = bDb.Tables.ToDictionary(t => t.Name);
        foreach (var name in aTables.Keys.Intersect(bTables.Keys))
        {
            var at = aTables[name]; var bt = bTables[name];
            if (at.Records.Count != bt.Records.Count)
                Console.WriteLine($"  {name}: record count differs ({at.Records.Count} vs {bt.Records.Count})");
        }

        // Dump first 32 bytes of raw inner data from each file
        Console.WriteLine("\nRaw inner first 32 bytes:");
        Console.WriteLine($"  A: {BitConverter.ToString(aInner, 0, Math.Min(32, aInner.Length))}");
        Console.WriteLine($"  B: {BitConverter.ToString(bInner, 0, Math.Min(32, bInner.Length))}");

        // Dump first table header from parsed data
        if (aDb.Tables.Count > 0 && bDb.Tables.Count > 0)
        {
            var at0 = aDb.Tables[0]; var bt0 = bDb.Tables[0];
            Console.WriteLine($"\nFirst table A: {at0.Name} type={at0.Type} RawKey=[{BitConverter.ToString(at0.RawKey)}]");
            Console.WriteLine($"First table B: {bt0.Name} type={bt0.Type} RawKey=[{BitConverter.ToString(bt0.RawKey)}]");
        }

        return 0;

        static byte[] Inflate(byte[] fb)
        {
            using var src = new System.IO.MemoryStream(fb, 0x4A, fb.Length - 0x4A);
            using var zs = new System.IO.Compression.ZLibStream(src, System.IO.Compression.CompressionMode.Decompress);
            using var dst = new System.IO.MemoryStream();
            zs.CopyTo(dst);
            return dst.ToArray();
        }
    }

    public static int RunConvert(string[] args)
    {
        int i = Array.IndexOf(args, "--convert");
        if (i + 2 >= args.Length)
        {
            Console.WriteLine("usage: --convert <src> <out> [--zero-personas]");
            return 1;
        }
        string srcPath = args[i + 1], outPath = args[i + 2];
        bool zeroPersonas = args.Contains("--zero-personas");

        string ver = DetectVersion(srcPath);
        if (ver == null) { Console.WriteLine("[error] not an FBCHUNKS M26/M27 save"); return 1; }
        string fn = Path.GetFileName(srcPath);
        bool isDraft = fn.StartsWith("CAREERDRAFT", StringComparison.OrdinalIgnoreCase);
        bool isFranchise = fn.StartsWith("CAREER-", StringComparison.OrdinalIgnoreCase) && !isDraft;
        string tplPath;
        string kind;
        if (isDraft) { tplPath = ver == "M27" ? TplDraftM26 : TplDraftM27; kind = "[draft]"; }
        else if (isFranchise) { tplPath = ver == "M27" ? TplFranchM26 : TplFranchM27; kind = "[franchise]"; }
        else { tplPath = ver == "M27" ? TplM26 : TplM27; kind = "[roster]"; }
        Console.WriteLine($"source: {srcPath} ({ver}) {kind}");
        Console.WriteLine($"template: {tplPath}");
        if (!isDraft && !isFranchise) Console.WriteLine($"personas: {(zeroPersonas ? "zeroed" : "randomized sampling")}");

        ConvertResult res;
        if (isDraft) res = ver == "M26" ? DraftConverter.M26ToM27(srcPath, tplPath, outPath) : DraftConverter.M27ToM26(srcPath, tplPath, outPath);
        else if (isFranchise) res = ver == "M26" ? FranchiseConverter.M26ToM27(srcPath, tplPath, outPath) : FranchiseConverter.M27ToM26(srcPath, tplPath, outPath);
        else res = ver == "M26"
                ? RosterProfiles.M26ToM27(RosterFile.Load(srcPath), RosterFile.Load(tplPath), outPath, !zeroPersonas)
                : RosterProfiles.M27ToM26(RosterFile.Load(srcPath), RosterFile.Load(tplPath), outPath);
        foreach (var l in res.Log) Console.WriteLine("  " + l);

        bool ok = true;
        if (isDraft || isFranchise) ok = File.Exists(outPath) && new FileInfo(outPath).Length > 1000;
        else RosterFile.Verify(outPath, m => { Console.WriteLine("  " + m); if (m.Contains("BAD") || m.Contains("FAIL")) ok = false; });
        Console.WriteLine(ok && res.Success ? $"DONE -> {outPath}" : "FAILED");
        return ok && res.Success ? 0 : 1;
    }
}
