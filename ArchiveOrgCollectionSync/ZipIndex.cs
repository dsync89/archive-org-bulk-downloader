namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Text;

    /// <summary>One entry of a zip's central directory.</summary>
    public sealed class ZipEntryInfo
    {
        public string Name { get; init; } = string.Empty;

        public ushort Flags { get; init; }

        public ushort Method { get; init; }

        public uint Crc { get; init; }

        public long CompressedSize { get; init; }

        public long Size { get; init; }

        public long LocalHeaderOffset { get; init; }

        public bool IsDirectory => this.Name.EndsWith('/');
    }

    /// <summary>
    /// Reads a zip's central directory (the index at the end of the file) from any seekable stream: a local file, or
    /// archive.org over HTTP range requests, where it costs a few KB instead of the whole file. Zip64-aware.
    /// </summary>
    public static class ZipIndex
    {
        private const uint EocdSignature = 0x06054b50;
        private const uint Zip64LocatorSignature = 0x07064b50;
        private const uint Zip64EocdSignature = 0x06064b50;
        private const uint CentralSignature = 0x02014b50;
        private const uint LocalSignature = 0x04034b50;

        static ZipIndex()
        {
            // Field initialisers run before this body, so the code page is looked up here, after registering it.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ZipIndex.Cp437 = Encoding.GetEncoding(437);
        }

        /// <summary>Gets the code page zip names use unless flag 0x800 says UTF-8.</summary>
        public static Encoding Cp437 { get; }

        public static string DecodeName(byte[] raw, ushort flags) => (flags & 0x800) != 0 ? Encoding.UTF8.GetString(raw) : ZipIndex.Cp437.GetString(raw);

        /// <exception cref="InvalidDataException">Not a zip, or a damaged one.</exception>
        public static List<ZipEntryInfo> ReadCentral(Stream stream)
        {
            long length = stream.Length;

            if (length < 22)
            {
                throw new InvalidDataException("Too short to be a zip.");
            }

            // The end record sits in the last 22 bytes plus up to 64 KB of comment.
            int tailLength = (int)Math.Min(length, 22 + 65535 + 20);
            byte[] tail = ZipIndex.ReadAt(stream, length - tailLength, tailLength);
            int eocd = -1;

            for (int i = tailLength - 22; i >= 0; i--)
            {
                if (BitConverter.ToUInt32(tail, i) == ZipIndex.EocdSignature)
                {
                    eocd = i;
                    break;
                }
            }

            if (eocd < 0)
            {
                throw new InvalidDataException("No zip end record.");
            }

            long count = BitConverter.ToUInt16(tail, eocd + 10);
            long cdSize = BitConverter.ToUInt32(tail, eocd + 12);
            long cdOffset = BitConverter.ToUInt32(tail, eocd + 16);

            if ((count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF) && eocd >= 20
                && BitConverter.ToUInt32(tail, eocd - 20) == ZipIndex.Zip64LocatorSignature)
            {
                long zip64Eocd = (long)BitConverter.ToUInt64(tail, eocd - 20 + 8);
                byte[] record = ZipIndex.ReadAt(stream, zip64Eocd, 56);

                if (BitConverter.ToUInt32(record, 0) != ZipIndex.Zip64EocdSignature)
                {
                    throw new InvalidDataException("Damaged zip64 end record.");
                }

                count = (long)BitConverter.ToUInt64(record, 32);
                cdSize = (long)BitConverter.ToUInt64(record, 40);
                cdOffset = (long)BitConverter.ToUInt64(record, 48);
            }

            if (cdOffset < 0 || cdSize < 0 || cdOffset + cdSize > length || cdSize > 256L * 1024 * 1024)
            {
                throw new InvalidDataException("Damaged zip central directory.");
            }

            byte[] cd = ZipIndex.ReadAt(stream, cdOffset, (int)cdSize);
            var entries = new List<ZipEntryInfo>((int)Math.Min(count, 100_000));
            int p = 0;

            for (long n = 0; n < count; n++)
            {
                if (p + 46 > cd.Length || BitConverter.ToUInt32(cd, p) != ZipIndex.CentralSignature)
                {
                    throw new InvalidDataException("Damaged zip central directory entry.");
                }

                ushort flags = BitConverter.ToUInt16(cd, p + 8);
                ushort method = BitConverter.ToUInt16(cd, p + 10);
                uint crc = BitConverter.ToUInt32(cd, p + 16);
                long compressed = BitConverter.ToUInt32(cd, p + 20);
                long size = BitConverter.ToUInt32(cd, p + 24);
                int nameLength = BitConverter.ToUInt16(cd, p + 28);
                int extraLength = BitConverter.ToUInt16(cd, p + 30);
                int commentLength = BitConverter.ToUInt16(cd, p + 32);
                long offset = BitConverter.ToUInt32(cd, p + 42);

                if (p + 46 + nameLength + extraLength + commentLength > cd.Length)
                {
                    throw new InvalidDataException("Damaged zip central directory entry.");
                }

                byte[] name = cd.AsSpan(p + 46, nameLength).ToArray();
                ZipIndex.ApplyZip64(cd, p + 46 + nameLength, extraLength, ref size, ref compressed, ref offset);
                entries.Add(new ZipEntryInfo
                {
                    Name = ZipIndex.DecodeName(name, flags),
                    Flags = flags,
                    Method = method,
                    Crc = crc,
                    CompressedSize = compressed,
                    Size = size,
                    LocalHeaderOffset = offset
                });
                p += 46 + nameLength + extraLength + commentLength;
            }

            return entries;
        }

        /// <summary>Opens one entry's uncompressed data (stored or deflated) as a forward-only stream.</summary>
        /// <exception cref="NotSupportedException">Any other compression method, or encryption.</exception>
        public static Stream OpenEntry(Stream stream, ZipEntryInfo entry)
        {
            if ((entry.Flags & 1) != 0)
            {
                throw new NotSupportedException("encrypted");
            }

            byte[] header = ZipIndex.ReadAt(stream, entry.LocalHeaderOffset, 30);

            if (BitConverter.ToUInt32(header, 0) != ZipIndex.LocalSignature)
            {
                throw new InvalidDataException("Damaged zip local header.");
            }

            long dataStart = entry.LocalHeaderOffset + 30 + BitConverter.ToUInt16(header, 26) + BitConverter.ToUInt16(header, 28);
            stream.Seek(dataStart, SeekOrigin.Begin);
            var raw = new SubStream(stream, entry.CompressedSize);

            return entry.Method switch
            {
                0 => raw,
                8 => new DeflateStream(raw, CompressionMode.Decompress),
                _ => throw new NotSupportedException($"compression method {entry.Method}")
            };
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes, or throws at the end of the stream.</summary>
        public static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;

            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);

                if (n <= 0)
                {
                    throw new InvalidDataException("Unexpected end of zip data.");
                }

                read += n;
            }
        }

        private static byte[] ReadAt(Stream stream, long offset, int count)
        {
            if (offset < 0 || offset + count > stream.Length)
            {
                throw new InvalidDataException("Zip offset out of range.");
            }

            stream.Seek(offset, SeekOrigin.Begin);
            byte[] buffer = new byte[count];
            ZipIndex.ReadExactly(stream, buffer, count);
            return buffer;
        }

        private static void ApplyZip64(byte[] data, int start, int length, ref long size, ref long compressed, ref long offset)
        {
            int p = start;

            while (p + 4 <= start + length)
            {
                ushort id = BitConverter.ToUInt16(data, p);
                ushort fieldLength = BitConverter.ToUInt16(data, p + 2);
                int q = p + 4;

                if (id == 1)
                {
                    if (size == 0xFFFFFFFF && q + 8 <= p + 4 + fieldLength)
                    {
                        size = (long)BitConverter.ToUInt64(data, q);
                        q += 8;
                    }

                    if (compressed == 0xFFFFFFFF && q + 8 <= p + 4 + fieldLength)
                    {
                        compressed = (long)BitConverter.ToUInt64(data, q);
                        q += 8;
                    }

                    if (offset == 0xFFFFFFFF && q + 8 <= p + 4 + fieldLength)
                    {
                        offset = (long)BitConverter.ToUInt64(data, q);
                    }

                    return;
                }

                p += 4 + fieldLength;
            }
        }

        /// <summary>A read-only window of <paramref name="length"/> bytes from the parent's current position.</summary>
        internal sealed class SubStream : Stream
        {
            private readonly Stream parent;
            private long remaining;

            public SubStream(Stream parent, long length)
            {
                this.parent = parent;
                this.remaining = length;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            /// <summary>Gets the bytes of the window not read yet.</summary>
            public long Remaining => this.remaining;

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (this.remaining <= 0)
                {
                    return 0;
                }

                int n = this.parent.Read(buffer, offset, (int)Math.Min(count, this.remaining));
                this.remaining -= n;
                return n;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
