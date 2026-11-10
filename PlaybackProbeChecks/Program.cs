using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StrmTool;

int count = 0;
void Check(bool ok, string why) { if (!ok) throw new Exception("FAIL: " + why); count++; }

var dir = Path.Combine(Path.GetTempPath(), "playback-probe-checks-" + Guid.NewGuid());
Directory.CreateDirectory(dir);
try
{
    const string Url = "http://example.invalid/d/sample.mkv";
    const long Size = 88246893816, Ticks = 103557230000;
    ILogger logger = NullLogger.Instance;
    var cache = new MediaInfoCache(logger);
    var on = new PluginConfiguration();

    List<MediaStream> Streams() => new()
    {
        new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc", Width = 3840, Height = 2160, BitRate = 80000000, BitDepth = 10, PixelFormat = "yuv420p10le", Profile = "Main 10" },
        new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "truehd", Channels = 8, SampleRate = 48000, BitRate = 4000000, Language = "eng" },
        new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Codec = "PGSSUB", Language = "chi" },
    };
    // What Jellyfin's own re-probe of the STRM leaves behind: same streams, detail gone.
    List<MediaStream> Degraded()
    {
        var streams = Streams();
        streams[0].BitRate = null;
        streams[0].BitDepth = null;
        streams[1].BitRate = null;
        return streams;
    }
    string NewStrm(string name, string content = Url)
    {
        var path = Path.Combine(dir, name + ".strm");
        File.WriteAllText(path, content);
        return path;
    }
    Task Save(string strm, string container = "mkv", int? totalBitrate = null) =>
        cache.SaveFullCacheAsync(strm, Streams(), Size, Ticks, container, totalBitrate: totalBitrate);
    void Tamper(string strm, Action<JsonObject> change)
    {
        var path = strm[..^".strm".Length] + ".strmtool.json";
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(node);
        File.WriteAllText(path, node.ToJsonString());
    }
    TestVideo Item(string strm, string shortcut = Url) => new TestVideo
    {
        Id = Guid.NewGuid(), Path = strm, Size = Size, RunTimeTicks = Ticks, Container = "mkv", Width = 3840, Height = 2160, IsShortcut = true, ShortcutPath = shortcut,
    };
    PlaybackProbeDecision Gate(BaseItem item, PluginConfiguration config = null, Func<Guid, IReadOnlyList<MediaStream>> stored = null) =>
        PlaybackProbeGate.Evaluate(item, config ?? on, cache, stored ?? (_ => Streams()));

    // ---- 1. Decision rule ----
    Check(on.SkipPlaybackProbe, "the switch defaults to on");

    var strm = NewStrm("示例影片 (2026) - 2160p");
    await Save(strm);
    Check(Gate(Item(strm)) == PlaybackProbeDecision.Skip, "valid cache, current shortcut, complete item: skip");

    Check(Gate(Item(strm), new PluginConfiguration { SkipPlaybackProbe = false }) == PlaybackProbeDecision.Disabled, "switch off: keep");
    Check(PlaybackProbeGate.Evaluate(Item(strm), null, cache, _ => Streams()) == PlaybackProbeDecision.Disabled, "no configuration (plugin not loaded): keep");

    Check(Gate(null) == PlaybackProbeDecision.NotStrm, "null item: keep");
    Check(Gate(new TestVideo { Path = null }) == PlaybackProbeDecision.NotStrm, "item without a path: keep");
    var mkv = Path.Combine(dir, "示例影片 (2026) - 2160p.mkv");
    Check(Gate(new TestVideo { Id = Guid.NewGuid(), Path = mkv, Size = Size, RunTimeTicks = Ticks, Width = 3840, Height = 2160 }) == PlaybackProbeDecision.NotStrm, "non-STRM path: keep");

    Check(Gate(Item(strm), new PluginConfiguration { ProbePathFilter = Path.Combine(dir, "elsewhere") }) == PlaybackProbeDecision.OutOfScope, "outside the probe scope: keep");
    Check(Gate(Item(strm), new PluginConfiguration { ProbePathFilter = dir }) == PlaybackProbeDecision.Skip, "inside the probe scope: skip");

    Check(Gate(Item(NewStrm("no-cache"))) == PlaybackProbeDecision.NoValidCache, "no cache file (new item): keep");
    Check(PlaybackProbeGate.Evaluate(Item(strm), on, null, _ => Streams()) == PlaybackProbeDecision.NoValidCache, "no cache reader: keep");
    var changed = NewStrm("changed");
    await Save(changed);
    File.WriteAllText(changed, "http://example.invalid/d/replaced.mkv");
    Check(Gate(Item(changed, "http://example.invalid/d/replaced.mkv")) == PlaybackProbeDecision.NoValidCache, "STRM content no longer matches the cached hash: keep");
    var tampers = new (string Name, Action<JsonObject> Change)[]
    {
        ("pointer-sized size", n => n["size"] = 93),
        ("zero runtime", n => n["runTimeTicks"] = 0),
        ("missing runtime", n => n.Remove("runTimeTicks")),
        ("invalid flag", n => n["isValid"] = false),
        ("no streams", n => n["mediaStreams"] = new JsonArray()),
        ("video without dimensions", n => n["mediaStreams"]![0]!["Width"] = 0),
        ("foreign hash", n => n["strmContentHash"] = new string('0', 64)),
    };
    foreach (var (name, change) in tampers)
    {
        var bad = NewStrm("bad-" + name);
        await Save(bad);
        Check(Gate(Item(bad)) == PlaybackProbeDecision.Skip, name + ": control is skippable before tampering");
        Tamper(bad, change);
        Check(Gate(Item(bad)) == PlaybackProbeDecision.NoValidCache, "cache with " + name + ": keep");
    }
    var corrupt = NewStrm("corrupt");
    await Save(corrupt);
    File.WriteAllText(corrupt[..^5] + ".strmtool.json", "{ not json");
    Check(Gate(Item(corrupt)) == PlaybackProbeDecision.NoValidCache, "unreadable cache: keep");

    const string IsoUrl = "http://example.invalid/d/disc.iso";
    var iso = NewStrm("原盘.iso", IsoUrl);
    await Save(iso, "bluray");
    Check(Gate(Item(iso, IsoUrl)) == PlaybackProbeDecision.IsoUpgradePending, "remote ISO cached before the bitrate/color stage: keep");
    await Save(iso, "bluray", 30000000);
    var isoItem = Item(iso, IsoUrl);
    isoItem.TotalBitrate = 30000000;
    Check(Gate(isoItem) == PlaybackProbeDecision.Skip, "remote ISO with the current probe stage: skip");

    Check(Gate(Item(strm, "http://example.invalid/d/old.mkv")) == PlaybackProbeDecision.ShortcutStale, "item still points at the previous URL: keep");
    Check(Gate(Item(strm, null)) == PlaybackProbeDecision.ShortcutStale, "item has no URL yet: keep");
    Check(Gate(Item(strm, string.Empty)) == PlaybackProbeDecision.ShortcutStale, "item has an empty URL: keep");
    Check(Gate(Item(strm, Url.ToUpperInvariant())) == PlaybackProbeDecision.ShortcutStale, "URL comparison is exact: keep");
    var local = NewStrm("local", "/media/movie.mkv");
    await Save(local);
    Check(Gate(Item(local, null)) == PlaybackProbeDecision.ShortcutStale, "local-path STRM (Jellyfin stores no URL for it): keep");
    var noisy = NewStrm("noisy", "#EXTM3U\r\n\r\n \t" + Url + " \r\nhttp://example.invalid/second\r\n");
    await Save(noisy);
    Check(PlaybackProbeGate.ReadShortcutPath(noisy) == Url, "STRM is read like Jellyfin reads it: comments, blanks and tabs dropped, first line wins");
    Check(Gate(Item(noisy)) == PlaybackProbeDecision.Skip, "STRM with comment and padding: skip");

    Check(Gate(Item(strm), stored: _ => null) == PlaybackProbeDecision.StreamsIncomplete, "stored streams unavailable: keep");
    Check(PlaybackProbeGate.Evaluate(Item(strm), on, cache, null) == PlaybackProbeDecision.StreamsIncomplete, "no stream reader: keep");
    Check(Gate(Item(strm), stored: _ => new List<MediaStream>()) == PlaybackProbeDecision.StreamsIncomplete, "no stored streams: keep");
    Check(Gate(Item(strm), stored: _ => Streams().Where(s => s.Type != MediaStreamType.Audio).ToList()) == PlaybackProbeDecision.StreamsIncomplete, "no stored audio stream: keep");
    Check(Gate(Item(strm), stored: _ => Streams().Where(s => s.Type != MediaStreamType.Video).ToList()) == PlaybackProbeDecision.StreamsIncomplete, "no stored video stream: keep");
    Check(Gate(Item(strm), stored: _ => Streams().Select(s => { s.IsExternal = s.Type == MediaStreamType.Audio; return s; }).ToList()) == PlaybackProbeDecision.StreamsIncomplete, "external streams do not count as the item's own: keep");

    TestVideo Reset(Action<TestVideo> change) { var item = Item(strm); change(item); return item; }
    Check(Gate(Reset(i => i.Size = 93)) == PlaybackProbeDecision.NeedsRestore, "93-byte size reset: restore first");
    Check(Gate(Reset(i => i.Size = null)) == PlaybackProbeDecision.NeedsRestore, "missing size: restore first");
    Check(Gate(Reset(i => i.RunTimeTicks = null)) == PlaybackProbeDecision.NeedsRestore, "missing runtime: restore first");
    Check(Gate(Reset(i => i.Width = 0)) == PlaybackProbeDecision.NeedsRestore, "missing dimensions: restore first");
    Check(Gate(Item(strm), stored: _ => Degraded()) == PlaybackProbeDecision.NeedsRestore, "stored streams lost detail: restore first");
    Check(Gate(Reset(i => i.Size = Size - 718)) == PlaybackProbeDecision.Skip, "a real size that differs from the cache is a probe disagreement, not a reset: skip");

    File.WriteAllText(Path.Combine(dir, "示例影片 (2026) - 2160p.zh-CN.srt"), "1\r\n00:00:40,416 --> 00:00:41,584\r\n示例字幕\r\n\r\n");
    ExternalSubtitleScanner.Configure(FakeLocalization.Create());
    try
    {
        Check(Gate(Item(strm)) == PlaybackProbeDecision.NeedsRestore, "subtitle file on disk missing from the stored streams: restore first");
        var withExternal = TechnicalMetadataVerifier.WithStoredExternal(Streams(), Streams(), ExternalSubtitleScanner.Discover(strm));
        Check(withExternal.Count == 4 && Gate(Item(strm), stored: _ => withExternal) == PlaybackProbeDecision.Skip, "external subtitle already stored: skip");
    }
    finally
    {
        ExternalSubtitleScanner.Reset();
        File.Delete(Path.Combine(dir, "示例影片 (2026) - 2160p.zh-CN.srt"));
    }
    int gateChecks = count;

    // ---- 2. Wrapper: GetPlaybackMediaSources ----
    IReadOnlyList<MediaSourceInfo> sources = new[] { new MediaSourceInfo { Id = "source" } };
    StrmPlaybackMediaSourceManager Wrapper(out RecordingProxy recorder, Func<IReadOnlyList<MediaStream>> stored = null, Func<PluginConfiguration> config = null,
        Func<BaseItem, CancellationToken, Task<bool>> restore = null, Exception asyncFailure = null, Exception syncFailure = null)
    {
        var (inner, rec) = RecordingProxy.Create<IMediaSourceManager>((m, a) =>
        {
            if (m.Name == "GetMediaStreams" && a[0] is Guid) return stored != null ? stored() : Streams();
            if (m.Name == "GetPlaybackMediaSources")
            {
                if (syncFailure != null) throw syncFailure;
                return asyncFailure != null ? Task.FromException<IReadOnlyList<MediaSourceInfo>>(asyncFailure) : Task.FromResult(sources);
            }

            return Values.Default(m.ReturnType);
        });
        recorder = rec;
        return new StrmPlaybackMediaSourceManager(inner, logger, config ?? (() => on), restore);
    }
    object[] PlaybackArgs(RecordingProxy recorder) => recorder.Calls.Single(c => c.Method.Name == "GetPlaybackMediaSources").Args;
    async Task<bool> ProbeFlag(BaseItem item, Func<IReadOnlyList<MediaStream>> stored = null, Func<PluginConfiguration> config = null, Func<BaseItem, CancellationToken, Task<bool>> restore = null)
    {
        var wrapper = Wrapper(out var recorder, stored, config, restore);
        var result = await wrapper.GetPlaybackMediaSources(item, null, true, true, default);
        if (!ReferenceEquals(result, sources)) throw new Exception("FAIL: the inner result must be returned as-is");
        return (bool)PlaybackArgs(recorder)[2];
    }

    var user = (User)Values.Distinct(typeof(User));
    var token = new CancellationTokenSource().Token;
    var playItem = Item(strm);
    var skipping = Wrapper(out var skipRecorder);
    var skipResult = await skipping.GetPlaybackMediaSources(playItem, user, true, true, token);
    var skipArgs = PlaybackArgs(skipRecorder);
    Check((bool)skipArgs[2] == false, "skippable item: Jellyfin's manager is called with allowMediaProbe=false");
    Check(ReferenceEquals(skipArgs[0], playItem) && ReferenceEquals(skipArgs[1], user) && (bool)skipArgs[3] && token.Equals(skipArgs[4]), "the other arguments are passed through untouched");
    Check(ReferenceEquals(skipResult, sources), "the inner result is returned as-is");
    Check(skipRecorder.Calls.Count(c => c.Method.Name == "GetPlaybackMediaSources") == 1, "the inner manager is called exactly once");

    var noSubstitution = Wrapper(out var noSubstitutionRecorder);
    await noSubstitution.GetPlaybackMediaSources(playItem, null, true, false, default);
    Check(!(bool)PlaybackArgs(noSubstitutionRecorder)[3] && PlaybackArgs(noSubstitutionRecorder)[1] == null, "enablePathSubstitution=false and a null user stay as given");

    var alreadyOff = Wrapper(out var offRecorder);
    await alreadyOff.GetPlaybackMediaSources(playItem, null, false, true, default);
    Check(!(bool)PlaybackArgs(offRecorder)[2] && offRecorder.Calls.Count == 1, "allowMediaProbe=false from the caller: forwarded directly, the rule is not even evaluated");

    Check(await ProbeFlag(Item(strm), config: () => new PluginConfiguration { SkipPlaybackProbe = false }), "switch off: probe kept");
    Check(await ProbeFlag(Item(strm), config: () => null), "plugin configuration unavailable: probe kept");
    Check(await ProbeFlag(new TestVideo { Id = Guid.NewGuid(), Path = mkv }), "non-STRM item: probe kept");
    Check(await ProbeFlag(Item(NewStrm("no-cache-2"))), "no cache: probe kept");
    Check(await ProbeFlag(Item(changed, "http://example.invalid/d/replaced.mkv")), "hash mismatch: probe kept");
    Check(await ProbeFlag(Item(strm, "http://example.invalid/d/old.mkv")), "stale URL: probe kept");
    Check(await ProbeFlag(Item(strm), stored: () => new List<MediaStream>()), "incomplete streams: probe kept");
    Check(!await ProbeFlag(isoItem), "remote ISO with the current probe stage: probe skipped");
    Check(await ProbeFlag(Item(iso, IsoUrl)), "ISO item that lost its bitrate and no restorer: probe kept");
    Check(await ProbeFlag(Reset(i => i.Size = 93)), "reset item and no restorer: probe kept");

    bool switchOn = true;
    var live = Wrapper(out var liveRecorder, config: () => new PluginConfiguration { SkipPlaybackProbe = switchOn });
    await live.GetPlaybackMediaSources(playItem, null, true, true, default);
    switchOn = false;
    await live.GetPlaybackMediaSources(playItem, null, true, true, default);
    var liveFlags = liveRecorder.Calls.Where(c => c.Method.Name == "GetPlaybackMediaSources").Select(c => (bool)c.Args[2]).ToArray();
    Check(liveFlags.SequenceEqual(new[] { false, true }), "the switch is read on every call: turning it off takes effect without a restart");

    Check(await ProbeFlag(Item(strm), stored: () => throw new IOException("database unavailable")), "the rule throws: probe kept, call still made");
    Check(await ProbeFlag(Item(strm), config: () => throw new InvalidOperationException("config unavailable")), "configuration throws: probe kept, call still made");
    var nullItem = Wrapper(out var nullRecorder);
    await nullItem.GetPlaybackMediaSources(null, null, true, true, default);
    Check((bool)PlaybackArgs(nullRecorder)[2] && PlaybackArgs(nullRecorder)[0] == null, "null item: handed to Jellyfin unchanged for it to judge");

    foreach (bool allow in new[] { true, false })
    {
        var boom = new InvalidOperationException("inner failure");
        Exception seen = null;
        try { await Wrapper(out _, asyncFailure: boom).GetPlaybackMediaSources(playItem, null, allow, true, default); } catch (Exception e) { seen = e; }
        Check(ReferenceEquals(seen, boom), $"a failure of Jellyfin's manager reaches the caller unchanged (allowMediaProbe={allow})");
        seen = null;
        try { await Wrapper(out _, syncFailure: boom).GetPlaybackMediaSources(playItem, null, allow, true, default); } catch (Exception e) { seen = e; }
        Check(ReferenceEquals(seen, boom), $"so does one thrown before its first await (allowMediaProbe={allow})");
    }

    int restores = 0;
    Check(!await ProbeFlag(Reset(i => { i.Size = 93; i.RunTimeTicks = null; }), restore: (i, _) => { restores++; i.Size = Size; i.RunTimeTicks = Ticks; return Task.FromResult(true); }) && restores == 1,
        "reset item restored from cache first: probe skipped");
    Check(await ProbeFlag(Reset(i => i.Size = 93), restore: (_, _) => Task.FromResult(true)), "restore reported done but the item is still reset: probe kept");
    Check(await ProbeFlag(Reset(i => i.Size = 93), restore: (_, _) => Task.FromResult(false)), "restore not possible: probe kept");
    Check(await ProbeFlag(Reset(i => i.Size = 93), restore: (_, _) => throw new TimeoutException("restore failed")), "restore throws: probe kept");
    restores = 0;
    await ProbeFlag(Item(strm), restore: (_, _) => { restores++; return Task.FromResult(true); });
    await ProbeFlag(Item(NewStrm("no-cache-3")), restore: (_, _) => { restores++; return Task.FromResult(true); });
    Check(restores == 0, "the restorer runs only for a reset item, never for a healthy or uncached one");

    foreach (var build in new Action[]
    {
        () => new StrmPlaybackMediaSourceManager(null, logger, () => on),
        () => new StrmPlaybackMediaSourceManager(RecordingProxy.Create<IMediaSourceManager>().Proxy, null, () => on),
        () => new StrmPlaybackMediaSourceManager(RecordingProxy.Create<IMediaSourceManager>().Proxy, logger, null),
    })
    {
        Exception seen = null;
        try { build(); } catch (Exception e) { seen = e; }
        Check(seen is ArgumentNullException, "the wrapper refuses to be built without its essentials");
    }
    int wrapperChecks = count - gateChecks;

    // ---- 3. Wrapper: every other member is a plain forward ----
    var members = typeof(IMediaSourceManager).GetMethods();
    Check(members.Length == 21, "IMediaSourceManager has the 21 members the wrapper was written against");
    Check(typeof(IMediaSourceManager).GetProperties().Length == 0 && typeof(IMediaSourceManager).GetEvents().Length == 0, "and no properties or events");
    foreach (var member in members.Where(m => m.Name != nameof(IMediaSourceManager.GetPlaybackMediaSources)))
    {
        object returned = Values.Distinct(member.ReturnType);
        var (inner, recorder) = RecordingProxy.Create<IMediaSourceManager>((_, _) => returned);
        var wrapper = new StrmPlaybackMediaSourceManager(inner, logger, () => on);
        var given = member.GetParameters().Select(p => Values.Distinct(p.ParameterType)).ToArray();
        var result = member.Invoke(wrapper, given);
        Check(recorder.Calls.Count == 1 && recorder.Calls[0].Method == member
            && given.Length == recorder.Calls[0].Args.Length && given.Zip(recorder.Calls[0].Args).All(p => Values.Same(p.First, p.Second))
            && (member.ReturnType == typeof(void) || Values.Same(returned, result)),
            $"{member.Name}({string.Join(", ", member.GetParameters().Select(p => p.ParameterType.Name))}) is forwarded once, same arguments, same result");
    }
    int forwardChecks = count - gateChecks - wrapperChecks;

    // ---- 4. Registration in Jellyfin's container ----
    var registrator = typeof(PluginServiceRegistrator);
    Check(typeof(IPluginServiceRegistrator).IsAssignableFrom(registrator) && registrator.IsPublic && !registrator.IsAbstract
        && registrator.GetConstructor(Type.EmptyTypes) != null && typeof(Plugin).Assembly.GetExportedTypes().Contains(registrator),
        "the registrator is a public, default-constructible exported type, which is how Jellyfin finds and creates it");

    FakeManager.Created = FakeManager.Disposed = 0;
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IMediaSourceManager, FakeManager>();                                   // Jellyfin's own registration comes first
    ((IPluginServiceRegistrator)Activator.CreateInstance(registrator)!).RegisterServices(services, null);   // then the plugin's
    Check(services.Count(d => d.ServiceType == typeof(IMediaSourceManager)) == 1, "exactly one IMediaSourceManager registration remains");
    using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
    {
        var resolved = provider.GetRequiredService<IMediaSourceManager>();
        Check(resolved is StrmPlaybackMediaSourceManager, "the plugin's later registration wins: consumers get the wrapper");
        var inner = ((StrmPlaybackMediaSourceManager)resolved).Inner;
        Check(inner is FakeManager, "the wrapper holds Jellyfin's implementation");
        Check(ReferenceEquals(provider.GetRequiredService<IMediaSourceManager>(), resolved) && provider.GetServices<IMediaSourceManager>().Count() == 1, "the wrapper is a singleton and the only implementation");
        Check(ReferenceEquals(provider.GetRequiredService<FakeManager>(), inner) && FakeManager.Created == 1, "Jellyfin's implementation is constructed exactly once (one owner of live stream state)");
        var parts = Array.Empty<IMediaSourceProvider>();
        resolved.AddParts(parts);
        Check(ReferenceEquals(((FakeManager)inner).Parts, parts), "AddParts at startup reaches Jellyfin's implementation");
        await resolved.GetPlaybackMediaSources(Item(strm), null, true, true, default);
        Check(((FakeManager)inner).ProbeFlags.SequenceEqual(new[] { true }), "plugin not loaded (no Plugin.Instance): probe kept, behavior unchanged");
    }
    Check(FakeManager.Disposed == 1, "the container still disposes Jellyfin's implementation");

    Check(!PluginServiceRegistrator.TryDecorateMediaSourceManager(services), "registering twice does not wrap the wrapper");
    Check(!PluginServiceRegistrator.TryDecorateMediaSourceManager(null) && !PluginServiceRegistrator.TryDecorateMediaSourceManager(new ServiceCollection()), "nothing to wrap: no-op");
    var unusual = new (string Name, Action<IServiceCollection> Register)[]
    {
        ("instance", s => s.AddSingleton<IMediaSourceManager>(new FakeManager())),
        ("factory", s => s.AddSingleton<IMediaSourceManager>(_ => new FakeManager())),
        ("transient", s => s.AddTransient<IMediaSourceManager, FakeManager>()),
        ("scoped", s => s.AddScoped<IMediaSourceManager, FakeManager>()),
        ("keyed", s => s.AddKeyedSingleton<IMediaSourceManager, FakeManager>("key")),
    };
    foreach (var (name, register) in unusual)
    {
        var other = new ServiceCollection();
        register(other);
        var before = other.ToArray();
        Check(!PluginServiceRegistrator.TryDecorateMediaSourceManager(other) && other.SequenceEqual(before), $"unexpected {name} registration: left exactly as it was");
    }
    int registrationChecks = count - gateChecks - wrapperChecks - forwardChecks;

    // ---- 5. Restore-then-skip against the real ItemUpdateListener ----
    Video Copy(Video v) => new Video { Id = v.Id, Path = v.Path, Size = v.Size, RunTimeTicks = v.RunTimeTicks, Container = v.Container, Width = v.Width, Height = v.Height, TotalBitrate = v.TotalBitrate };
    (ItemUpdateListener Listener, TestVideo Live, Func<IReadOnlyList<MediaStream>> Db, Func<int> Submissions) Library(string name, int submitDelayMs = 0, bool repositories = true)
    {
        var path = NewStrm(name);
        Save(path).GetAwaiter().GetResult();
        var item = Item(path);
        item.Size = 93;
        item.RunTimeTicks = null;
        item.Width = 0;
        item.Height = 0;
        IReadOnlyList<MediaStream> db = Degraded();
        Video persisted = Copy(item);
        int submissions = 0;
        item.Submit = async (v, t) =>
        {
            Interlocked.Increment(ref submissions);
            if (submitDelayMs > 0) await Task.Delay(submitDelayMs, t);
            persisted = Copy(v);
        };
        var library = RecordingProxy.Create<ILibraryManager>((m, _) => m.Name == "GetItemById" ? item : Values.Default(m.ReturnType)).Proxy;
        var itemRepo = RecordingProxy.Create<IItemRepository>((m, _) => m.Name == "RetrieveItem" ? Copy(persisted) : Values.Default(m.ReturnType)).Proxy;
        var streamRepo = RecordingProxy.Create<IMediaStreamRepository>((m, a) =>
        {
            if (m.Name == "SaveMediaStreams") { db = ((IEnumerable<MediaStream>)a[1]).ToList(); return null; }
            return m.Name == "GetMediaStreams" ? db : Values.Default(m.ReturnType);
        }).Proxy;
        var listener = repositories ? new ItemUpdateListener(library, logger, on, cache, itemRepo, streamRepo) : new ItemUpdateListener(library, logger, on, cache);
        return (listener, item, () => db, () => submissions);
    }

    var quick = Library("恢复-快");
    Check(ReferenceEquals(ItemUpdateListener.Current, quick.Listener), "the running listener is reachable from the playback path");
    Check(Gate(quick.Live, stored: _ => quick.Db()) == PlaybackProbeDecision.NeedsRestore, "control: the item starts out reset");
    Check(!await ProbeFlag(quick.Live, stored: quick.Db, restore: (i, t) => quick.Listener.TryRestoreNowAsync(i, TimeSpan.FromSeconds(5), t)), "reset item: restored from cache, then the probe is skipped");
    Check(quick.Live.Size == Size && quick.Live.RunTimeTicks == Ticks && quick.Live.Width == 3840 && quick.Db()[0].BitRate == 80000000 && quick.Submissions() == 1, "the restore wrote the cached values through Jellyfin's repositories, once");
    Check(await quick.Listener.TryRestoreNowAsync(quick.Live, TimeSpan.FromSeconds(5), default) && quick.Submissions() == 1, "asking again for a healthy item completes without writing");
    Check(!await quick.Listener.TryRestoreNowAsync(null, TimeSpan.FromSeconds(5), default), "null item: not restored");
    quick.Listener.Dispose();
    Check(ItemUpdateListener.Current == null, "a disposed listener is no longer offered");
    Check(!await quick.Listener.TryRestoreNowAsync(quick.Live, TimeSpan.FromSeconds(5), default), "a disposed listener restores nothing");

    // An item whose own probe is still running: the restore has to queue behind it (ProbeCoordinator).
    var busy = Library("恢复-排队");
    var probeLease = await ProbeCoordinator.EnterAsync(busy.Live.Id, default);
    var watch = Stopwatch.StartNew();
    var first = busy.Listener.TryRestoreNowAsync(busy.Live, TimeSpan.FromMilliseconds(150), default);
    var second = busy.Listener.TryRestoreNowAsync(busy.Live, TimeSpan.FromMilliseconds(150), default);
    Check(!await first && !await second && watch.ElapsedMilliseconds < 2000 && busy.Live.Size == 93, "a restore that cannot finish within the allowed wait is not waited for");
    Check(await ProbeFlag(busy.Live, stored: busy.Db, restore: (i, t) => busy.Listener.TryRestoreNowAsync(i, TimeSpan.FromMilliseconds(50), t)), "and playback then keeps the probe");
    probeLease.Dispose();
    Check(await busy.Listener.TryRestoreNowAsync(busy.Live, TimeSpan.FromSeconds(10), default) && busy.Submissions() == 1 && busy.Live.Size == Size, "all requests joined the one queued restore, which still finishes afterwards");
    busy.Listener.Dispose();

    // The database write is slow: the item in memory and its streams are already restored, which is what playback reads.
    var slow = Library("恢复-慢", submitDelayMs: 1500);
    var pending = slow.Listener.TryRestoreNowAsync(slow.Live, TimeSpan.FromSeconds(10), default);
    Check(!await ProbeFlag(slow.Live, stored: slow.Db) && !pending.IsCompleted, "restore already applied and only its save still running: probe skipped");
    Check(await pending && slow.Submissions() == 1, "the slow save completes once");
    slow.Listener.Dispose();

    var bare = Library("恢复-无仓储", repositories: false);
    Check(!await bare.Listener.TryRestoreNowAsync(bare.Live, TimeSpan.FromSeconds(5), default) && bare.Submissions() == 0 && bare.Live.Size == 93, "without Jellyfin's repositories nothing is written");
    bare.Listener.Dispose();

    var failing = Library("恢复-失败");
    failing.Live.Submit = (_, _) => throw new IOException("database is locked");
    Check(await ProbeFlag(failing.Live, stored: failing.Db, restore: (i, t) => failing.Listener.TryRestoreNowAsync(i, TimeSpan.FromSeconds(5), t)), "a failed restore: probe kept");
    failing.Listener.Dispose();
    int restoreChecks = count - gateChecks - wrapperChecks - forwardChecks - registrationChecks;

    Console.WriteLine($"  decision rule {gateChecks}, wrapper {wrapperChecks}, forwarding {forwardChecks}, registration {registrationChecks}, restore {restoreChecks}");
    Console.WriteLine($"PASS {count} playback probe checks");
}
finally
{
    ExternalSubtitleScanner.Reset();
    Directory.Delete(dir, true);
}
