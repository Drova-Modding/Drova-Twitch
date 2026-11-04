using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using MelonLoader;

namespace TwitchMod.Integration
{
    /// <summary>
    /// Reads a channel's chat anonymously over Twitch IRC, so chat commands work without the Affiliate status that
    /// channel points need. Reconnects until disposed.
    /// </summary>
    internal sealed class TwitchChat : IDisposable
    {
        private const string Server = "wss://irc-ws.chat.twitch.tv:443";

        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
        private static readonly Regex ChatLine = new(@"^:(\w+)!\S+ PRIVMSG \S+ :(.*)$");

        private readonly CancellationTokenSource _stop = new();

        /// <summary>The channel's login name.</summary>
        public string Channel { get; }

        /// <summary>Chat messages by the sender's login name, filled from a background thread.</summary>
        public ConcurrentQueue<(string Viewer, string Text)> Messages { get; } = new();

        /// <summary>Starts reading <paramref name="channel"/>'s chat.</summary>
        public TwitchChat(string channel)
        {
            Channel = channel;
            _ = Run(channel.ToLowerInvariant());
        }

        /// <summary>Stops reading.</summary>
        public void Dispose() => _stop.Cancel();

        /// <summary>Reads one IRC line as a chat message.</summary>
        public static bool TryParse(string line, [NotNullWhen(true)] out string? viewer, [NotNullWhen(true)] out string? text)
        {
            Match match = ChatLine.Match(line);
            viewer = match.Success ? match.Groups[1].Value : null;
            text = match.Success ? match.Groups[2].Value : null;
            return match.Success;
        }

        private async Task Run(string channel)
        {
            byte[] buffer = new byte[4096];
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using ClientWebSocket socket = new();
                    await socket.ConnectAsync(new Uri(Server), _stop.Token).ConfigureAwait(false);
                    // justinfan with any number is Twitch's anonymous read-only login.
                    await Send(socket, "NICK justinfan" + Random.Shared.Next(10000, 100000)).ConfigureAwait(false);
                    await Send(socket, "JOIN #" + channel).ConfigureAwait(false);
                    MelonLogger.Msg("[Twitch] Reading chat commands in #" + channel + ".");

                    using MemoryStream message = new();
                    while (true)
                    {
                        WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _stop.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                            throw new EndOfStreamException("chat closed the connection");

                        message.Write(buffer, 0, result.Count);
                        if (!result.EndOfMessage)
                            continue;

                        string lines = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                        message.SetLength(0);
                        foreach (string line in lines.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (line.StartsWith("PING", StringComparison.Ordinal))
                                await Send(socket, "PONG" + line[4..]).ConfigureAwait(false);
                            else if (TryParse(line, out string? viewer, out string? text))
                                Messages.Enqueue((viewer, text));
                        }
                    }
                }
                catch (Exception e)
                {
                    if (_stop.IsCancellationRequested)
                        return;

                    MelonLogger.Warning("[Twitch] Chat disconnected, retrying in " + RetryDelay.TotalSeconds + " s: " + e.Message);
                    await Task.Delay(RetryDelay, _stop.Token).ContinueWith(_ => { }).ConfigureAwait(false);
                }
            }
        }

        private Task Send(ClientWebSocket socket, string line)
        {
            return socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(line + "\r\n")), WebSocketMessageType.Text, true, _stop.Token);
        }
    }
}
