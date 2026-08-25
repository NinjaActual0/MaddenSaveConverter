using System.Text;

namespace MaddenSaveConverter.Tdb2;

public static class SixBit
{
    public static string Decode(byte[] data)
    {
        var sb = new StringBuilder();
        int totalBits = data.Length * 8;
        for (int off = 0; off + 6 <= totalBits; off += 6)
            sb.Append((char)(GetBits(data, off, 6) + 32));
        return sb.ToString();
    }

    public static byte[] Compress(string name)
    {
        var bits = new List<int>();
        foreach (char c0 in name)
        {
            char c = c0;
            if (c >= 'a' && c <= 'z') c = char.ToUpper(c);
            if (c is >= 'A' and <= 'Z') { bits.Add(c - 32); continue; }
            if (c is >= '0' and <= '9') { bits.Add(c - 32); continue; }
            throw new InvalidDataException("unsupported six-bit char '" + c + "'");
        }
        while (bits.Count % 4 != 0) bits.Add(0);
        var bytes = new byte[bits.Count / 4 * 3];
        for (int group = 0; group * 4 < bits.Count; group++)
        {
            int o = group * 3;
            int b0 = bits[group * 4];
            int b1 = bits[group * 4 + 1];
            int b2 = bits[group * 4 + 2];
            int b3 = bits[group * 4 + 3];
            bytes[o] = (byte)((b0 << 2) | (b1 >> 4));
            bytes[o + 1] = (byte)(((b1 & 0xF) << 4) | (b2 >> 2));
            bytes[o + 2] = (byte)(((b2 & 0x3) << 6) | b3);
        }
        return bytes;
    }

    static int GetBits(byte[] data, int offset, int length)
    {
        int value = 0;
        for (int i = 0; i < length; i++)
        {
            int bitIndex = offset + i;
            value = (value << 1) | ((data[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1);
        }
        return value;
    }
}

public static class Leb
{
    // Exact port of utilService.readModifiedLebCompressedInteger
    public static long Read(byte[] buf)
    {
        long value = 0;
        bool isNegative = false;
        for (int i = buf.Length - 1; i >= 0; i--)
        {
            long currentByte = buf[i];
            if (i != buf.Length - 1) currentByte ^= 0x80;
            if (i == 0 && (currentByte & 0x40) == 0x40)
            {
                currentByte ^= 0x40;
                isNegative = true;
            }
            long factor = 1L << (i * 6);
            if (i > 1) factor <<= 1;
            value += currentByte * factor;
        }
        return isNegative ? -value : value;
    }

    // Exact port of utilService.writeModifiedLebCompressedInteger
    public static byte[] Write(long signedValue)
    {
        bool isNegative = signedValue < 0;
        ulong value = (ulong)Math.Abs(signedValue);

        static long Factor(int i)
        {
            long factor = 1L << (i * 6);
            if (i > 1) factor <<= 1;
            return factor;
        }

        int numBytes = 1;
        while (value >= (ulong)(Factor(numBytes - 1) * (numBytes == 1 ? 64 : 128)))
            numBytes++;

        var components = new long[numBytes];
        ulong remaining = value;
        for (int i = numBytes - 1; i >= 0; i--)
        {
            long f = Factor(i);
            long maxValue = i == 0 ? 63 : 127;
            components[i] = (long)(remaining / (ulong)f);
            if (components[i] > maxValue) components[i] = maxValue;
            remaining -= (ulong)(components[i] * f);
        }
        for (int i = 0; i < numBytes && remaining > 0; i++)
        {
            long f = Factor(i);
            long maxValue = i == 0 ? 63 : 127;
            long canAdd = Math.Min(maxValue - components[i], (long)(remaining / (ulong)f));
            components[i] += canAdd;
            remaining -= (ulong)(canAdd * f);
        }

        var bytes = new byte[numBytes];
        for (int i = 0; i < numBytes; i++)
        {
            long b = components[i];
            if (i == 0)
            {
                if (isNegative) b |= 0x40;
                if (numBytes > 1) b |= 0x80;
            }
            else if (i < numBytes - 1)
            {
                b |= 0x80;
            }
            bytes[i] = (byte)b;
        }
        return bytes;
    }
}
