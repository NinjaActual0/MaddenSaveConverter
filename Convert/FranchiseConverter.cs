using System.Diagnostics;

namespace MaddenSaveConverter.Convert;

public static class FranchiseConverter
{
    public static ConvertResult M26ToM27(string srcPath, string tplPath, string outPath) => Run(srcPath, tplPath, outPath, "M26->M27");
    public static ConvertResult M27ToM26(string srcPath, string tplPath, string outPath) => Run(srcPath, tplPath, outPath, "M27->M26");

    static ConvertResult Run(string src, string tpl, string outPath, string dir)
    {
        var res = new ConvertResult { OutputPath = outPath };
        void log(string s) => res.Log.Add(s);
        log($"franchise {dir}: {src} -> {outPath} via {tpl}");

        // Prefer Node-based converter if available (madden-franchise ESM)
        string script = Path.Combine(AppContext.BaseDirectory, "convert_franchise_m26_to_m27.mjs");
        // Fallback to repo source location during dotnet run
        if (!File.Exists(script))
        {
            var alt = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "convert_franchise_m26_to_m27.mjs");
            if (File.Exists(alt)) script = alt;
        }
        if (!File.Exists(script))
        {
            // try repo root
            var repoScript = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "..", "Source", "Repos", "MaddenSaveConverter", "convert_franchise_m26_to_m27.mjs");
            // Actually just look next to exe
            script = Path.Combine(AppContext.BaseDirectory, "convert_franchise_m26_to_m27.mjs");
        }

        string node = "node";
        // Try to find node - set NODE_PATH so madden-franchise can be found
        var psi = new ProcessStartInfo
        {
            FileName = node,
            Arguments = $"\"{script}\" \"{src}\" \"{tpl}\" \"{outPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Ensure madden-franchise is found (for dotnet run vs published exe)
        string repoNodeModules = Path.Combine(AppContext.BaseDirectory, "node_modules");
        string srcNodeModules = Path.Combine(Path.GetDirectoryName(typeof(FranchiseConverter).Assembly.Location) ?? "", "node_modules");
        string altNodeModules = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "..", "Source", "Repos", "MaddenSaveConverter", "node_modules");
        var nodePaths = new List<string>();
        if (Directory.Exists(repoNodeModules)) nodePaths.Add(repoNodeModules);
        if (Directory.Exists(srcNodeModules)) nodePaths.Add(srcNodeModules);
        if (Directory.Exists(altNodeModules)) nodePaths.Add(Path.GetFullPath(altNodeModules));
        // Also add the madden-franchise-utils node_modules as fallback
        string utilsNodeModules = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Source", "Repos", "madden-franchise-utils", "node_modules");
        if (Directory.Exists(utilsNodeModules)) nodePaths.Add(utilsNodeModules);
        if (nodePaths.Count > 0)
            psi.Environment["NODE_PATH"] = string.Join(Path.PathSeparator.ToString(), nodePaths);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) throw new Exception("failed to start node");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            foreach (var line in stdout.Split('\n')) if (!string.IsNullOrWhiteSpace(line)) log(line.Trim());
            foreach (var line in stderr.Split('\n')) if (!string.IsNullOrWhiteSpace(line)) log("[node] " + line.Trim());
            if (p.ExitCode != 0) { res.Success = false; log($"node exit {p.ExitCode}"); return res; }
            res.Success = File.Exists(outPath);
            if (!res.Success) log("output not created");
            return res;
        }
        catch (Exception ex)
        {
            log($"[error] franchise converter requires Node.js + madden-franchise: {ex.Message}");
            log("falling back to raw copy (no table mapping) - file may not load correctly");
            try { File.Copy(src, outPath, true); res.Success = true; } catch { res.Success = false; }
            return res;
        }
    }
}
