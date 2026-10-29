namespace ArchiveOrgCollectionSync
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Runtime.ExceptionServices;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;
    using System.Xml.Serialization;

    using Microsoft.Win32.SafeHandles;

    public enum VerifyMode
    {
        Md5,
        SizeOnly
    }

    public sealed class SyncOptions
    {
        public int FilesInParallel { get; set; } = 4;

        public int ConnectionsPerFile { get; set; } = 4;

        public VerifyMode Verify { get; set; } = VerifyMode.Md5;

        public bool OriginalsOnly { get; set; } = true;

        public bool DeleteExtraFiles { get; set; }
    }

    /// <summary>
    /// Downloads collection files with N files in parallel and, for large files, K ranged connections per file into a
    /// resumable <c>.part</c> file. Never touches the UI: progress lives in <see cref="FileItem"/> and <see cref="SyncLog"/>.
    /// </summary>
    public sealed class DownloadEngine
    {
        public const long SegmentThreshold = 32L * 1024 * 1024;

        private const int MaxAttempts = 4;

        private const int MaxConcurrentVerifies = 2;

        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

        private static readonly HttpClient Http = DownloadEngine.CreateClient();

        private readonly SyncOptions options;
        private readonly SyncLog log;
        private readonly VerifiedLedger? ledger;
        private readonly bool reverifyAll;
        private readonly SemaphoreSlim verifySlots = new(MaxConcurrentVerifies);
        private long bytesReceived;
        private int activeConnections;

        /// <param name="ledger">Files verified in earlier runs; they are skipped without hashing.</param>
        /// <param name="reverifyAll">Ignore the ledger and MD5-check every existing file (Verify All).</param>
        public DownloadEngine(SyncOptions options, SyncLog log, VerifiedLedger? ledger = null, bool reverifyAll = false)
        {
            this.options = options;
            this.log = log;
            this.ledger = ledger;
            this.reverifyAll = reverifyAll;
        }

        private bool UseMd5 => this.reverifyAll || this.options.Verify == VerifyMode.Md5;

        public long BytesReceived => Interlocked.Read(ref this.bytesReceived);

        public int ActiveConnections => Volatile.Read(ref this.activeConnections);

        public static async Task<File[]> FetchFileListAsync(string collection, CancellationToken ct)
        {
            string url = $"https://archive.org/download/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(collection)}_files.xml";
            string xml = await DownloadEngine.Http.GetStringAsync(url, ct);

            var serializer = new XmlSerializer(typeof(FileCollection));
            using var reader = new StringReader(xml);
            return ((FileCollection)(serializer.Deserialize(reader) ?? throw new InvalidDataException("The file list is empty."))).Files;
        }

        /// <summary>Deletes files under <paramref name="folder"/> that are not in the collection (in-progress .part files are kept).</summary>
        public (int Deleted, int Errors) DeleteExtraFiles(string folder, IEnumerable<File> collectionFiles)
        {
            var keep = new HashSet<string>(collectionFiles.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            int deleted = 0;
            int errors = 0;

            foreach (string filePath in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(folder, filePath).Replace('\\', '/');
                string original = relative.EndsWith(".part.json", StringComparison.OrdinalIgnoreCase) ? relative[..^10]
                    : relative.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? relative[..^5]
                    : relative;

                if (keep.Contains(original))
                {
                    continue;
                }

                try
                {
                    System.IO.File.Delete(filePath);
                    this.log.Write(LogLevel.Info, "Deleted because it does not exist in the Archive.org collection.", relative);
                    deleted++;
                }
                catch (Exception ex)
                {
                    this.log.Write(LogLevel.Error, $"Unable to delete file from disk. - {ex.Message}", relative);
                    errors++;
                }
            }

            return (deleted, errors);
        }

        public async Task RunAsync(string collection, string folder, IReadOnlyCollection<FileItem> items, CancellationToken ct)
        {
            var queue = Channel.CreateUnbounded<FileItem>();

            foreach (var item in items)
            {
                queue.Writer.TryWrite(item);
            }

            queue.Writer.Complete();

            var workers = Enumerable.Range(0, Math.Max(1, this.options.FilesInParallel)).Select(_ => Task.Run(
                async () =>
                {
                    while (!ct.IsCancellationRequested && queue.Reader.TryRead(out var item))
                    {
                        await this.ProcessAsync(collection, folder, item, ct);
                    }
                },
                CancellationToken.None));

            try
            {
                await Task.WhenAll(workers);
            }
            finally
            {
                this.ledger?.Save();
            }
        }

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 256,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(30),
                AutomaticDecompression = DecompressionMethods.None
            };

            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ArchiveOrgBulkDownloader/1.3 (+https://github.com/dsync89/archive-org-bulk-downloader)");
            return client;
        }

        private static string FileUrl(string collection, string name)
        {
            return $"https://archive.org/download/{Uri.EscapeDataString(collection)}/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}";
        }

        private static void EnsureSuccess(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            int code = (int)response.StatusCode;
            string message = $"HTTP {code} {response.ReasonPhrase}";

            if (code == 416)
            {
                throw new RangeNotSupportedException(message);
            }

            if (code is 408 or 429 or >= 500)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                throw new RetryableHttpException(message, retryAfter);
            }

            throw new PermanentDownloadException(message);
        }

        private async Task ProcessAsync(string collection, string folder, FileItem item, CancellationToken ct)
        {
            string destination = Path.GetFullPath(Path.Combine(folder, item.Name.Replace('/', Path.DirectorySeparatorChar)));
            string partPath = destination + ".part";
            bool isFileList = item.Name.Equals($"{collection}_files.xml", StringComparison.OrdinalIgnoreCase);

            try
            {
                if (!destination.StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PermanentDownloadException("File path escapes the destination folder.");
                }

                item.SetState(FileStatus.Checking);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException());

                if (System.IO.File.Exists(destination))
                {
                    if (isFileList)
                    {
                        item.SetBytes(item.Size);
                        item.SetState(FileStatus.Skipped, "skipped (exists)");
                        return;
                    }

                    if (!this.reverifyAll && this.ledger?.IsVerified(item, destination, this.UseMd5) == true)
                    {
                        item.SetBytes(item.Size);
                        item.SetState(FileStatus.Skipped, "skipped (verified earlier)");
                        this.log.Write(LogLevel.Debug, "Verified in an earlier run and unchanged. Skipping without re-checking.", item.Name);
                        return;
                    }

                    if (await this.MatchesAsync(destination, item, ct))
                    {
                        bool hashed = this.UseMd5 && item.Md5.Length > 0;
                        this.ledger?.Record(item, destination, hashed);
                        item.SetBytes(item.Size);
                        item.SetState(FileStatus.Skipped, hashed ? "skipped (md5 ok)" : "skipped (size ok)");
                        this.log.Write(LogLevel.Debug, hashed ? "Already exists with the correct checksum. Skipping." : "Already exists with the correct size. Skipping.", item.Name);
                        return;
                    }

                    this.ledger?.Remove(item.Name);
                    this.log.Write(LogLevel.Warn, "Existing file has an incorrect size or checksum. Re-downloading.", item.Name);
                }

                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        item.SetState(FileStatus.Downloading);
                        await this.DownloadAsync(collection, item, partPath, ct);
                        break;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is not PermanentDownloadException && attempt < MaxAttempts)
                    {
                        if (ex is RangeNotSupportedException)
                        {
                            item.ForceSingleStream = true;
                        }

                        var delay = (ex as RetryableHttpException)?.RetryAfter ?? TimeSpan.FromSeconds(2 << (attempt - 1));
                        delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delay;
                        item.SetState(FileStatus.Downloading, $"retry {attempt}/{MaxAttempts - 1} in {delay.TotalSeconds:0}s");
                        this.log.Write(LogLevel.Warn, $"Attempt {attempt} failed: {ex.Message} Retrying in {delay.TotalSeconds:0}s.", item.Name);
                        await Task.Delay(delay, ct);
                    }
                }

                string verified = "done";

                if (!isFileList && this.UseMd5 && item.Md5.Length > 0)
                {
                    item.SetConnections(0);
                    item.SetState(FileStatus.Verifying);

                    if (!await this.Md5MatchesAsync(partPath, item.Md5, ct))
                    {
                        System.IO.File.Delete(partPath);
                        System.IO.File.Delete(partPath + ".json");
                        item.SetBytes(0);
                        item.SetState(FileStatus.Failed, "checksum mismatch");
                        this.log.Write(LogLevel.Error, "Checksum does not match after downloading! The partial file was discarded.", item.Name);
                        return;
                    }

                    verified = "done (md5 ok)";
                }

                System.IO.File.Move(partPath, destination, true);
                System.IO.File.Delete(partPath + ".json");

                if (!isFileList)
                {
                    this.ledger?.Record(item, destination, verified.Contains("md5"));
                }

                item.SetState(FileStatus.Done, verified);
                this.log.Write(LogLevel.Success, $"Downloaded successfully ({Format.Bytes(item.BytesDone)}){(verified.Contains("md5") ? ", checksum confirmed" : string.Empty)}.", item.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                item.SetState(FileStatus.Queued, item.BytesDone > 0 ? "paused" : null);
            }
            catch (Exception ex)
            {
                item.SetState(FileStatus.Failed, $"failed: {ex.Message}");
                this.log.Write(LogLevel.Error, $"An error occurred downloading the file. - {ex.Message}", item.Name);
            }
            finally
            {
                item.SetConnections(0);
            }
        }

        private async Task<bool> MatchesAsync(string path, FileItem item, CancellationToken ct)
        {
            if (item.HasSize && new FileInfo(path).Length != item.Size)
            {
                return false;
            }

            if (!this.UseMd5 || item.Md5.Length == 0)
            {
                return true;
            }

            return await this.Md5MatchesAsync(path, item.Md5, ct);
        }

        private async Task<bool> Md5MatchesAsync(string path, string md5Hash, CancellationToken ct)
        {
            await this.verifySlots.WaitAsync(ct);

            try
            {
                using var md5 = MD5.Create();
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
                return string.Equals(Convert.ToHexString(await md5.ComputeHashAsync(stream, ct)), md5Hash, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                this.verifySlots.Release();
            }
        }

        private async Task DownloadAsync(string collection, FileItem item, string partPath, CancellationToken ct)
        {
            if (!item.HasSize)
            {
                // No size in the file list (e.g. <collection>_files.xml itself): plain single-stream download.
                await this.DownloadUnknownSizeAsync(DownloadEngine.FileUrl(collection, item.Name), partPath, item, ct);
                return;
            }

            string statePath = partPath + ".json";
            var state = PartState.Load(statePath);

            if (state == null
                || state.Size != item.Size
                || state.Md5 != item.Md5
                || !System.IO.File.Exists(partPath)
                || (item.ForceSingleStream && (state.Segments.Count > 1 || state.Done > 0)))
            {
                int segments = item.ForceSingleStream || item.Size < SegmentThreshold ? 1 : Math.Max(1, this.options.ConnectionsPerFile);
                state = PartState.Create(item.Size, item.Md5, segments);
                System.IO.File.Delete(partPath);
            }
            else if (state.Done > 0)
            {
                this.log.Write(LogLevel.Info, $"Resuming at {Format.Bytes(state.Done)} of {Format.Bytes(item.Size)}.", item.Name);
            }

            item.SetBytes(state.Done);

            await using var stream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);

            if (stream.Length != item.Size)
            {
                stream.SetLength(item.Size);
            }

            var pending = state.Segments.Where(s => !s.IsComplete).ToList();

            if (pending.Count == 0)
            {
                return;
            }

            var (url, remoteSize) = await DownloadEngine.ResolveAsync(DownloadEngine.FileUrl(collection, item.Name), ct);

            if (remoteSize is { } size && size != item.Size)
            {
                throw new PermanentDownloadException(
                    $"File changed on archive.org since the scan (now {Format.Bytes(size)}, list says {Format.Bytes(item.Size)}). Retry Failed refreshes the file list.");
            }

            item.SetConnections(pending.Count);
            item.SetState(FileStatus.Downloading);

            using var segmentsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Exception? failure = null;

            async Task RunSegment(PartSegment segment)
            {
                try
                {
                    await this.DownloadSegmentAsync(url, stream.SafeFileHandle, segment, item, segmentsCts.Token);
                }
                catch (OperationCanceledException) when (segmentsCts.IsCancellationRequested)
                {
                    // Cancelled by the user or because a sibling segment failed.
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    segmentsCts.Cancel();
                }
            }

            var saver = DownloadEngine.SaveStatePeriodicallyAsync(state, statePath, segmentsCts.Token);

            try
            {
                await Task.WhenAll(pending.Select(RunSegment));
            }
            finally
            {
                segmentsCts.Cancel();
                await saver;
                state.Save(statePath);
            }

            ct.ThrowIfCancellationRequested();

            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private async Task DownloadUnknownSizeAsync(string url, string partPath, FileItem item, CancellationToken ct)
        {
            item.SetBytes(0);
            item.SetConnections(1);
            Interlocked.Increment(ref this.activeConnections);

            try
            {
                using var response = await DownloadEngine.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                DownloadEngine.EnsureSuccess(response);
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true);
                byte[] buffer = new byte[1 << 16];
                int read;

                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    item.AddBytes(read);
                    Interlocked.Add(ref this.bytesReceived, read);
                }
            }
            finally
            {
                Interlocked.Decrement(ref this.activeConnections);
            }
        }

        /// <returns>The data-node URL and the file's current size on archive.org, when the server reports it.</returns>
        private static async Task<(string Url, long? Size)> ResolveAsync(string url, CancellationToken ct)
        {
            // Follow the archive.org -> iaNNNNNN.us.archive.org redirect once, so segments go straight to the data node.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await DownloadEngine.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            DownloadEngine.EnsureSuccess(response);
            return (response.RequestMessage?.RequestUri?.AbsoluteUri ?? url, response.Content.Headers.ContentRange?.Length);
        }

        private static async Task SaveStatePeriodicallyAsync(PartState state, string path, CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    state.Save(path);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task DownloadSegmentAsync(string url, SafeFileHandle handle, PartSegment segment, FileItem item, CancellationToken ct)
        {
            long position = segment.Start + Interlocked.Read(ref segment.Done);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            Interlocked.Increment(ref this.activeConnections);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(position, segment.End);
                using var response = await DownloadEngine.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
                DownloadEngine.EnsureSuccess(response);

                if (response.StatusCode != HttpStatusCode.PartialContent && (position != 0 || segment.End != item.Size - 1))
                {
                    throw new RangeNotSupportedException("The server ignored the Range request.");
                }

                await using var body = await response.Content.ReadAsStreamAsync(idle.Token);

                while (position <= segment.End)
                {
                    idle.CancelAfter(IdleTimeout);
                    int read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, segment.End - position + 1)), idle.Token);

                    if (read == 0)
                    {
                        throw new IOException("The connection closed before the download completed.");
                    }

                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, ct);
                    position += read;
                    Interlocked.Add(ref segment.Done, read);
                    item.AddBytes(read);
                    Interlocked.Add(ref this.bytesReceived, read);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No data received for {IdleTimeout.TotalSeconds:0} seconds.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                Interlocked.Decrement(ref this.activeConnections);
            }
        }

        private sealed class PartState
        {
            private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

            public long Size { get; set; }

            public string Md5 { get; set; } = string.Empty;

            public List<PartSegment> Segments { get; set; } = new();

            public long Done => this.Segments.Sum(s => Interlocked.Read(ref s.Done));

            public static PartState Create(long size, string md5, int segments)
            {
                var state = new PartState { Size = size, Md5 = md5 };
                long chunk = (size + segments - 1) / Math.Max(1, segments);

                for (long start = 0; start < size; start += chunk)
                {
                    state.Segments.Add(new PartSegment { Start = start, End = Math.Min(size, start + chunk) - 1 });
                }

                return state;
            }

            public static PartState? Load(string path)
            {
                try
                {
                    return System.IO.File.Exists(path) ? JsonSerializer.Deserialize<PartState>(System.IO.File.ReadAllText(path), JsonOptions) : null;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    return null;
                }
            }

            public void Save(string path)
            {
                lock (this)
                {
                    try
                    {
                        string temp = path + ".tmp";
                        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
                        System.IO.File.Move(temp, path, true);
                    }
                    catch (IOException)
                    {
                        // Losing a resume checkpoint only costs re-downloading part of a segment.
                    }
                }
            }
        }

        private sealed class PartSegment
        {
            public long Start;

            public long End;

            public long Done;

            public bool IsComplete => this.Start + Interlocked.Read(ref this.Done) > this.End;
        }
    }

    public class RetryableHttpException : Exception
    {
        public RetryableHttpException(string message, TimeSpan? retryAfter)
            : base(message)
        {
            this.RetryAfter = retryAfter;
        }

        public TimeSpan? RetryAfter { get; }
    }

    public class PermanentDownloadException : Exception
    {
        public PermanentDownloadException(string message)
            : base(message)
        {
        }
    }

    public class RangeNotSupportedException : Exception
    {
        public RangeNotSupportedException(string message)
            : base(message)
        {
        }
    }
}
