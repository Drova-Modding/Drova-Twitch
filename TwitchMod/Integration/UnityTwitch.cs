using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MelonLoader;
using TwitchSDK;
using TwitchSDK.Interop;
using UnityEngine;
using HttpMethod = System.Net.Http.HttpMethod;
using SdkHttpMethod = TwitchSDK.Interop.HttpMethod;

namespace TwitchMod.Integration
{
    /// <summary>
    /// Twitch SDK wired to MelonLoader logging, with token-safe HTTP, logging and storage.
    /// </summary>
    internal class UnityTwitch(string clientId, bool useEventSubProxy = false) : TwitchSDKApi(clientId, useEventSubProxy)
    {
        private MelonPAL? _pal;

        protected override PlatformAbstractionLayer CreatePAL() => _pal = new MelonPAL();

        /// <summary>
        /// Posts <paramref name="message"/> in the channel as the logged-in streamer. Does nothing before the SDK made its first Helix call.
        /// </summary>
        public Task SendChatMessage(string channelId, string message) => _pal?.SendChatMessage(channelId, message) ?? Task.CompletedTask;

        private sealed class MelonPAL : ManagedPAL
        {
            private const string KeyringService = "DrovaTwitchMod";
            private const string Helix = "https://api.twitch.tv/helix/";
            private readonly Dictionary<int, ClientWebSocket> _sockets = new();
            private int _nextSocket = 1;

            private readonly string _basePath = Application.persistentDataPath;
            private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

            /// <summary>The headers of the SDK's latest Helix call. The SDK has no chat call and keeps its token to itself.</summary>
            private volatile string? _helixAuthorization;
            private volatile string? _helixClientId;

            static MelonPAL()
            {
                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    // Disposing an EventStream faults its private pending wait with "terminating stream", which only means it closed.
                    if (e.Exception.InnerException is CoreLibraryException twitchException && twitchException.Message != "terminating stream")
                        MelonLogger.Warning("[Twitch] Unhandled exception: " + twitchException);
                };
            }

            protected override string HttpUserAgent => "Drova-Agent";

            /** ManagedPAL.WebRequest without its Console.WriteLine calls.
             * Those print the bearer token to the MelonLoader console, which streamers often show.
             */
            protected override async Task<WebRequestResult> WebRequest(WebRequestRequest req)
            {
                using HttpRequestMessage message = new();
                message.RequestUri = new Uri(req.Uri);
                message.Method = req.Method switch
                {
                    SdkHttpMethod.Get => HttpMethod.Get,
                    SdkHttpMethod.Post => HttpMethod.Post,
                    SdkHttpMethod.Put => HttpMethod.Put,
                    SdkHttpMethod.Patch => HttpMethod.Patch,
                    SdkHttpMethod.Delete => HttpMethod.Delete,
                    _ => throw new ArgumentOutOfRangeException(nameof(req), req.Method, "Unsupported HTTP method."),
                };
                if (req.Method is SdkHttpMethod.Post or SdkHttpMethod.Put or SdkHttpMethod.Patch)
                    message.Content = new StringContent(req.RequestBody, Encoding.UTF8, req.ContentType);
                if (!string.IsNullOrEmpty(req.ClientId))
                    message.Headers.Add("Client-Id", req.ClientId);
                if (!string.IsNullOrEmpty(req.Authorization))
                {
                    message.Headers.Add("Authorization", req.Authorization);
                    if (req.Uri.StartsWith(Helix, StringComparison.Ordinal))
                    {
                        _helixAuthorization = req.Authorization;
                        _helixClientId = req.ClientId;
                    }
                }
                message.Headers.UserAgent.Add(new ProductInfoHeaderValue(HttpUserAgent, "0.2"));

                using HttpResponseMessage response = await _http.SendAsync(message).ConfigureAwait(false);
                return new WebRequestResult
                {
                    HttpStatus = (int)response.StatusCode,
                    ResponseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                };
            }

            public async Task SendChatMessage(string channelId, string text)
            {
                string? authorization = _helixAuthorization;
                string? clientId = _helixClientId;
                if (authorization == null || clientId == null)
                {
                    MelonLogger.Warning("[Twitch] Could not answer in chat: no Twitch API call seen yet.");
                    return;
                }

                using HttpRequestMessage message = new(HttpMethod.Post, Helix + "chat/messages");
                message.Headers.Add("Client-Id", clientId);
                message.Headers.Add("Authorization", authorization);
                message.Content = new StringContent(JsonSerializer.Serialize(new { broadcaster_id = channelId, sender_id = channelId, message = text }),
                    Encoding.UTF8, "application/json");
                try
                {
                    using HttpResponseMessage response = await _http.SendAsync(message).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        MelonLogger.Warning("[Twitch] Could not answer in chat: " + (int)response.StatusCode);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    MelonLogger.Warning("[Twitch] Could not answer in chat: " + e.Message);
                }
            }

            /** ManagedPAL's websockets without its Console.WriteLine calls, which print every EventSub message,
             * keepalives included. ManagedPAL keeps its sockets private, so these four keep their own.
             */
            protected override async Task<int> CreateWebSocket(CreateWebSocketRequest req)
            {
                ClientWebSocket socket = new();
                int handle;
                lock (_sockets)
                {
                    handle = _nextSocket++;
                    _sockets[handle] = socket;
                }
                await socket.ConnectAsync(new Uri(req.Url), CancellationToken.None).ConfigureAwait(false);
                return handle;
            }

            protected override async Task SendWebSocketMessage(SendWebSocketMessageRequest req)
            {
                await GetSocket(req.Handle).SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(req.Message)),
                    WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }

