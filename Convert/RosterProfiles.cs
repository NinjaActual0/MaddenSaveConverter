using MaddenSaveConverter.Tdb2;

namespace MaddenSaveConverter.Convert;

public class ConvertResult
{
    public string OutputPath;
    public bool Success;
    public List<string> Log = new();
}

public static class RosterProfiles
{
    // Table order each target game expects (observed from official rosters)
    static readonly string[] M27Order = { "BLOB", "DCHT", "DFTP", "INJY", "PLAY", "PLCT", "PRSN", "TEAM" };
    static readonly string[] M26Order = { "BLOB", "DCHT", "DFTP", "INJY", "PLAY", "PLGS", "TEAM" };

    public static ConvertResult M26ToM27(RosterFile src, RosterFile tpl, string outPath, bool randomizePersona = true)
    {
        var res = new ConvertResult();
        void log(string s) => res.Log.Add(s);
        Tdb2Table Get(RosterFile f, string n) => f.Db.Tables.FirstOrDefault(t => t.Name == n);

        var play = Get(src, "PLAY") ?? throw new InvalidDataException("source has no PLAY table");
        log($"M26 PLAY records: {play.NumEntries}");

        // 1. build PLCT (contracts) from M26 PLAY salary fields
        var tplPlct = Get(tpl, "PLCT") ?? throw new InvalidDataException("template has no PLCT table");
        var donor = tplPlct.Records[0].Fields;
        var rows = new List<Dictionary<string, long>>();
        foreach (var r in play.Records)
        {
            long G(string k) => r.Fields.TryGetValue(k, out var f) && f.Raw != null ? f.IntValue : 0;
            if (!r.Fields.ContainsKey("PGID")) continue;
            rows.Add(new Dictionary<string, long>
            {
                ["PGID"] = G("PGID"),
                ["PCON"] = G("PCON"),
                ["PSA0"] = G("PSA0"), ["PSA1"] = G("PSA1"), ["PSA2"] = G("PSA2"), ["PSA3"] = G("PSA3"),
                ["PSA4"] = G("PSA4"), ["PSA5"] = G("PSA5"), ["PSA6"] = G("PSA6"),
                ["PSA7"] = 0,
                ["PSB0"] = G("PSB0"), ["PSB1"] = G("PSB1"), ["PSB2"] = G("PSB2"), ["PSB3"] = G("PSB3"), ["PSB4"] = G("PSB4"),
                ["CNTC"] = 0, ["CSGY"] = 0, ["CTCY"] = 0, ["CTVY"] = 0
            });
        }
        log($"building PLCT rows: {rows.Count}");
        BuildTable(tplPlct, donor, rows);

        // 2. synthesize PRSN (personas) keyed by PGID. M26 has no persona table, so rows are
        //    built fresh: by default each converted player samples a REAL persona row from the
        //    template (shuffled for even coverage) so DNA data is varied instead of uniform
        //    zeroes; randomizePersona=false reproduces the original zeroed JS-converter output.
        var tplPrsn = Get(tpl, "PRSN") ?? throw new InvalidDataException("template has no PRSN table");
        var prsnRows = new List<Dictionary<string, long>>();
        if (randomizePersona)
        {
            var donors = tplPrsn.Records
                .Where(r => r.Fields.ContainsKey("PGID"))
                .Select(r => r.Fields.Where(kv => kv.Key != "PGID")
                                     .ToDictionary(kv => kv.Key, kv => kv.Value.IntValue))
                .ToList();
            if (donors.Count > 0)
            {
                var rng = new Random();
                var shuffled = donors.OrderBy(_ => rng.Next()).ToList();
                foreach (var r in rows)
                    prsnRows.Add(new Dictionary<string, long>(shuffled[prsnRows.Count % shuffled.Count])
                    {
                        ["PGID"] = r["PGID"]
                    });
                log($"persona variants sampled from {donors.Count} template PRSN rows");
            }
        }
        if (prsnRows.Count == 0)
        {
            prsnRows = rows.Select(r => new Dictionary<string, long>
            {
                ["PGID"] = r["PGID"], ["PRFC"] = 0,
                ["DNA0"] = 0, ["DNA1"] = 0, ["DNA2"] = 0, ["DNA3"] = 0,
                ["DNA4"] = 0, ["DNA5"] = 0, ["DNA6"] = 0, ["DNA7"] = 0
            }).ToList();
        }
        log($"building PRSN rows: {prsnRows.Count}");
        BuildTable(tplPrsn, tplPrsn.Records[0].Fields, prsnRows);

        // 3. assemble in M27 order; data tables from source, PLCT/PRSN rebuilt from template donors
        var newTables = new List<Tdb2Table>();
        foreach (var n in M27Order)
        {
            if (n == "PLCT") { newTables.Add(tplPlct); continue; }
            if (n == "PRSN") { newTables.Add(tplPrsn); continue; }
            var t = Get(src, n) ?? throw new InvalidDataException($"M26 source missing table {n}");
            newTables.Add(t);
        }

        tpl.Db.Tables = newTables;
        tpl.Save(outPath);
        res.OutputPath = outPath;
        res.Success = true;
        return res;
    }

