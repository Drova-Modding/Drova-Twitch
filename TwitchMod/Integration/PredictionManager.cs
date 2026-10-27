using Drova_Modding_API.Access;
#if DEBUG
using Drova_Modding_API.Systems.Testing;
#endif
using HarmonyLib;
using Il2CppDrova;
using Il2CppDrova.GlobalVarSystem;
using Il2CppInterop.Runtime.Attributes;
using MelonLoader;
using TwitchSDK;
using TwitchSDK.Interop;
using UnityEngine;

namespace TwitchMod.Integration
{
    /// <summary>
    /// Runs a "Will I beat the boss?" channel points prediction. It opens when the streamer comes near a boss,
    /// voting closes after a short window, and it resolves when the boss or the streamer goes down.
    /// Leaving the fight or anything unclear cancels it, which refunds every vote.
    /// </summary>
    [RegisterTypeInIl2Cpp]
    internal class PredictionManager(IntPtr ptr) : MonoBehaviour(ptr)
    {
        /// <summary>
        /// Option key of the switch that turns boss predictions on.
        /// </summary>
        public const string EnabledKey = "TwitchMod_Predictions";

        private const int VotingSeconds = 30;
        private const float ScanInterval = 1f;
        private const float StartRange = 450f;
        private const float FleeRange = 1500f;
        private const float FleeSeconds = 20f;
        private const float MaxFightSeconds = 900f;
        private const float RetryCooldown = 60f;
        private const string Win = "Yes";
        private const string Loss = "No";

        private static readonly Boss[] Bosses =
        [
            new("Brutus", "Brutus", null, "BrutusCave_Fight.BrutusDead"),
            new("the Great Tusk", "GreatTusk", "GVars_Fight_GreatTuskFight.StartRitual_Start", "GVars_Fight_GreatTuskFight.MainFight_Finished"),
            new("the Library Golem", "Golem_Library", null, null),
            new("the Auwald spider", "BigSpider_AuwaldDungeon", null, null),
            new("the Flesh Boss", "FleshBoss", null, null),
        ];

        private static readonly List<Boss> TestBosses = [];

        private static readonly HashSet<string> BossActorNames = Bosses.Select(boss => boss.ActorName).ToHashSet();

        private static readonly List<Actor> LoadedBosses = [];

        private static readonly Dictionary<string, AGVar<bool>> FlagsByName = new();

        private readonly Dictionary<string, float> _cooldownUntil = new();

        private AuthManager? _authManager;
        private Boss? _boss;
        private Actor? _bossActor;
        private Actor? _player;
        private GameTask<Prediction>? _prediction;
        private string? _result;
        private float _nextScan;
        private float _startedAt;
        private float _farSince = -1f;
        private string _lastFight = "none";

        private void Start()
        {
            _authManager = GetComponent<AuthManager>();

#if DEBUG
            TestAgent.RegisterCommand("twitch_boss", name =>
            {
                TestBosses.Add(new Boss(name.Trim(), name.Trim(), null, null));
                BossActorNames.Add(name.Trim());
                return "boss " + name.Trim();
            });
            TestAgent.RegisterCommand("twitch_fight", _ => _lastFight);
#endif
        }

