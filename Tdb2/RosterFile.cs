using System.IO.Compression;
using System.Text;

namespace MaddenSaveConverter.Tdb2;

public class RosterFile
{
    public byte[] Header;     // 0x4A bytes
    public Tdb2File Db;
    public int SaveSize = 6291530;

    public string VersionString
    {
        get
        {
            int end = Array.IndexOf(Header, (byte)0, 0x2E);
            if (end < 0) end = Math.Min(0x2E + 40, Header.Length);
            return Encoding.ASCII.GetString(Header, 0x2E, Math.Min(end, Header.Length) - 0x2E);
        }
    }

    public string GameTag
    {
        get
        {
            var v = VersionString;
            if (v.Contains("Madden-27")) return "M27";
            if (v.Contains("Madden-26")) return "M26";
            if (v.Contains("College")) return "CFB27";
            if (v.Contains("Madden-25")) return "M25";
            return "UNK";
        }
    }

    public static RosterFile Load(string path)
    {
        var fb = File.ReadAllBytes(path);
        if (fb.Length < 0x4A || Encoding.ASCII.GetString(fb, 0, 8) != "FBCHUNKS")
            throw new InvalidDataException(path + " is not an FBCHUNKS save file");

        int dataStart;
        if (fb.Length >= 0x53 && fb[0x52] == 0x78 && (fb[0x53] == 0x9C || fb[0x53] == 0xDA || fb[0x53] == 0x01))
            dataStart = 0x52;
        else if (fb.Length >= 0x4B && fb[0x4A] == 0x78 && (fb[0x4B] == 0x9C || fb[0x4B] == 0xDA || fb[0x4B] == 0x01))
            dataStart = 0x4A;
        else
        {
            int year = BitConverter.ToUInt16(fb, 0x16);
            dataStart = year >= 2021 ? 0x4A : 0x3E;
        }

        var inner = Inflate(fb, dataStart);
        var rf = new RosterFile { Header = fb[0..dataStart], SaveSize = fb.Length };
        rf.Db = Tdb2Reader.Parse(inner, Console.WriteLine);
        return rf;
    }

    static byte[] Inflate(byte[] fb, int start)
    {
        using var src = new MemoryStream(fb, start, fb.Length - start);
        using var zs = new ZLibStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        zs.CopyTo(dst);
        return dst.ToArray();
    }

    // Port of MaddenRosterHelper.save(): header + deflate(level 9) + recomputed len/CRC,
    // then normalized to the exact save size.
    public void Save(string path)
    {
        var inner = Tdb2Writer.Serialize(Db);

        byte[] deflated;
        using (var src = new MemoryStream(inner))
        using (var dst = new MemoryStream())
        {
            using (var zs = new ZLibStream(dst, CompressionLevel.Optimal))
                src.CopyTo(zs);
            deflated = dst.ToArray();
        }

        uint crc = Crc32Be(inner);
        var header = (byte[])Header.Clone();
        BitConverter.GetBytes(inner.Length).CopyTo(header, 0x12);
        BitConverter.GetBytes(crc).CopyTo(header, 0x1A);

        using var outMs = new MemoryStream();
        outMs.Write(header);
        outMs.Write(deflated);
        var full = outMs.ToArray();

        if (full.Length > SaveSize)
            Array.Resize(ref full, SaveSize);
        else if (full.Length < SaveSize)
            full = full.Concat(new byte[SaveSize - full.Length]).ToArray();

        File.WriteAllBytes(path, full);
    }

    public static bool Verify(string path, Action<string> log)
    {
        var fb = File.ReadAllBytes(path);
        int dataStart = (fb.Length >= 0x53 && fb[0x52] == 0x78 && (fb[0x53] == 0x9C || fb[0x53] == 0xDA || fb[0x53] == 0x01)) ? 0x52 : 0x4A;
        var inner = Inflate(fb, dataStart);
        uint storedLen = BitConverter.ToUInt32(fb, 0x12);
        uint storedCrc = BitConverter.ToUInt32(fb, 0x1A);
        uint calcCrc = Crc32Be(inner);
        bool lenOk = storedLen == (uint)inner.Length;
        bool crcOk = storedCrc == calcCrc;
        log($"  size {fb.Length} | len {(lenOk ? "OK" : $"BAD ({storedLen} vs {inner.Length})")} | crc {(crcOk ? "OK" : $"BAD (0x{storedCrc:X8} vs 0x{calcCrc:X8})")}");
        return lenOk && crcOk;
    }

    // CRC-32/BZIP2 (poly 0x04C11DB7, MSB-first, init/xorout 0xFFFFFFFF) — the roster checksum
    public static uint Crc32Be(byte[] data)
    {
        const uint Poly = 0x04C11DB7u;
        var table = new uint[16];
        uint crc = 0x80000000u;
        table[0] = 0;
        for (int i = 1; i < 16; i <<= 1)
        {
            crc = (crc << 1) ^ ((crc & 0x80000000u) != 0 ? Poly : 0);
            for (int j = 0; j < i; j++)
                table[i + j] = crc ^ table[j];
        }
        crc ^= 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            crc = (crc << 4) ^ table[crc >> 28];
            crc = (crc << 4) ^ table[crc >> 28];
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
