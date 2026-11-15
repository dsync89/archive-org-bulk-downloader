namespace ArchiveOrgCollectionSync.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using Xunit;

    using static SnapshotDiffTests;

    /// <summary>
    /// The repack proof must say "same" only when every file inside really is the same, and say "changed" or "not
    /// proven" for anything else. Fixtures: "old" zips are written the way a typical zip tool does (version 20, Unix
    /// attributes, real dates, folder entries); "new" inner zips are TorrentZip-style, written here independently of
    /// the code under test.
    /// </summary>
    public sealed class RepackProofTests : IDisposable
    {
        private static readonly (string Name, byte[] Data)[] Roms =
        {
            ("1941j/41_01.4a", Bytes(1, 3000)),
            ("41_09.12b", Bytes(2, 70_000)),
            ("41-5k.10a", Bytes(3, 513)),
            ("buf1", Bytes(4, 0)),
        };

        private readonly string folder = Path.Combine(Path.GetTempPath(), "aocs-repack-" + Guid.NewGuid().ToString("N")[..8]);

        public RepackProofTests()
        {
            Directory.CreateDirectory(this.folder);
        }

        public void Dispose()
        {
            Directory.Delete(this.folder, true);
        }

        [Fact]
        public void Crc32MatchesTheStandardAndCombines()
        {
            Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
            byte[] a = Bytes(7, 1234);
            byte[] b = Bytes(8, 77_777);
            Assert.Equal(SlowCrc(a.Concat(b).ToArray()), Crc32.Combine(Crc32.Compute(a), Crc32.Compute(b), b.Length));
            Assert.Equal(SlowCrc(b), Crc32.Compute(b));
        }

        [Fact]
        public void ZipIndexReadsWhatZipArchiveWrote()
        {
            byte[] zip = OldInner(Roms, withFolder: true);
            using var archive = new ZipArchive(new MemoryStream(zip));
            var index = ZipIndex.ReadCentral(new MemoryStream(zip));

            Assert.Equal(archive.Entries.Count, index.Count);

            foreach (var entry in archive.Entries)
            {
                var mine = index.Single(e => e.Name == entry.FullName);
                Assert.Equal(entry.Crc32, mine.Crc);
                Assert.Equal(entry.Length, mine.Size);
            }
        }

        [Fact]
        public void HeaderOnlyRezipIsProvenSame()
        {
            var result = Prove(Outer(("1941.zip", OldInner(Roms, withFolder: false))), Outer(("1941.zip", Canonical(Roms))));
            Assert.Equal(ProofVerdict.Same, result.Verdict);
            Assert.Contains("the inner zip matches", result.Note);
        }

        [Fact]
        public void DroppedFolderEntriesAndSortedOrderAreProvenSame()
        {
            var sorted = Roms.OrderBy(r => r.Name.ToLowerInvariant(), StringComparer.Ordinal).ToArray();
            Assert.Equal(ProofVerdict.Same, Prove(Outer(("g.zip", OldInner(Roms, withFolder: true))), Outer(("g.zip", Canonical(sorted)))).Verdict);
            Assert.Equal(ProofVerdict.Same, Prove(Outer(("g.zip", OldInner(Roms, withFolder: true))), Outer(("g.zip", Canonical(Roms)))).Verdict);
        }

        [Fact]
        public void SameCrcsOnlyContainerChangedIsSame()
        {
            byte[] inner = Canonical(Roms);
            var result = Prove(Outer(("g.zip", inner), ("readme.txt", Bytes(9, 50))), Outer(("readme.txt", Bytes(9, 50)), ("g.zip", inner)));
            Assert.Equal(ProofVerdict.Same, result.Verdict);
        }

        public static IEnumerable<object[]> RealChanges()
        {
            var roms = Roms.ToList();
            byte[] tweaked = (byte[])roms[1].Data.Clone();
            tweaked[12345] ^= 1;

            yield return new object[] { "one byte of one ROM", Outer(("g.zip", Canonical(roms.Select((r, i) => i == 1 ? (r.Name, tweaked) : r)))) };
            yield return new object[] { "a ROM added", Outer(("g.zip", Canonical(roms.Append(("new.bin", Bytes(5, 10)))))) };
            yield return new object[] { "a ROM removed", Outer(("g.zip", Canonical(roms.Skip(1)))) };
            yield return new object[] { "a ROM renamed", Outer(("g.zip", Canonical(roms.Select((r, i) => i == 2 ? ("41-5k.10b", r.Data) : r)))) };
            yield return new object[] { "a ROM moved to another inner zip", Outer(("g.zip", Canonical(roms.Take(3))), ("bios.zip", Canonical(roms.Skip(3)))) };
            yield return new object[] { "an inner zip added", Outer(("g.zip", Canonical(roms)), ("clone.zip", Canonical(roms.Take(1)))) };
            yield return new object[] { "an inner zip renamed", Outer(("g2.zip", Canonical(roms))) };
        }

        [Theory]
        [MemberData(nameof(RealChanges))]
        public void RealChangesAreNeverSame(string what, byte[] remote)
        {
            var result = Prove(Outer(("g.zip", OldInner(Roms, withFolder: true))), remote);
            Assert.True(result.Verdict == ProofVerdict.Changed, $"{what}: {result}");
        }

        [Fact]
        public void PlainFileChangeIsChanged()
        {
            var result = Prove(Outer(("game.exe", Bytes(1, 100))), Outer(("game.exe", Bytes(2, 100))));
            Assert.Equal(ProofVerdict.Changed, result.Verdict);
        }

        [Fact]
        public void CompressedRomsRebuiltAsStoredAreProvenSame()
        {
            // Seen on tp-roms_0 (Atomiswave, NAOMI): the old inner zips deflated their ROMs, the new ones store them.
            var local = Outer(("g.zip", OldInner(Roms, withFolder: true, CompressionLevel.Optimal)));
            Assert.Equal(ProofVerdict.Same, Prove(local, Outer(("g.zip", Canonical(Roms)))).Verdict);
            Assert.Equal(ProofVerdict.Changed, Prove(local, Outer(("g.zip", Canonical(Roms.Skip(1))))).Verdict);
        }

        [Fact]
        public void UnknownCompressionInsideIsNotProven()
        {
            byte[] inner = Canonical(Roms);
            inner[8] = 14; // LZMA in the first local header
            var result = Prove(Outer(("g.zip", inner)), Outer(("g.zip", Canonical(Roms.Select(r => (r.Name, r.Data.Reverse().ToArray()))))));
            Assert.Equal(ProofVerdict.NotProven, result.Verdict);
        }

        [Fact]
        public void DamagedLocalRomIsNotProven()
        {
            // Flip a ROM byte inside the stored inner zip but keep its header CRC: your copy is damaged.
            byte[] inner = Canonical(Roms);
            int at = IndexOf(inner, Roms[1].Data.AsSpan(0, 64).ToArray()) + 100;
            inner[at] ^= 0xFF;
            var result = Prove(Outer(("g.zip", inner)), Outer(("g.zip", Canonical(Roms))));
            Assert.Equal(ProofVerdict.NotProven, result.Verdict);
            Assert.Contains("damaged", result.Note);
        }

        [Fact]
        public void NonZipOrTruncatedLocalIsNotProven()
        {
            var remote = Outer(("g.zip", Canonical(Roms)));
            Assert.Equal(ProofVerdict.NotProven, Prove(Bytes(3, 5000), remote).Verdict);
            byte[] whole = Outer(("g.zip", OldInner(Roms, withFolder: false)));
            Assert.Equal(ProofVerdict.NotProven, Prove(whole[..(whole.Length - 30)], remote).Verdict);
        }

        [Fact]
        public void ListContentsAndDescribeShowRomLevelChanges()
        {
            var before = RepackProof.ListContents(new MemoryStream(Outer(("g.zip", OldInner(Roms, withFolder: true)))), CancellationToken.None);
            var changed = Outer(("g.zip", Canonical(Roms.Skip(1).Append(("clone/x.bin", Bytes(6, 9))))));
            var after = RepackProof.ListContents(new MemoryStream(changed), CancellationToken.None);

            string text = RepackProof.Describe(before, after);
            Assert.Contains("+1 g.zip/clone/x.bin", text);
            Assert.Contains("−1 g.zip/1941j/41_01.4a", text);
            Assert.Contains("same 4 files", RepackProof.Describe(before, RepackProof.ListContents(new MemoryStream(Outer(("g.zip", Canonical(Roms)))), CancellationToken.None)));
        }

        [Fact]
        public void ProvenRepackIsKeptAndCountsAsUpToDate()
        {
            string name = "TP/1941.zip";
            byte[] local = Outer(("1941.zip", OldInner(Roms, withFolder: false)));
            byte[] remote = Outer(("1941.zip", Canonical(Roms)));
            string path = Path.Combine(this.folder, "TP", "1941.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, local);
            string localMd5 = Convert.ToHexString(MD5.HashData(local)).ToLowerInvariant();
            string remoteMd5 = Convert.ToHexString(MD5.HashData(remote)).ToLowerInvariant();

            var ledger = VerifiedLedger.Load(this.folder);
            var info = new FileInfo(path);
            ledger.Put(name, new LedgerEntry { Size = local.Length, Md5 = localMd5, LocalLength = info.Length, LocalWriteUtc = info.LastWriteTimeUtc, Md5Verified = true });
            var a = F(name, local.Length, localMd5);
            var b = F(name, remote.Length, remoteMd5);
            var row = new SyncRow(new DiffEntry(ChangeKind.Changed, a, b), true);
            var rows = new[] { row };
            SyncPlan.ApplyClassification(rows, SyncPlan.Classify(this.folder, ledger, rows, CancellationToken.None));
            Assert.Equal(LocalState.MatchesA, row.Local);
            Assert.Single(SyncPlan.ProofCandidates(rows, keepRepacks: true));
            Assert.Empty(SyncPlan.ProofCandidates(rows, keepRepacks: false));

            var result = SyncPlan.Prove("item", this.folder, ledger, row, (_, _) => new MemoryStream(remote), null, CancellationToken.None);
            Assert.Equal(ProofVerdict.Same, result.Verdict);
            SyncPlan.ApplyClassification(rows, SyncPlan.Classify(this.folder, ledger, rows, CancellationToken.None));

            // Kept by default, but still tickable to fetch archive.org's exact bytes.
            Assert.Equal(LocalState.MatchesB, row.Local);
            Assert.True(row.Repack);
            Assert.Equal("≡ B (repack)", row.LocalText);
            Assert.Equal(SyncAction.Replace, row.Action);
            Assert.False(row.IsChecked);
            Assert.StartsWith("keep yours", row.ActionText);
            Assert.Empty(SyncPlan.ProofCandidates(rows, keepRepacks: true));

            // "All" never ticks a proven re-zip; only an explicit tick replaces it.
            SyncPlan.TickAll(rows);
            Assert.False(row.IsChecked);
            row.IsChecked = true;
            Assert.Equal(SyncAction.Replace, row.Action);
            SyncPlan.TickAll(rows);
            Assert.False(row.IsChecked);

            // Start skips it as verified; your own MD5 is kept for Verify All.
            var item = new FileItem(b);
            Assert.True(ledger.IsVerified(item, path, requireMd5: true));
            Assert.Equal(localMd5, ledger.RepackEntry(item)!.Md5);

            // A later re-upload (new MD5) expires the proof.
            Assert.False(ledger.IsVerified(new FileItem(F(name, remote.Length, "ffff")), path, requireMd5: true));

            // So does a change to your file.
            System.IO.File.AppendAllText(path, "x");
            Assert.False(ledger.IsVerified(item, path, requireMd5: true));
        }

        [Fact]
        public void ChangedZipIsNotRecordedAsEquivalent()
        {
            string name = "g.zip";
            byte[] local = Outer(("g.zip", OldInner(Roms, withFolder: false)));
            byte[] remote = Outer(("g.zip", Canonical(Roms.Skip(1))));
            System.IO.File.WriteAllBytes(Path.Combine(this.folder, name), local);
            var ledger = VerifiedLedger.Load(this.folder);
            var row = new SyncRow(new DiffEntry(ChangeKind.Changed, F(name, local.Length, "aa"), F(name, remote.Length, "bb")), true);

            var result = SyncPlan.Prove("item", this.folder, ledger, row, (_, _) => new MemoryStream(remote), null, CancellationToken.None);

            Assert.Equal(ProofVerdict.Changed, result.Verdict);
            Assert.False(ledger.Files.ContainsKey(name));
        }

        [Fact]
        public void RemoteFailureIsNotProven()
        {
            System.IO.File.WriteAllBytes(Path.Combine(this.folder, "g.zip"), Outer(("g.zip", Canonical(Roms))));
            var ledger = VerifiedLedger.Load(this.folder);
            var row = new SyncRow(new DiffEntry(ChangeKind.Changed, F("g.zip", 1, "aa"), F("g.zip", 2, "bb")), true);

            var result = SyncPlan.Prove("item", this.folder, ledger, row, (_, _) => throw new IOException("range requests not available"), null, CancellationToken.None);

            Assert.Equal(ProofVerdict.NotProven, result.Verdict);
            Assert.Empty(ledger.Files);
        }

        [Fact]
        public void SnapshotZeroPrefersARunNewerThanTheFolderList()
        {
            string f = Path.Combine(this.folder, "seed-newer");
            Directory.CreateDirectory(f);
            string xml = Path.Combine(f, "newer_files.xml");
            System.IO.File.WriteAllText(xml, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><files><file name=\"g.zip\" source=\"original\"><size>5</size><md5>aa</md5></file></files>");
            System.IO.File.SetLastWriteTime(xml, DateTime.Now.AddDays(-4));
            var session = new SessionRecord { Id = "newer_x", Collection = "newer", Folder = f, Started = DateTime.Now.AddDays(-1), Files = { new SessionFile { Name = "g.zip", Md5 = "bb", Size = 6, HasSize = true } } };

            var link = LinkStore.GetOrCreate("u", "newer", f, new[] { session });

            Assert.StartsWith("session", Assert.Single(link.Snapshots).Source);
            Assert.Equal("bb", Assert.Single(LinkStore.LoadSnapshot(link, link.Snapshots[0])).Md5);
        }

        [Fact]
        public void SnapshotZeroKeepsTheFolderListWhenNoRunIsNewer()
        {
            string f = Path.Combine(this.folder, "seed-older");
            Directory.CreateDirectory(f);
            System.IO.File.WriteAllText(Path.Combine(f, "older_files.xml"), "<?xml version=\"1.0\" encoding=\"UTF-8\"?><files><file name=\"g.zip\" source=\"original\"><size>5</size><md5>aa</md5></file></files>");
            var session = new SessionRecord { Id = "older_x", Collection = "older", Folder = f, Started = DateTime.Now.AddDays(-3), Files = { new SessionFile { Name = "g.zip", Md5 = "bb", Size = 6, HasSize = true } } };

            var link = LinkStore.GetOrCreate("u", "older", f, new[] { session });

            Assert.Equal("folder", Assert.Single(link.Snapshots).Source);
        }

        private static ProofResult Prove(byte[] local, byte[] remote)
        {
            return RepackProof.Prove(new MemoryStream(local), ZipIndex.ReadCentral(new MemoryStream(remote)), null, CancellationToken.None);
        }

        private static byte[] Bytes(int seed, int length)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        /// <summary>An inner zip the way a typical tool writes it: real dates, Unix attributes, optional folder entries.</summary>
        private static byte[] OldInner(IEnumerable<(string Name, byte[] Data)> roms, bool withFolder, CompressionLevel level = CompressionLevel.NoCompression)
        {
            using var stream = new MemoryStream();

            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (withFolder)
                {
                    zip.CreateEntry("1941j/").LastWriteTime = new DateTimeOffset(2024, 10, 31, 13, 28, 46, TimeSpan.Zero);
                }

                foreach (var (name, data) in roms)
                {
                    var entry = zip.CreateEntry(name, level);
                    entry.LastWriteTime = new DateTimeOffset(2024, 10, 31, 13, 28, 46, TimeSpan.Zero);
                    entry.ExternalAttributes = 0x01800000;
                    using var s = entry.Open();
                    s.Write(data);
                }
            }

            return stream.ToArray();
        }

        /// <summary>A TorrentZip-style stored zip, written byte by byte here (independent of RepackProof).</summary>
        private static byte[] Canonical(IEnumerable<(string Name, byte[] Data)> roms)
        {
            using var body = new MemoryStream();
            using var central = new MemoryStream();
            var w = new BinaryWriter(body);
            var c = new BinaryWriter(central);
            int count = 0;

            foreach (var (name, data) in roms)
            {
                byte[] n = Encoding.ASCII.GetBytes(name);
                uint crc = SlowCrc(data);
                uint offset = (uint)body.Length;
                w.Write(0x04034b50u); w.Write((ushort)10); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0xBC00); w.Write((ushort)0x2198);
                w.Write(crc); w.Write((uint)data.Length); w.Write((uint)data.Length); w.Write((ushort)n.Length); w.Write((ushort)0); w.Write(n); w.Write(data);
                c.Write(0x02014b50u); c.Write((ushort)10); c.Write((ushort)10); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0xBC00); c.Write((ushort)0x2198);
                c.Write(crc); c.Write((uint)data.Length); c.Write((uint)data.Length); c.Write((ushort)n.Length); c.Write((ushort)0); c.Write((ushort)0);
                c.Write((ushort)0); c.Write((ushort)0); c.Write(0u); c.Write(offset); c.Write(n);
                count++;
            }

            w.Flush();
            c.Flush();
            uint cdOffset = (uint)body.Length;
            body.Write(central.ToArray());
            w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)count); w.Write((ushort)count);
            w.Write((uint)central.Length); w.Write(cdOffset); w.Write((ushort)0);
            w.Flush();
            return body.ToArray();
        }

        /// <summary>An outer zip, deflated (the uploader's tool compresses differently, so its bytes never match).</summary>
        private static byte[] Outer(params (string Name, byte[] Data)[] files)
        {
            using var stream = new MemoryStream();

            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, data) in files)
                {
                    using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    s.Write(data);
                }
            }

            return stream.ToArray();
        }

        private static uint SlowCrc(byte[] data)
        {
            uint c = 0xFFFFFFFF;

            foreach (byte b in data)
            {
                c ^= b;

                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }
            }

            return ~c;
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                {
                    return i;
                }
            }

            throw new InvalidOperationException("not found");
        }
    }
}
