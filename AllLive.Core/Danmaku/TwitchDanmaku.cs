using AllLive.Core.Interface;
using AllLive.Core.Models;
using System;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Core.Danmaku
{
    // Anonymous read-only IRC connection. No user credential or outgoing chat.
    public class TwitchDanmaku : ILiveDanmaku
    {
        public event EventHandler<LiveMessage> NewMessage;
        public event EventHandler<string> OnClose;
        public int HeartbeatTime => 30000;
        private CancellationTokenSource cancellation;
        private Task receiving;
        public void Heartbeat() { } // Reply to server PING in the receive loop.

        public async Task Start(object args)
        {
            await Stop();
            var channel = Twitch.NormalizeChannel(args?.ToString());
            cancellation = new CancellationTokenSource();
            receiving = Receive(channel, cancellation.Token);
        }

        public async Task Stop()
        {
            var source = cancellation;
            cancellation = null;
            if (source == null) return;
            source.Cancel();
            try { if (receiving != null) await receiving; } catch (Exception) when (source.IsCancellationRequested) { }
            source.Dispose();
        }

        private async Task Receive(string channel, CancellationToken cancellationToken)
        {
            int delay = 2;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using (var socket = new ClientWebSocket())
                    {
                        socket.Options.Proxy = WebRequest.DefaultWebProxy;
                        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            connect.CancelAfter(TimeSpan.FromSeconds(20));
                            await socket.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), connect.Token);
                        }
                        foreach (var command in new[] { "CAP REQ :twitch.tv/tags twitch.tv/commands", "PASS SCHMOOPIIE",
                            "NICK justinfan" + new Random().Next(100000, 999999), "JOIN #" + channel })
                            await Send(socket, command + "\r\n", cancellationToken);
                        var buffer = new byte[8192];
                        var pending = new StringBuilder();
                        var decoder = Encoding.UTF8.GetDecoder();
                        var chars = new char[8192];
                        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                        {
                            var read = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                            if (read.MessageType == WebSocketMessageType.Close) break;
                            if (read.MessageType != WebSocketMessageType.Text) continue;
                            pending.Append(chars, 0, decoder.GetChars(buffer, 0, read.Count, chars, 0));
                            string text = pending.ToString();
                            int end;
                            bool reconnect = false;
                            while ((end = text.IndexOf("\r\n", StringComparison.Ordinal)) >= 0)
                            {
                                var line = text.Substring(0, end);
                                text = text.Substring(end + 2);
                                if (line.StartsWith("PING ")) await Send(socket, "PONG " + line.Substring(5) + "\r\n", cancellationToken);
                                if (line.Contains(" RECONNECT")) { reconnect = true; break; }
                                if (line.Contains(" 001 ")) delay = 2;
                                var message = ParseMessage(line);
                                if (message != null) NewMessage?.Invoke(this, message);
                            }
                            pending.Clear(); pending.Append(text);
                            if (pending.Length > 65536) throw new FormatException("IRC line too long");
                            if (reconnect) break;
                        }
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    OnClose?.Invoke(this, "Twitch 弹幕连接中断，正在重连");
                }
                if (cancellationToken.IsCancellationRequested) return;
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                delay = Math.Min(delay * 2, 30);
            }
        }

        private static Task Send(ClientWebSocket socket, string text, CancellationToken token)
            => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, token);

        public static LiveMessage ParseMessage(string line)
        {
            var match = Regex.Match(line, @"(?:^| ):(?<login>[^ !]+)![^ ]+ PRIVMSG #[^ ]+ :(?<text>.*)$");
            if (!match.Success) return null;
            var name = match.Groups["login"].Value;
            if (line.StartsWith("@"))
            {
                var tags = line.Substring(1, line.IndexOf(' ') - 1).Split(';');
                var display = tags.FirstOrDefault(t => t.StartsWith("display-name="));
                if (!string.IsNullOrEmpty(display?.Substring(13)))
                    name = Regex.Replace(display.Substring(13), @"\\(.)", m => m.Groups[1].Value == "s" ? " " : m.Groups[1].Value == ":" ? ";" : m.Groups[1].Value == "n" ? "\n" : m.Groups[1].Value == "r" ? "\r" : m.Groups[1].Value);
            }
            var message = match.Groups["text"].Value;
            if (message.StartsWith("\u0001ACTION ") && message.EndsWith("\u0001")) message = message.Substring(8, message.Length - 9);
            return new LiveMessage { Type = LiveMessageType.Chat, UserName = name, Message = message };
        }
    }
}
