using System.IO.Compression;

namespace MaddenSaveConverter.Tdb2;

public class Tdb2ParseException : Exception
{
    public long Offset;
    public Tdb2ParseException(string msg, long offset) : base($"{msg} (inner offset 0x{offset:X})") => Offset = offset;
}

public static class Tdb2Reader
{
    public static Tdb2File Parse(byte[] inner, Action<string> log = null)
    {
        var p = new Cur(inner);
        var file = new Tdb2File();
        int warnings = 0;
        while (p.Pos < p.B.Length && warnings < 20)
        {
            int tableStart = p.Pos;
            try
            {
                var t = ParseTable(p, topLevel: true);
                file.Tables.Add(t);
                log?.Invoke($"   {t.Name} type={t.Type} entries={t.NumEntries} @0x{tableStart:X} -> 0x{p.Pos:X}");
                // guard against drift: sane top-level tables have type 0-5 and a non-negative entry count
                if (t.Type is > 5 or < 0 || t.NumEntries < 0)
                    throw new Tdb2ParseException("implausible table header - parser drift", tableStart);
            }
            catch (EndOfStreamException) { break; }
            catch (Tdb2ParseException e)
            {
                warnings++;
                log?.Invoke($"[warn] {e.Message}");
                break;
            }
        }
        return file;
    }

    private sealed class Cur
    {
        public readonly byte[] B;
        public int Pos;
        public Cur(byte[] b) => B = b;
        public byte U8() { if (Pos >= B.Length) throw new EndOfStreamException(); return B[Pos++]; }
        public byte Peek() { if (Pos >= B.Length) throw new EndOfStreamException(); return B[Pos]; }
        public byte[] Read(int n)
        {
            if (Pos + n > B.Length) throw new EndOfStreamException();
            var r = new byte[n];
            Buffer.BlockCopy(B, Pos, r, 0, n);
            Pos += n;
            return r;
        }
        public byte[] ReadLebBytes()
        {
            var list = new List<byte> { U8() };
            while ((list[^1] & 0x80) != 0) list.Add(U8());
            return list.ToArray();
        }

        // LEB whose first byte was already consumed
        public byte[] ReadLebFrom(byte first)
        {
            var list = new List<byte> { first };
            while ((list[^1] & 0x80) != 0) list.Add(U8());
            return list.ToArray();
        }
    }

    private static Tdb2Table ParseTable(Cur p, bool topLevel)
    {
        int start = p.Pos;
        var head = p.Read(5);
        var t = new Tdb2Table
        {
            RawKey = head,
            Name = SixBit.Decode(head[0..3]),
            Type = head[3],
            Unknown1 = head[4]
        };

        byte b0 = p.U8();
        if (t.Type == 5)
        {
            t.Unknown2 = b0;
            t.NumEntriesRaw = p.ReadLebBytes();       // fresh LEB byte follows the storage-type byte
        }
        else if (t.Type == 3)
        {
            var extra = p.Read(3);
            t.RawKey = new byte[9];
            Buffer.BlockCopy(head, 0, t.RawKey, 0, 5);
            t.RawKey[5] = b0;
            Buffer.BlockCopy(extra, 0, t.RawKey, 6, 3);
            t.NumEntriesRaw = p.ReadLebBytes();       // fresh LEB byte follows the extended key
        }
        else
        {
            t.NumEntriesRaw = p.ReadLebFrom(b0);      // type 4: LEB begins with the byte we just ate
        }

        long entries = t.NumEntries;
        for (long i = 0; i < entries; i++)
        {
            var rec = new Tdb2Record { Index = i };
            if (t.Type == 5)
            {
                rec.Index = Leb.Read(p.ReadLebBytes());
                if (t.Unknown2 == 2)
                {
                    int gzLen = (int)Leb.Read(p.ReadLebBytes());
                    var gzBytes = p.Read(gzLen);
                    rec.RawCompressed = gzBytes;          // preserve original stream for byte-exact save
                    ParseCompressedRecord(gzBytes, rec, t);
                    t.Records.Add(rec);
                    continue;
                }
            }
            ParseFields(p, rec, t, null);
            t.Records.Add(rec);          // _pushTableRecord equivalent
        }

        if (topLevel) t.NormalizeRecords();
        return t;
    }

