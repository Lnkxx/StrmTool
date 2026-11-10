using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using StrmTool;
using static ProxyFactory;

internal static class Program
{
    private static int _checks;

    private static async Task<int> Main()
    {
        await CheckIncompleteCandidateAsync("missing dimensions", 4_000_000, 600_000_000, 0, 0);
        await CheckIncompleteCandidateAsync("missing size", 0, 600_000_000, 1920, 1080);
        await CheckIncompleteCandidateAsync("missing duration", 4_000_000, null, 1920, 1080);
        await CheckChangedPathRejectedAsync();
        await CheckChangedSourceRejectedAsync();

        Console.WriteLine($"PASS {_checks} candidate rejection checks");
        await CommitSimulation.Run();
        return 0;
    }

    private static async Task CheckIncompleteCandidateAsync(
        string label,
        long? size,
        long? runTimeTicks,
        int? width,
        int? height)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "sample.strm");
        File.WriteAllText(path, "https://media.invalid/sample.mkv");

        var item = NewVideo(path);
        var harness = CreateHarness(item, CreateMediaInfo(size, runTimeTicks, width, height));
        var result = await harness.Service.ProbeMediaStreamsAsync(item, CancellationToken.None);

        Check(result.Success, $"{label}: probe returns the incomplete candidate");
        Check(result.SourcePath == path, $"{label}: candidate keeps its source path");
        Check(harness.Counters.TotalWrites == 0, $"{label}: probing performs no writes");
        await ExpectFailureAsync(
            () => harness.Service.ApplyProbeResultAsync(item, result, CancellationToken.None),
            ProbeFailureKind.InvalidMedia,
            $"{label}: apply rejects incomplete candidate");
        Check(harness.Counters.TotalWrites == 0, $"{label}: rejected candidate performs no writes");
    }

    private static async Task CheckChangedPathRejectedAsync()
    {
        using var directory = new TemporaryDirectory();
        var oldPath = Path.Combine(directory.Path, "old.strm");
        var newPath = Path.Combine(directory.Path, "new.strm");
        File.WriteAllText(oldPath, "https://media.invalid/old.mkv");
        File.WriteAllText(newPath, "https://media.invalid/new.mkv");

        var item = NewVideo(oldPath);
        var harness = CreateHarness(item, CreateMediaInfo(4_000_000, 600_000_000, 1920, 1080));
        var result = await harness.Service.ProbeMediaStreamsAsync(item, CancellationToken.None);
        item.Path = newPath;

        await ExpectFailureAsync(
            () => harness.Service.ApplyProbeResultAsync(item, result, CancellationToken.None),
            ProbeFailureKind.SourceChanged,
            "changed item path is rejected");
        Check(harness.Counters.TotalWrites == 0, "changed item path performs no writes");
    }

    private static async Task CheckChangedSourceRejectedAsync()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "sample.strm");
        File.WriteAllText(path, "https://media.invalid/before.mkv");

        var item = NewVideo(path);
        var harness = CreateHarness(item, CreateMediaInfo(4_000_000, 600_000_000, 1920, 1080));
        var result = await harness.Service.ProbeMediaStreamsAsync(item, CancellationToken.None);
        File.WriteAllText(path, "https://media.invalid/after.mkv");

        await ExpectFailureAsync(
            () => harness.Service.ApplyProbeResultAsync(item, result, CancellationToken.None),
            ProbeFailureKind.SourceChanged,
            "changed STRM content is rejected");
        Check(harness.Counters.TotalWrites == 0, "changed STRM content performs no writes");
    }

    private static TestHarness CreateHarness(Video item, MediaInfo mediaInfo)
    {
        var counters = new WriteCounters();
        var libraryManager = CreateProxy<ILibraryManager>((method, _) =>
        {
            if (method.Name == "GetItemById")
            {
                counters.LibraryLookups++;
                return item;
            }

            if (method.Name is "UpdateItemAsync" or "UpdateItemsAsync")
            {
                counters.LibraryWrites++;
            }

            return DefaultValue(method.ReturnType);
        });

        var mediaEncoder = CreateProxy<IMediaEncoder>((method, _) =>
            method.Name == "GetMediaInfo"
                ? Task.FromResult(mediaInfo)
                : DefaultValue(method.ReturnType));

        var mediaStreamRepository = CreateProxy<IMediaStreamRepository>((method, _) =>
        {
            if (method.Name == "SaveMediaStreams")
            {
                counters.MediaStreamWrites++;
            }

            return method.Name == "GetMediaStreams"
                ? Array.Empty<MediaStream>()
                : DefaultValue(method.ReturnType);
        });

        var chapterRepository = CreateProxy<IChapterRepository>((method, _) =>
        {
            if (method.Name == "SaveChapters")
            {
                counters.ChapterWrites++;
            }

            return method.Name == "GetChapters"
                ? Array.Empty<ChapterInfo>()
                : DefaultValue(method.ReturnType);
        });

        var itemRepository = CreateProxy<IItemRepository>((method, _) =>
        {
            if (method.Name.StartsWith("Save", StringComparison.Ordinal)
                || method.Name.StartsWith("Delete", StringComparison.Ordinal))
            {
                counters.ItemRepositoryWrites++;
            }

            return method.Name == "RetrieveItem"
                ? item
                : DefaultValue(method.ReturnType);
        });

        var service = new StrmMediaInfoService(
            libraryManager,
            mediaEncoder,
            mediaStreamRepository,
            chapterRepository,
            NullLogger.Instance,
            itemRepository);

        return new TestHarness(service, counters);
    }

    private static Video NewVideo(string path)
    {
        return new Video
        {
            Id = Guid.NewGuid(),
            Path = path,
            Name = "sample"
        };
    }

    private static MediaInfo CreateMediaInfo(long? size, long? runTimeTicks, int? width, int? height)
    {
        return new MediaInfo
        {
            Size = size,
            RunTimeTicks = runTimeTicks,
            Container = "mkv",
            MediaStreams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Width = width,
                    Height = height
                }
            }
        };
    }

    private static async Task ExpectFailureAsync(
        Func<Task> action,
        ProbeFailureKind expectedKind,
        string label)
    {
        try
        {
            await action();
        }
        catch (ProbeFailureException exception)
        {
            Check(exception.Kind == expectedKind, $"{label}: expected {expectedKind}, got {exception.Kind}");
            return;
        }

        throw new InvalidOperationException($"{label}: expected ProbeFailureException");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException(label);
        }

        _checks++;
    }

    internal static object? DefaultValue(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var valueType = returnType.GetGenericArguments()[0];
            var value = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
            var fromResult = typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == nameof(Task.FromResult) && method.IsGenericMethodDefinition);
            return fromResult.MakeGenericMethod(valueType).Invoke(null, new[] { value });
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }

    private sealed class TestHarness
    {
        public TestHarness(StrmMediaInfoService service, WriteCounters counters)
        {
            Service = service;
            Counters = counters;
        }

        public StrmMediaInfoService Service { get; }
        public WriteCounters Counters { get; }
    }

    private sealed class WriteCounters
    {
        public int LibraryLookups { get; set; }
        public int LibraryWrites { get; set; }
        public int ItemRepositoryWrites { get; set; }
        public int MediaStreamWrites { get; set; }
        public int ChapterWrites { get; set; }
        public int TotalWrites => LibraryWrites + ItemRepositoryWrites + MediaStreamWrites + ChapterWrites;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "strm-commit-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

public class DelegateProxy<T> : DispatchProxy where T : class
{
    public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
        {
            throw new InvalidOperationException("Proxy invocation did not include a method");
        }

        return Handler?.Invoke(targetMethod, args);
    }
}

internal static class ProxyFactory
{
    public static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, DelegateProxy<T>>();
        ((DelegateProxy<T>)(object)proxy).Handler = handler;
        return proxy;
    }
}