        private void Update()
        {
            ApplyResult();

            if (Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + ScanInterval;

            if (_boss == null)
            {
                TryStartFight();
                return;
            }

            string? outcome = CheckFight(_boss);
            if (outcome == null)
                return;

            _lastFight = outcome + " " + _boss.Name;
            MelonLogger.Msg("[Twitch] Boss fight " + _lastFight + ".");
            if (outcome != Win && outcome != Loss)
                _cooldownUntil[_boss.Name] = Time.unscaledTime + RetryCooldown;

            _result = _prediction != null ? outcome : null;
            _boss = null;
            _bossActor = null;
            ApplyResult();
        }

        private void TryStartFight()
        {
            if (!PlayerAccess.TryGetPlayer(out Actor? player) || player == null
                || (ConfigAccessor.TryGetConfigValue(EnabledKey, out bool enabled) && !enabled))
                return;

            foreach (Boss boss in Bosses.Concat(TestBosses))
            {
                if (_cooldownUntil.TryGetValue(boss.Name, out float until) && Time.unscaledTime < until)
                    continue;
                if (boss.WinVar != null && ReadBool(boss.WinVar))
                    continue;

                Actor? actor = FindBoss(boss, player, boss.StartVar != null ? FleeRange : StartRange);
                if (boss.StartVar != null ? !ReadBool(boss.StartVar) : actor == null)
                    continue;

                _boss = boss;
                _bossActor = actor;
                _player = player;
                _startedAt = Time.unscaledTime;
                _farSince = -1f;
                _lastFight = "fighting " + boss.Name;
                MelonLogger.Msg("[Twitch] Boss fight against " + boss.Name + " started.");
                OpenPrediction(boss);
                return;
            }
        }

        [HideFromIl2Cpp]
        private string? CheckFight(Boss boss)
        {
            if (_bossActor == null && _player != null)
                _bossActor = FindBoss(boss, _player, FleeRange);

            Health? bossHealth = _bossActor != null ? _bossActor.GetHealth() : null;
            if ((bossHealth != null && bossHealth.IsDead) || (boss.WinVar != null && ReadBool(boss.WinVar)))
                return Win;

            if (!PlayerAccess.TryGetPlayer(out Actor? player) || player == null || _player == null
                || player.Pointer != _player.Pointer || player.GetHealth() == null || player.GetHealth().IsDead)
                return Loss;

            if (Time.unscaledTime - _startedAt > MaxFightSeconds)
                return "timed out";

            if (_bossActor == null)
                return boss.StartVar != null ? null : "fled";

            float distance = (_bossActor.GetFeetPosition() - player.GetFeetPosition()).magnitude;
            if (distance <= FleeRange)
            {
                _farSince = -1f;
                return null;
            }

            if (_farSince < 0f)
                _farSince = Time.unscaledTime;
            return Time.unscaledTime - _farSince > FleeSeconds ? "fled" : null;
        }

        [HideFromIl2Cpp]
        private void OpenPrediction(Boss boss)
        {
            UnityTwitch? twitch = TwitchManager.Twitch;
            if (twitch == null || _authManager == null || !_authManager.IsLoggedIn || _prediction != null)
                return;

            _prediction = twitch.NewPrediction(new PredictionDefinition
            {
                Title = "Will I beat " + boss.Name + "?",
                Outcomes = [Win, Loss],
                Duration = VotingSeconds
            });
        }

        private void ApplyResult()
        {
            if (_prediction == null || _result == null || !_prediction.IsCompleted)
                return;

            GameTask<Prediction> task = _prediction;
            string result = _result;
            _prediction = null;
            _result = null;

            if (task.Exception != null)
            {
                MelonLogger.Warning("[Twitch] Could not start the prediction, it needs an Affiliate or Partner channel: " + task.Exception.InnerException?.Message);
                return;
            }

            Prediction prediction = task.MaybeResult;
            PredictionOutcome? outcome = prediction.Info.Outcomes?.FirstOrDefault(candidate => candidate.Title == result);
            if (outcome != null)
                prediction.Resolve(outcome);
            else
                prediction.Cancel();
        }

        private static Actor? FindBoss(Boss boss, Actor player, float range)
        {
            Vector2 origin = player.GetFeetPosition();
            LoadedBosses.RemoveAll(actor => actor == null);
            foreach (Actor actor in LoadedBosses)
            {
                Health? health = actor.GetHealth();
                if (actor.IsInitialized && CleanName(actor) == boss.ActorName && health != null && !health.IsDead
                    && (actor.GetFeetPosition() - origin).magnitude <= range)
                    return actor;
            }
            return null;
        }

        /// <summary>
        /// Keeps <paramref name="entity"/> if it is a boss. Called for every entity the game registers.
        /// </summary>
        public static void OnEntityRegistered(Entity entity)
        {
            if (!BossActorNames.Contains(CleanName(entity)))
                return;

            Actor? actor = entity.TryCast<Actor>();
            if (actor != null)
                LoadedBosses.Add(actor);
        }

        /// <summary>
        /// Forgets <paramref name="entity"/>. Called for every entity the game unregisters.
        /// </summary>
        public static void OnEntityUnregistered(Entity entity)
        {
            for (int i = LoadedBosses.Count - 1; i >= 0; i--)
            {
                Actor loaded = LoadedBosses[i];
                if (loaded == null || loaded.Pointer == entity.Pointer)
                    LoadedBosses.RemoveAt(i);
            }
        }

        private static string CleanName(UnityEngine.Object entity)
        {
            return entity.name.Replace("(Clone)", string.Empty).TrimEnd('_', ' ');
        }

        private static bool ReadBool(string listAndVar)
        {
            if (FlagsByName.TryGetValue(listAndVar, out AGVar<bool>? cached) && cached != null)
                return cached.GetValue();

            SubDatabase_GVars? database = ProviderAccess.GVarDatabase;
            int dot = listAndVar.IndexOf('.');
            if (database == null || dot < 0)
                return false;

            string listName = listAndVar.Substring(0, dot);
            for (int index = 0; index < database.AllGVars.Count; index++)
            {
                GVarList list = database.AllGVars[index];
                if (list == null || list.name != listName)
                    continue;

                AGVarBase gvar = list.GetGVarByName(listAndVar.Substring(dot + 1));
                AGVar<bool>? asBool = gvar != null ? gvar.TryCast<AGVar<bool>>() : null;
                if (asBool == null)
                    return false;

                FlagsByName[listAndVar] = asBool;
                return asBool.GetValue();
            }
            return false;
        }

        private sealed record Boss(string Name, string ActorName, string? StartVar, string? WinVar);
    }

    [HarmonyPatch(typeof(EntityGameHandler), nameof(EntityGameHandler.RegisterEntity))]
    internal static class ActorRegisteredPatch
    {
        private static void Postfix(Entity entity)
        {
            if (entity != null)
                PredictionManager.OnEntityRegistered(entity);
        }
    }

    [HarmonyPatch(typeof(EntityGameHandler), nameof(EntityGameHandler.UnregisterEntity))]
    internal static class ActorUnregisteredPatch
    {
        private static void Prefix(Entity entity)
        {
            if (entity != null)
                PredictionManager.OnEntityUnregistered(entity);
        }
    }
}
