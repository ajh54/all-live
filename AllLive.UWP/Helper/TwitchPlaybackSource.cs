using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Streaming.Adaptive;
using HttpClient = Windows.Web.Http.HttpClient;

namespace AllLive.UWP.Helper
{
    /// <summary>Owns the native HLS reader and its HTTP client for one playback session.</summary>
    public sealed class TwitchPlaybackSource : IDisposable
    {
        public MediaSource Source { get; }
        public AdaptiveMediaSource Adaptive { get; }
        private readonly HttpClient http;
        private readonly Action<string> log;
        private bool disposed;

        private TwitchPlaybackSource(AdaptiveMediaSource adaptive, HttpClient client, Action<string> logger)
        {
            Adaptive = adaptive;
            http = client;
            log = logger;
            Source = MediaSource.CreateFromAdaptiveMediaSource(adaptive);
            Adaptive.DownloadFailed += OnDownloadFailed;
        }

        public static async Task<TwitchPlaybackSource> CreateAsync(string url, Action<string> log)
        {
            var client = new HttpClient();
            AdaptiveMediaSource adaptive = null;
            try
            {
                // Windows HTTP uses the existing system proxy, including for HLS segments.
                client.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", AllLive.Core.Twitch.UserAgent);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                {
                    var result = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(url), client).AsTask(timeout.Token);
                    adaptive = result.MediaSource;
                    if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
                        throw new InvalidOperationException("Twitch 原生 HLS 初始化失败：" + result.Status);
                }
                return new TwitchPlaybackSource(adaptive, client, log);
            }
            catch
            {
                adaptive?.Dispose();
                client.Dispose();
                throw;
            }
        }

        private void OnDownloadFailed(AdaptiveMediaSource sender, AdaptiveMediaSourceDownloadFailedEventArgs args)
        {
            // Never log signed resource URLs or playback tokens.
            if (!disposed) log?.Invoke("Twitch 原生 HLS 下载失败，资源类型 " + args.ResourceType);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Adaptive.DownloadFailed -= OnDownloadFailed;
            Source.Dispose();
            Adaptive.Dispose();
            http.Dispose();
        }
    }
}
