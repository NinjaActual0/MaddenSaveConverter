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

    // "M26"/"M27"/null based on the FBCHUNKS header version string
    public static string DetectVersion(string path)
    {
        var header = new byte[0x2E + 40];
        using var fs = File.OpenRead(path);
        if (fs.Read(header, 0, header.Length) < 0x2E) return null;
        if (System.Text.Encoding.ASCII.GetString(header, 0, 8) != "FBCHUNKS") return null;
        int end = Array.IndexOf(header, (byte)0, 0x2E);
        var ver = System.Text.Encoding.ASCII.GetString(header, 0x2E, end < 0 ? 40 : end - 0x2E);
        if (ver.Contains("Madden-27")) return "M27";
        if (ver.Contains("Madden-26")) return "M26";
        return null;
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
        string tplPath = ver == "M27" ? TplM26 : TplM27;
        Console.WriteLine($"source: {srcPath} ({ver})");
        Console.WriteLine($"template: {tplPath}");
        Console.WriteLine($"personas: {(zeroPersonas ? "zeroed" : "randomized sampling")}");

        ConvertResult res = ver == "M26"
            ? RosterProfiles.M26ToM27(RosterFile.Load(srcPath), RosterFile.Load(tplPath), outPath, !zeroPersonas)
            : RosterProfiles.M27ToM26(RosterFile.Load(srcPath), RosterFile.Load(tplPath), outPath);
        foreach (var l in res.Log) Console.WriteLine("  " + l);

        bool ok = true;
        RosterFile.Verify(outPath, m => { Console.WriteLine("  " + m); if (m.Contains("BAD") || m.Contains("FAIL")) ok = false; });
        Console.WriteLine(ok && res.Success ? $"DONE -> {outPath}" : "FAILED");
        return ok && res.Success ? 0 : 1;
    }
}
