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
        private const string ChatModeKey = "TwitchMod_ChatMode";
        private const string ChatCooldownKey = "TwitchMod_ChatCooldown";
        private const int ChatCooldownDefault = 120;
        private const int MinChatCooldown = 10;
        private const int MaxChatCooldown = 3600;
        private const float ReplyInterval = 1f;
        private const float HelpInterval = 30f;
        private const string HelpCommand = "!drova";
        private const string ChannelEventsLockName = @"Local\DrovaTwitchMod_ChannelEvents";

        private const string SpeziChannel = "entwicklerwg";
        private const string SpeziItemId = "twitch_spezi";
        private const int SpeziChance = 3;

        private const string SchnitzlChannel = "sir_schnitzl";
        private const string SurpriseText = "Schnitzl surprise!";

        private const float MaxHoldSeconds = 600f;

        private static readonly int[] SurpriseBag = [0, 1, 2, 3, 4];
        private static readonly HashSet<int> SurpriseDrawn = [];

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
                (player, done) => SpawnOrSurprise(CoopSpawns.Kind.Monster, player, done)),
            new("Bandits", "Drova: Bandit ambush", "Sends a group of bandits after the streamer.", 4000, 120,
                (player, done) => SpawnOrSurprise(CoopSpawns.Kind.Bandits, player, done)),
            new("Elite", "Drova: Elite foe", "The nearest enemy becomes a boss with 150 to 450% health and damage.", 3000, 60,
                CoopElites.MakeElite),
            new("StatusEffect", "Drova: Random effect", "A random food, potion or bad effect such as bleeding, poison or getting drunk.", 500, 30,
                (player, done) => done(RewardEffects.GiveRandomStatusEffect(player))),
        ];

        private AuthManager? _authManager;
        private Actor? _player;
        private Task? _registerTask;
        private GameTask<EventStream<CustomRewardEvent>>? _streamTask;
        private GameTask<EventStream<ChannelRaidEvent>>? _raidTask;
        private int[] _registeredCosts = [];
        private readonly Queue<(Reward Reward, Func<bool, Task> Finish, float Deadline)> _held = new();
        private readonly List<Task> _open = [];
        private Task _lastReplace = Task.CompletedTask;
        private float _retryAt;
        private static bool _speziGiven;
        private Task? _signOutTask;
        private TwitchChat? _chat;
        private bool _rewardsEnabled;
        private static bool _chatSwitch;
        private float _nextReply;
        private float _nextHelp;
        private readonly Dictionary<string, float> _viewerReadyAt = new();
        private readonly Dictionary<string, float> _viewerTold = new();
        private PollManager? _polls;
        private PredictionManager? _predictions;
#if DEBUG
        private string _lastTestResult = "none";
        private static int _surprises;
        private GameTask<UserInfo>? _userLookup;
