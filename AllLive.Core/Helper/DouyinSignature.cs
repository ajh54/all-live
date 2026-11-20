using Jint;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AllLive.Core.Helper
{
    public static class DouyinSignature
    {
        private static readonly Lazy<string> Script = new Lazy<string>(() =>
        {
            using (var stream = typeof(DouyinSignature).Assembly.GetManifestResourceStream("AllLive.Core.Scripts.douyin-abogus.js"))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("Missing Douyin signing resource")))
                return reader.ReadToEnd();
        });

        public static string SignUrl(string url, string userAgent)
        {
            var uri = new Uri(url);
            var query = string.Join("&", uri.Query.TrimStart('?').Split('&').Where(p =>
                p.Length > 0 && !p.StartsWith("a_bogus=", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("msToken=", StringComparison.OrdinalIgnoreCase)));
            var bytes = new byte[80];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            query += (query.Length == 0 ? "" : "&") + "msToken=" + Uri.EscapeDataString(token);
            var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(5)));
            engine.Execute(Script.Value);
            var signature = engine.Invoke("getABogus", query, userAgent).AsString();
            if (string.IsNullOrWhiteSpace(signature)) throw new InvalidOperationException("抖音请求签名生成失败");
            return uri.GetLeftPart(UriPartial.Path) + "?" + query + "&a_bogus=" + Uri.EscapeDataString(signature);
        }
    }
}
