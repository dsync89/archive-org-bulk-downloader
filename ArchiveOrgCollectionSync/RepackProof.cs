namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Threading;

    public enum ProofVerdict
    {
        /// <summary>Not checked (not a changed zip, or the stage was skipped).</summary>
        None,

        /// <summary>Proven: every file inside is the same as archive.org's; only the zip packaging differs.</summary>
        Same,

        /// <summary>Something inside really changed (files added, removed or different).</summary>
        Changed,

        /// <summary>Couldn't be proven either way (not a zip, compressed ROMs, damaged, network…): treated as changed.</summary>
        NotProven
    }

    public sealed class ProofResult
    {
        public ProofResult(ProofVerdict verdict, string note)
        {
            this.Verdict = verdict;
            this.Note = note;
        }

        public ProofVerdict Verdict { get; }

        /// <summary>Gets a one-line reason, shown in the Sync grid's tooltip and the log.</summary>
        public string Note { get; }

        public override string ToString() => $"{this.Verdict}: {this.Note}";
    }

    /// <summary>
    /// Proves that a changed zip on archive.org holds exactly the files you already have ("repack"), without
    /// downloading it. ROM collections are TorrentZipped: the outer zip's index lists the CRC of each inner romset zip,
    /// and inner romset zips store their ROMs uncompressed with fixed headers. Rebuilding your inner zip the
    /// TorrentZip way (same ROM bytes stored, folder entries dropped, version 10, no attributes, date 1996-12-24 23:32) and
    /// getting archive.org's exact CRC and size proves every ROM is identical. Anything that can't be proven this way
    /// is reported as <see cref="ProofVerdict.NotProven"/>, which Sync treats like a real change.
    /// </summary>
    public static class RepackProof
    {
        private const uint LocalSignature = 0x04034b50;
        private const uint CentralSignature = 0x02014b50;
        private const uint EocdSignature = 0x06054b50;
        private const ushort TorrentZipTime = 0xBC00; // 23:32:00
        private const ushort TorrentZipDate = 0x2198; // 1996-12-24

        /// <summary>Compares your zip with archive.org's index of the new one.</summary>
        /// <param name="local">Your copy (seekable).</param>
        /// <param name="remote">Archive.org's central directory for the new version.</param>
        /// <param name="bytesRead">Gets the compressed bytes read from <paramref name="local"/> (for progress).</param>
        public static ProofResult Prove(Stream local, IReadOnlyList<ZipEntryInfo> remote, Action<long>? bytesRead, CancellationToken ct)
        {
            try
            {
                var mine = RepackProof.ByName(ZipIndex.ReadCentral(local));
                var theirs = RepackProof.ByName(remote);
                var added = theirs.Keys.Where(n => !mine.ContainsKey(n)).ToList();
                var removed = mine.Keys.Where(n => !theirs.ContainsKey(n)).ToList();
                var changed = new List<string>();
                var notProven = new List<string>();
                int rebuilt = 0;

                foreach (var (name, r) in theirs)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!mine.TryGetValue(name, out var l) || (l.Crc == r.Crc && l.Size == r.Size))
                    {
                        continue;
                    }

                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        changed.Add(name);
                        continue;
                    }

                    try
                    {
                        using var inner = ZipIndex.OpenEntry(local, l);
                        var candidates = RepackProof.CanonicalForms(inner, ct);
                        bytesRead?.Invoke(l.CompressedSize);

                        if (candidates.Any(c => c.Crc == r.Crc && c.Size == r.Size))
                        {
                            rebuilt++;
                        }
                        else
                        {
                            changed.Add(name);
                        }
                    }
                    catch (NotSupportedException ex)
                    {
                        notProven.Add($"{name} ({ex.Message})");
                    }
                }

                if (added.Count > 0 || removed.Count > 0 || changed.Count > 0)
                {
                    var parts = new List<string>();
                    RepackProof.AddPart(parts, "+", added);
                    RepackProof.AddPart(parts, "−", removed);
                    RepackProof.AddPart(parts, "*", changed);
                    return new ProofResult(ProofVerdict.Changed, "contents differ from yours: " + string.Join("; ", parts) + (changed.Count > 0 ? " (file-by-file details after the download)" : string.Empty));
                }

                if (notProven.Count > 0)
                {
                    return new ProofResult(ProofVerdict.NotProven, "couldn't prove: " + string.Join("; ", notProven.Take(3)));
                }

                return new ProofResult(
                    ProofVerdict.Same,
                    rebuilt > 0
                        ? $"the same {Format.Plural(theirs.Count, "file")} inside; rebuilt from your ROMs, {(rebuilt == 1 ? "the inner zip matches" : $"all {rebuilt} inner zips match")} archive.org's exactly, only the zip packaging differs"
                        : $"the same {Format.Plural(theirs.Count, "file")} inside with the same CRCs, only the zip container differs");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                return new ProofResult(ProofVerdict.NotProven, "couldn't prove: " + ex.Message);
            }
        }

        /// <summary>
        /// Reads an inner zip front to back and returns the CRC and size its TorrentZip rebuild would have, in the
        /// order found and in TorrentZip's sorted order. Checks every ROM's CRC on the way, so a damaged copy is never
        /// "proven".
        /// </summary>
        /// <exception cref="NotSupportedException">Compressed ROMs, data descriptors, encryption or zip64 inside.</exception>
        public static List<(uint Crc, long Size)> CanonicalForms(Stream inner, CancellationToken ct)
        {
            var entries = new List<(byte[] Name, ushort Utf8, string Key, uint Crc, long Size)>();
            byte[] header = new byte[30];
            byte[] buffer = new byte[1 << 16];

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                ZipIndex.ReadExactly(inner, header, 4);
                uint signature = BitConverter.ToUInt32(header, 0);

                if (signature is RepackProof.CentralSignature or RepackProof.EocdSignature)
                {
                    break;
                }

                if (signature != RepackProof.LocalSignature)
                {
                    throw new InvalidDataException("damaged inner zip");
                }

                ZipIndex.ReadExactly(inner, buffer, 26);
                Buffer.BlockCopy(buffer, 0, header, 4, 26);
                ushort flags = BitConverter.ToUInt16(header, 6);
                ushort method = BitConverter.ToUInt16(header, 8);
                uint crc = BitConverter.ToUInt32(header, 14);
                long compressed = BitConverter.ToUInt32(header, 18);
                long size = BitConverter.ToUInt32(header, 22);
                int nameLength = BitConverter.ToUInt16(header, 26);
                int extraLength = BitConverter.ToUInt16(header, 28);

                if ((flags & 1) != 0)
                {
                    throw new NotSupportedException("encrypted");
                }

                if ((flags & 8) != 0)
                {
                    throw new NotSupportedException("inner zip uses data descriptors");
                }

                if (compressed == 0xFFFFFFFF || size == 0xFFFFFFFF)
                {
                    throw new NotSupportedException("zip64 inner zip");
                }

                byte[] name = new byte[nameLength];
                ZipIndex.ReadExactly(inner, name, nameLength);
                RepackProof.Skip(inner, extraLength, buffer);
                string key = ZipIndex.DecodeName(name, flags);

                if (key.EndsWith('/'))
                {
                    RepackProof.Skip(inner, compressed, buffer);
                    continue;
                }

                if (method is not (0 or 8))
                {
                    throw new NotSupportedException($"ROMs inside use compression method {method}");
                }

                // Stored or deflated, the rebuild stores the ROM's own bytes: read (inflate) them and check CRC and size.
                var window = new ZipIndex.SubStream(inner, compressed);
                uint actual = 0;
                long length = 0;

                using (var data = method == 8 ? new DeflateStream(window, CompressionMode.Decompress, leaveOpen: true) : (Stream)window)
                {
                    int n;

                    while ((n = data.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        actual = Crc32.Append(actual, buffer.AsSpan(0, n));
                        length += n;
                    }
                }

                RepackProof.Skip(inner, window.Remaining, buffer);

                if (actual != crc || length != size)
                {
                    throw new InvalidDataException($"{key}: CRC doesn't match, your copy is damaged");
                }

                entries.Add((name, (ushort)(flags & 0x800), key, crc, size));
            }

            // Drain the rest so a DeflateStream reaches its end cleanly.
            while (inner.Read(buffer, 0, buffer.Length) > 0)
            {
            }

            if (entries.Count > 0xFFFF)
            {
                throw new NotSupportedException("too many files for a plain zip");
            }

            var sorted = entries.OrderBy(e => e.Key.ToLowerInvariant(), StringComparer.Ordinal).ToList();
            var forms = new List<(uint Crc, long Size)> { RepackProof.Canonical(entries) };

            if (!sorted.SequenceEqual(entries))
            {
                forms.Add(RepackProof.Canonical(sorted));
            }

            return forms;
        }

        /// <summary>Lists the files inside a zip, one level into inner zips ("game.zip/rom.bin"), by name with CRC and size.</summary>
        public static Dictionary<string, (uint Crc, long Size)> ListContents(Stream zip, CancellationToken ct)
        {
            var result = new Dictionary<string, (uint, long)>(StringComparer.Ordinal);

            foreach (var entry in ZipIndex.ReadCentral(zip).Where(e => !e.IsDirectory))
            {
                ct.ThrowIfCancellationRequested();

                if (entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var inner = ZipIndex.OpenEntry(zip, entry);
                        var roms = RepackProof.ForwardList(inner, ct);

                        foreach (var (name, crc, size) in roms)
                        {
                            result[entry.Name + "/" + name] = (crc, size);
                        }

                        continue;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                    {
                        // Not a readable zip: compare it as one file.
                    }
                }

                result[entry.Name] = (entry.Crc, entry.Size);
            }

            return result;
        }

        /// <summary>Lists an inner zip's files from its local headers, front to back, skipping their data.</summary>
        private static List<(string Name, uint Crc, long Size)> ForwardList(Stream inner, CancellationToken ct)
        {
            var result = new List<(string, uint, long)>();
            byte[] header = new byte[30];
            byte[] buffer = new byte[1 << 16];

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                ZipIndex.ReadExactly(inner, header, 4);
                uint signature = BitConverter.ToUInt32(header, 0);

                if (signature is RepackProof.CentralSignature or RepackProof.EocdSignature)
                {
                    break;
                }

                if (signature != RepackProof.LocalSignature)
                {
                    throw new InvalidDataException("damaged inner zip");
                }

                ZipIndex.ReadExactly(inner, buffer, 26);
                Buffer.BlockCopy(buffer, 0, header, 4, 26);
                ushort flags = BitConverter.ToUInt16(header, 6);

                if ((flags & 8) != 0)
                {
                    throw new NotSupportedException("inner zip uses data descriptors");
                }

                byte[] name = new byte[BitConverter.ToUInt16(header, 26)];
                ZipIndex.ReadExactly(inner, name, name.Length);
                RepackProof.Skip(inner, BitConverter.ToUInt16(header, 28), buffer);
                RepackProof.Skip(inner, BitConverter.ToUInt32(header, 18), buffer);
                string key = ZipIndex.DecodeName(name, flags);

                if (!key.EndsWith('/'))
                {
                    result.Add((key, BitConverter.ToUInt32(header, 14), BitConverter.ToUInt32(header, 22)));
                }
            }

            while (inner.Read(buffer, 0, buffer.Length) > 0)
            {
            }

            return result;
        }

        /// <summary>What changed between an old and a new copy of a zip, file by file (after a download).</summary>
        public static string Describe(Dictionary<string, (uint Crc, long Size)> before, Dictionary<string, (uint Crc, long Size)> after)
        {
            var added = after.Keys.Where(k => !before.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var removed = before.Keys.Where(k => !after.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var changed = after.Keys.Where(k => before.TryGetValue(k, out var b) && b != after[k]).OrderBy(k => k, StringComparer.Ordinal).ToList();

            if (added.Count + removed.Count + changed.Count == 0)
            {
                return $"same {Format.Plural(after.Count, "file")} inside (identical CRCs); only the packaging changed";
            }

            var parts = new List<string>();
            RepackProof.AddPart(parts, "+", added);
            RepackProof.AddPart(parts, "−", removed);
            RepackProof.AddPart(parts, "*", changed);
            return string.Join("; ", parts) + $" ({after.Count - added.Count - changed.Count} unchanged)";
        }

        private static void AddPart(List<string> parts, string glyph, List<string> names)
        {
            if (names.Count > 0)
            {
                parts.Add($"{glyph}{names.Count} " + string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", …" : string.Empty));
            }
        }

        private static Dictionary<string, ZipEntryInfo> ByName(IEnumerable<ZipEntryInfo> entries)
        {
            var map = new Dictionary<string, ZipEntryInfo>(StringComparer.Ordinal);

            foreach (var entry in entries.Where(e => !e.IsDirectory))
            {
                if (!map.TryAdd(entry.Name, entry))
                {
                    throw new InvalidDataException($"duplicate name {entry.Name}");
                }
            }

            return map;
        }

        /// <summary>CRC and size of the stored, TorrentZip-style zip holding these entries in this order.</summary>
        private static (uint Crc, long Size) Canonical(List<(byte[] Name, ushort Utf8, string Key, uint Crc, long Size)> entries)
        {
            uint crc = 0;
            long offset = 0;
            using var central = new MemoryStream();
            using var writer = new BinaryWriter(central);

            foreach (var e in entries)
            {
                using var local = new MemoryStream();
                using (var w = new BinaryWriter(local, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    w.Write(RepackProof.LocalSignature);
                    w.Write((ushort)10);
                    w.Write(e.Utf8);
                    w.Write((ushort)0);
                    w.Write(RepackProof.TorrentZipTime);
                    w.Write(RepackProof.TorrentZipDate);
                    w.Write(e.Crc);
                    w.Write((uint)e.Size);
                    w.Write((uint)e.Size);
                    w.Write((ushort)e.Name.Length);
                    w.Write((ushort)0);
                    w.Write(e.Name);
                }

                crc = Crc32.Append(crc, local.ToArray());
                crc = Crc32.Combine(crc, e.Crc, e.Size);

                writer.Write(RepackProof.CentralSignature);
                writer.Write((ushort)10);
                writer.Write((ushort)10);
                writer.Write(e.Utf8);
                writer.Write((ushort)0);
                writer.Write(RepackProof.TorrentZipTime);
                writer.Write(RepackProof.TorrentZipDate);
                writer.Write(e.Crc);
                writer.Write((uint)e.Size);
                writer.Write((uint)e.Size);
                writer.Write((ushort)e.Name.Length);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write(0u);
                writer.Write((uint)offset);
                writer.Write(e.Name);

                offset += local.Length + e.Size;

                if (offset > uint.MaxValue)
                {
                    throw new NotSupportedException("inner zip over 4 GB");
                }
            }

            writer.Write(RepackProof.EocdSignature);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)entries.Count);
            writer.Write((ushort)entries.Count);
            writer.Write((uint)(central.Length - 0));
            writer.Write((uint)offset);
            writer.Write((ushort)0);
            writer.Flush();

            // The central directory size excludes the 22-byte end record written just above.
            byte[] tail = central.ToArray();
            int cdLength = tail.Length - 22;
            BitConverter.TryWriteBytes(tail.AsSpan(cdLength + 12, 4), (uint)cdLength);
            crc = Crc32.Append(crc, tail);
            return (crc, offset + tail.Length);
        }

        private static void Skip(Stream stream, long count, byte[] buffer)
        {
            while (count > 0)
            {
                int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));

                if (n <= 0)
                {
                    throw new InvalidDataException("unexpected end of inner zip");
                }

                count -= n;
            }
        }
    }
}