#endif
        private Mutex? _channelEventsLock;
        private bool _ownsChannelEvents;
        private bool _toldNotOwner;
        private GameObject? _logIn;
        private GameObject? _logInHint;
        private GameObject? _logOut;
        private GameObject? _rewardsSwitch;
        private GameObject? _modeSwitch;

        private void Start()
        {
            _authManager = GetComponent<AuthManager>();
            _polls = GetComponent<PollManager>();
            _predictions = GetComponent<PredictionManager>();
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
                new("Rewards", "Viewer rewards", ELanguage.en),
                new("Rewards", "Zuschauer-Belohnungen", ELanguage.de),
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
                new("Toast", "[viewer] redeemed [reward]", ELanguage.en),
                new("Toast", "[viewer] hat [reward] eingelöst", ELanguage.de),
                new("ChannelType", "Channel type", ELanguage.en),
                new("ChannelType", "Kanaltyp", ELanguage.de),
                new("Partner", "Affiliate / Partner", ELanguage.en),
                new("Partner", "Affiliate / Partner", ELanguage.de),
                new("ChatOnly", "Chat only", ELanguage.en),
                new("ChatOnly", "Nur Chat", ELanguage.de),
                new("ChatHint", "Affiliate and Partner channels use channel points, Twitch polls and predictions. Chat only works on any channel: viewers type !gold, !monster and the other reward names, with a cooldown per viewer, and vote on polls and predictions in chat. A channel without Affiliate or Partner always uses chat.", ELanguage.en),
                new("ChatHint", "Affiliate- und Partner-Kanäle nutzen Kanalpunkte, Twitch-Umfragen und -Vorhersagen. Nur Chat geht auf jedem Kanal: Zuschauer schreiben !gold, !monster und die anderen Belohnungsnamen, mit Abklingzeit pro Zuschauer, und stimmen im Chat über Umfragen und Vorhersagen ab. Ein Kanal ohne Affiliate oder Partner nutzt immer den Chat.", ELanguage.de),
                new("RaidToast", "[raider] raids with [viewers] viewers! Bandits are coming, and they carry gold.", ELanguage.en),
                new("RaidToast", "[raider] raidet mit [viewers] Zuschauern! Banditen kommen, und sie tragen Gold.", ELanguage.de),
                new("Help", "Drova chat commands: [commands]. Each viewer can use one every [seconds] s.", ELanguage.en),
                new("Help", "Drova-Chat-Befehle: [commands]. Jeder Zuschauer kann alle [seconds] s einen nutzen.", ELanguage.de),
                new("CooldownReply", "@[viewer] [command] is ready for you again in [seconds] s.", ELanguage.en),
                new("CooldownReply", "@[viewer] [command] ist für dich in [seconds] s wieder bereit.", ELanguage.de),
                new("ChatCooldown", "Chat command cooldown per viewer (seconds)", ELanguage.en),
                new("ChatCooldown", "Abklingzeit der Chat-Befehle pro Zuschauer (Sekunden)", ELanguage.de),
                new(SpeziItemId + "_name", "Spezi", ELanguage.en),
                new(SpeziItemId + "_name", "Spezi", ELanguage.de),
                new(SpeziItemId + "_description", "Es ist so lecker, dass man fast einen ganzen Tisch davon vollstellen möchte oder 2? Naja Hauptsache Willi wird nicht vergessen", ELanguage.en),
                new(SpeziItemId + "_description", "Es ist so lecker, dass man fast einen ganzen Tisch davon vollstellen möchte oder 2? Naja Hauptsache Willi wird nicht vergessen", ELanguage.de),
            ], ModName);

#if DEBUG
            TestAgent.RegisterCommand("twitch_reward", TestReward);
            TestAgent.RegisterCommand("twitch_result", _ => _lastTestResult);
            TestAgent.RegisterCommand("twitch_channel", name => AuthManager.LoginName = name.Trim());
            TestAgent.RegisterCommand("twitch_type", type =>
            {
                AuthManager.BroadcasterType = type.Trim();
                return "chat=" + ChatMode;
            });
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

            TestAgent.RegisterCommand("twitch_chat", line =>
            {
                if (!TwitchChat.TryParse(line.Trim(), out string? viewer, out string? text))
                    return "unparsed";
                _lastTestResult = "pending";
                return OnChat(viewer, text, applied => _lastTestResult = applied.ToString()) ? _lastTestResult : "refused";
            });

            TestAgent.RegisterCommand("twitch_raid", viewers =>
            {
                _lastTestResult = "pending";
                OnRaid("TestRaider", int.Parse(viewers.Trim()), applied => _lastTestResult = applied.ToString());
                return _lastTestResult;
            });

            TestAgent.RegisterCommand("twitch_surprises", _ => _surprises.ToString());
            TestAgent.RegisterCommand("twitch_monster", guid => RewardEffects.ForcedMonster = guid.Trim());
            TestAgent.RegisterCommand("twitch_items", _ => string.Join(",", (RewardEffects.GetItemPool() ?? []).Select(item => item.ReadableId + ":" + item.BuyValue)));
            TestAgent.RegisterCommand("twitch_strong", _ => RewardEffects.StrongItemsGiven.ToString());
