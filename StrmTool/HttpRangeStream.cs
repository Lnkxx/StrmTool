using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace StrmTool
{
    /// <summary>
    /// Presents an HTTP resource with byte-range support as a seekable read-only stream.
    /// </summary>
    internal sealed class HttpRangeStream : Stream
    {
        private static long _resumeAtTicks;
        private const int DefaultChunkSize = 1024 * 1024;
        private const int DefaultMaxCachedChunks = 32;

        private readonly HttpClient _httpClient;
        private readonly Uri _sourceUri;
        private readonly CancellationToken _cancellationToken;
        private readonly int _chunkSize;
        private readonly int _maxCachedChunks;
        private readonly Dictionary<long, CacheEntry> _cache = new Dictionary<long, CacheEntry>();
        private readonly LinkedList<long> _lru = new LinkedList<long>();
        private long _position;
        private bool _disposed;

        private HttpRangeStream(
            HttpClient httpClient,
            Uri sourceUri,
            long length,
            string entityTag,
            DateTimeOffset? lastModified,
            CancellationToken cancellationToken,
            int chunkSize,
            int maxCachedChunks)
        {
            _httpClient = httpClient;
            _sourceUri = sourceUri;
            Length = length;
            EntityTag = entityTag;
            LastModified = lastModified;
            _cancellationToken = cancellationToken;
            _chunkSize = chunkSize;
            _maxCachedChunks = maxCachedChunks;
        }

        public string EntityTag { get; }

        public DateTimeOffset? LastModified { get; }

        public long BytesDownloaded { get; private set; }

        public int RequestCount { get; private set; }

        public override bool CanRead => !_disposed;

        public override bool CanSeek => !_disposed;

        public override bool CanWrite => false;

        public override long Length { get; }

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public static async Task<HttpRangeStream> OpenAsync(
            string sourceUrl,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            int chunkSize = DefaultChunkSize,
            int maxCachedChunks = DefaultMaxCachedChunks)
        {
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri) ||
                (sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("A valid HTTP(S) URL is required.", nameof(sourceUrl));
            }

            if (chunkSize < 64 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }

            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _resumeAtTicks))
                throw new ProbeFailureException(ProbeFailureKind.RateLimited);

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.None
            };
            var client = new HttpClient(handler, true)
            {
                Timeout = timeout
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StrmTool/2.4 RemoteIsoProbe");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw HttpFailure(response);
                }

                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.Length is not long length || length <= 0 || contentRange.From != 0 || contentRange.To != 0)
                {
                    throw new InvalidOperationException("Remote ISO range response did not include its total length.");
                }

                var probeByte = await ReadBoundedAsync(response, 1, cancellationToken).ConfigureAwait(false);
                if (probeByte.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"Remote ISO returned {probeByte.Length} bytes for a one-byte range probe.");
                }

                if (response.Headers.ETag == null || response.Headers.ETag.IsWeak)
                    throw new ProbeFailureException(ProbeFailureKind.SourceChanged);

                var result = new HttpRangeStream(
                    client,
                    sourceUri,
                    length,
                    response.Headers.ETag?.ToString(),
                    response.Content.Headers.LastModified,
                    cancellationToken,
                    chunkSize,
                    Math.Max(1, maxCachedChunks));
                result.BytesDownloaded = 1;
                result.RequestCount = 1;
                return result;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException();
            }

            ThrowIfDisposed();
            _cancellationToken.ThrowIfCancellationRequested();
            if (count == 0 || _position >= Length)
            {
                return 0;
            }

            var totalRead = 0;
            var remaining = (int)Math.Min(count, Length - _position);
            while (remaining > 0)
            {
                var chunkIndex = _position / _chunkSize;
                var chunkOffset = (int)(_position % _chunkSize);
                var chunk = GetChunk(chunkIndex);
                var copyLength = Math.Min(remaining, chunk.Length - chunkOffset);
                if (copyLength <= 0)
                {
                    break;
                }

                Buffer.BlockCopy(chunk, chunkOffset, buffer, offset + totalRead, copyLength);
                _position += copyLength;
                totalRead += copyLength;
                remaining -= copyLength;
            }

            return totalRead;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            var rented = new byte[buffer.Length];
            var read = Read(rented, 0, rented.Length);
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (next < 0)
            {
                throw new IOException("Cannot seek before the beginning of the remote ISO.");
            }

            _position = next;
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _httpClient.Dispose();
                _cache.Clear();
                _lru.Clear();
            }

            base.Dispose(disposing);
        }

        private byte[] GetChunk(long chunkIndex)
        {
            if (_cache.TryGetValue(chunkIndex, out var cached))
            {
                _lru.Remove(cached.Node);
                _lru.AddFirst(cached.Node);
                return cached.Data;
            }

            var start = checked(chunkIndex * (long)_chunkSize);
            var end = Math.Min(Length - 1, start + _chunkSize - 1);
            using var request = new HttpRequestMessage(HttpMethod.Get, _sourceUri);
            request.Headers.Range = new RangeHeaderValue(start, end);
            if (EntityTag != null && !EntityTag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(EntityTag));
            else if (LastModified.HasValue) request.Headers.IfRange = new RangeConditionHeaderValue(LastModified.Value);
            using var response = _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    _cancellationToken)
                .GetAwaiter().GetResult();

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw HttpFailure(response);
            }

            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange?.From != start || contentRange.To != end || contentRange.Length != Length)
            {
                throw new IOException(
                    $"Remote ISO returned an unexpected Content-Range for {start}-{end}.");
            }

            if (EntityTag != null && EntityTag != response.Headers.ETag?.ToString())
                throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
            if (LastModified.HasValue && LastModified != response.Content.Headers.LastModified)
                throw new ProbeFailureException(ProbeFailureKind.SourceChanged);
            var expected = checked((int)(end - start + 1));
            var data = ReadBoundedAsync(response, expected, _cancellationToken).GetAwaiter().GetResult();
            if (data.Length != expected)
            {
                throw new EndOfStreamException(
                    $"Remote ISO range {start}-{end} returned {data.Length} of {expected} bytes.");
            }

            RequestCount++;
            BytesDownloaded += data.Length;
            var node = _lru.AddFirst(chunkIndex);
            _cache.Add(chunkIndex, new CacheEntry(data, node));
            while (_cache.Count > _maxCachedChunks)
            {
                var last = _lru.Last;
                if (last == null)
                {
                    break;
                }

                _lru.RemoveLast();
                _cache.Remove(last.Value);
            }

            return data;
        }

        private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int expected, CancellationToken token)
        {
            using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[checked(expected + 1)];
            int received = 0;
            while (received < buffer.Length)
            {
                int read = await body.ReadAsync(buffer.AsMemory(received), token).ConfigureAwait(false);
                if (read == 0) break;
                received += read;
            }
            if (received != expected) throw new IOException("Range response body length is inconsistent");
            Array.Resize(ref buffer, expected);
            return buffer;
        }

        private static Exception HttpFailure(HttpResponseMessage response)
        {
            var status = response.StatusCode;
            var kind = status == HttpStatusCode.Forbidden || status == HttpStatusCode.Unauthorized ? ProbeFailureKind.AccessDenied
                : (int)status == 429 ? ProbeFailureKind.RateLimited
                : (int)status >= 500 ? ProbeFailureKind.TransientNetwork : ProbeFailureKind.InvalidMedia;
            if (kind == ProbeFailureKind.RateLimited || kind == ProbeFailureKind.AccessDenied)
            {
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromMinutes(kind == ProbeFailureKind.RateLimited ? 15 : 5);
                delay = delay < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
                // One ISO queue: pause it instead of repeatedly hitting the same provider.
                Interlocked.Exchange(ref _resumeAtTicks, DateTime.UtcNow.Add(delay).Ticks);
            }
            return new ProbeFailureException(kind);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        private sealed class CacheEntry
        {
            public CacheEntry(byte[] data, LinkedListNode<long> node)
            {
                Data = data;
                Node = node;
            }

            public byte[] Data { get; }

            public LinkedListNode<long> Node { get; }
        }
    }
}
