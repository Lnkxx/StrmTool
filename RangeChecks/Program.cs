using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using StrmTool;

internal static class Program
{
    private const int ChunkSize = 64 * 1024;
    private static readonly Type RangeStreamType = typeof(ProbeFailureException).Assembly.GetType("StrmTool.HttpRangeStream", throwOnError: true)!;
    private static readonly MethodInfo OpenMethod = RangeStreamType.GetMethod("OpenAsync", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly FieldInfo ResumeAtTicks = RangeStreamType.GetField("_resumeAtTicks", BindingFlags.NonPublic | BindingFlags.Static)!;

    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("strong ETag is stable and sent in If-Range", StableEntityTagAsync),
            ("missing initial ETag is rejected", MissingInitialEntityTagAsync),
            ("weak initial ETag is rejected", WeakInitialEntityTagAsync),
            ("missing later ETag is rejected", MissingLaterEntityTagAsync),
            ("changed later ETag is rejected", ChangedLaterEntityTagAsync),
            ("changed Content-Range total length is rejected", ChangedTotalLengthAsync),
            ("HTTP 429 is classified and activates cooldown", RateLimitedCooldownAsync),
            ("HTTP 403 is classified and activates cooldown", AccessDeniedCooldownAsync),
            ("opening can be canceled", CancellationAsync)
        };

        foreach (var test in tests)
        {
            ResetCooldown();
            await test.Run().ConfigureAwait(false);
            Console.WriteLine($"PASS {test.Name}");
        }

