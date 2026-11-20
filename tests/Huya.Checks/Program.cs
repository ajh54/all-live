using AllLive.Core;
using System.Web;
using System.Text;
using System.Security.Cryptography;

void Check(bool value, string description)
{
    if (!value) throw new Exception(description);
    Console.WriteLine("PASS " + description);
}
string Md5(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
const long uid = 0x12312345678;
const long expectedRotatedUid = 0x12334567812;
var fm = Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes("secret_$0_$1_$2_$3")));
foreach (var platform in new[] { "100", "103" })
{
    var token = $"fm={fm}&ctype=huya_pc_exe&t={platform}&wsTime=6fffffff&fs=bgct";
    var signed = HttpUtility.ParseQueryString(Huya.BuildAntiCode("example", uid, token));
    var actualUid = platform == "103" ? uid : expectedRotatedUid;
    Check(signed[platform == "103" ? "uid" : "u"] == actualUid.ToString(), "UID preserved for platform " + platform);
    var inner = Md5($"{signed["seqid"]}|huya_pc_exe|{platform}");
    Check(signed["wsSecret"] == Md5($"secret_{actualUid}_example_{inner}_6fffffff"), "signature verified independently for platform " + platform);
    Check(signed["fm"] == Uri.UnescapeDataString(fm), "fm survives query encoding");
}
Check(Huya.BuildAntiCode("example", uid, "wsSecret=ready") == "wsSecret=ready", "already signed token preserved");
