using AllLive.Core.Helper;
using AllLive.Core.Interface;
using AllLive.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using WebSocketSharp;
/*
* 斗鱼弹幕实现
* 参考项目：
* https://github.com/IsoaSFlus/danmaku
* https://www.cnblogs.com/sdflysha/p/20210117-douyu-barrage-with-dotnet.html
* 
* 斗鱼如果使用System.Net.WebSockets.ClientWebSocket,30秒后会被关闭连接...
*/

namespace AllLive.Core.Danmaku
{
    public class DouyuDanmaku : ILiveDanmaku
    {
        // 斗鱼官方《弹幕服务器第三方接入协议》要求 45 秒内发一次心跳。
        // 实测服务端容忍度远高于此（完全不发心跳也能活过 90 秒），所以斗鱼断连
        // 不是心跳问题，这里保持官方值即可；真正的改进是下面「响应 pingreq」。
        public int HeartbeatTime => 45 * 1000;

        /// <summary>重连退避的起始间隔/秒</summary>
        private const int ReconnectBaseDelay = 2;
        /// <summary>重连退避的最大间隔/秒</summary>
        private const int ReconnectMaxDelay = 30;

        public event EventHandler<LiveMessage> NewMessage;
        public event EventHandler<string> OnClose;
        private readonly string ServerUrl = "wss://danmuproxy.douyu.com:8506";
        Timer timer;
        WebSocket ws;
        string roomId;
        private readonly object reconnectLock = new object();
        private volatile bool stopped;
        private bool reconnecting;
        private int retryCount;
        public DouyuDanmaku()
        {
            ws = CreateSocket();
            timer = new Timer(HeartbeatTime);
            timer.Elapsed += Timer_Elapsed;

        }

