using AllLive.Core;
using AllLive.UWP.Helper;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Security.Cryptography;

if (!args.Contains("--live")) { Console.WriteLine("Use --live [channel] to test native playback for 120 seconds."); return; }
var channel = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "ohnepixel";
var site = new Twitch();
var room = await site.GetRoomDetail(channel);
var qualities = await site.GetPlayQuality(room);
using var source = await TwitchPlaybackSource.CreateAsync((string)qualities.First().Data, Console.WriteLine);
using var player = new MediaPlayer { IsMuted = true, IsVideoFrameServerEnabled = true, AutoPlay = true };
using var surface = VideoFrame.CreateAsDirect3D11SurfaceBacked(DirectXPixelFormat.B8G8R8A8UIntNormalized, 320, 180);
using var cpuFrame = new VideoFrame(BitmapPixelFormat.Bgra8, 320, 180);
var item = new MediaPlaybackItem(source.Source);
int frames = 0, failures = 0;
player.VideoFrameAvailable += (_, _) => Interlocked.Increment(ref frames);
player.MediaFailed += (_, e) => { Interlocked.Increment(ref failures); Console.WriteLine("MediaFailed " + e.Error); };
player.Source = item;
player.Play();
var hashes = new HashSet<string>();
double lastPosition = 0;
int stagnant = 0;
for (var i = 0; i < 24; i++)
{
    await Task.Delay(5000);
    if (failures > 0) throw new Exception("Native player failed");
    player.CopyFrameToVideoSurface(surface.Direct3DSurface);
    await surface.CopyToAsync(cpuFrame);
    var buffer = new Windows.Storage.Streams.Buffer(320 * 180 * 4);
    cpuFrame.SoftwareBitmap.CopyToBuffer(buffer);
    var pixels = new byte[buffer.Length];
    using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
    hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
    var position = player.PlaybackSession.Position.TotalSeconds;
    stagnant = position > lastPosition + 1 ? 0 : stagnant + 1;
    lastPosition = position;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { seconds = (i + 1) * 5, frames, changedFrames = hashes.Count, audioTracks = item.AudioTracks.Count, state = player.PlaybackSession.PlaybackState.ToString(), position }));
    if (stagnant >= 3) throw new Exception("Playback stopped advancing for 15 seconds");
}
player.Pause();
player.Source = null;
if (frames < 500 || hashes.Count < 10 || item.AudioTracks.Count == 0) throw new Exception("Missing continuous video or audio track");
source.Dispose(); // Explicit cleanup and the using scope must be safe together.
Console.WriteLine("PASS native HLS continuous frames, progress, audio track and cleanup");
