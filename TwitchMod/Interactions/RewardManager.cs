using Drova_Modding_API.Access;
#if DEBUG
using Drova_Modding_API.Systems.Testing;
#endif
using Drova_Modding_API.UI.Builder;
using Il2CppDrova;
using Il2CppDrova.Items;
using Il2CppInterop.Runtime.Attributes;
using MelonLoader;
using TwitchMod.Integration;
using TwitchSDK;
using TwitchSDK.Interop;
using UnityEngine;
using static Il2CppCustomFramework.Localization.LocalizationDB;

namespace TwitchMod.Interactions
{
    /// <summary>
    /// Registers the channel point rewards while the streamer is logged in and applies redemptions in game.
    /// A redemption that cannot be applied, for example in the main menu, is canceled so the viewer gets the points back.
    /// </summary>
    [RegisterTypeInIl2Cpp]
    internal class RewardManager(IntPtr ptr) : MonoBehaviour(ptr)
    {
        private const string ModName = "TwitchMod";
        private const string RewardColor = "#9146FF";
        private const float RetryDelay = 30f;
        private const string GoldAmountKey = "TwitchMod_GoldAmount";
        private const string RewardsEnabledKey = "TwitchMod_Rewards";
        private const int GoldAmountDefault = 10;
        private const int MaxCost = 10000;
        private const int MinPollSeconds = 15;
        private const int MaxPollSeconds = 300;

        private const string SpeziChannel = "entwicklerwg";
        private const string SpeziItemId = "twitch_spezi";
        private const int SpeziChance = 3;

        private const float MaxHoldSeconds = 600f;

        private static readonly Reward[] Rewards =
        [
            new("RandomItem", "Drova: Random item", "Gives the streamer a random weapon, armor or consumable.", 500, 0,
                (player, done) => done(GiveRandomItemOrSpezi(player))),
            new("Gold", "Drova: Gold", "Gives the streamer gold.", 300, 0,
                (player, done) => done(RewardEffects.GiveItem(player, Item.READABLEID_CURRENCY, GetInt(GoldAmountKey, GoldAmountDefault)))),
            new("HealingPotion", "Drova: Healing potion", "Gives the streamer a healing potion.", 200, 0,
                (player, done) => done(RewardEffects.GiveItem(player, "item_potion_health_0", 1))),
            new("FocusPotion", "Drova: Focus potion", "Gives the streamer a focus potion.", 200, 0,
                (player, done) => done(RewardEffects.GiveItem(player, "item_potion_flow_0", 1))),
            new("Monster", "Drova: Spawn monster", "Sends a monster after the streamer.", 2000, 60,
                (player, done) => CoopSpawns.Spawn(CoopSpawns.Kind.Monster, player, done)),
            new("Bandits", "Drova: Bandit ambush", "Sends a group of bandits after the streamer.", 4000, 120,
                (player, done) => CoopSpawns.Spawn(CoopSpawns.Kind.Bandits, player, done)),
            new("Elite", "Drova: Elite foe", "The nearest enemy becomes a boss with 150 to 400% health and damage.", 3000, 60,
                CoopElites.MakeElite),
            new("StatusEffect", "Drova: Random effect", "A random food, potion or bad effect such as bleeding, poison or getting drunk.", 500, 30,
                (player, done) => done(RewardEffects.GiveRandomStatusEffect(player))),
        ];

        private AuthManager? _authManager;
        private Actor? _player;
        private Task? _registerTask;
        private GameTask<EventStream<CustomRewardEvent>>? _streamTask;
        private int[] _registeredCosts = [];
        private readonly Queue<(Reward Reward, Func<bool, Task> Finish, float Deadline)> _held = new();
        private readonly List<Task> _open = [];
        private Task _lastReplace = Task.CompletedTask;
        private float _retryAt;
        private static bool _speziGiven;
        private Task? _signOutTask;
#if DEBUG
        private string _lastTestResult = "none";
        private GameTask<UserInfo>? _userLookup;
#endif
        private GameObject? _logIn;
        private GameObject? _logInHint;
        private GameObject? _logOut;
        private GameObject? _rewardsSwitch;

