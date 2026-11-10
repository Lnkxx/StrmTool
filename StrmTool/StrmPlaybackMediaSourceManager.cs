using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace StrmTool
{
    /// <summary>
    /// Wraps Jellyfin's own media source manager. Every member is forwarded unchanged; the only difference is that
    /// <see cref="GetPlaybackMediaSources"/> turns <c>allowMediaProbe</c> off when <see cref="PlaybackProbeGate"/>
    /// proves the probe redundant. Failures of the gate never reach the caller: the original call is made as-is.
    /// </summary>
    public sealed class StrmPlaybackMediaSourceManager : IMediaSourceManager
    {
        private readonly IMediaSourceManager _inner;
        private readonly ILogger _logger;
        private readonly MediaInfoCache _cache;
        private readonly Func<PluginConfiguration> _config;
        private readonly Func<BaseItem, CancellationToken, Task<bool>> _restore;

        /// <param name="inner">Jellyfin's media source manager.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="config">Read on every call, so the switch works without a restart.</param>
        /// <param name="restore">Restores a reset item from its cache; true when the restore ran to completion.</param>
        public StrmPlaybackMediaSourceManager(
            IMediaSourceManager inner,
            ILogger logger,
            Func<PluginConfiguration> config,
            Func<BaseItem, CancellationToken, Task<bool>> restore = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _restore = restore;
            _cache = new MediaInfoCache(logger);
        }

        /// <summary>Gets the wrapped manager.</summary>
        public IMediaSourceManager Inner => _inner;

        public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken)
        {
            return allowMediaProbe
                ? GetPlaybackMediaSourcesGated(item, user, enablePathSubstitution, cancellationToken)
                : _inner.GetPlaybackMediaSources(item, user, false, enablePathSubstitution, cancellationToken);
        }

        private async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSourcesGated(BaseItem item, User user, bool enablePathSubstitution, CancellationToken cancellationToken)
        {
            bool allowMediaProbe = !await CanSkipProbeAsync(item, cancellationToken).ConfigureAwait(false);
            return await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> CanSkipProbeAsync(BaseItem item, CancellationToken cancellationToken)
        {
            try
            {
                var config = _config();
                var decision = PlaybackProbeGate.Evaluate(item, config, _cache, _inner.GetMediaStreams);
                if (decision == PlaybackProbeDecision.NeedsRestore && _restore != null
                    && await _restore(item, cancellationToken).ConfigureAwait(false))
                {
                    decision = PlaybackProbeGate.Evaluate(item, config, _cache, _inner.GetMediaStreams);
                }

                if (decision == PlaybackProbeDecision.Skip)
                {
                    _logger.LogInformation("Playback probe skipped for {Name}: cache and stored streams are current", Path.GetFileNameWithoutExtension(item.Path));
                    return true;
                }

                if (decision != PlaybackProbeDecision.Disabled && decision != PlaybackProbeDecision.NotStrm && decision != PlaybackProbeDecision.OutOfScope)
                {
                    _logger.LogInformation("Playback probe kept for {Name}: {Reason}", Path.GetFileNameWithoutExtension(item.Path), decision);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Playback probe gate failed for item {ItemId}; Jellyfin's own behavior applies", item?.Id);
                return false;
            }
        }

        public void AddParts(IEnumerable<IMediaSourceProvider> providers) => _inner.AddParts(providers);

        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => _inner.GetMediaStreams(itemId);

        public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => _inner.GetMediaStreams(query);

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => _inner.GetMediaAttachments(itemId);

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => _inner.GetMediaAttachments(query);

        public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null) => _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

        public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken) => _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);

        public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken) => _inner.OpenLiveStream(request, cancellationToken);

        public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken) => _inner.OpenLiveStreamInternal(request, cancellationToken);

        public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) => _inner.GetLiveStream(id, cancellationToken);

        public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

        public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

        public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

        public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken) => _inner.GetRecordingStreamMediaSources(info, cancellationToken);

        public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

        public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken) => _inner.GetLiveStreamMediaInfo(id, cancellationToken);

        public bool SupportsDirectStream(string path, MediaProtocol protocol) => _inner.SupportsDirectStream(path, protocol);

        public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);

        public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user) => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

        public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken) => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
    }
}
