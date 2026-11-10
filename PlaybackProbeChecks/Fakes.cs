using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

/// <summary>Records every call it receives; the handler decides what to return.</summary>
public class RecordingProxy : DispatchProxy
{
    public List<(MethodInfo Method, object[] Args)> Calls { get; } = new();

    public Func<MethodInfo, object[], object> Handler { get; set; }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        Calls.Add((targetMethod, args));
        return Handler != null ? Handler(targetMethod, args) : Values.Default(targetMethod.ReturnType);
    }

    public static (T Proxy, RecordingProxy Recorder) Create<T>(Func<MethodInfo, object[], object> handler = null) where T : class
    {
        var proxy = Create<T, RecordingProxy>();
        var recorder = (RecordingProxy)(object)proxy;
        recorder.Handler = handler;
        return (proxy, recorder);
    }
}

public static class Values
{
    /// <summary>What a method with nothing to say returns: completed tasks, zero values, null references.</summary>
    public static object Default(Type type)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var result = type.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, new[] { Default(result) });
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    /// <summary>A fresh, recognisable, non-default value of the given type.</summary>
    public static object Distinct(Type type)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(string)) return Guid.NewGuid().ToString("N");
        if (type == typeof(bool)) return true;
        if (type == typeof(Guid)) return Guid.NewGuid();
        if (type == typeof(CancellationToken)) return new CancellationTokenSource().Token;
        if (type.IsEnum) return Enum.GetValues(type).Cast<object>().Last();
        if (type == typeof(Task)) return Task.FromResult(new object());
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var arguments = type.GetGenericArguments();
            if (definition == typeof(Task<>))
            {
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(arguments[0]).Invoke(null, new[] { Distinct(arguments[0]) });
            }

            if (definition == typeof(Tuple<,>))
            {
                return Activator.CreateInstance(type, Distinct(arguments[0]), Distinct(arguments[1]));
            }

            if (definition == typeof(IReadOnlyList<>) || definition == typeof(IEnumerable<>))
            {
                return Array.CreateInstance(arguments[0], 1);
            }
        }

        if (type.IsInterface)
        {
            return typeof(DispatchProxy).GetMethods().Single(m => m.Name == "Create" && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(type, typeof(RecordingProxy)).Invoke(null, null);
        }

        if (type.IsAbstract) return RuntimeHelpers.GetUninitializedObject(typeof(Video));
        return type.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(type) : RuntimeHelpers.GetUninitializedObject(type);
    }

    public static bool Same(object a, object b) => a is ValueType ? Equals(a, b) : ReferenceEquals(a, b);
}

/// <summary>A video whose repository save is a test hook, as BaseItem's own needs a running server.</summary>
public sealed class TestVideo : Video
{
    public Func<TestVideo, CancellationToken, Task> Submit { get; set; } = (_, _) => Task.CompletedTask;

    public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken) => Submit(this, cancellationToken);
}

/// <summary>Stands in for Jellyfin's concrete MediaSourceManager in container tests.</summary>
public sealed class FakeManager : IMediaSourceManager, IDisposable
{
    public static int Created;
    public static int Disposed;

    public FakeManager() { Interlocked.Increment(ref Created); }

    public IEnumerable<IMediaSourceProvider> Parts { get; private set; }

    public List<bool> ProbeFlags { get; } = new();

    public void Dispose() => Interlocked.Increment(ref Disposed);

    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => Parts = providers;

    public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        ProbeFlags.Add(allowMediaProbe);
        return Task.FromResult<IReadOnlyList<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => Array.Empty<MediaStream>();
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => throw new NotImplementedException();
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => throw new NotImplementedException();
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => throw new NotImplementedException();
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null) => throw new NotImplementedException();
    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) => throw new NotImplementedException();
    public ILiveStream GetLiveStreamInfo(string id) => throw new NotImplementedException();
    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => throw new NotImplementedException();
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task CloseLiveStream(string id) => throw new NotImplementedException();
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken) => throw new NotImplementedException();
    public bool SupportsDirectStream(string path, MediaProtocol protocol) => throw new NotImplementedException();
    public MediaProtocol GetPathProtocol(string path) => throw new NotImplementedException();
    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user) => throw new NotImplementedException();
    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken) => throw new NotImplementedException();
}