#endif

            _rewardsEnabled = RewardsOn();
            _chatSwitch = ChatSwitchOn();
            OptionMenuAccess.Instance.OnOptionMenuOpen += BuildOptions;
            OptionMenuAccess.Instance.OnOptionMenuClose += () =>
            {
                _rewardsEnabled = RewardsOn();
                _chatSwitch = ChatSwitchOn();
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

            ReadChat();

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
                ResetRaids();
                _registerTask = null;
                return;
            }

            if (Time.unscaledTime < _retryAt)
                return;

            if (!OwnsChannelEvents())
            {
                _retryAt = Time.unscaledTime + RetryDelay;
                return;
            }

            ReadRaids(twitch);

            // The broadcaster type decides between channel points and chat commands.
            if (AuthManager.BroadcasterType == null)
                return;

            if (_registerTask == null)
            {
                CustomRewardDefinition[] definitions = Definitions();
                _registeredCosts = definitions.Select(definition => (int)definition.Cost).ToArray();
                _registerTask = ReplaceRewards(() => twitch.ReplaceCustomRewards(definitions).Task);
                if (definitions.Length > 0)
                {
                    _registerTask.ContinueWith(task => MelonLogger.Warning(
                        "[Twitch] Could not create channel point rewards. They need an Affiliate or Partner channel. " + task.Exception?.InnerException?.Message),
                        TaskContinuationOptions.OnlyOnFaulted);
                }
            }

            // Without rewards there is nothing to redeem, and a channel without channel points would fail every retry.
            if (_registeredCosts.Length == 0)
            {
                ResetSubscription();
                return;
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

        /// <summary>
        /// Whether this is the one Drova on the PC that runs the rewards. The login is stored for the whole PC, and every
        /// further game subscribing to the same channel's events makes Twitch refuse all of them (429).
        /// </summary>
        private bool OwnsChannelEvents()
        {
            if (_ownsChannelEvents)
                return true;

            _channelEventsLock ??= new Mutex(false, ChannelEventsLockName);
            try
            {
                _ownsChannelEvents = _channelEventsLock.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                _ownsChannelEvents = true;
            }

            if (!_ownsChannelEvents && !_toldNotOwner)
            {
                _toldNotOwner = true;
                MelonLogger.Warning("[Twitch] Another Drova on this PC holds the channel point rewards until it closes, so this one leaves them to it.");
            }
            return _ownsChannelEvents;
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
                    ResetRaids();
                    _registerTask = null;
                    UnityTwitch? twitch = TwitchManager.Twitch;
                    if (twitch != null && _signOutTask == null)
                        _signOutTask = ReplaceRewards(() => twitch.ReplaceCustomRewards().Task);
                    ShowAccount(false);
                })
                .CreateSwitch(LocalizationAccess.GetLocalizedString(ModName, "ChannelType"), LocalizationAccess.GetLocalizedString(ModName, "ChatOnly"),
                    LocalizationAccess.GetLocalizedString(ModName, "Partner"), ChatModeKey)
                .CreateDisclaimer(LocalizationAccess.GetLocalizedString(ModName, "ChatHint"))
                .CreateSwitch(LocalizationAccess.GetLocalizedString(ModName, "Rewards"), LocalizationAccess.GetLocalizedString(ModName, "On"),
                    LocalizationAccess.GetLocalizedString(ModName, "Off"), RewardsEnabledKey, true)
                .CreateDisclaimer(LocalizationAccess.GetLocalizedString(ModName, "Disclaimer"));
            foreach (Reward reward in Rewards)
                builder.CreateSlider(LocalizationAccess.GetLocalizedString(ModName, reward.Key), reward.OptionKey, 1, MaxCost, reward.DefaultCost);
            List<GameObject> rows = builder.CreateSlider(LocalizationAccess.GetLocalizedString(ModName, "GoldAmount"), GoldAmountKey, 1, 1000, GoldAmountDefault)
                .CreateSlider(LocalizationAccess.GetLocalizedString(ModName, "PollSeconds"), PollManager.PollSecondsKey, MinPollSeconds, MaxPollSeconds, PollManager.PollSecondsDefault)
                .CreateSwitch(LocalizationAccess.GetLocalizedString(ModName, "Predictions"), LocalizationAccess.GetLocalizedString(ModName, "On"),
                    LocalizationAccess.GetLocalizedString(ModName, "Off"), PredictionManager.EnabledKey, true)
                .CreateSlider(LocalizationAccess.GetLocalizedString(ModName, "ChatCooldown"), ChatCooldownKey, MinChatCooldown, MaxChatCooldown, ChatCooldownDefault)
                .CreateInputActionSection([new(LocalizationAccess.GetLocalizedString(ModName, "PollKey"), PollManager.StartAction)])
                .Build();

            _logIn = rows[1];
            _logInHint = rows[2];
            _logOut = rows[3];
            _modeSwitch = rows[4];
            _rewardsSwitch = rows[6];
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
            // A channel without Affiliate or Partner has no choice to make.
            if (_modeSwitch != null)
                _modeSwitch.SetActive(AuthManager.BroadcasterType != string.Empty);
        }

        /// <summary>
        /// Reads an integer option, or <paramref name="fallback"/> before the options menu ever wrote it.
        /// </summary>
        public static int GetInt(string key, int fallback)
        {
            return ConfigAccessor.TryGetConfigValue(key, out int value) ? value : fallback;
        }

        private static bool RewardsOn() => !ConfigAccessor.TryGetConfigValue(RewardsEnabledKey, out bool value) || value;

        private static bool ChatSwitchOn() => ConfigAccessor.TryGetConfigValue(ChatModeKey, out bool value) && value;

        /// <summary>
        /// Whether rewards, polls and predictions run over chat instead of channel points and Twitch's own polls and predictions.
        /// A channel Twitch reports without Affiliate or Partner always does, because the others fail there.
        /// </summary>
        public static bool ChatMode => _chatSwitch || AuthManager.BroadcasterType == string.Empty;

        private static CustomRewardDefinition[] Definitions()
        {
            return RewardsOn() && !ChatMode ? Rewards.Select(reward => reward.ToDefinition()).ToArray() : [];
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
            if (twitch == null || !_ownsChannelEvents)
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

        /// <summary>
        /// A spawn reward. On sir_schnitzl's channel one spawn in five comes twice and says so in chat.
        /// </summary>
        [HideFromIl2Cpp]
        private static void SpawnOrSurprise(CoopSpawns.Kind kind, Actor player, Action<bool> done)
        {
            bool surprise = string.Equals(AuthManager.LoginName, SchnitzlChannel, StringComparison.OrdinalIgnoreCase)
                && RewardEffects.Draw(SurpriseDrawn, SurpriseBag) == 0;
            CoopSpawns.Spawn(kind, player, spawned =>
            {
                if (spawned && surprise)
                {
                    // A second spawn instead of a doubled one, so the co-op spawn message stays as it is.
                    CoopSpawns.Spawn(kind, player, _ => { });
                    TwitchManager.Say(SurpriseText);
#if DEBUG
                    _surprises++;
#endif
                }
                done(spawned);
            });
        }

        /// <summary>
        /// Reads chat commands and votes in chat mode. Only the Drova holding the channel events reads chat,
        /// so a second game on the PC does not apply every command twice.
        /// </summary>
        private void ReadChat()
        {
            string? channel = AuthManager.LoginName;
            bool reading = ChatMode && _ownsChannelEvents && _authManager != null && _authManager.IsLoggedIn && channel != null;
            if (_chat != null && (!reading || _chat.Channel != channel))
            {
                _chat.Dispose();
                _chat = null;
            }
            if (!reading)
                return;

            if (_chat == null)
            {
                _chat = new TwitchChat(channel!);
                if (_rewardsEnabled)
                    TwitchManager.Say(HelpText());
            }
            while (_chat.Messages.TryDequeue(out (string Viewer, string Text) message))
                OnChat(message.Viewer, message.Text, null);
        }

        /// <summary>
        /// Counts a poll or prediction vote, or delivers the reward a chat command names.
        /// Returns whether it was a reward and off cooldown.
        /// </summary>
        [HideFromIl2Cpp]
        private bool OnChat(string viewer, string text, Action<bool>? done)
        {
            if ((_polls != null && _polls.OnChatVote(viewer, text)) || (_predictions != null && _predictions.OnChatVote(viewer, text)) || !_rewardsEnabled)
                return false;

            string command = text.Trim().Split(' ', 2)[0];
            if (string.Equals(command, HelpCommand, StringComparison.OrdinalIgnoreCase))
            {
                if (Time.unscaledTime >= _nextHelp && ReplyInChat(HelpText()))
                    _nextHelp = Time.unscaledTime + HelpInterval;
                return false;
            }

            Reward? reward = Rewards.FirstOrDefault(candidate => string.Equals("!" + candidate.Key, command, StringComparison.OrdinalIgnoreCase));
            if (reward == null)
                return false;

            // The reward's own cooldown holds for the whole chat, as with channel points. The chat cooldown holds per viewer.
            float readyAt = Math.Max(reward.ReadyAt, _viewerReadyAt.GetValueOrDefault(viewer));
            if (Time.unscaledTime < readyAt)
            {
                // One reply per viewer and cooldown, so spamming a command does not spam chat back.
                int seconds = Mathf.CeilToInt(readyAt - Time.unscaledTime);
                if (_viewerTold.GetValueOrDefault(viewer) != readyAt
                    && ReplyInChat(Localized("CooldownReply").Replace("[viewer]", viewer).Replace("[command]", command).Replace("[seconds]", seconds.ToString())))
                    _viewerTold[viewer] = readyAt;
                return false;
            }

            // ponytail: one entry per viewer who used a command this session, prune if chats get huge.
            _viewerReadyAt[viewer] = Time.unscaledTime + GetInt(ChatCooldownKey, ChatCooldownDefault);
            reward.ReadyAt = Time.unscaledTime + reward.CooldownSeconds;
            Deliver(reward, applied =>
            {
                MelonLogger.Msg("[Twitch] " + viewer + " used " + command + (applied ? "." : ", it could not be applied."));
                if (applied)
                {
                    ShowToast(viewer, reward);
                }
                else
                {
                    _viewerReadyAt.Remove(viewer);
                    reward.ReadyAt = 0f;
                }
                done?.Invoke(applied);
                return Task.CompletedTask;
            });
            return true;
        }

        private static string HelpText()
        {
            return Localized("Help").Replace("[commands]", string.Join(" ", Rewards.Select(reward => "!" + reward.Key.ToLowerInvariant())))
                .Replace("[seconds]", GetInt(ChatCooldownKey, ChatCooldownDefault).ToString());
        }

        /// <summary>
        /// Answers in chat unless the last answer was too recent. Returns whether it answered.
        /// </summary>
        private bool ReplyInChat(string text)
        {
            if (Time.unscaledTime < _nextReply)
                return false;

            // ponytail: one reply per second for the whole chat stays under Twitch's 100 messages per 30 s for the broadcaster. Drops replies in a flood.
            _nextReply = Time.unscaledTime + ReplyInterval;
            TwitchManager.Say(text);
            return true;
        }

        /// <summary>
        /// Reads incoming raids while the rewards are on. They run in both modes, channel points and chat commands.
        /// </summary>
        private void ReadRaids(UnityTwitch twitch)
        {
            if (!_rewardsEnabled)
            {
                ResetRaids();
                return;
            }

            _raidTask ??= twitch.SubscribeToChannelRaidEvents();
            if (!_raidTask.IsCompleted)
                return;

            try
            {
                EventStream<ChannelRaidEvent> stream = _raidTask.MaybeResult;
                while (stream.TryGetNextEvent(out ChannelRaidEvent raid))
                {
                    if (raid != null)
                        OnRaid(raid.FromBroadcasterName, (int)raid.Viewers, null);
                }
            }
            catch (AggregateException e)
            {
                MelonLogger.Warning("[Twitch] Raid events failed, retrying in " + RetryDelay + " s: " + e.InnerException?.Message);
                ResetRaids();
                _retryAt = Time.unscaledTime + RetryDelay;
            }
        }

        /// <summary>
        /// Sends a bandit group at the streamer, bigger with more raiders, and the bandits carry gold to loot.
        /// </summary>
        [HideFromIl2Cpp]
        private void OnRaid(string raider, int viewers, Action<bool>? done)
        {
            ToastAccess.Show(Localized("RaidToast").Replace("[raider]", raider).Replace("[viewers]", viewers.ToString()));
            Reward raid = new("Raid", "Raid by " + raider, string.Empty, 0, 0,
                (player, spawned) => CoopSpawns.Spawn(CoopSpawns.Kind.Bandits, player, spawned, Math.Max(1, viewers)));
            Deliver(raid, applied =>
            {
                MelonLogger.Msg("[Twitch] " + raider + " raided with " + viewers + " viewers" + (applied ? "." : ", but the ambush could not spawn."));
                done?.Invoke(applied);
                return Task.CompletedTask;
            });
        }

        private void ResetRaids()
        {
            _raidTask?.Task.ContinueWith(task => task.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            _raidTask = null;
        }

        [HideFromIl2Cpp]
        private static void ShowToast(string viewer, Reward reward)
        {
            ToastAccess.Show(Localized("Toast").Replace("[viewer]", viewer).Replace("[reward]", reward.Title));
        }

        /// <summary>
        /// The text for <paramref name="key"/> in the game's language. Placeholders are written [name], because the
        /// localization files wrap each value in braces.
        /// </summary>
        private static string Localized(string key) => LocalizationAccess.GetLocalizedString(ModName, key).GetLocalizedString(null);

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

            // Twitch does not reliably enforce the global cooldown.
            if (Time.unscaledTime < reward.ReadyAt)
            {
                MelonLogger.Msg("[Twitch] " + reward.Title + " is still on cooldown.");
                _open.Add(Finish(false));
                return;
            }
            reward.ReadyAt = Time.unscaledTime + reward.CooldownSeconds;

            Deliver(reward, Finish);
            return;

            Task Finish(bool applied)
            {
                MelonLogger.Msg("[Twitch] " + redemption.RedeemerName + " redeemed " + reward.Title + (applied ? "." : ", refunded."));
                if (applied)
                    ShowToast(redemption.RedeemerName, reward);
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
            public int CooldownSeconds { get; } = cooldownSeconds;
            public float ReadyAt { get; set; }

            public CustomRewardDefinition ToDefinition() => new()
            {
                Title = Title,
                Prompt = prompt,
                Cost = Cost,
                BackgroundColor = RewardColor,
                IsGlobalCooldownEnabled = CooldownSeconds > 0,
                GlobalCooldownSeconds = CooldownSeconds
            };
        }
    }
}
