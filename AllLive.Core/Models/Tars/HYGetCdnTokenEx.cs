using Tup.Tars;

namespace AllLive.Core.Models.Tars
{
    // Wire schema follows xiaoyaocz/dart_simple_live (MIT).
    public class HYTokenUserId : TarsStruct
    {
        public override void ReadFrom(TarsInputStream input) { }
        public override void WriteTo(TarsOutputStream output)
        {
            output.Write(0L, 0);
            output.Write("", 1);
            output.Write("", 2);
            output.Write("pc_exe&7060000&official", 3);
            output.Write("", 4);
            output.Write(0, 5);
            output.Write("", 6);
            output.Write("", 7);
        }
    }

    public class HYGetCdnTokenExReq : TarsStruct
    {
        public string StreamName { get; set; } = "";
        public override void ReadFrom(TarsInputStream input) { }
        public override void WriteTo(TarsOutputStream output)
        {
            output.Write("", 0);
            output.Write(StreamName, 1);
            output.Write(0, 2);
            output.Write(new HYTokenUserId(), 3);
            output.Write(66, 4);
        }
    }

    public class HYGetCdnTokenExResp : TarsStruct
    {
        public string FlvToken { get; set; } = "";
        public override void ReadFrom(TarsInputStream input)
        {
            FlvToken = input.Read(FlvToken, 0, isRequire: false);
        }
        public override void WriteTo(TarsOutputStream output) { output.Write(FlvToken, 0); }
    }
}
