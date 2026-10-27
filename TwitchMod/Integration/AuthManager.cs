using UnityEngine;
using MelonLoader;
using TwitchSDK;
using TwitchSDK.Interop;

namespace TwitchMod.Integration
{
    /// <summary>
    /// Device-code login for the streamer.
    /// The user code is never shown in-game or logged, because a viewer could redeem it first.
    /// </summary>
    [RegisterTypeInIl2Cpp]
    internal class AuthManager(IntPtr ptr) : MonoBehaviour(ptr)
    {
        private const float PollInterval = 1f;

        private static readonly TwitchOAuthScope[] Scopes = [TwitchOAuthScope.Channel.ManagePolls, TwitchOAuthScope.Channel.ManagePredictions, TwitchOAuthScope.Channel.ManageRedemptions];

        private GameTask<AuthState>? _authStateTask;
        private GameTask<AuthenticationInfo>? _authInfoTask;
        private GameTask<UserInfo>? _userInfoTask;
        private AuthStatus _status = AuthStatus.Loading;
        private bool _browserOpened;
        private float _nextPoll;

        /// <summary>
        /// Whether the streamer is logged in, as of the last poll.
        /// </summary>
        public bool IsLoggedIn => _status == AuthStatus.LoggedIn;

        /// <summary>
        /// The logged-in channel's login name, or null until Twitch answered.
        /// </summary>
        public static string? LoginName { get; internal set; }

        private void Update()
        {
            UnityTwitch? twitch = TwitchManager.Twitch;
            if (twitch == null || Time.unscaledTime < _nextPoll)
                return;
            _nextPoll = Time.unscaledTime + PollInterval;

            if (_authStateTask == null || _authStateTask.IsCompleted)
            {
                AuthState? state = _authStateTask?.MaybeResult;
                if (state != null && state.Status != _status)
                {
                    if (state.Status == AuthStatus.LoggedIn && (state.Scopes == null || !Scopes.All(scope => state.Scopes.Contains(scope.Scope))))
                    {
                        MelonLogger.Warning("[Twitch] Login is missing permissions. Log in again under Options, Modding, Twitch.");
                        twitch.LogOut();
                    }
                    else
                    {
                        _status = state.Status;
                        LoginName = null;
                        MelonLogger.Msg("[Twitch] " + _status);
                    }
                }
                _authStateTask = twitch.GetAuthState();
            }

            if (IsLoggedIn && LoginName == null)
                _userInfoTask ??= twitch.GetMyUserInfo();

            if (_userInfoTask is { IsCompleted: true })
            {
                if (_userInfoTask.Exception == null && IsLoggedIn)
                {
                    LoginName = _userInfoTask.MaybeResult?.LoginName;
                    MelonLogger.Msg("[Twitch] Logged in as " + LoginName);
                }
                _userInfoTask = null;
            }

            if (_authInfoTask is not { IsCompleted: true })
                return;

            if (_authInfoTask.Exception != null)
            {
                MelonLogger.Warning("[Twitch] Login failed, log in again under Options, Modding, Twitch.");
                _authInfoTask = null;
                return;
            }

            if (_status == AuthStatus.LoggedIn)
            {
                _authInfoTask = null;
                return;
            }

            AuthenticationInfo? info = _authInfoTask.MaybeResult;
            if (_browserOpened || _status != AuthStatus.WaitingForCode || info == null || string.IsNullOrEmpty(info.UserCode))
                return;

            Application.OpenURL(info.Uri.Contains(info.UserCode) ? info.Uri : info.Uri + info.UserCode);
            _browserOpened = true;
            MelonLogger.Msg("[Twitch] Login opened in your browser. Approve it there.");
        }

        /// <summary>
        /// Starts the login flow unless already logged in or a login is pending.
        /// </summary>
        public void SignIn()
        {
            UnityTwitch? twitch = TwitchManager.Twitch;
            if (twitch == null || IsLoggedIn || _authInfoTask != null)
                return;

            _browserOpened = false;
            _authInfoTask = twitch.GetAuthenticationInfo(Scopes);
        }

        /// <summary>
        /// Ends the session. The core overwrites the stored token.
        /// </summary>
        public void SignOut()
        {
            _authInfoTask = null;
            TwitchManager.Twitch?.LogOut();
        }
    }
}
