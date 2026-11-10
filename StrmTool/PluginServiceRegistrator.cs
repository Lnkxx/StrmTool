using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StrmTool
{
    /// <summary>
    /// Jellyfin calls this after its own services are registered, which is what lets the media source manager be
    /// wrapped without touching Jellyfin itself.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <summary>How long playback may wait for a reset item to be restored from cache before the probe is kept.</summary>
        internal static readonly TimeSpan RestoreWait = TimeSpan.FromSeconds(5);

        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            TryDecorateMediaSourceManager(serviceCollection);
        }

        /// <summary>
        /// Replaces Jellyfin's <see cref="IMediaSourceManager"/> registration with the wrapper. Jellyfin's
        /// implementation stays a container-owned singleton, so exactly one instance exists and it holds all live
        /// stream state. Anything unexpected leaves the registrations untouched.
        /// </summary>
        /// <returns>True when the wrapper was registered.</returns>
        public static bool TryDecorateMediaSourceManager(IServiceCollection services)
        {
            try
            {
                if (services == null)
                {
                    return false;
                }

                var original = services.LastOrDefault(d => d.ServiceType == typeof(IMediaSourceManager));
                if (original == null || original.IsKeyedService || original.Lifetime != ServiceLifetime.Singleton)
                {
                    return false;
                }

                var implementation = original.ImplementationType;
                if (implementation == null || implementation == typeof(StrmPlaybackMediaSourceManager)
                    || !typeof(IMediaSourceManager).IsAssignableFrom(implementation))
                {
                    return false;
                }

                services.TryAddSingleton(implementation);
                services.AddSingleton<IMediaSourceManager>(provider =>
                    Wrap((IMediaSourceManager)provider.GetRequiredService(implementation), provider));
                services.Remove(original);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IMediaSourceManager Wrap(IMediaSourceManager inner, IServiceProvider provider)
        {
            ILogger logger = NullLogger.Instance;
            try
            {
                logger = provider.GetService<ILoggerFactory>()?.CreateLogger<StrmPlaybackMediaSourceManager>() ?? logger;
                var wrapped = Create(inner, logger);
                logger.LogInformation("Playback probe gate installed over {Inner}", inner.GetType().FullName);
                return wrapped;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Playback probe gate unavailable; Jellyfin's media source manager is used unchanged");
                return inner;
            }
        }

        // Kept out of line so that a failure to load the wrapper type surfaces inside Wrap's try block.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IMediaSourceManager Create(IMediaSourceManager inner, ILogger logger)
        {
            return new StrmPlaybackMediaSourceManager(
                inner,
                logger,
                () => Plugin.Instance?.Configuration,
                (item, token) => ItemUpdateListener.Current?.TryRestoreNowAsync(item, RestoreWait, token) ?? Task.FromResult(false));
        }
    }
}
