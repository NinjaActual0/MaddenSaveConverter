using System.IO;
using System.IO.Compression;

namespace MaddenSaveConverter.Tdb2;

public static class Tdb2Writer
{
    public static byte[] Serialize(Tdb2File file)
    {
        using var ms = new MemoryStream();
        foreach (var t in file.Tables) WriteTable(ms, t);
        return ms.ToArray();
    }

    static void WriteTable(MemoryStream ms, Tdb2Table t)
    {
        ms.Write(t.RawKey);
        if (t.Type == 5)
            ms.WriteByte((byte)t.Unknown2);

        ms.Write(t.NumEntriesRaw);

        // keyed type-5 records must be sorted by index (JS writer sorts too)
        if (t.Type == 5)
            t.Records.Sort((a, b) => a.Index.CompareTo(b.Index));

        foreach (var rec in t.Records)
        {
            if (t.Type == 5)
                ms.Write(Leb.Write(rec.Index));

            if (t.Type == 5 && t.Unknown2 == 2)
            {
                // Pass through the ORIGINAL compressed bytes when the record was never
                // modified: the game's inflater can be picky about foreign deflate output,
                // and untouched blobs should survive a conversion byte-exactly.
                if (rec.RawCompressed != null)
                {
                    ms.Write(Leb.Write(rec.RawCompressed.Length));
                    ms.Write(rec.RawCompressed);
                }
                else
                {
                    var body = WriteCompressedRecordBody(t, rec);
                    var gz = Gzip(body);
                    ms.Write(Leb.Write(gz.Length));
                    ms.Write(gz);
                }
            }
            else
            {
                WritePlainRecordFields(ms, rec);
                ms.WriteByte(0x00);
            }
        }
    }

    static void WritePlainRecordFields(MemoryStream ms, Tdb2Record rec)
    {
        foreach (var key in rec.Fields.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var f = rec.Fields[key];
            ms.Write(f.RawKey);
            if (f.Type == Tdb2Field.TYPE_STRING)
                ms.Write(Leb.Write(f.Length));

            if (f.Type == Tdb2Field.TYPE_SUBTABLE_COMPRESSED)
            {
                var st = f.SubTable;
                ms.WriteByte((byte)st.Unknown1);
                ms.WriteByte((byte)st.Unknown2);
                ms.Write(st.NumEntriesRaw);
                WriteCompressedSubtable(ms, st);
            }
            else if (f.Type == Tdb2Field.TYPE_SUBTABLE)
            {
                var st = f.SubTable;
                ms.WriteByte((byte)st.Unknown1);
                ms.Write(st.NumEntriesRaw);
                WriteSubtableRecords(ms, st);
            }
            else
            {
                ms.Write(f.Raw);
            }
        }
    }

    // subTableWriter.write: field-type-4 subtable records (no per-record index emitted)
    static void WriteSubtableRecords(MemoryStream ms, Tdb2Table st)
    {
        foreach (var rec in st.Records)
        {
            foreach (var key in rec.Fields.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var f = rec.Fields[key];
                ms.Write(f.RawKey);
                if (f.Type == Tdb2Field.TYPE_STRING)
                    ms.Write(Leb.Write(f.Length));
                if (f.Type == Tdb2Field.TYPE_SUBTABLE)
                {
                    var sst = f.SubTable;
                    ms.WriteByte((byte)sst.Unknown1);
                    ms.Write(sst.NumEntriesRaw);
                    WriteSubtableRecords(ms, sst);
                }
                else
                {
                    ms.Write(f.Raw);
                }
            }
            ms.WriteByte(0x00);
        }
    }

    // _writeCompressedSubtable: field-type-5 subtable with indexed records
    static void WriteCompressedSubtable(MemoryStream ms, Tdb2Table st)
    {
        st.Records.Sort((a, b) => a.Index.CompareTo(b.Index));
        foreach (var rec in st.Records)
        {
            ms.Write(Leb.Write(rec.Index));
            // pass through original gz bytes for untouched records (game-safe)
            if (rec.RawCompressed != null)
            {
                ms.Write(Leb.Write(rec.RawCompressed.Length));
                ms.Write(rec.RawCompressed);
            }
            else
            {
                var body = WriteCompressedRecordBody(st, rec);
                var gz = Gzip(body);
                ms.Write(Leb.Write(gz.Length));
                ms.Write(gz);
            }
        }
    }

    // _writeCompressedRecord: CHAN/CHVI headers for BLBM/BLOB + alphabetical fields
    static byte[] WriteCompressedRecordBody(Tdb2Table t, Tdb2Record rec)
    {
        using var ms = new MemoryStream();

        if ((t.Name is "BLBM" or "BLOB") && !rec.IsSubRecord)
        {
            ms.Write(Chan());
            ms.Write(Chan());
            if (rec.SubRecord == null)
            {
                ms.Write(new byte[] { 0x00, 0x00 });
            }
            else
            {
                var body = WriteCompressedRecordBody(t, rec.SubRecord);
                ms.Write(body);
            }
        }

        if (!rec.IsSubRecord)
        {
            ms.Write(Chvi());
        }

        foreach (var key in rec.Fields.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var f = rec.Fields[key];
            ms.Write(f.RawKey);
            if (f.Type == Tdb2Field.TYPE_STRING)
                ms.Write(Leb.Write(f.Length));
            if (f.Type != Tdb2Field.TYPE_SUBTABLE)
            {
                ms.Write(f.Raw);
            }
            else
            {
                var st = f.SubTable;
                ms.WriteByte((byte)st.Unknown1);
                ms.Write(st.NumEntriesRaw);
                WriteSubtableRecords(ms, st);
            }
        }
        ms.WriteByte(0x00);
        return ms.ToArray();
    }

    // utilService.compress6BitString("CHAN"/"CHVI") + 0x03 field-type byte
    // C=35,H=40,A=33,N=46 -> 100011 101000 100001 101110 -> 8E 88 6E
    static byte[] Chan() => new byte[] { 0x8E, 0x88, 0x6E, 0x03 };
    // C=35,H=40,V=54,I=41 -> 100011 101000 110110 101001 -> 8E 8D A9
    static byte[] Chvi() => new byte[] { 0x8E, 0x8D, 0xA9, 0x03 };

    public static byte[] Gzip(byte[] data)
    {
        using var src = new MemoryStream(data);
        using var dst = new MemoryStream();
        using (var gz = new GZipStream(dst, CompressionLevel.Optimal))
            src.CopyTo(gz);
        return dst.ToArray();
    }
}
