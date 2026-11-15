namespace ArchiveOrgCollectionSync
{
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading;

    /// <summary>
    /// A read-only, seekable view of a remote file through HTTP range requests, read in 64 KB blocks. Used to read a
    /// zip's index on archive.org (a few KB) without downloading the file. Synchronous by design: callers run it on a
    /// worker thread.
    /// </summary>
    public sealed class HttpRangeStream : Stream
    {
        private const int Block = 64 * 1024;

        private readonly HttpClient client;
        private readonly CancellationToken ct;
        private Uri uri;
        private long position;
        private long bufferStart = -1;
        private byte[] buffer = Array.Empty<byte>();

        private HttpRangeStream(HttpClient client, Uri uri, long length, CancellationToken ct)
        {
            this.client = client;
            this.uri = uri;
            this.Length = length;
            this.ct = ct;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length { get; }

        public override long Position
        {
            get => this.position;
            set => this.position = value;
        }

        /// <summary>Gets the bytes fetched so far.</summary>
        public long Fetched { get; private set; }

        /// <summary>Opens <paramref name="url"/>, following archive.org's redirect to the storage node once.</summary>
        /// <exception cref="IOException">The server doesn't support range requests or the file is gone.</exception>
        public static HttpRangeStream Open(HttpClient client, string url, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.Length is not { } length)
            {
                throw new IOException($"range requests not available (HTTP {(int)response.StatusCode})");
            }

            return new HttpRangeStream(client, response.RequestMessage?.RequestUri ?? new Uri(url), length, ct) { Fetched = 1 };
        }

        public override int Read(byte[] destination, int offset, int count)
        {
            if (this.position >= this.Length || count == 0)
            {
                return 0;
            }

            if (this.bufferStart < 0 || this.position < this.bufferStart || this.position >= this.bufferStart + this.buffer.Length)
            {
                this.Fill(this.position, Math.Max(count, HttpRangeStream.Block));
            }

            int available = (int)(this.bufferStart + this.buffer.Length - this.position);
            int n = Math.Min(count, available);
            Buffer.BlockCopy(this.buffer, (int)(this.position - this.bufferStart), destination, offset, n);
            this.position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            this.position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => this.position + offset,
                _ => this.Length + offset
            };
            return this.position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Fill(long start, int count)
        {
            long end = Math.Min(this.Length, start + count) - 1;
            using var request = new HttpRequestMessage(HttpMethod.Get, this.uri);
            request.Headers.Range = new RangeHeaderValue(start, end);
            using var response = this.client.Send(request, HttpCompletionOption.ResponseHeadersRead, this.ct);

            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                throw new IOException($"range request failed (HTTP {(int)response.StatusCode})");
            }

            this.uri = response.RequestMessage?.RequestUri ?? this.uri;
            using var body = response.Content.ReadAsStream(this.ct);
            using var copy = new MemoryStream();
            body.CopyTo(copy);

            if (copy.Length != end - start + 1)
            {
                throw new IOException("short range response");
            }

            this.buffer = copy.ToArray();
            this.bufferStart = start;
            this.Fetched += this.buffer.Length;
        }
    }
}
