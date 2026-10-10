using AllLive.Core.Helper;
using AllLive.Core.Interface;
using AllLive.Core.Models;
using AllLive.Core.Models.Tars;
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
using Tup.Tars;
using WebSocketSharp;


/*
* 虎牙弹幕实现
* 参考项目：
* https://github.com/BacooTang/huya-danmu
* https://github.com/IsoaSFlus/danmaku
*/
namespace AllLive.Core.Danmaku
{
    public class HuyaDanmakuArgs
    {
        public HuyaDanmakuArgs(long ayyuid, long topSid, long subSid)
        {
            this.Ayyuid = ayyuid;
            this.SubSid = subSid;
            this.TopSid = topSid;
        }
        public long Ayyuid { get; set; }
        public long TopSid { get; set; }
        public long SubSid { get; set; }
    }
    public class HuyaDanmaku : ILiveDanmaku
    {
        // 虎牙服务端 60 秒收不到心跳就直接断开（实测 close code 1006）。
        // 间隔必须明显小于这条死线：按 60 秒发的话，心跳会因为定时器精度、
        // 调度延迟和网络 RTT 而卡在死线上晚到几毫秒，连接每次都会被踢掉。
        public int HeartbeatTime => 30 * 1000;

        /// <summary>重连退避的起始间隔/秒</summary>
        private const int ReconnectBaseDelay = 2;
        /// <summary>重连退避的最大间隔/秒</summary>
        private const int ReconnectMaxDelay = 30;

        public event EventHandler<LiveMessage> NewMessage;
        public event EventHandler<string> OnClose;
        readonly byte[] heartBeatData;
        private readonly string ServerUrl = "wss://cdnws.api.huya.com";
        Timer timer;
        WebSocket ws;
        HuyaDanmakuArgs args;
        private readonly object reconnectLock = new object();
        private volatile bool stopped;
        private bool reconnecting;
        private int retryCount;
        public HuyaDanmaku()
        {
            heartBeatData = Convert.FromBase64String("ABQdAAwsNgBM");
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
                ws.Send(JoinData(args.Ayyuid, args.TopSid, args.SubSid));

            });
            timer.Start();

        }
        private void Ws_OnMessage(object sender, MessageEventArgs e)
        {
            try
            {
                var stream = new TarsInputStream(e.RawData);
                var type = stream.Read(0, 0, false);
                if (type == 7)
                {
                    stream = new TarsInputStream(stream.Read(new byte[0], 1, false));
                    HYPushMessage wSPushMessage = new HYPushMessage();
                    wSPushMessage.ReadFrom(stream);
                    if (wSPushMessage.Uri == 1400)
                    {

                        HYMessage messageNotice = new HYMessage();
                        messageNotice.ReadFrom(new TarsInputStream(wSPushMessage.Msg));
                        var uname = messageNotice.UserInfo.NickName;
                        var content = messageNotice.Content;
                        var color = messageNotice.BulletFormat.FontColor;
                        NewMessage?.Invoke(this, new LiveMessage()
                        {
                            Type = LiveMessageType.Chat,
                            Message = content,
                            UserName = uname,
                            Color = color <= 0 ? DanmakuColor.White : new DanmakuColor(color),
                        });

                    }
                    if (wSPushMessage.Uri == 8006)
                    {
                        long online = 0;
                        var s = new TarsInputStream(wSPushMessage.Msg);
                        online = s.Read(online, 0, false);
                        NewMessage?.Invoke(this, new LiveMessage()
                        {
                            Type = LiveMessageType.Online,
                            Data = online,
                        });
                    }
                }
                else if (type == 22)
                {
                    Debug.WriteLine($"收到消息:[Type:{type}]");
                    stream = new TarsInputStream(stream.Read(new byte[0], 1, false));
                    HYPushMessageV2 wSPushMessage = new HYPushMessageV2();
                    wSPushMessage.ReadFrom(stream);
                    foreach (var item in wSPushMessage.MsgItem)
                    {
                        if (item.Uri == 1400)
                        {
                            HYMessage messageNotice = new HYMessage();
                            messageNotice.ReadFrom(new TarsInputStream(item.Msg));
                            var uname = messageNotice.UserInfo.NickName;
                            var content = messageNotice.Content;
                            var color = messageNotice.BulletFormat.FontColor;
                            NewMessage?.Invoke(this, new LiveMessage()
                            {
                                Type = LiveMessageType.Chat,
                                Message = content,
                                UserName = uname,
                                Color = color <= 0 ? DanmakuColor.White : new DanmakuColor(color),
                            });

                        }
                        if (item.Uri == 8006)
                        {
                            long online = 0;
                            var s = new TarsInputStream(item.Msg);
                            online = s.Read(online, 0, false);
                            NewMessage?.Invoke(this, new LiveMessage()
                            {
                                Type = LiveMessageType.Online,
                                Data = online,
                            });
                        }
                    }
                  
                }
            }
            catch (Exception)
            {
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
        /// 连上后 Ws_OnOpen 会重新发送进房信息并重启心跳。
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
                    ws.Send(heartBeatData);
                }
                catch (Exception)
                {
                }
            });
        }

        public async Task Start(object args)
        {
            this.args = (HuyaDanmakuArgs)args;
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

        private byte[] JoinData(long ayyuid, long tid, long sid)
        {
            var oos = new TarsOutputStream();
            oos.Write(ayyuid, 0);
            oos.Write(true, 1);
            oos.Write("", 2);
            oos.Write("", 3);
            oos.Write(tid, 4);
            oos.Write(sid, 5);
            oos.Write(0, 6);
            oos.Write(0, 7);

            var wscmd = new TarsOutputStream();
            wscmd.Write(1, 0);
            wscmd.Write(oos.toByteArray(), 1);
            return wscmd.toByteArray();


        }
    }



}
