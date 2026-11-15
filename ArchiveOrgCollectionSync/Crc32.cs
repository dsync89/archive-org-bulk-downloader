namespace ArchiveOrgCollectionSync
{
    using System;

    /// <summary>
    /// The zip CRC-32 (slicing-by-8), plus <see cref="Combine"/>: the CRC of A+B from CRC(A), CRC(B) and B's length,
    /// so a rebuilt zip's CRC can be computed in any entry order without holding the data (zlib's crc32_combine).
    /// </summary>
    public static class Crc32
    {
        private const uint Polynomial = 0xEDB88320;
        private static readonly uint[] Table = Crc32.BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data) => Crc32.Append(0, data);

        /// <summary>Continues <paramref name="crc"/> (a finished CRC, as stored in zips) over more data.</summary>
        public static uint Append(uint crc, ReadOnlySpan<byte> data)
        {
            uint c = ~crc;
            var t = Crc32.Table;
            int i = 0;

            for (; i + 8 <= data.Length; i += 8)
            {
                uint one = BitConverter.ToUInt32(data.Slice(i, 4)) ^ c;
                uint two = BitConverter.ToUInt32(data.Slice(i + 4, 4));
                c = t[(7 * 256) + (one & 0xFF)] ^ t[(6 * 256) + ((one >> 8) & 0xFF)] ^ t[(5 * 256) + ((one >> 16) & 0xFF)] ^ t[(4 * 256) + (one >> 24)]
                    ^ t[(3 * 256) + (two & 0xFF)] ^ t[(2 * 256) + ((two >> 8) & 0xFF)] ^ t[256 + ((two >> 16) & 0xFF)] ^ t[two >> 24];
            }

            for (; i < data.Length; i++)
            {
                c = t[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            }

            return ~c;
        }

        /// <summary>CRC of the concatenation A+B, given CRC(A), CRC(B) and B's length.</summary>
        public static uint Combine(uint crcA, uint crcB, long lengthB)
        {
            if (lengthB <= 0)
            {
                return crcA;
            }

            var even = new uint[32];
            var odd = new uint[32];

            // Operator for one zero bit in odd.
            odd[0] = Crc32.Polynomial;
            uint row = 1;

            for (int n = 1; n < 32; n++)
            {
                odd[n] = row;
                row <<= 1;
            }

            Crc32.Square(even, odd); // two zero bits
            Crc32.Square(odd, even); // four zero bits

            uint crc = crcA;

            do
            {
                Crc32.Square(even, odd);

                if ((lengthB & 1) != 0)
                {
                    crc = Crc32.Times(even, crc);
                }

                lengthB >>= 1;

                if (lengthB == 0)
                {
                    break;
                }

                Crc32.Square(odd, even);

                if ((lengthB & 1) != 0)
                {
                    crc = Crc32.Times(odd, crc);
                }

                lengthB >>= 1;
            }
            while (lengthB != 0);

            return crc ^ crcB;
        }

        private static uint Times(uint[] matrix, uint vector)
        {
            uint sum = 0;

            for (int i = 0; vector != 0; i++, vector >>= 1)
            {
                if ((vector & 1) != 0)
                {
                    sum ^= matrix[i];
                }
            }

            return sum;
        }

        private static void Square(uint[] square, uint[] matrix)
        {
            for (int n = 0; n < 32; n++)
            {
                square[n] = Crc32.Times(matrix, matrix[n]);
            }
        }

        private static uint[] BuildTable()
        {
            var table = new uint[8 * 256];

            for (uint i = 0; i < 256; i++)
            {
                uint c = i;

                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? Crc32.Polynomial ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            for (int i = 0; i < 256; i++)
            {
                for (int s = 1; s < 8; s++)
                {
                    uint prev = table[((s - 1) * 256) + i];
                    table[(s * 256) + i] = (prev >> 8) ^ table[prev & 0xFF];
                }
            }

            return table;
        }
    }
}