    // Port of _onTableFieldStart / _checkTableRecordEnd. startBuf = already-consumed bytes
    // belonging to the upcoming field chunk (used by TYPE_UNK excess forwarding).
    private static void ParseFields(Cur p, Tdb2Record rec, Tdb2Table t, byte[] startBuf)
    {
        while (true)
        {
            int need = 5 - (startBuf?.Length ?? 0);
            var chunk = startBuf ?? Array.Empty<byte>();
            if (need > 0) chunk = chunk.Concat(p.Read(need)).ToArray();
            startBuf = null;

            var f = new Tdb2Field
            {
                RawKey = chunk[0..4],
                Key = SixBit.Decode(chunk[0..3]),
                Type = chunk[3]
            };
            t.AddFieldDefinition(f);

            switch (f.Type)
            {
                case Tdb2Field.TYPE_INT:
                {
                    var lebBytes = new List<byte> { chunk[4] };
                    while ((lebBytes[^1] & 0x80) != 0) lebBytes.Add(p.U8());
                    f.Raw = lebBytes.ToArray();
                    if (f.Key is "UNWI" or "TREF")
                        f.Raw = f.Raw.Concat(new[] { p.U8() }).ToArray(); // unconditional extra byte (uncompressed path)
                    rec.Fields[f.Key] = f;
                    break;
                }
                case Tdb2Field.TYPE_STRING:
                {
                    var lenBytes = new List<byte> { chunk[4] };
                    while ((lenBytes[^1] & 0x80) != 0) lenBytes.Add(p.U8());
                    f.Length = (int)Leb.Read(lenBytes.ToArray());
                    f.Raw = p.Read(f.Length);
                    rec.Fields[f.Key] = f;
                    break;
                }
                case Tdb2Field.TYPE_UNK:
                {
                    f.Raw = Array.Empty<byte>();
                    rec.Fields[f.Key] = f;
                    // the consumed 6th byte is actually the first byte of the NEXT field chunk
                    startBuf = new[] { chunk[4] };
                    continue;
                }
                case Tdb2Field.TYPE_SUBTABLE:
                case Tdb2Field.TYPE_SUBTABLE_COMPRESSED:
                {
                    var st = new Tdb2Table
                    {
                        RawKey = f.RawKey,
                        Name = f.Key,
                        Type = f.Type,
                        Unknown1 = chunk[4],
                        IsSubTable = true
                    };
                    if (f.Type == Tdb2Field.TYPE_SUBTABLE_COMPRESSED)
                    {
                        st.Unknown2 = p.U8();
                        st.NumEntriesRaw = p.ReadLebBytes();
                    }
                    else
                    {
                        st.NumEntriesRaw = p.ReadLebFrom(chunk[4]);   // type-4 subtable: LEB starts from the unk1 byte itself
                    }
                    ParseSubTableRecords(p, st);
                    f.SubTable = st;
                    rec.Fields[f.Key] = f;
                    break;
                }
                case Tdb2Field.TYPE_FLOAT:
                {
                    var rest = p.Read(3);
                    f.Raw = new[] { chunk[4] }.Concat(rest).ToArray();
                    rec.Fields[f.Key] = f;
                    break;
                }
                default:
                    throw new Tdb2ParseException($"unsupported field type 0x{f.Type:X2} ('{f.Key}')", p.Pos);
            }

            byte end = p.U8();
            if (end == 0x00) return;      // record terminator
            startBuf = new[] { end };     // non-zero: first byte of the next field chunk (JS forwards it)
        }
    }

    private static void ParseSubTableRecords(Cur p, Tdb2Table st)
    {
        long entries = st.NumEntries;
        for (long i = 0; i < entries; i++)
        {
            var rec = new Tdb2Record { Index = i };
            if (st.Type == 5)
            {
                rec.Index = Leb.Read(p.ReadLebBytes());
                if (st.Unknown2 == 2)
                {
                    int gzLen = (int)Leb.Read(p.ReadLebBytes());
                    var gzBytes = p.Read(gzLen);
                    rec.RawCompressed = gzBytes;          // preserve original stream for byte-exact save
                    ParseCompressedRecord(gzBytes, rec, st);
                    st.Records.Add(rec);
                    continue;
                }
            }
            ParseFields(p, rec, st, null);
            st.Records.Add(rec);
        }
    }

    // Port of _onCompressedTableFieldStart: gunzip, skip CHVI header, handle BLBM/BLOB subrecords.
    private static void ParseCompressedRecord(byte[] gz, Tdb2Record rec, Tdb2Table t)
    {
        byte[] inner = Gunzip(gz);
        var rp = new Cur(inner);
        rp.Read(4); // CHVI header

        if (t.Name is "BLBM" or "BLOB")
        {
            rp.Read(4); // subrecord header
            if (rp.Peek() != 0x00 && rp.Peek() != 0x8E)
            {
                var sub = new Tdb2Record { IsSubRecord = true, ParentRecord = rec };
                ParseDecompressedFields(rp, sub, t);
                rp.Read(4); // parent CHVI header follows the subrecord terminator
                ParseDecompressedFields(rp, rec, t);
            }
            else
            {
                rp.Read(2); // two null bytes
                rp.Read(4); // parent CHVI header
                ParseDecompressedFields(rp, rec, t);
            }
        }
        else
        {
            ParseDecompressedFields(rp, rec, t);
        }
    }

