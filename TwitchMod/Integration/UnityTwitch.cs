using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MelonLoader;
using TwitchSDK;
using TwitchSDK.Interop;
using UnityEngine;

namespace TwitchMod.Integration
{
    /// <summary>
    /// Twitch SDK wired to MelonLoader logging, with token-safe HTTP, logging and storage.
    /// </summary>
    internal class UnityTwitch(string clientId, bool useEventSubProxy = false) : TwitchSDKApi(clientId, useEventSubProxy)
    {
        protected override PlatformAbstractionLayer CreatePAL() => new MelonPAL();

        private sealed class MelonPAL : ManagedPAL
        {
            private const string KeyringService = "DrovaTwitchMod";

            private readonly string _basePath = Application.persistentDataPath;
            private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

            static MelonPAL()
            {
                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    if (e.Exception.InnerException is CoreLibraryException twitchException)
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
                    TwitchSDK.Interop.HttpMethod.Get => System.Net.Http.HttpMethod.Get,
                    TwitchSDK.Interop.HttpMethod.Post => System.Net.Http.HttpMethod.Post,
                    TwitchSDK.Interop.HttpMethod.Put => System.Net.Http.HttpMethod.Put,
                    TwitchSDK.Interop.HttpMethod.Patch => System.Net.Http.HttpMethod.Patch,
                    TwitchSDK.Interop.HttpMethod.Delete => System.Net.Http.HttpMethod.Delete,
                    _ => throw new ArgumentOutOfRangeException(nameof(req), req.Method, "Unsupported HTTP method."),
                };
                if (req.Method is TwitchSDK.Interop.HttpMethod.Post or TwitchSDK.Interop.HttpMethod.Put or TwitchSDK.Interop.HttpMethod.Patch)
                    message.Content = new StringContent(req.RequestBody, Encoding.UTF8, req.ContentType);
                if (!string.IsNullOrEmpty(req.ClientId))
                    message.Headers.Add("Client-Id", req.ClientId);
                if (!string.IsNullOrEmpty(req.Authorization))
                    message.Headers.Add("Authorization", req.Authorization);
                message.Headers.UserAgent.Add(new ProductInfoHeaderValue(HttpUserAgent, "0.2"));

                using HttpResponseMessage response = await _http.SendAsync(message).ConfigureAwait(false);
                return new WebRequestResult
                {
                    HttpStatus = (int)response.StatusCode,
                    ResponseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                };
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
