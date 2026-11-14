namespace ArchiveOrgCollectionSync.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using Xunit;

    using static SnapshotDiffTests;

    internal static class TestSetup
    {
        /// <summary>Points the app's data folder (sessions, ledgers, links) at a temp folder before anything reads it.</summary>
        [ModuleInitializer]
        internal static void Init()
        {
            Environment.SetEnvironmentVariable("AOCS_APPDATA", Path.Combine(Path.GetTempPath(), "aocs-tests-appdata-" + Environment.ProcessId));
        }
    }

    public sealed class SyncPlanTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "aocs-tests-" + Guid.NewGuid().ToString("N")[..8]);

        public SyncPlanTests()
        {
            Directory.CreateDirectory(this.folder);
        }

        public void Dispose()
        {
            Directory.Delete(this.folder, true);
        }

        [Theory]
        [InlineData(ChangeKind.Added, LocalState.Missing, false, true, SyncAction.Download)]
        [InlineData(ChangeKind.Added, LocalState.Missing, false, false, SyncAction.None)]
        [InlineData(ChangeKind.Added, LocalState.MatchesB, false, true, SyncAction.None)]
        [InlineData(ChangeKind.Added, LocalState.Unknown, false, true, SyncAction.Check)]
        [InlineData(ChangeKind.Removed, LocalState.MatchesA, false, true, SyncAction.MoveAway)]
        [InlineData(ChangeKind.Removed, LocalState.Missing, false, true, SyncAction.None)]
        [InlineData(ChangeKind.Renamed, LocalState.MatchesA, false, true, SyncAction.Rename)]
        [InlineData(ChangeKind.Renamed, LocalState.Unknown, false, true, SyncAction.Rename)]
        [InlineData(ChangeKind.Renamed, LocalState.Differs, false, true, SyncAction.Replace)]
        [InlineData(ChangeKind.Renamed, LocalState.MatchesA, true, true, SyncAction.MoveAway)]
        [InlineData(ChangeKind.Renamed, LocalState.Missing, false, false, SyncAction.None)]
        [InlineData(ChangeKind.Moved, LocalState.MatchesA, false, true, SyncAction.Rename)]
        [InlineData(ChangeKind.Changed, LocalState.MatchesA, false, true, SyncAction.Replace)]
        [InlineData(ChangeKind.Changed, LocalState.MatchesB, false, true, SyncAction.None)]
        [InlineData(ChangeKind.Changed, LocalState.Unknown, false, true, SyncAction.Check)]
        [InlineData(ChangeKind.Changed, LocalState.Missing, false, true, SyncAction.Download)]
        [InlineData(ChangeKind.Changed, LocalState.Missing, false, false, SyncAction.None)]
        [InlineData(ChangeKind.Touched, LocalState.MatchesA, false, true, SyncAction.None)]
        public void Plan(ChangeKind kind, LocalState local, bool targetExists, bool wanted, SyncAction expected)
        {
            Assert.Equal(expected, SyncRow.Plan(kind, local, targetExists, wanted));
        }

        [Fact]
        public void UnactionableRowCannotBeTicked()
        {
            var row = new SyncRow(new DiffEntry(ChangeKind.Touched, F("a", 1, "aa"), F("a", 1, "aa")), true);
            row.IsChecked = true;
            Assert.False(row.IsChecked);
        }

        [Fact]
        public void BaselineAdoptsSessionVerificationWithoutHashing()
        {
            var (name, md5) = this.Write("TP/game.zip", "hello");
            var session = Session(DateTime.Now.AddMinutes(1), new SessionFile { Name = name, Size = 5, HasSize = true, Md5 = md5, Status = FileStatus.Done });
            var ledger = VerifiedLedger.Load(this.folder);

            var result = SyncPlan.Baseline(this.folder, ledger, new[] { session }, new[] { F(name, 5, md5, sha1: "s1") }, null, CancellationToken.None);

            Assert.Equal(1, result.Adopted);
            Assert.Equal(0, result.NeedHash);
            var entry = ledger.Current(name, new FileInfo(this.PathOf(name)));
            Assert.NotNull(entry);
            Assert.True(entry!.Md5Verified);
            Assert.Equal("s1", entry.Sha1);
            Assert.StartsWith("session ", entry.Source);
        }

        [Fact]
        public void BaselineDoesNotTrustFilesWrittenAfterTheSession()
        {
            var (name, md5) = this.Write("g.zip", "hello");
            var session = Session(DateTime.Now.AddHours(-2), new SessionFile { Name = name, Size = 5, HasSize = true, Md5 = md5, Status = FileStatus.Done });
            var ledger = VerifiedLedger.Load(this.folder);

            var result = SyncPlan.Baseline(this.folder, ledger, new[] { session }, new[] { F(name, 5, md5) }, null, CancellationToken.None);

            Assert.Equal(0, result.Adopted);
            Assert.Equal(1, result.NeedHash);
        }

        [Fact]
        public void BaselineCountsDifferentSizeAndMissing()
        {
            this.Write("small.zip", "abc");
            var ledger = VerifiedLedger.Load(this.folder);
            var result = SyncPlan.Baseline(this.folder, ledger, Array.Empty<SessionRecord>(), new[] { F("small.zip", 99, "aa"), F("gone.zip", 1, "bb") }, null, CancellationToken.None);
            Assert.Equal(1, result.Different);
            Assert.Equal(1, result.Missing);
        }

        [Fact]
        public void JudgeUsesLedgerToTellVersions()
        {
            var (name, md5) = this.Write("g.zip", "hello");
            var ledger = VerifiedLedger.Load(this.folder);
            var a = F(name, 5, md5);
            var b = F(name, 6, "bb");
            Assert.Equal(LocalState.Unknown, SyncPlan.Judge(this.folder, ledger, name, a, b).State);

            var info = new FileInfo(this.PathOf(name));
            ledger.Put(name, new LedgerEntry { Size = 5, Md5 = md5, LocalLength = info.Length, LocalWriteUtc = info.LastWriteTimeUtc, Md5Verified = true });
            Assert.Equal((LocalState.MatchesA, true), SyncPlan.Judge(this.folder, ledger, name, a, b));
            Assert.Equal(LocalState.MatchesB, SyncPlan.Judge(this.folder, ledger, name, b, a).State);
            Assert.Equal(LocalState.Differs, SyncPlan.Judge(this.folder, ledger, name, F(name, 9, "cc"), F(name, 8, "dd")).State);
            Assert.Equal(LocalState.Missing, SyncPlan.Judge(this.folder, ledger, "nope.zip", a, b).State);
        }

        [Fact]
        public void ApplyRenamesMovesAndWritesUndo()
        {
            var (oldName, md5) = this.Write("TP/Game (v1).zip", "hello");
            var (removedName, md5r) = this.Write("TP/Gone.zip", "bye");
            var ledger = VerifiedLedger.Load(this.folder);

            var rename = new SyncRow(new DiffEntry(ChangeKind.Renamed, F(oldName, 5, md5), F("TP/Game.zip", 5, md5)), true);
            var remove = new SyncRow(new DiffEntry(ChangeKind.Removed, F(removedName, 3, md5r), null), true);
            var leave = new SyncRow(new DiffEntry(ChangeKind.Removed, F("TP/Keep.zip", 3, "kk"), null), true);
            this.Write("TP/Keep.zip", "bye");
            var rows = new[] { rename, remove, leave };
            SyncPlan.ApplyClassification(rows, SyncPlan.Classify(this.folder, ledger, rows, CancellationToken.None));
            rename.IsChecked = remove.IsChecked = true;
            leave.IsChecked = false;

            string removed = Path.Combine(this.folder, "_removed", "stamp");
            var (moves, errors) = SyncPlan.Apply(this.folder, removed, ledger, new[] { rename, remove, leave }, null, CancellationToken.None);

            Assert.Empty(errors);
            Assert.Equal(2, moves.Count);
            Assert.True(System.IO.File.Exists(this.PathOf("TP/Game.zip")));
            Assert.False(System.IO.File.Exists(this.PathOf(oldName)));
            Assert.True(System.IO.File.Exists(Path.Combine(removed, "TP", "Gone.zip")));
            Assert.True(System.IO.File.Exists(this.PathOf("TP/Keep.zip")));
            Assert.True(System.IO.File.Exists(Path.Combine(removed, "undo.json")));
        }

        [Fact]
        public void LinkStoreSeedsFromFolderListAndSkipsUnchangedSnapshots()
        {
            System.IO.File.WriteAllText(
                Path.Combine(this.folder, "item_files.xml"),
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><files><file name=\"g.zip\" source=\"original\"><mtime>1</mtime><size>5</size><md5>aa</md5><sha1>ss</sha1></file></files>");

            var link = LinkStore.GetOrCreate("https://archive.org/details/item", "item", this.folder, Array.Empty<SessionRecord>());
            var first = Assert.Single(link.Snapshots);
            Assert.Equal("folder", first.Source);
            var loaded = LinkStore.LoadSnapshot(link, first);
            Assert.Equal("ss", Assert.Single(loaded).Sha1);

            Assert.Null(LinkStore.AddSnapshot(link, loaded, "sync", DateTime.Now));
            Assert.NotNull(LinkStore.AddSnapshot(link, new[] { F("g.zip", 6, "bb") }, "sync", DateTime.Now));
            Assert.Equal(2, link.Snapshots.Count);
        }

        private static SessionRecord Session(DateTime ended, params SessionFile[] files) => new()
        {
            Id = "test_" + Guid.NewGuid().ToString("N")[..6],
            Started = ended.AddMinutes(-5),
            Ended = ended,
            Options = new SyncOptions { Verify = VerifyMode.Md5 },
            Files = files.ToList()
        };

        private string PathOf(string name) => Path.Combine(this.folder, name.Replace('/', Path.DirectorySeparatorChar));

        private (string Name, string Md5) Write(string name, string content)
        {
            string path = this.PathOf(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return (name, Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant());
        }
    }
}