    // Port of _onDecompressedTableFieldStart + _checkCompressedTableRecordEnd
    private static void ParseDecompressedFields(Cur p, Tdb2Record rec, Tdb2Table t)
    {
        while (true)
        {
            var rawKey = p.Read(4);
            var f = new Tdb2Field
            {
                RawKey = rawKey,
                Key = SixBit.Decode(rawKey[0..3]),
                Type = rawKey[3]
            };
            t.AddFieldDefinition(f);

            switch (f.Type)
            {
                case Tdb2Field.TYPE_INT:
                {
                    f.Raw = p.ReadLebBytes();
                    if ((f.Key is "UNWI" or "WRST") && p.Peek() == 0x00)
                        f.Raw = f.Raw.Concat(new[] { p.U8() }).ToArray(); // conditional extra zero
                    rec.Fields[f.Key] = f;
                    break;
                }
                case Tdb2Field.TYPE_STRING:
                {
                    f.Length = (int)Leb.Read(p.ReadLebBytes());
                    f.Raw = p.Read(f.Length);
                    rec.Fields[f.Key] = f;
                    break;
                }
                case Tdb2Field.TYPE_UNK:
                    f.Raw = p.Peek() == 0 ? new[] { p.U8() } : Array.Empty<byte>();
                    rec.Fields[f.Key] = f;
                    break;
                case Tdb2Field.TYPE_SUBTABLE:
                {
                    var st = new Tdb2Table
                    {
                        RawKey = f.RawKey,
                        Name = f.Key,
                        Type = f.Type,
                        Unknown1 = p.U8(),
                        NumEntriesRaw = p.ReadLebBytes(),
                        IsSubTable = true
                    };
                    ParseDecompressedSubTableRecords(p, st);
                    f.SubTable = st;
                    rec.Fields[f.Key] = f;
                    break;
                }
                case Tdb2Field.TYPE_FLOAT:
                    f.Raw = p.Read(4);
                    rec.Fields[f.Key] = f;
                    break;
                default:
                    throw new Tdb2ParseException($"unsupported compressed field type 0x{f.Type:X2}", p.Pos);
            }

            if (p.Peek() == 0x00)
            {
                p.U8(); // terminator
                return;
            }
        }
    }

    // Port of _readCompressedRecordSubTable: records inside decompressed subtables use
    // 4-byte field keys and plain parsing (no UNWI/WRST extra-zero, no UNK byte-eat).
    private static void ParseDecompressedSubTableRecords(Cur p, Tdb2Table st)
    {
        for (long i = 0; i < st.NumEntries; i++)
        {
            var rec = new Tdb2Record { Index = i };
            while (p.Peek() != 0x00)
            {
                var rawKey = p.Read(4);
                var f = new Tdb2Field
                {
                    RawKey = rawKey,
                    Key = SixBit.Decode(rawKey[0..3]),
                    Type = rawKey[3]
                };
                st.AddFieldDefinition(f);
                switch (f.Type)
                {
                    case Tdb2Field.TYPE_INT:
                        f.Raw = p.ReadLebBytes();
                        break;
                    case Tdb2Field.TYPE_STRING:
                        f.Length = (int)Leb.Read(p.ReadLebBytes());
                        f.Raw = p.Read(f.Length);
                        break;
                    case Tdb2Field.TYPE_UNK:
                        f.Raw = Array.Empty<byte>();
                        break;
                    case Tdb2Field.TYPE_SUBTABLE:
                    {
                        var nested = new Tdb2Table
                        {
                            RawKey = f.RawKey,
                            Name = f.Key,
                            Type = f.Type,
                            Unknown1 = p.U8(),
                            NumEntriesRaw = p.ReadLebBytes(),
                            IsSubTable = true
                        };
                        ParseDecompressedSubTableRecords(p, nested);
                        f.SubTable = nested;
                        break;
                    }
                    case Tdb2Field.TYPE_FLOAT:
                        f.Raw = p.Read(4);
                        break;
                    default:
                        throw new Tdb2ParseException($"unsupported subtable field type 0x{f.Type:X2} ('{f.Key}')", p.Pos);
                }
                rec.Fields[f.Key] = f;
            }
            p.U8(); // record terminator null
            st.Records.Add(rec);
        }
    }

    public static byte[] Gunzip(byte[] gz)
    {
        using var src = new MemoryStream(gz);
        using var gzs = new GZipStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        gzs.CopyTo(dst);
        return dst.ToArray();
    }
}