            protected override async Task CloseWebSocket(CloseWebSocketRequest req)
            {
                ClientWebSocket socket = GetSocket(req.Handle);
                lock (_sockets)
                    _sockets.Remove(req.Handle);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "normal closure", CancellationToken.None).ConfigureAwait(false);
                socket.Dispose();
            }

            protected override void DisposeManaged()
            {
                base.DisposeManaged();
                lock (_sockets)
                {
                    foreach (ClientWebSocket socket in _sockets.Values)
                        socket.Dispose();
                    _sockets.Clear();
                }
            }

            private ClientWebSocket GetSocket(int handle)
            {
                lock (_sockets)
                    return _sockets[handle];
            }

            protected override async Task<string> RecvWebSocketMessage(RecvWebSocketMessageRequest req)
            {
                ClientWebSocket socket = GetSocket(req.Handle);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(req.TimeoutSeconds));
                using MemoryStream message = new();
                byte[] buffer = new byte[1024];
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new EndOfStreamException("websocket is closed");
                    if (result.MessageType != WebSocketMessageType.Text)
                        throw new InvalidOperationException("not receiving text");
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }

            /** The core stores its access and refresh token through ReadFile and WriteFile.
             * Windows and Proton encrypt them with DPAPI. Native Linux and macOS use the system keyring,
             * or an owner-only file when no keyring tool is installed.
            */
            protected override async Task<string> ReadFile(ReadFileRequest req)
            {
                if (!OperatingSystem.IsWindows())
                {
                    string? secret = await Task.Run(() => LoadFromKeyring(req.Path)).ConfigureAwait(false);
                    if (secret != null)
                        return secret;
                }

                try
                {
                    byte[] data = await File.ReadAllBytesAsync(Path.Combine(_basePath, req.Path)).ConfigureAwait(false);
                    if (OperatingSystem.IsWindows())
                        data = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(data);
                }
                catch (Exception e) when (e is FileNotFoundException or CryptographicException)
                {
                    return string.Empty;
                }
            }

            protected override async Task WriteFile(WriteFileRequest req)
            {
                string path = Path.Combine(_basePath, req.Path);
                byte[] data = Encoding.UTF8.GetBytes(req.Data);
                if (OperatingSystem.IsWindows())
                {
                    data = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                }
                else
                {
                    if (await Task.Run(() => StoreInKeyring(req.Path, req.Data)).ConfigureAwait(false))
                    {
                        File.Delete(path);
                        return;
                    }
                    File.Create(path).Dispose();
                    Process.Start("chmod", ["600", path])?.WaitForExit();
                }
                await File.WriteAllBytesAsync(path, data).ConfigureAwait(false);
            }

            private static string? LoadFromKeyring(string account)
            {
                return OperatingSystem.IsMacOS()
                    ? RunTool("security", ["find-generic-password", "-s", KeyringService, "-a", account, "-w"], null)?.TrimEnd('\n')
                    : RunTool("secret-tool", ["lookup", "service", KeyringService, "file", account], null)?.TrimEnd('\n');
            }

            /// <summary>
            /// Stores <paramref name="data"/> in the keyring, or clears the entry when it is empty.
            /// Returns false when the caller has to fall back to the file.
            /// </summary>
            private static bool StoreInKeyring(string account, string data)
            {
                bool macOS = OperatingSystem.IsMacOS();
                if (data.Length == 0)
                {
                    if (macOS)
                        RunTool("security", ["delete-generic-password", "-s", KeyringService, "-a", account], null);
                    else
                        RunTool("secret-tool", ["clear", "service", KeyringService, "file", account], null);
                    return false;
                }

                if (macOS)
                    RunTool("security", ["-i"], $"add-generic-password -U -s {KeyringService} -a \"{account}\" -X {Convert.ToHexString(Encoding.UTF8.GetBytes(data))}\n");
                else
                    RunTool("secret-tool", ["store", "--label=" + KeyringService, "service", KeyringService, "file", account], data);

                return LoadFromKeyring(account) == data;
            }

            /// <summary>
            /// Runs a command line tool and returns its output, or null when it is missing or fails.
            /// </summary>
            private static string? RunTool(string fileName, string[] arguments, string? input)
            {
                try
                {
                    ProcessStartInfo info = new(fileName)
                    {
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    foreach (string argument in arguments)
                        info.ArgumentList.Add(argument);
                    using Process? process = Process.Start(info);
                    if (process == null)
                        return null;

                    process.StandardInput.Write(input ?? string.Empty);
                    process.StandardInput.Close();
                    Task<string> error = process.StandardError.ReadToEndAsync();
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    error.Wait();
                    return process.ExitCode == 0 ? output : null;
                }
                catch (Win32Exception)
                {
                    return null;
                }
            }

            protected override Task Log(LogRequest req)
            {
                if (req.Level == LogLevel.Debug || req.Message.Contains("token", StringComparison.OrdinalIgnoreCase))
                    return Task.CompletedTask;

                switch (req.Level)
                {
                    case LogLevel.Warning:
                        MelonLogger.Warning("[Twitch] " + req.Message);
                        break;
                    case LogLevel.Error:
                        MelonLogger.Error("[Twitch] " + req.Message);
                        break;
                    default:
                        MelonLogger.Msg("[Twitch] " + req.Message);
                        break;
                }
                return Task.CompletedTask;
            }

            protected override Task<string> GetFileIOBasePath(CancellationToken _) => Task.FromResult(_basePath);
        }
    }
}
