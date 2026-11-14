namespace ArchiveOrgCollectionSync.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Xunit;

    using static SnapshotDiffTests;

    public sealed class LinkHistoryTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "aocs-tests-" + Guid.NewGuid().ToString("N")[..8]);

        public LinkHistoryTests()
        {
            Directory.CreateDirectory(this.folder);
        }

        public void Dispose()
        {
            Directory.Delete(this.folder, true);
        }

        private static SnapshotInfo Snap(int day) => new() { Id = $"s{day}", Taken = new DateTime(2026, 11, day, 12, 0, 0), Source = "sync" };

        private static long Unix(int day) => new DateTimeOffset(new DateTime(2026, 11, day, 12, 0, 0)).ToUnixTimeSeconds();

        [Fact]
        public void DescribeStates()
        {
            var link = new LinkRecord { Collection = "c", Folder = "f" };
            Assert.Equal("?", LinkHistory.Describe(link).Glyph);

            link.Snapshots.Add(Snap(10));
            Assert.Equal("·", LinkHistory.Describe(link).Glyph);

            link.ItemLastUpdated = Unix(9);
            link.LastCheck = new LinkCheck { Checked = DateTime.Now, RemoteUpdated = Unix(9) };
            Assert.Equal("●", LinkHistory.Describe(link).Glyph);

            link.LastCheck.RemoteUpdated = Unix(12);
            Assert.Equal("▲", LinkHistory.Describe(link).Glyph);

            link.LastCheck.FolderMissing = true;
            Assert.Equal("✖", LinkHistory.Describe(link).Glyph);

            link.LastCheck = new LinkCheck { ItemMissing = true };
            Assert.Equal("✖", LinkHistory.Describe(link).Glyph);

            link.LastCheck = new LinkCheck { Error = "offline" };
            Assert.Equal("⚠", LinkHistory.Describe(link).Glyph);
        }

        [Fact]
        public void DescribeWithoutSyncComparesWithSnapshotTime()
        {
            // Only Start ran (no ItemLastUpdated): changed when archive.org's date is after the snapshot.
            var link = new LinkRecord { Snapshots = { Snap(10) }, LastCheck = new LinkCheck { RemoteUpdated = Unix(11) } };
            Assert.Equal("▲", LinkHistory.Describe(link).Glyph);
            link.LastCheck.RemoteUpdated = Unix(9);
            Assert.Equal("●", LinkHistory.Describe(link).Glyph);
        }

        [Fact]
        public void DescribeNeverSyncedButChangedAfterLastRun()
        {
            var link = new LinkRecord { LastCheck = new LinkCheck { RemoteUpdated = Unix(12) } };
            Assert.Equal("▲", LinkHistory.Describe(link, new DateTime(2026, 11, 10)).Glyph);
            Assert.Equal("?", LinkHistory.Describe(link, new DateTime(2026, 11, 13)).Glyph);
            Assert.Equal("?", LinkHistory.Describe(link).Glyph);
        }

        [Theory]
        [InlineData("history/files/TP/g.zip.~2~", "TP/g.zip", true, 2)]
        [InlineData("history/files/tp/G.ZIP.~1~", "TP/g.zip", true, 1)]
        [InlineData("history/files/TP/g.zip.~x~", "TP/g.zip", false, 0)]
        [InlineData("TP/g.zip", "TP/g.zip", false, 0)]
        public void HistoryCopyNames(string historyName, string name, bool expected, int number)
        {
            Assert.Equal(expected, LinkHistory.IsHistoryCopyOf(historyName, name, out int n));
            Assert.Equal(number, n);
        }

        [Fact]
        public void BuildCollapsesRunsAddsKeptCopiesRunsAndLocal()
        {
            var snapshots = new List<(SnapshotInfo, ArchiveOrgCollectionSync.File[])>
            {
                (Snap(1), new[] { F("g.zip", 10, "aa") }),
                (Snap(2), new[] { F("g.zip", 10, "aa") }),
                (Snap(3), new[] { F("g.zip", 12, "bb"), F("history/files/g.zip.~1~", 10, "aa", mtime: Unix(3).ToString(), old: true) }),
                (Snap(4), new[] { F("other.zip", 1, "cc") })
            };
            var session = new SessionRecord { Started = new DateTime(2026, 11, 1, 13, 0, 0), Files = { new SessionFile { Name = "g.zip", Md5 = "aa", Status = FileStatus.Done } } };

            var versions = LinkHistory.Build("g.zip", snapshots, new[] { session }, localMd5: "bb", localHashed: true);

            Assert.Equal(4, versions.Count);
            Assert.NotNull(versions[0].HistoryPath);
            Assert.Contains("(2)", versions[1].Source);
            Assert.Contains("downloaded", versions[1].Note);
            Assert.Equal("bb", versions[2].Md5);
            Assert.Equal("= on disk ✔", versions[2].Local);
            Assert.Contains("not listed", versions[3].Note);
        }

        [Fact]
        public void RelocateMovesLinkSessionsAndLedger()
        {
            string oldFolder = Path.Combine(this.folder, "old");
            string newFolder = Path.Combine(this.folder, "new");
            Directory.CreateDirectory(oldFolder);
            Directory.CreateDirectory(newFolder);

            var link = LinkStore.GetOrCreate("u", "relo", oldFolder, Array.Empty<SessionRecord>());
            LinkStore.AddSnapshot(link, new[] { F("g.zip", 1, "aa") }, "sync", DateTime.Now);
            LinkStore.Save(link);
            var ledger = VerifiedLedger.Load(oldFolder);
            ledger.Put("g.zip", new LedgerEntry { Md5 = "aa", Size = 1 });
            ledger.Save();
            var session = new SessionRecord { Id = "relo_" + Guid.NewGuid().ToString("N")[..6], Collection = "relo", Folder = oldFolder, Started = DateTime.Now };
            SessionStore.Save(session);

            LinkStore.Relocate(link, newFolder, new[] { session });

            Assert.Null(LinkStore.Find("relo", oldFolder));
            var moved = LinkStore.Find("relo", newFolder);
            Assert.NotNull(moved);
            Assert.Single(moved!.Snapshots);
            Assert.Single(LinkStore.LoadSnapshot(moved, moved.Snapshots[0]));
            Assert.Equal(1, VerifiedLedger.Load(newFolder).Count);
            Assert.Equal(0, VerifiedLedger.Load(oldFolder).Count);
            Assert.Equal(newFolder, SessionStore.LoadAll().First(s => s.Id == session.Id).Folder);
            SessionStore.Delete(session);
        }

        [Fact]
        public void SavedLinkWithoutSnapshotsStillGetsSnapshotZero()
        {
            string f = Path.Combine(this.folder, "seed");
            Directory.CreateDirectory(f);
            LinkStore.Save(new LinkRecord { Id = LinkStore.KeyFor("seed", f), Collection = "seed", Folder = f });
            var session = new SessionRecord { Id = "seed_x", Collection = "seed", Folder = f, Started = DateTime.Now, Files = { new SessionFile { Name = "g.zip", Md5 = "aa", Size = 1, HasSize = true } } };

            var link = LinkStore.GetOrCreate("u", "seed", f, new[] { session });

            Assert.Single(link.Snapshots);
            Assert.StartsWith("session", link.Snapshots[0].Source);
        }
    }
}