        ResetCooldown();
        Console.WriteLine($"RangeChecks passed ({tests.Length} cases).");
        return 0;
    }

    private static async Task StableEntityTagAsync()
    {
        var payload = MakePayload(ChunkSize + 23);
        await using var server = new LoopbackServer((request, _) => Task.FromResult<FakeResponse?>(
            PartialResponse(payload, request, "\"stable-v1\"")));

        using var stream = await OpenAsync(server.Url).ConfigureAwait(false);
        Equal(payload.LongLength, stream.Length, "reported length");
        stream.Seek(ChunkSize, SeekOrigin.Begin);
        Equal((int)payload[ChunkSize], stream.ReadByte(), "range byte");

        var requests = server.Requests.ToArray();
        Equal(2, requests.Length, "request count");
        Equal("bytes=0-0", Header(requests[0], "Range"), "initial range");
        Equal($"bytes={ChunkSize}-{payload.Length - 1}", Header(requests[1], "Range"), "second range");
        Equal("\"stable-v1\"", Header(requests[1], "If-Range"), "If-Range validator");
    }

    private static async Task MissingInitialEntityTagAsync()
    {
        var payload = MakePayload(ChunkSize + 1);
        await using var server = new LoopbackServer((request, _) => Task.FromResult<FakeResponse?>(
            PartialResponse(payload, request, entityTag: null)));

        var failure = await ThrowsAsync<ProbeFailureException>(() => OpenAsync(server.Url)).ConfigureAwait(false);
        Equal(ProbeFailureKind.SourceChanged, failure.Kind, "missing initial ETag classification");
        Equal(1, server.Requests.Count, "request count");
    }

    private static async Task WeakInitialEntityTagAsync()
    {
        var payload = MakePayload(ChunkSize + 1);
        await using var server = new LoopbackServer((request, _) => Task.FromResult<FakeResponse?>(
            PartialResponse(payload, request, "W/\"weak-v1\"")));

        var failure = await ThrowsAsync<ProbeFailureException>(() => OpenAsync(server.Url)).ConfigureAwait(false);
        Equal(ProbeFailureKind.SourceChanged, failure.Kind, "weak initial ETag classification");
        Equal(1, server.Requests.Count, "request count");
    }

    private static async Task MissingLaterEntityTagAsync()
    {
        await CheckLaterEntityTagAsync(entityTag: null).ConfigureAwait(false);
    }

    private static async Task ChangedLaterEntityTagAsync()
    {
        await CheckLaterEntityTagAsync("\"stable-v2\"").ConfigureAwait(false);
    }

    private static async Task CheckLaterEntityTagAsync(string? entityTag)
    {
        var payload = MakePayload(ChunkSize + 1);
        var requestNumber = 0;
        await using var server = new LoopbackServer((request, _) =>
        {
            var validator = Interlocked.Increment(ref requestNumber) == 1 ? "\"stable-v1\"" : entityTag;
            return Task.FromResult<FakeResponse?>(PartialResponse(payload, request, validator));
        });

        using var stream = await OpenAsync(server.Url).ConfigureAwait(false);
        stream.Seek(ChunkSize, SeekOrigin.Begin);
        var failure = await ThrowsAsync<ProbeFailureException>(
            () => Task.Run(() => stream.ReadByte())).ConfigureAwait(false);
        Equal(ProbeFailureKind.SourceChanged, failure.Kind, "later ETag classification");
        Equal(2, server.Requests.Count, "request count");
        Equal("\"stable-v1\"", Header(server.Requests.Last(), "If-Range"), "If-Range validator");
    }

    private static async Task ChangedTotalLengthAsync()
    {
        var payload = MakePayload(ChunkSize + 1);
        var requestNumber = 0;
        await using var server = new LoopbackServer((request, _) =>
        {
            var number = Interlocked.Increment(ref requestNumber);
            var response = PartialResponse(payload, request, "\"stable-v1\"");
            return Task.FromResult<FakeResponse?>(number == 1
                ? response
                : response with { ContentRangeTotal = payload.Length + 1L });
        });

        using var stream = await OpenAsync(server.Url).ConfigureAwait(false);
        stream.Seek(ChunkSize, SeekOrigin.Begin);
        await ThrowsAsync<IOException>(() => Task.Run(() => stream.ReadByte())).ConfigureAwait(false);
        Equal(2, server.Requests.Count, "request count");
    }

    private static async Task RateLimitedCooldownAsync()
    {
        await CheckHttpCooldownAsync(429, ProbeFailureKind.RateLimited).ConfigureAwait(false);
    }

    private static async Task AccessDeniedCooldownAsync()
    {
        await CheckHttpCooldownAsync(403, ProbeFailureKind.AccessDenied).ConfigureAwait(false);
    }

    private static async Task CheckHttpCooldownAsync(int statusCode, ProbeFailureKind expectedKind)
    {
        await using var server = new LoopbackServer((_, _) => Task.FromResult<FakeResponse?>(
            new FakeResponse(statusCode, Array.Empty<byte>(), RetryAfter: "1")));

        var failure = await ThrowsAsync<ProbeFailureException>(() => OpenAsync(server.Url)).ConfigureAwait(false);
        Equal(expectedKind, failure.Kind, $"HTTP {statusCode} classification");
        Assert(Convert.ToInt64(ResumeAtTicks.GetValue(null)) > DateTime.UtcNow.Ticks, "cooldown timestamp must be in the future");

        var blocked = await ThrowsAsync<ProbeFailureException>(() => OpenAsync(server.Url)).ConfigureAwait(false);
        Equal(ProbeFailureKind.RateLimited, blocked.Kind, "cooldown rejection classification");
        Equal(1, server.Requests.Count, "cooldown blocks the next HTTP request");
    }

    private static async Task CancellationAsync()
    {
        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackServer(async (_, serverToken) =>
        {
            requestSeen.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, serverToken).ConfigureAwait(false);
            return null;
        });

        using var cancellation = new CancellationTokenSource();
        var opening = OpenAsync(server.Url, cancellation.Token);
        await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => opening).ConfigureAwait(false);
    }

    private static async Task<Stream> OpenAsync(string url, CancellationToken cancellationToken = default)
    {
        var taskObject = OpenMethod.Invoke(null, new object?[]
        {
            url,
            TimeSpan.FromSeconds(5),
            cancellationToken,
            ChunkSize,
            4
        }) ?? throw new InvalidOperationException("OpenAsync returned no task.");

        if (taskObject is not Task task)
        {
            throw new InvalidOperationException("OpenAsync did not return a Task.");
        }

        await task.ConfigureAwait(false);
        return (Stream)(task.GetType().GetProperty("Result")?.GetValue(task)
            ?? throw new InvalidOperationException("OpenAsync returned no stream."));
    }

    private static FakeResponse PartialResponse(byte[] payload, HttpRequestRecord request, string? entityTag)
    {
        var range = Header(request, "Range");
        var requested = range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
            ? range[6..].Split('-', 2)
            : throw new InvalidOperationException($"Unexpected Range header: {range}");
        var start = long.Parse(requested[0], System.Globalization.CultureInfo.InvariantCulture);
        var end = long.Parse(requested[1], System.Globalization.CultureInfo.InvariantCulture);
        if (start < 0 || end < start || end >= payload.LongLength)
        {
            throw new InvalidOperationException($"Out-of-range request {start}-{end} for {payload.LongLength} bytes.");
        }

        var body = payload.AsSpan(checked((int)start), checked((int)(end - start + 1))).ToArray();
        return new FakeResponse(206, body, start, end, payload.LongLength, entityTag);
    }

    private static byte[] MakePayload(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        return payload;
    }

    private static string Header(HttpRequestRecord request, string name)
    {
        return request.Headers.TryGetValue(name, out var value) ? value : string.Empty;
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void ResetCooldown() => ResumeAtTicks.SetValue(null, 0L);

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record HttpRequestRecord(string Method, string Target, Dictionary<string, string> Headers);

    private sealed record FakeResponse(
        int StatusCode,
        byte[] Body,
        long? ContentRangeFrom = null,
        long? ContentRangeTo = null,
        long? ContentRangeTotal = null,
        string? EntityTag = null,
        string? RetryAfter = null);

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<HttpRequestRecord, CancellationToken, Task<FakeResponse?>> _handler;
        private readonly ConcurrentBag<Task> _handlers = new();
        private readonly Task _acceptLoop;

        public LoopbackServer(Func<HttpRequestRecord, CancellationToken, Task<FakeResponse?>> handler)
        {
            _handler = handler;
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            Url = $"http://127.0.0.1:{endpoint.Port}/iso";
            _acceptLoop = AcceptLoopAsync();
        }

        public string Url { get; }

        public ConcurrentQueue<HttpRequestRecord> Requests { get; } = new();

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    _handlers.Add(HandleClientAsync(client));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(
                        stream,
                        Encoding.ASCII,
                        detectEncodingFromByteOrderMarks: false,
                        bufferSize: 1024,
                        leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(requestLine))
                    {
                        return;
                    }

                    var requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    if (requestParts.Length < 2)
                    {
                        return;
                    }

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    while (true)
                    {
                        var line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(line))
                        {
                            break;
                        }

                        var separator = line.IndexOf(':');
                        if (separator > 0)
                        {
                            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                        }
                    }

                    var request = new HttpRequestRecord(requestParts[0], requestParts[1], headers);
                    Requests.Enqueue(request);
                    var response = await _handler(request, _stop.Token).ConfigureAwait(false);
                    if (response is not null)
                    {
                        await SendResponseAsync(stream, response, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
            }
        }

        private static async Task SendResponseAsync(NetworkStream stream, FakeResponse response, CancellationToken cancellationToken)
        {
            var reason = response.StatusCode switch
            {
                206 => "Partial Content",
                403 => "Forbidden",
                429 => "Too Many Requests",
                _ => "Response"
            };
            var builder = new StringBuilder()
                .Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(reason).Append("\r\n")
                .Append("Content-Length: ").Append(response.Body.Length).Append("\r\n")
                .Append("Connection: close\r\n");

            if (response.ContentRangeFrom.HasValue && response.ContentRangeTo.HasValue && response.ContentRangeTotal.HasValue)
            {
                builder.Append("Content-Range: bytes ")
                    .Append(response.ContentRangeFrom.Value).Append('-')
                    .Append(response.ContentRangeTo.Value).Append('/')
                    .Append(response.ContentRangeTotal.Value).Append("\r\n");
            }

            if (response.EntityTag is not null)
            {
                builder.Append("ETag: ").Append(response.EntityTag).Append("\r\n");
            }

            if (response.RetryAfter is not null)
            {
                builder.Append("Retry-After: ").Append(response.RetryAfter).Append("\r\n");
            }

            builder.Append("\r\n");
            var headers = Encoding.ASCII.GetBytes(builder.ToString());
            await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
            if (response.Body.Length > 0)
            {
                await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await Task.WhenAll(_handlers.ToArray()).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _stop.Dispose();
        }
    }
}