    public static ConvertResult M27ToM26(RosterFile src, RosterFile tpl, string outPath)
    {
        var res = new ConvertResult();
        void log(string s) => res.Log.Add(s);
        Tdb2Table Get(RosterFile f, string n) => f.Db.Tables.FirstOrDefault(t => t.Name == n);

        var play27 = Get(src, "PLAY") ?? throw new InvalidDataException("source has no PLAY table");
        var plct27 = Get(src, "PLCT");
        var tplPlay = Get(tpl, "PLAY") ?? throw new InvalidDataException("template has no PLAY table");

        // salary map PGID -> contract row (M27 keeps salaries in PLCT)
        var salaryByPgid = new Dictionary<long, Dictionary<string, long>>();
        if (plct27 != null)
        {
            foreach (var r in plct27.Records)
            {
                if (!r.Fields.TryGetValue("PGID", out var pg)) continue;
                var row = new Dictionary<string, long>();
                foreach (var kv in r.Fields)
                    if (kv.Key is "PCON" || kv.Key.StartsWith("PSA") || kv.Key.StartsWith("PSB"))
                        row[kv.Key] = kv.Value.Raw != null ? kv.Value.IntValue : 0;
                salaryByPgid[pg.IntValue] = row;
            }
            log($"PLCT contracts indexed: {salaryByPgid.Count}");
        }

        // inject M26-only salary keys into every swapped PLAY record (donor rawKeys from template)
        int migrated = 0;
        var donorSalary = tplPlay.Records[0].Fields;
        foreach (var rec in play27.Records)
        {
            if (!rec.Fields.TryGetValue("PGID", out var pgf)) continue;
            long pgid = pgf.IntValue;
            if (!salaryByPgid.TryGetValue(pgid, out var s)) continue;

            foreach (var key in new[] { "PCON", "PSA0", "PSA1", "PSA2", "PSA3", "PSA4", "PSA5", "PSA6",
                                        "PSB0", "PSB1", "PSB2", "PSB3", "PSB4", "PSB5", "PSB6" })
            {
                if (rec.Fields.ContainsKey(key)) continue;
                if (!donorSalary.TryGetValue(key, out var donorField)) continue;
                var nf = donorField.Clone();
                nf.IntValue = s.TryGetValue(key, out var v) ? v : 0;
                rec.Fields[key] = nf;
                migrated++;
            }
            // PSBO exists in both schemas; fill from template default when absent
            if (!rec.Fields.ContainsKey("PSBO") && donorSalary.TryGetValue("PSBO", out var psboDonor))
                rec.Fields["PSBO"] = psboDonor.Clone();
        }
        log($"salary fields injected into PLAY: {migrated}");

        // assemble in M26 order; data tables from source; PLGS only exists in the M26 template
        var newTables = new List<Tdb2Table>();
        foreach (var n in M26Order)
        {
            if (n == "INJY" && Get(src, n) == null)
            {
                log("note: source has no INJY - using template's");
                newTables.Add(Get(tpl, "INJY"));
                continue;
            }
            if (n == "PLAY")
            {
                newTables.Add(play27);
                continue;
            }
            var t = Get(src, n) ?? Get(tpl, n) ?? throw new InvalidDataException($"neither source nor template has {n}");
            newTables.Add(t);
        }

        tpl.Db.Tables = newTables;
        tpl.Save(outPath);
        res.OutputPath = outPath;
        res.Success = true;
        return res;
    }

    static void BuildTable(Tdb2Table table, Dictionary<string, Tdb2Field> donorFields, List<Dictionary<string, long>> rows)
    {
        table.Records.Clear();
        foreach (var row in rows)
        {
            var rec = new Tdb2Record();
            foreach (var kv in donorFields)
            {
                if (!row.TryGetValue(kv.Key, out var val))
                    throw new InvalidDataException("missing value for field " + kv.Key);
                var f = new Tdb2Field
                {
                    Key = kv.Value.Key,
                    RawKey = (byte[])kv.Value.RawKey.Clone(),
                    Type = kv.Value.Type
                };
                f.IntValue = val; // re-encodes LEB raw exactly like JS value setter
                rec.Fields[f.Key] = f;
            }
            table.Records.Add(rec);
        }
        table.NumEntries = table.Records.Count;
        table.NumEntriesRaw = Leb.Write(table.NumEntries);   // writer serializes the raw bytes
    }
}