        private void Start()
        {
            _authManager = GetComponent<AuthManager>();
            PlayerAccess.OnPlayerFound += player => _player = player;

            LocalizationAccess.CreateLocalizationEntries(
            [
                new("Title", "Twitch", ELanguage.en),
                new("Title", "Twitch", ELanguage.de),
                new("Account", "Twitch account", ELanguage.en),
                new("Account", "Twitch-Konto", ELanguage.de),
                new("LogIn", "Log in", ELanguage.en),
                new("LogIn", "Anmelden", ELanguage.de),
                new("LogInHint", "Logging in opens a browser window to approve the mod on Twitch.", ELanguage.en),
                new("LogInHint", "Die Anmeldung öffnet ein Browserfenster, um die Mod auf Twitch zu bestätigen.", ELanguage.de),
                new("LogOut", "Log out", ELanguage.en),
                new("LogOut", "Abmelden", ELanguage.de),
                new("Disclaimer", "Channel point costs are updated on Twitch when this menu closes.", ELanguage.en),
                new("Disclaimer", "Die Kanalpunkte-Kosten werden beim Schließen des Menüs auf Twitch aktualisiert.", ELanguage.de),
                new("Rewards", "Channel point rewards", ELanguage.en),
                new("Rewards", "Kanalpunkte-Belohnungen", ELanguage.de),
                new("Predictions", "Boss predictions", ELanguage.en),
                new("Predictions", "Boss-Vorhersagen", ELanguage.de),
                new("On", "On", ELanguage.en),
                new("On", "An", ELanguage.de),
                new("Off", "Off", ELanguage.en),
                new("Off", "Aus", ELanguage.de),
                new("PollSeconds", "Dialogue poll duration (seconds)", ELanguage.en),
                new("PollSeconds", "Dauer der Dialog-Umfrage (Sekunden)", ELanguage.de),
                new("PollKey", "Start dialogue poll", ELanguage.en),
                new("PollKey", "Dialog-Umfrage starten", ELanguage.de),
                new("GoldAmount", "Gold per redemption", ELanguage.en),
                new("GoldAmount", "Gold pro Einlösung", ELanguage.de),
                new("RandomItem", "Random item cost", ELanguage.en),
                new("RandomItem", "Kosten zufälliger Gegenstand", ELanguage.de),
                new("Gold", "Gold cost", ELanguage.en),
                new("Gold", "Kosten Gold", ELanguage.de),
                new("HealingPotion", "Healing potion cost", ELanguage.en),
                new("HealingPotion", "Kosten Heiltrank", ELanguage.de),
                new("FocusPotion", "Focus potion cost", ELanguage.en),
                new("FocusPotion", "Kosten Fokustrank", ELanguage.de),
                new("Monster", "Monster cost", ELanguage.en),
                new("Monster", "Kosten Monster", ELanguage.de),
                new("Bandits", "Bandit ambush cost", ELanguage.en),
                new("Bandits", "Kosten Banditenüberfall", ELanguage.de),
                new("Elite", "Elite foe cost", ELanguage.en),
                new("Elite", "Kosten Elite-Gegner", ELanguage.de),
                new("StatusEffect", "Random effect cost", ELanguage.en),
                new("StatusEffect", "Kosten zufälliger Effekt", ELanguage.de),
                new(SpeziItemId + "_name", "Spezi", ELanguage.en),
                new(SpeziItemId + "_name", "Spezi", ELanguage.de),
                new(SpeziItemId + "_description", "Es ist so lecker, dass man fast einen ganzen Tisch davon vollstellen kann oder 2? Naja Hauptsache Willi geht es damit gut", ELanguage.en),
                new(SpeziItemId + "_description", "Es ist so lecker, dass man fast einen ganzen Tisch davon vollstellen kann oder 2? Naja Hauptsache Willi geht es damit gut", ELanguage.de),
            ], ModName);

#if DEBUG
            TestAgent.RegisterCommand("twitch_reward", TestReward);
            TestAgent.RegisterCommand("twitch_result", _ => _lastTestResult);
            TestAgent.RegisterCommand("twitch_channel", name => AuthManager.LoginName = name.Trim());
            TestAgent.RegisterCommand("twitch_user", login =>
            {
                _userLookup = (TwitchManager.Twitch ?? throw new InvalidOperationException("no twitch")).GetUserInfoByLoginName(login.Trim());
                return "pending";
            });
            TestAgent.RegisterState(() => "channel=" + (AuthManager.LoginName ?? "-") + " named=" + (AuthManager.LoginName != null ? 1 : 0) + " lookup=" + (_userLookup is not { IsCompleted: true } ? "none"
                : _userLookup.Exception != null ? "error:" + _userLookup.Exception.InnerException?.Message
                : _userLookup.MaybeResult?.LoginName + "/" + _userLookup.MaybeResult?.ChannelId));

            TestAgent.RegisterCommand("twitch_replace", key =>
            {
                if (key.Trim().Length > 0)
                    TestReward(key);
                ReplaceRewards(() => Task.CompletedTask);
                return "open=" + _open.Count(task => !task.IsCompleted);
            });
            TestAgent.RegisterCommand("twitch_replaced", _ => _lastReplace.IsCompleted ? "done" : "waiting");

            TestAgent.RegisterCommand("twitch_status", key => (_player != null && RewardEffects.GiveStatusEffect(_player, key.Trim())).ToString());
            TestAgent.RegisterState(() => "hp=" + (_player != null && _player.GetHealth() != null ? _player.GetHealth().CurHealth : -1f)
                .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

            TestAgent.RegisterCommand("twitch_items", _ => string.Join(",", (RewardEffects.GetItemPool() ?? []).Select(item => item.ReadableId)));
#endif

            OptionMenuAccess.Instance.OnOptionMenuOpen += BuildOptions;
            OptionMenuAccess.Instance.OnOptionMenuClose += () =>
            {
                if (!_registeredCosts.SequenceEqual(Definitions().Select(definition => (int)definition.Cost)))
                    _registerTask = null;
            };
        }

        private void Update()
        {
            DeliverHeld();
            CoopSpawns.Tick();
            CoopElites.Tick();

            UnityTwitch? twitch = TwitchManager.Twitch;
            if (twitch == null || _authManager == null)
                return;

            if (_signOutTask != null)
            {
                if (!_signOutTask.IsCompleted)
                    return;
                _signOutTask = null;
                _authManager.SignOut();
            }

            if (!_authManager.IsLoggedIn)
            {
                ResetSubscription();
                _registerTask = null;
                return;
            }

            if (Time.unscaledTime < _retryAt)
                return;

            if (_registerTask == null)
            {
                CustomRewardDefinition[] definitions = Definitions();
                _registeredCosts = definitions.Select(definition => (int)definition.Cost).ToArray();
                _registerTask = ReplaceRewards(() => twitch.ReplaceCustomRewards(definitions).Task);
                _registerTask.ContinueWith(task => MelonLogger.Warning(
                    "[Twitch] Could not create channel point rewards. They need an Affiliate or Partner channel. " + task.Exception?.InnerException?.Message),
                    TaskContinuationOptions.OnlyOnFaulted);
            }

            _streamTask ??= twitch.SubscribeToCustomRewardEvents();
            if (!_streamTask.IsCompleted)
                return;

            try
            {
                EventStream<CustomRewardEvent> stream = _streamTask.MaybeResult;
                while (stream.TryGetNextEvent(out CustomRewardEvent redemption))
                    Redeem(twitch, redemption);
            }
            catch (AggregateException e)
            {
                MelonLogger.Warning("[Twitch] Channel point events failed, retrying in " + RetryDelay + " s: " + e.InnerException?.Message);
                ResetSubscription();
                _retryAt = Time.unscaledTime + RetryDelay;
            }
        }

        private void BuildOptions()
        {
            OptionUIBuilder? builder = OptionMenuAccess.Instance.GetBuilder(ModName);
            if (builder == null)
                return;


            builder.CreateTitle(LocalizationAccess.GetLocalizedString(ModName, "Title"))
                .CreateButton(LocalizationAccess.GetLocalizedString(ModName, "Account"), LocalizationAccess.GetLocalizedString(ModName, "LogIn"), () =>
                {
                    if (_authManager != null)
                        _authManager.SignIn();
                    ShowAccount(true);
                })
                .CreateDisclaimer(LocalizationAccess.GetLocalizedString(ModName, "LogInHint"))
                .CreateButton(LocalizationAccess.GetLocalizedString(ModName, "Account"), LocalizationAccess.GetLocalizedString(ModName, "LogOut"), () =>
                {
                    ResetSubscription();
                    _registerTask = null;
                    UnityTwitch? twitch = TwitchManager.Twitch;
                    if (twitch != null && _signOutTask == null)
                        _signOutTask = ReplaceRewards(() => twitch.ReplaceCustomRewards().Task);
                    ShowAccount(false);
                })
                .CreateSwitch(LocalizationAccess.GetLocalizedString(ModName, "Rewards"), LocalizationAccess.GetLocalizedString(ModName, "On"),
                    LocalizationAccess.GetLocalizedString(ModName, "Off"), RewardsEnabledKey, true)
                .CreateDisclaimer(LocalizationAccess.GetLocalizedString(ModName, "Disclaimer"));
            foreach (Reward reward in Rewards)
                builder.CreateSlider(LocalizationAccess.GetLocalizedString(ModName, reward.Key), reward.OptionKey, 1, MaxCost, reward.DefaultCost);
            List<GameObject> rows = builder.CreateSlider(LocalizationAccess.GetLocalizedString(ModName, "GoldAmount"), GoldAmountKey, 1, 1000, GoldAmountDefault)
                .CreateSlider(LocalizationAccess.GetLocalizedString(ModName, "PollSeconds"), PollManager.PollSecondsKey, MinPollSeconds, MaxPollSeconds, PollManager.PollSecondsDefault)
                .CreateSwitch(LocalizationAccess.GetLocalizedString(ModName, "Predictions"), LocalizationAccess.GetLocalizedString(ModName, "On"),
                    LocalizationAccess.GetLocalizedString(ModName, "Off"), PredictionManager.EnabledKey, true)
                .CreateInputActionSection([new(LocalizationAccess.GetLocalizedString(ModName, "PollKey"), PollManager.StartAction)])
                .Build();

            _logIn = rows[1];
            _logInHint = rows[2];
            _logOut = rows[3];
            _rewardsSwitch = rows[4];
            ShowAccount(_authManager != null && _authManager.IsLoggedIn);
        }

        private void ShowAccount(bool loggedIn)
        {
            if (_logIn != null)
                _logIn.SetActive(!loggedIn);
            if (_logInHint != null)
                _logInHint.SetActive(!loggedIn);
            if (_logOut != null)
                _logOut.SetActive(loggedIn);
            if (_rewardsSwitch != null)
                _rewardsSwitch.SetActive(loggedIn);
        }

        /// <summary>
        /// Reads an integer option, or <paramref name="fallback"/> before the options menu ever wrote it.
        /// </summary>
        public static int GetInt(string key, int fallback)
        {
            return ConfigAccessor.TryGetConfigValue(key, out int value) ? value : fallback;
        }

        private static CustomRewardDefinition[] Definitions()
        {
            bool enabled = !ConfigAccessor.TryGetConfigValue(RewardsEnabledKey, out bool value) || value;
            return enabled ? Rewards.Select(reward => reward.ToDefinition()).ToArray() : [];
        }

        /// <summary>
        /// Replaces the rewards on Twitch after refunding every held or still open redemption.
        /// Twitch marks the pending redemptions of a removed reward as fulfilled, so the viewers would pay for nothing.
        /// </summary>
        [HideFromIl2Cpp]
        private Task ReplaceRewards(Func<Task> replace)
        {
            CoopElites.Tick(true);
            while (_held.Count > 0)
                _open.Add(_held.Dequeue().Finish(false));

            _open.RemoveAll(task => task.IsCompleted);
            _lastReplace = Task.WhenAll(_open.Append(_lastReplace))
                .ContinueWith(_ => replace()).Unwrap();
            return _lastReplace;
        }

        private void OnApplicationQuit()
        {
            UnityTwitch? twitch = TwitchManager.Twitch;
            if (twitch == null)
                return;

            CoopSpawns.Tick(true);
            try
            {
                ReplaceRewards(() => twitch.ReplaceCustomRewards().Task).Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
            }
        }

        private static bool GiveRandomItemOrSpezi(Actor player)
        {
            if (string.Equals(AuthManager.LoginName, SpeziChannel, StringComparison.OrdinalIgnoreCase) &&(!_speziGiven || UnityEngine.Random.Range(0, SpeziChance) == 0)
                && RewardEffects.GiveItem(player, SpeziItemId, 1))
                return _speziGiven = true;
            return RewardEffects.GiveRandomItem(player);
        }

        private void ResetSubscription()
        {
            _streamTask?.Task.ContinueWith(task => task.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            _streamTask = null;
        }

        [HideFromIl2Cpp]
        private void Redeem(UnityTwitch twitch, CustomRewardEvent? redemption)
        {
            if (redemption == null || redemption.Status != CustomRewardRedemptionState.Unfulfilled)
                return;

            Reward? reward = Rewards.FirstOrDefault(candidate => candidate.Title == redemption.CustomRewardTitle);
            if (reward == null)
                return;

            Deliver(reward, Finish);
            return;

            Task Finish(bool applied)
            {
                MelonLogger.Msg("[Twitch] " + redemption.RedeemerName + " redeemed " + reward.Title + (applied ? "." : ", refunded."));
                return twitch.ResolveCustomReward(redemption, applied ? CustomRewardRedemptionState.Fulfilled : CustomRewardRedemptionState.Canceled).Task;
            }
        }

#if DEBUG
        private string TestReward(string key)
        {
            Reward reward = Rewards.FirstOrDefault(candidate => candidate.Key == key.Trim())
                ?? throw new InvalidOperationException("no reward " + key);
            _lastTestResult = "pending";
            Deliver(reward, applied =>
            {
                _lastTestResult = applied.ToString();
                return Task.CompletedTask;
            });
            return _lastTestResult + " cost=" + reward.Cost;
        }
#endif

        /// <summary>
        /// Queues a reward until the streamer is free, so nothing lands during a dialogue, a cutscene, a death or a load.
        /// </summary>
        [HideFromIl2Cpp]
        private void Deliver(Reward reward, Func<bool, Task> finish)
        {
            _held.Enqueue((reward, finish, Time.unscaledTime + MaxHoldSeconds));
            DeliverHeld();
        }

        private void DeliverHeld()
        {
            while (_held.Count > 0)
            {
                (Reward reward, Func<bool, Task> finish, float deadline) = _held.Peek();
                bool free = PlayerAccess.IsFree(_player);
                if (!free && Time.unscaledTime < deadline)
                    return;

                _held.Dequeue();
                if (!free)
                {
                    MelonLogger.Warning("[Twitch] " + reward.Title + " refunded: the streamer was busy for too long.");
                    _open.Add(finish(false));
                    continue;
                }

                TaskCompletionSource<Task> resolved = new();
                _open.Add(resolved.Task.Unwrap());
                try
                {
                    reward.Apply(_player!, applied => resolved.TrySetResult(finish(applied)));
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[Twitch] " + reward.Title + " failed: " + e);
                    resolved.TrySetResult(finish(false));
                }
            }
        }

        private sealed class Reward(string key, string title, string prompt, int defaultCost, int cooldownSeconds, Action<Actor, Action<bool>> apply)
        {
            public string Key { get; } = key;
            public string OptionKey { get; } = ModName + "_" + key + "Cost";
            public int DefaultCost { get; } = defaultCost;
            public string Title { get; } = title;
            public Action<Actor, Action<bool>> Apply { get; } = apply;
            public int Cost => GetInt(OptionKey, DefaultCost);

            public CustomRewardDefinition ToDefinition() => new()
            {
                Title = Title,
                Prompt = prompt,
                Cost = Cost,
                BackgroundColor = RewardColor,
                IsGlobalCooldownEnabled = cooldownSeconds > 0,
                GlobalCooldownSeconds = cooldownSeconds
            };
        }
    }
}
