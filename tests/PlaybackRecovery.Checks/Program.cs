using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Runtime.Loader;

// Exercise the actual production methods without requiring a UWP runtime.
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var vm = Parse("AllLive.UWP/ViewModels/LiveRoomVM.cs");
var page = Parse("AllLive.UWP/Views/LiveRoomPage.xaml.cs");
string Method(SyntaxNode node, string name) => node.DescendantNodes().OfType<MethodDeclarationSyntax>()
    .Single(m => m.Identifier.Text == name).ToFullString();
SyntaxNode Parse(string relative)
{
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, relative)));
    var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length > 0) throw new Exception(string.Join("\n", errors.Select(d => d.ToString())));
    return tree.GetRoot();
}
var source = """
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
public class LiveRoomDetail { public bool Status = true; public object Data; }
public class LivePlayQuality { public string Quality; public object Data; }
public class PlayurlLine { public string Name; public string Url; }
public enum LogType { INFO, ERROR }
public static class LogHelper { public static void Log(string s, LogType t, Exception e = null) {} }
public class FakeSite
{
    public Func<Task<LiveRoomDetail>> FetchDetail;
    public Func<LiveRoomDetail,Task<List<LivePlayQuality>>> FetchQuality;
    public Func<LiveRoomDetail,LivePlayQuality,Task<List<string>>> FetchUrls;
    public Task<LiveRoomDetail> GetRoomDetail(object id) => FetchDetail();
    public Task<List<LivePlayQuality>> GetPlayQuality(LiveRoomDetail d) => FetchQuality(d);
    public Task<List<string>> GetPlayUrls(LiveRoomDetail d, LivePlayQuality q) => FetchUrls(d,q);
}
public class VM
{
    public FakeSite Site = new FakeSite(); public object RoomId = "room";
    public int roomLoadVersion; public bool Living = true;
    public LiveRoomDetail detail = new LiveRoomDetail { Data = "expired" };
    public LivePlayQuality currentQuality = new LivePlayQuality { Quality = "HD", Data = "old-cdn" };
    public PlayurlLine currentLine;
    public List<PlayurlLine> Lines = new List<PlayurlLine> { new PlayurlLine { Url = "old-url" }, new PlayurlLine { Url = "old-backup" } };
    public List<LivePlayQuality> Qualities;
    public LivePlayQuality CurrentQuality => currentQuality;
    public PlayurlLine CurrentLine => currentLine;
    public VM() { currentLine = Lines[0]; }
    public void DoPropertyChanged(string name) {}
""" + Method(vm, "RefreshCurrentLineUrlAsync") + """
}
public class Player
{
    public VM liveRoomVM = new VM(); public int playbackVersion; public int replayCount;
    public const int MaxReplayCount = 3; public bool playbackStopped;
    public int SetCalls; public Task<bool> SetPlayer(string url) { SetCalls++; return Task.FromResult(true); }
    public Task<bool> Replay() => ReplayCurrentLineAsync();
""" + Method(page, "ReplayCurrentLineAsync") + """
}
public static class Checks
{
    static int passed;
    static void Check(bool ok, string name) { if(!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    static VM Ready()
    {
        var v = new VM();
        v.Site.FetchDetail = () => Task.FromResult(new LiveRoomDetail { Data = "fresh-signature" });
        v.Site.FetchQuality = d => {
            if(!Equals(d.Data,"fresh-signature")) throw new Exception("stale detail");
            return Task.FromResult(new List<LivePlayQuality> { new LivePlayQuality { Quality="HD",Data="fresh-cdn" } });
        };
        v.Site.FetchUrls = (d,q) => {
            if(!Equals(d.Data,"fresh-signature") || !Equals(q.Data,"fresh-cdn")) throw new Exception("stale auth or cdn");
            return Task.FromResult(new List<string> { "fresh-url", "fresh-backup" });
        };
        return v;
    }
    public static async Task Run()
    {
        var v = Ready();
        Check(await v.RefreshCurrentLineUrlAsync() == "fresh-url", "fresh room authentication and quality are used");
        Check(v.Lines[1].Url == "fresh-backup" && v.CurrentLine == v.Lines[0], "backup lines and selection updated");
        v = Ready(); v.Site.FetchDetail = () => throw new Exception("network failure");
        Check(await v.RefreshCurrentLineUrlAsync() == null && v.Living, "network failure never means offline");
        v = Ready(); v.Site.FetchDetail = () => Task.FromResult<LiveRoomDetail>(null);
        Check(await v.RefreshCurrentLineUrlAsync() == null && v.Living, "empty room response never means offline");
        v = Ready(); v.Site.FetchDetail = () => Task.FromResult(new LiveRoomDetail { Status=false });
        Check(await v.RefreshCurrentLineUrlAsync() == null && !v.Living, "explicit platform offline is respected");
        v = Ready(); v.currentQuality.Quality = "removed-quality";
        Check(await v.RefreshCurrentLineUrlAsync() == "fresh-url", "removed quality falls back to a fresh available quality");
        v = Ready(); v.Site.FetchUrls = (d,q) => Task.FromResult(new List<string>());
        Check(await v.RefreshCurrentLineUrlAsync() == null && v.Lines[0].Url == "old-url" && v.Living,
            "empty URL result preserves previous state");
        v = Ready(); var pending = new TaskCompletionSource<LiveRoomDetail>();
        v.Site.FetchDetail = () => pending.Task; var refresh = v.RefreshCurrentLineUrlAsync();
        v.currentLine = v.Lines[1]; pending.SetResult(new LiveRoomDetail());
        Check(await refresh == null && v.CurrentLine == v.Lines[1], "manual line change cancels stale refresh");
        v = Ready(); pending = new TaskCompletionSource<LiveRoomDetail>();
        v.Site.FetchDetail = () => pending.Task; refresh = v.RefreshCurrentLineUrlAsync(); v.roomLoadVersion++;
        pending.SetResult(new LiveRoomDetail());
        Check(await refresh == null && Equals(v.detail.Data,"expired"), "room reload cancels stale refresh");
        var p = new Player { liveRoomVM = Ready() };
        Check(await p.Replay() && p.SetCalls == 1, "refreshed URL reaches player");
        p = new Player { liveRoomVM = Ready() };
        p.liveRoomVM.Site.FetchDetail = () => throw new Exception("offline network");
        Check(!await p.Replay() && p.replayCount == 1 && p.SetCalls == 0, "failed fetch consumes bounded retry");
        p.replayCount = 3;
        Check(!await p.Replay() && p.replayCount == 3, "retry limit stops more requests");
        p = new Player { liveRoomVM = Ready() }; var replay = p.Replay(); p.playbackStopped = true;
        Check(await replay && p.SetCalls == 0, "leaving room cancels delayed playback");
        p = new Player { liveRoomVM = Ready() }; replay = p.Replay(); p.playbackVersion++;
        Check(await replay && p.SetCalls == 0, "manual refresh cancels delayed playback");
        Console.WriteLine(passed + " checks passed; both modified UWP files parsed successfully.");
    }
}
""";
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator)
    .Select(p => MetadataReference.CreateFromFile(p));
var compilation = CSharpCompilation.Create("RecoveryChecks", new[] { CSharpSyntaxTree.ParseText(source) }, references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
output.Position = 0;
var assembly = AssemblyLoadContext.Default.LoadFromStream(output);
await (Task)assembly.GetType("Checks")!.GetMethod("Run")!.Invoke(null, null)!;
