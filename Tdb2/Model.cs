namespace MaddenSaveConverter.Tdb2;

public class Tdb2Field
{
    public const int TYPE_INT = 0;
    public const int TYPE_STRING = 1;
    public const int TYPE_UNK = 3;
    public const int TYPE_SUBTABLE = 4;
    public const int TYPE_SUBTABLE_COMPRESSED = 5;
    public const int TYPE_FLOAT = 10;

    public string Key;
    public byte[] RawKey;      // 4 bytes: 3 six-bit + type byte
    public int Type;
    public int Length;         // string fields only
    public byte[] Raw;
    public Tdb2Table SubTable; // subtable fields only

    public long IntValue
    {
        get => Leb.Read(Raw);
        set => Raw = Leb.Write(value);
    }

    public float FloatValue
    {
        get
        {
            if (Raw == null || Raw.Length != 4) return 0f;
            var rev = (byte[])Raw.Clone(); Array.Reverse(rev);
            return BitConverter.ToSingle(rev, 0);
        }
        set
        {
            var b = BitConverter.GetBytes(value); Array.Reverse(b);
            Raw = b;
        }
    }

    public string StringValue
    {
        get
        {
            if (Raw == null) return "";
            var s = System.Text.Encoding.UTF8.GetString(Raw);
            return s.Length > 0 ? s[..^1] : s; // strip trailing NUL like JS
        }
        set
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            var withNul = new byte[bytes.Length + 1];
            Array.Copy(bytes, withNul, bytes.Length);
            Length = withNul.Length;
            Raw = withNul;
        }
    }

    public Tdb2Field Clone()
    {
        return new Tdb2Field
        {
            Key = Key,
            RawKey = (byte[])RawKey.Clone(),
            Type = Type,
            Length = Length,
            Raw = Raw == null ? null : (byte[])Raw.Clone(),
            SubTable = SubTable
        };
    }
}

public class FieldDefinition
{
    public string Name;
    public int Type;
    public byte[] RawKey; // first-seen raw key bytes for exact reconstruction
}

public class Tdb2Record
{
    public long Index;                                // keyed tables
    public Dictionary<string, Tdb2Field> Fields = new(StringComparer.Ordinal);
    public Tdb2Record SubRecord;                      // BLBM/BLOB compressed subrecords
    public bool IsSubRecord;
    public Tdb2Record ParentRecord;
}

public class Tdb2Table
{
    public string Name;
    public int Type;
    public int Unknown1;
    public int Unknown2;                              // type-5 storage type only
    public byte[] RawKey;                             // exactly what the writer emits before the entry count
    public byte[] NumEntriesRaw;
    public List<Tdb2Record> Records = new();
    public List<FieldDefinition> FieldDefinitions = new();
    public bool IsSubTable;

    public long NumEntries
    {
        get => Leb.Read(NumEntriesRaw);
        set => NumEntriesRaw = Leb.Write(value);
    }

    public void AddFieldDefinition(Tdb2Field f)
    {
        if (!FieldDefinitions.Any(d => d.Name == f.Key))
            FieldDefinitions.Add(new FieldDefinition { Name = f.Key, Type = f.Type, RawKey = (byte[])f.RawKey.Clone() });
    }

    // Port of JS _normalizeRecords: inject missing fields so every record carries the full schema.
    public void NormalizeRecords()
    {
        foreach (var rec in Records)
        {
            foreach (var def in FieldDefinitions)
            {
                if (rec.Fields.ContainsKey(def.Name)) continue;
                if (def.Type == Tdb2Field.TYPE_SUBTABLE || def.Type == Tdb2Field.TYPE_SUBTABLE_COMPRESSED) continue;

                var nf = new Tdb2Field
                {
                    Key = def.Name,
                    Type = def.Type,
                    RawKey = def.RawKey ?? SixBit.Compress(def.Name).Concat(new byte[] { (byte)def.Type }).ToArray()
                };
                switch (def.Type)
                {
                    case Tdb2Field.TYPE_INT:
                        nf.Raw = Leb.Write(0);
                        break;
                    case Tdb2Field.TYPE_STRING:
                        nf.Raw = new byte[] { 0x00 };
                        nf.Length = 1;
                        break;
                    case Tdb2Field.TYPE_UNK:
                        nf.Raw = new byte[] { 0x00 };
                        break;
                    case Tdb2Field.TYPE_FLOAT:
                        nf.FloatValue = 0.0f;
                        break;
                }
                rec.Fields[def.Name] = nf;
            }
        }
    }
}

public class Tdb2File
{
    public List<Tdb2Table> Tables = new();
}
