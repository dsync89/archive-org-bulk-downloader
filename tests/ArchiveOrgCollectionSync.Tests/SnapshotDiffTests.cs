namespace ArchiveOrgCollectionSync.Tests
{
    using System.Linq;
    using Xunit;

    public class SnapshotDiffTests
    {
        internal static File F(string name, long size, string md5, string sha1 = "", string mtime = "", string source = "original", bool old = false)
            => new() { Name = name, Size = size, SizeSpecified = true, Md5 = md5, Sha1 = sha1, Mtime = mtime, Source = source, OldVersion = old };

        [Fact]
        public void IdenticalListsHaveNoChanges()
        {
            var a = new[] { F("x.zip", 10, "aa"), F("d/y.zip", 20, "bb") };
            Assert.Empty(SnapshotDiff.Compare(a, a.Select(f => F(f.Name, f.Size, f.Md5)).ToArray()));
        }

        [Fact]
        public void AddedAndRemoved()
        {
            var diff = SnapshotDiff.Compare(new[] { F("old.zip", 1, "aa") }, new[] { F("new.zip", 2, "bb") });
            Assert.Equal(2, diff.Count);
            Assert.Contains(diff, e => e.Kind == ChangeKind.Removed && e.Name == "old.zip" && e.B == null);
            Assert.Contains(diff, e => e.Kind == ChangeKind.Added && e.Name == "new.zip" && e.A == null);
        }

        [Fact]
        public void SameContentNewNameIsRenamed()
        {
            var diff = SnapshotDiff.Compare(new[] { F("TP/Game (v1).zip", 100, "aa") }, new[] { F("TP/Game.zip", 100, "aa") });
            var e = Assert.Single(diff);
            Assert.Equal(ChangeKind.Renamed, e.Kind);
            Assert.Equal("TP/Game (v1).zip", e.OldName);
            Assert.Equal("TP/Game.zip", e.Name);
        }

        [Fact]
        public void SameFileNameOtherFolderIsMoved()
        {
            var e = Assert.Single(SnapshotDiff.Compare(new[] { F("A/Game.zip", 100, "aa") }, new[] { F("B/Game.zip", 100, "aa") }));
            Assert.Equal(ChangeKind.Moved, e.Kind);
        }

        [Fact]
        public void SameNameDifferentChecksumIsChanged()
        {
            var e = Assert.Single(SnapshotDiff.Compare(new[] { F("g.zip", 100, "aa") }, new[] { F("g.zip", 100, "bb") }));
            Assert.Equal(ChangeKind.Changed, e.Kind);
        }

        [Fact]
        public void SameNameDifferentSizeIsChanged()
        {
            var e = Assert.Single(SnapshotDiff.Compare(new[] { F("g.zip", 100, "") }, new[] { F("g.zip", 101, "") }));
            Assert.Equal(ChangeKind.Changed, e.Kind);
        }

        [Fact]
        public void OnlyUploadTimeMovedIsTouched()
        {
            var e = Assert.Single(SnapshotDiff.Compare(new[] { F("g.zip", 100, "aa", mtime: "1") }, new[] { F("g.zip", 100, "aa", mtime: "2") }));
            Assert.Equal(ChangeKind.Touched, e.Kind);
        }

        [Fact]
        public void RenamedAndChangedAtOnceIsRemovedPlusAdded()
        {
            var diff = SnapshotDiff.Compare(new[] { F("a.zip", 100, "aa") }, new[] { F("b.zip", 120, "bb") });
            Assert.Equal(new[] { ChangeKind.Removed, ChangeKind.Added }.OrderBy(k => k), diff.Select(d => d.Kind).OrderBy(k => k));
        }

        [Fact]
        public void SnapshotWithoutSha1OrMtimeDoesNotLookChanged()
        {
            // Snapshot #0 rebuilt from an old session has MD5 only; archive.org now also lists SHA-1 and mtime.
            var diff = SnapshotDiff.Compare(new[] { F("g.zip", 100, "AA") }, new[] { F("g.zip", 100, "aa", sha1: "ff", mtime: "5") });
            Assert.Empty(diff);
        }

        [Fact]
        public void DuplicateContentPairsDeterministically()
        {
            var a = new[] { F("one.zip", 5, "aa"), F("two.zip", 5, "aa") };
            var b = new[] { F("uno.zip", 5, "aa"), F("dos.zip", 5, "aa") };
            var diff = SnapshotDiff.Compare(a, b);
            Assert.Equal(2, diff.Count);
            Assert.All(diff, e => Assert.Equal(ChangeKind.Renamed, e.Kind));
            Assert.Equal(diff.Select(d => d.OldName).OrderBy(n => n), new[] { "one.zip", "two.zip" });
        }

        [Fact]
        public void MovePreferredOverRenameWhenBothFit()
        {
            var a = new[] { F("A/Game.zip", 5, "aa") };
            var b = new[] { F("B/Other.zip", 5, "aa"), F("B/Game.zip", 5, "aa") };
            var diff = SnapshotDiff.Compare(a, b);
            Assert.Contains(diff, e => e.Kind == ChangeKind.Moved && e.Name == "B/Game.zip");
            Assert.Contains(diff, e => e.Kind == ChangeKind.Added && e.Name == "B/Other.zip");
        }

        [Fact]
        public void NamesCompareIgnoringCase()
        {
            Assert.Empty(SnapshotDiff.Compare(new[] { F("Game.ZIP", 5, "aa") }, new[] { F("game.zip", 5, "aa") }));
        }

        [Theory]
        [InlineData("history/files/g.zip.~1~", "original", false, true, false)]
        [InlineData("g.zip", "original", true, true, false)]
        [InlineData("item_files.xml", "metadata", false, false, false)]
        [InlineData("item_meta.xml", "metadata", false, true, false)]
        [InlineData("item_meta.xml", "metadata", false, false, true)]
        [InlineData("g.zip", "original", false, true, true)]
        [InlineData("thumb.jpg", "derivative", false, true, false)]
        public void IsTracked(string name, string source, bool old, bool originalsOnly, bool expected)
        {
            Assert.Equal(expected, SnapshotDiff.IsTracked(F(name, 1, "aa", source: source, old: old), "item", originalsOnly));
        }

        [Fact]
        public void FolderOfName()
        {
            Assert.Equal("TeknoParrot Collection/", SnapshotDiff.Folder("TeknoParrot Collection/g.zip"));
            Assert.Equal(string.Empty, SnapshotDiff.Folder("g.zip"));
        }
    }
}
