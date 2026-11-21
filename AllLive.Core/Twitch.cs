using AllLive.Core.Interface;
using AllLive.Core.Models;
using AllLive.Core.Danmaku;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AllLive.Core
{
    public class Twitch : ILiveSite
    {
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36";
        private const string StreamFields = "title viewersCount previewImageURL(width:640,height:360) broadcaster { login displayName profileImageURL(width:150) }";
        private const string UserFields = "login displayName profileImageURL(width:150) stream { title viewersCount previewImageURL(width:640,height:360) }";
        private static readonly HttpClient http = CreateClient();
        public string Name => "Twitch";
        public ILiveDanmaku GetDanmaku() => new TwitchDanmaku();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            return client;
        }

        private static async Task<JToken> Query(string query, object variables = null)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql"))
            {
                // Public web-client identifier, not a user credential.
                request.Headers.Add("Client-ID", "kimne78kx3ncx6brgo4mv6wki5h1ko");
                request.Content = new StringContent(JsonConvert.SerializeObject(new { query, variables }), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request))
                {
                    response.EnsureSuccessStatusCode();
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                    if (json["errors"] is JArray errors && errors.Count > 0)
                        throw new InvalidOperationException("Twitch 接口返回错误：" + errors[0]["message"]);
                    return json["data"] ?? throw new InvalidOperationException("Twitch 返回数据为空");
                }
            }
        }

        public static string NormalizeChannel(string input)
        {
            input = (input ?? "").Trim();
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                if (uri.Host != "twitch.tv" && !uri.Host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("请输入 Twitch 频道名或频道链接");
                input = uri.AbsolutePath.Trim('/');
            }
            if (!Regex.IsMatch(input, "^[A-Za-z0-9_]{1,25}$") || new[] { "directory", "videos", "settings", "downloads", "search" }.Contains(input.ToLowerInvariant()))
                throw new ArgumentException("请输入 Twitch 频道名，例如 twitchdev");
            return input.ToLowerInvariant();
        }

        public async Task<List<LiveCategory>> GetCategores()
        {
            var data = await Query("{ games(first:100) { edges { node { id name boxArtURL(width:285,height:380) } } } }");
            return new List<LiveCategory> { new LiveCategory { ID = "twitch", Name = "热门分类", Children = data["games"]["edges"].Select(e => new LiveSubCategory {
                ID = (string)e["node"]["id"], Name = (string)e["node"]["name"], Pic = (string)e["node"]["boxArtURL"], ParentID = "twitch"
            }).ToList() } };
        }

        private static LiveRoomItem RoomItem(JToken user, JToken stream)
        {
            return new LiveRoomItem {
                RoomID = (string)user["login"], UserName = (string)user["displayName"],
                Title = (string)stream?["title"] ?? "未开播", Online = (int?)stream?["viewersCount"] ?? 0,
                Cover = (string)stream?["previewImageURL"] ?? (string)user["profileImageURL"]
            };
        }

        public async Task<LiveSearchResult> Search(string keyword, int page = 1)
        {
            if (page > 1) return new LiveSearchResult { Rooms = new List<LiveRoomItem>(), HasMore = false };
            if (keyword.IndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase) >= 0) keyword = NormalizeChannel(keyword);
            var data = await Query("query($term:String!) { searchFor(userQuery:$term,platform:\"web\") { channels { edges { item { ... on User { " + UserFields + " } } } } } }", new { term = keyword });
            var rooms = data["searchFor"]["channels"]["edges"].Select(e => e["item"]).Where(u => u?["login"] != null)
                .Select(u => RoomItem(u, u["stream"]?.Type == JTokenType.Null ? null : u["stream"])).ToList();
            return new LiveSearchResult { Rooms = rooms, HasMore = false };
        }

        public Task<LiveCategoryResult> GetRecommendRooms(int page = 1) => GetStreams(null, page);
        public Task<LiveCategoryResult> GetCategoryRooms(LiveSubCategory category, int page = 1) => GetStreams(category.ID, page);
        private async Task<LiveCategoryResult> GetStreams(string game, int page)
        {
            // Anonymous directory pagination can require Twitch's browser integrity check.
            // Expose the public first page only rather than repeatedly failing on Load More.
            if (page > 1)
                return new LiveCategoryResult { Rooms = new List<LiveRoomItem>(), HasMore = false };
            var selection = "streams(first:30) { edges { node { " + StreamFields + " } } }";
            var query = game == null ? "{ " + selection + " }" : "query($game:ID!) { game(id:$game) { " + selection + " } }";
            var data = await Query(query, game == null ? null : new { game });
            var edges = (game == null ? data["streams"] : data["game"]?["streams"])?["edges"] as JArray ?? new JArray();
            return new LiveCategoryResult { HasMore = false,
                Rooms = edges.Select(e => RoomItem(e["node"]["broadcaster"], e["node"])).ToList() };
        }

        public async Task<LiveRoomDetail> GetRoomDetail(object roomId)
        {
            var channel = NormalizeChannel(roomId?.ToString());
            var data = await Query("query($login:String!) { user(login:$login) { " + UserFields + " description broadcastSettings { title } } }", new { login = channel });
            var user = data["user"];
            if (user == null || user.Type == JTokenType.Null) throw new InvalidOperationException("Twitch 频道不存在");
            var stream = user["stream"]?.Type == JTokenType.Null ? null : user["stream"];
            var item = RoomItem(user, stream);
            return new LiveRoomDetail { RoomID = channel, Title = (string)stream?["title"] ?? (string)user["broadcastSettings"]?["title"] ?? "未开播",
                UserName = item.UserName, Cover = item.Cover, UserAvatar = (string)user["profileImageURL"], Online = item.Online,
                Status = stream != null, Introduction = (string)user["description"], Notice = "", Url = "https://www.twitch.tv/" + channel,
                Data = channel, DanmakuData = channel };
        }

        public async Task<List<LivePlayQuality>> GetPlayQuality(LiveRoomDetail roomDetail)
        {
            if (!roomDetail.Status) return new List<LivePlayQuality>();
            var channel = NormalizeChannel(roomDetail.RoomID);
            var data = await Query("query($login:String!) { streamPlaybackAccessToken(channelName:$login,params:{platform:\"web\",playerBackend:\"mediaplayer\",playerType:\"site\"}) { value signature } }", new { login = channel });
            var token = data["streamPlaybackAccessToken"];
            if (string.IsNullOrEmpty((string)token?["signature"])) throw new InvalidOperationException("Twitch 未提供播放凭据，该频道可能需要登录或订阅");
            var url = "https://usher.ttvnw.net/api/v2/channel/hls/" + channel + ".m3u8?allow_source=true&allow_audio_only=true&playlist_include_framerate=true&player=twitchweb&sig=" + Uri.EscapeDataString((string)token["signature"]) + "&token=" + Uri.EscapeDataString((string)token["value"]);
            using (var response = await http.GetAsync(url))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Twitch 播放列表不可用（HTTP " + (int)response.StatusCode + "），请检查节点或频道访问权限");
                return ParsePlaylist(await response.Content.ReadAsStringAsync(), new Uri(url));
            }
        }

        public static List<LivePlayQuality> ParsePlaylist(string playlist, Uri baseUri)
        {
            if (!playlist.TrimStart().StartsWith("#EXTM3U")) throw new FormatException("Twitch 播放列表格式无效");
            var result = new List<LivePlayQuality>();
            string pending = null;
            foreach (var raw in playlist.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF:")) { pending = line; continue; }
                if (pending == null || line.Length == 0 || line.StartsWith("#")) continue;
                var resolution = Regex.Match(pending, @"RESOLUTION=\d+x(\d+)");
                var group = Regex.Match(pending, "VIDEO=\"([^\"]+)\"");
                var quality = resolution.Success ? resolution.Groups[1].Value + "p" : "仅音频";
                var fps = Regex.Match(pending, @"FRAME-RATE=([\d.]+)");
                if (fps.Success && double.TryParse(fps.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate) && rate > 30) quality += "60";
                if (group.Groups[1].Value == "chunked") quality += "（原画）";
                var uri = new Uri(baseUri, line);
                if (uri.Scheme != "https" && uri.Scheme != "http") throw new FormatException("不支持的播放地址");
                result.Add(new LivePlayQuality { Quality = quality, Data = uri.AbsoluteUri, Sort = result.Count });
                pending = null;
            }
            if (result.Count == 0) throw new InvalidOperationException("Twitch 暂未提供可播放画质");
            return result;
        }

        public Task<List<string>> GetPlayUrls(LiveRoomDetail roomDetail, LivePlayQuality qn) => Task.FromResult(new List<string> { (string)qn.Data });
        public async Task<bool> GetLiveStatus(object roomId) => (await GetRoomDetail(roomId)).Status;
        public Task<List<LiveSuperChatMessage>> GetSuperChatMessages(object roomId) => Task.FromResult(new List<LiveSuperChatMessage>());
    }
}