        private WebSocket CreateSocket()
        {
            var socket = new WebSocket(ServerUrl);
            socket.OnOpen += Ws_OnOpen;
            socket.OnError += Ws_OnError;
            socket.OnMessage += Ws_OnMessage;
            socket.OnClose += Ws_OnClose;
            return socket;
        }
        private async void Ws_OnOpen(object sender, EventArgs e)
        {
            await Task.Run(() =>
            {
                //发送进房信息
                ws.Send(SerializeDouyu($"type@=loginreq/roomid@={roomId}/"));
                ws.Send(SerializeDouyu($"type@=joingroup/rid@={roomId}/gid@=-9999/"));
            });
            timer.Start();

        }
        private void Ws_OnMessage(object sender, MessageEventArgs e)
        {
            // 一帧里可能粘着多个 STT 包（实测最多 16 个），必须逐个解析；
            // 原来只读第一个包就返回，一帧里其余的弹幕会被全部丢掉。
            foreach (var result in DeserializeDouyuAll(e.RawData))
            {
                try
                {
                    var json = SttToJObject(result);
                    var type = json["type"]?.ToString();
                    switch (type)
                    {
                        // 服务端下发的 pingreq：需要回一次心跳，否则可能被判超时
                        case "pingreq":
                            Heartbeat();
                            break;
                        //斗鱼好像不会返回人气值
                        //有些直播间存在阴间弹幕，不知道什么情况
                        case "chatmsg":
                            // 屏蔽机器人弹幕
                            if (json["dms"] == null)
                            {
                                break;
                            }
                            NewMessage?.Invoke(this, new LiveMessage()
                            {
                                UserName = json["nn"].ToString(),
                                Message = json["txt"].ToString(),
                                Color = GetColor(json["col"].ToInt32())
                            });
                            break;
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private DanmakuColor GetColor(int type)
        {
            switch (type)
            {
                case 1:
                    return DanmakuColor.Red;
                case 2:
                    return DanmakuColor.FromRgb(30, 135, 240);
                case 3:
                    return DanmakuColor.FromRgb(122, 200, 75);
                case 4:
                    return DanmakuColor.FromRgb(255, 127, 0);
                case 5:
                    return DanmakuColor.FromRgb(155, 57, 244);
                case 6:
                    return DanmakuColor.FromRgb(255, 105, 180);
                default:
                    return DanmakuColor.White;
            }
        }

        private void Ws_OnClose(object sender, CloseEventArgs e)
        {
            // 忽略已经被替换掉的旧连接（重连过程中它会触发一次关闭事件）
            if (stopped || !ReferenceEquals(sender, ws))
            {
                return;
            }
            OnClose?.Invoke(this, string.IsNullOrEmpty(e.Reason) ? "连接已经关闭" : $"连接已经关闭：{e.Reason}");
            _ = Reconnect();
        }

        private void Ws_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            if (stopped || !ReferenceEquals(sender, ws))
            {
                return;
            }
            OnClose?.Invoke(this, "连接出错：" + e.Message);
            _ = Reconnect();
        }

        /// <summary>
        /// 断线自动重连：停掉心跳 -> 退避等待 -> 换新连接重连。
        /// 连上后 Ws_OnOpen 会重新发送 loginreq/joingroup 并重启心跳。
        /// </summary>
        private async Task Reconnect()
        {
            if (stopped || !BeginReconnect())
            {
                return;
            }
            try
            {
                while (!stopped)
                {
                    timer.Stop();
                    var delay = Math.Min(ReconnectMaxDelay, ReconnectBaseDelay * ++retryCount);
                    OnClose?.Invoke(this, $"连接已断开，{delay} 秒后自动重连");
                    await Task.Delay(delay * 1000).ConfigureAwait(false);
                    if (stopped)
                    {
                        return;
                    }
                    try
                    {
                        var old = ws;
                        ws = CreateSocket();
                        try
                        {
                            old?.Close();
                        }
                        catch (Exception)
                        {
                        }
                        await Task.Run(() => ws.Connect()).ConfigureAwait(false);
                        retryCount = 0;
                        return;
                    }
                    catch (Exception ex)
                    {
                        OnClose?.Invoke(this, "重连失败：" + ex.Message);
                    }
                }
            }
            finally
            {
                EndReconnect();
            }
        }

        private bool BeginReconnect()
        {
            lock (reconnectLock)
            {
                if (reconnecting)
                {
                    return false;
                }
                reconnecting = true;
                return true;
            }
        }

        private void EndReconnect()
        {
            lock (reconnectLock)
            {
                reconnecting = false;
            }
        }

        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            Heartbeat();
        }

        public async void Heartbeat()
        {
            if (stopped)
            {
                return;
            }
            await Task.Run(() =>
            {
                try
                {
                    ws.Send(SerializeDouyu($"type@=mrkl/"));
                }
                catch (Exception)
                {
                }
            });
        }

        public async Task Start(object args)
        {
            this.roomId = args.ToString();
            stopped = false;
            retryCount = 0;
            await Task.Run(() =>
            {
                ws.Connect();
            });
        }

        public async Task Stop()
        {
            stopped = true;
            timer.Stop();
            await Task.Run(() =>
            {
                try
                {
                    ws.Close();
                }
                catch (Exception)
                {
                }
            });
        }

        private byte[] SerializeDouyu(string body)
        {
            const short ClientSendToServer = 689;
            const byte Encrypted = 0;
            const byte Reserved = 0;

            byte[] bodyBuffer = Encoding.UTF8.GetBytes(body);
            using (var ms = new MemoryStream(bodyBuffer.Length + 13))
            {
                using (var writer = new BinaryWriter(ms))
                {
                    // 长度字段必须是 UTF-8 字节数：body.Length 是字符数，
                    // 遇到非 ASCII 内容（中文等）会算错导致服务端解析失败
                    var packetLength = 4 + 4 + bodyBuffer.Length + 1;
                    writer.Write(packetLength);
                    writer.Write(packetLength);
                    writer.Write(ClientSendToServer);
                    writer.Write(Encrypted);
                    writer.Write(Reserved);
                    writer.Write(bodyBuffer);
                    writer.Write((byte)0);
                    writer.Flush();

                    return ms.ToArray();
                }
            }



        }

        /// <summary>
        /// 解析一帧数据里的全部 STT 包。
        /// 斗鱼服务端会把多个包粘在一个 WebSocket 帧里（实测最多 16 个），
        /// 必须按包头里的长度循环剥离，不能只读第一个。
        /// </summary>
        private List<string> DeserializeDouyuAll(byte[] bytes)
        {
            var result = new List<string>();
            if (bytes == null)
            {
                return result;
            }
            var offset = 0;
            while (offset + 12 <= bytes.Length)
            {
                int fullMsgLength = BitConverter.ToInt32(bytes, offset);
                // 包体长度 = 总长度 - (第二个长度字段4 + 包类型2 + 加密1 + 保留1 + 结尾1)
                if (fullMsgLength < 9 || offset + 4 + fullMsgLength > bytes.Length)
                {
                    break;
                }
                var bodyLength = fullMsgLength - 9;
                if (bodyLength > 0)
                {
                    result.Add(Encoding.UTF8.GetString(bytes, offset + 12, bodyLength));
                }
                offset += 4 + fullMsgLength;
            }
            return result;
        }
        //辣鸡STT
        private JToken SttToJObject(string str)
        {
            if (str.Contains("//"))
            {
                var result = new JArray();
                foreach (var field in str.Split(new[] { "//" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    result.Add(SttToJObject(field));
                }
                return result;
            }
            if (str.Contains("@="))
            {
                var result = new JObject();
                foreach (var field in str.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var tokens = field.Split(new[] { "@=" }, StringSplitOptions.None);
                    var k = tokens[0];
                    var v = UnscapeSlashAt(tokens[1]);
                    result[k] = SttToJObject(v);
                }
                return result;
            }
            else if (str.Contains("@A="))
            {
                return SttToJObject(UnscapeSlashAt(str));
            }
            else
            {
                return UnscapeSlashAt(str);
            }


        }
        private string UnscapeSlashAt(string str)
        {
            return str
                .Replace("@S", "/")
                .Replace("@A", "@");
        }

    }
}
