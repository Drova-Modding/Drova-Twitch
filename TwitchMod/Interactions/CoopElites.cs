using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
#if DEBUG
using Drova_Modding_API.Systems.Testing;
#endif
using HarmonyLib;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace TwitchMod.Interactions
{
    /// <summary>
    /// Turns the nearest enemy around the streamer into an elite.
    /// In co-op every machine buffs its own copy of the enemy, because the host owns its health
    /// while either side may run its attacks. The streamer's machine picks the enemy and the factor and broadcasts both.
    /// An elite pays more experience, in proportion to what its factor adds. In co-op the killer's gain is what the party is paid.
    /// </summary>
    internal static class CoopElites
    {
        private const ushort EliteId = 202;
        private const float SearchRadius = 400f;
        private const float ChaseRange = 900f;
        private const float MinFactor = 1.5f;
        private const float MaxFactor = 4.5f;
        private const float ArmorPerFactor = 4f;
        private const float WaitSeconds = 60f;
        private const float ScanInterval = 0.25f;

        private const float MatchRange = 60f;

        /// <summary>The factor at which an elite pays double experience. The bonus scales with what the factor adds over an ordinary enemy.</summary>
        private const float FactorForDoubleExperience = 4.5f;

        private static readonly Dictionary<int, (Actor Actor, float Factor)> Elites = new();
        private static readonly List<INetPeer> Peers = [];
        private static readonly List<(Actor Player, Action<bool> Done, float Deadline)> Waiting = [];

        private static float _nextScan;

#if DEBUG
        private static Actor? _lastElite;
#endif

        /// <summary>
        /// Binds the elite message. Call once at startup.
        /// </summary>
        public static void Register(HarmonyLib.Harmony harmony)
        {
            NetworkAccess.RegisterWithId<EliteMessage>(EliteId, OnElite);

            // The kill payout, by signature since an inherited instance overload shares the name.
            Hooking.TryPrefix(harmony,
                AccessTools.Method(typeof(PlayerAttributeStats), nameof(PlayerAttributeStats.CheckPlayerGainExp),
                    [typeof(Actor), typeof(HealthChangeArgs), typeof(int)]),
                typeof(CoopElites), nameof(GainExpPrefix), "PlayerAttributeStats.CheckPlayerGainExp");

#if DEBUG
            TestAgent.RegisterCommand("twitch_elite", rest => DescribeLastElite(rest.Trim()));
            TestAgent.RegisterState(() => "elite=" + DescribeLastElite(string.Empty));
#endif
        }

        /// <summary>Raises what an elite's death pays: the bonus grows from nothing at factor 1 to double at <see cref="FactorForDoubleExperience"/>.</summary>
        internal static void GainExpPrefix(Actor owner, ref int expOnDead)
        {
            try
            {
                if (owner == null || expOnDead <= 0 || !Elites.TryGetValue(owner.GetInstanceID(), out (Actor Actor, float Factor) elite))
                    return;

                int raised = Mathf.RoundToInt(expOnDead * (1f + ((elite.Factor - 1f) / (FactorForDoubleExperience - 1f))));
#if DEBUG
                _lastPayout = $"{expOnDead}->{raised}";
#endif
                expOnDead = raised;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Twitch] Raising an elite's experience failed: " + e);
            }
        }

#if DEBUG
        private static string _lastPayout = "none";

        private static string DescribeLastElite(string part)
        {
            if (part == "paid")
                return _lastPayout;

            if (_lastElite == null || !DamageAccess.TryGetPhysicalDamageType(out DamageStatDesc? physical)
                || !DamageAccess.TryGetMagicalDamageType(out DamageStatDesc? magical))
                return "none";

            Health health = _lastElite.GetHealth();
            AttributeStats stats = _lastElite.GetStats();
            if (health == null || stats == null || health.IsDead)
                return "none";

            float damage = physical.RelatedDamageFactor != null ? stats.GetGenericStat(physical.RelatedDamageFactor).GetFloatValue() : 0f;
            if (part == "hp")
                return (health.MaxHealth * 100 / Math.Max(health.BaseMaxHealth, 1)).ToString();
            if (part == "dmg")
                return Mathf.RoundToInt(damage * 100f).ToString();

            int armor = physical.RelatedResistanceType != null ? stats.GetGenericStat(physical.RelatedResistanceType).GetIntValue() : 0;
            int magicResistance = magical.RelatedResistanceType != null ? stats.GetGenericStat(magical.RelatedResistanceType).GetIntValue() : 0;
            return $"max{health.MaxHealth}:dmg{Mathf.RoundToInt(damage * 100f)}:arm{armor}:mres{magicResistance}";
        }
#endif

        /// <summary>
        /// Makes the nearest hostile enemy an elite. With none nearby it waits for the first one to come close,
        /// and <paramref name="done"/> gets false when none did in time, so the redemption gets refunded.
        /// </summary>
        public static void MakeElite(Actor player, Action<bool> done)
        {
            if (!TryMakeElite(player))
                Waiting.Add((player, done, Time.unscaledTime + WaitSeconds));
            else
                done(true);
        }

        /// <summary>
        /// Gives waiting redemptions their elite once an enemy comes close, and refunds the ones that ran out of time.
        /// <paramref name="expireAll"/> refunds every waiting redemption, before the rewards are removed.
        /// </summary>
        public static void Tick(bool expireAll = false)
        {
            if (Waiting.Count == 0 || (!expireAll && Time.unscaledTime < _nextScan))
                return;
            _nextScan = Time.unscaledTime + ScanInterval;

            for (int i = Waiting.Count - 1; i >= 0; i--)
            {
                (Actor player, Action<bool> done, float deadline) = Waiting[i];

                bool expired = expireAll || player == null || Time.unscaledTime > deadline;
                if (!expired && !TryMakeElite(player!))
                    continue;

                Waiting.RemoveAt(i);
                if (expired && !expireAll)
                    MelonLogger.Warning("[Twitch] Elite refunded: no enemy came close.");
                done(!expired);
            }
        }

        private static bool TryMakeElite(Actor player)
        {
            if (!EnemyAccess.TryFindNearest(player, SearchRadius, actor => !Elites.ContainsKey(actor.GetInstanceID()), out Actor? enemy))
                return false;

            float factor = UnityEngine.Random.Range(MinFactor, MaxFactor);
            if (!Apply(enemy, factor))
                return false;

            if (NetworkAccess.IsConnected)
                NetworkAccess.SendToAll(ToMessage(enemy, factor));
            return true;
        }

        private static bool Apply(Actor enemy, float factor)
        {
            int resistance = Mathf.RoundToInt(factor * ArmorPerFactor);
            if (!ActorBuffAccess.TryApply(enemy, new ActorBuff(factor, factor, resistance, resistance)))
                return false;
            EnemyAccess.MakeAggressive(enemy, ChaseRange);

            foreach ((int id, (Actor elite, float _)) in Elites)
            {
                if (elite == null)
                    Elites.Remove(id);
            }
            Elites[enemy.GetInstanceID()] = (enemy, factor);
#if DEBUG
            _lastElite = enemy;
#endif
            MelonLogger.Msg($"[Twitch] {enemy.name} is now an elite at {factor * 100f:0}%.");
            return true;
        }

        private static EliteMessage ToMessage(Actor enemy, float factor)
        {
            GuidComponent? guidComponent = enemy.GetGuidComponent();
            string guid = guidComponent != null ? guidComponent.GuidString ?? string.Empty : string.Empty;
            Vector2 position = enemy.transform.position;
            return new EliteMessage { Guid = guid, Name = enemy.name, X = position.x, Y = position.y, Factor = factor };
        }

        private static void OnElite(INetPeer sender, EliteMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                NetworkAccess.GetPeers(Peers);
                foreach (INetPeer peer in Peers.Where(peer => peer.Id != sender.Id))
                    NetworkAccess.Send(peer, message);
            }

            try
            {
                Actor? enemy = Resolve(message);
                if (enemy == null || Elites.ContainsKey(enemy.GetInstanceID()) || !Apply(enemy, message.Factor))
                    MelonLogger.Msg("[Twitch] A teammate's elite is not loaded here.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Twitch] Applying a teammate's elite failed: " + e);
            }
        }

        private static Actor? Resolve(EliteMessage message)
        {
            EntityGameHandler? handler = ProviderAccess.GetEntityGameHandler();
            if (handler == null)
                return null;

            if (!string.IsNullOrEmpty(message.Guid))
            {
                Actor byGuid = handler.GetActorFromGuid(message.Guid);
                if (byGuid != null)
                    return byGuid;
            }

            Vector2 origin = new(message.X, message.Y);
            Actor? best = null;
            float bestDistance = MatchRange * MatchRange;
            foreach (Entity entity in handler.GetDynamicEntitiesInCircle(origin, Vector2.zero, 0f, MatchRange, null))
            {
                Actor? actor = entity != null ? entity.TryCast<Actor>() : null;
                if (actor == null || actor.name != message.Name)
                    continue;

                float distance = ((Vector2)actor.transform.position - origin).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = actor;
                }
            }
            return best;
        }

        private struct EliteMessage : INetMessage
        {
            public string Guid;
            public string Name;
            public float X;
            public float Y;
            public float Factor;

            public void Write(INetWriter writer)
            {
                writer.Put(Guid);
                writer.Put(Name);
                writer.Put(X);
                writer.Put(Y);
                writer.Put(Factor);
            }

            public void Read(INetReader reader)
            {
                Guid = reader.GetString();
                Name = reader.GetString();
                X = reader.GetFloat();
                Y = reader.GetFloat();
                Factor = reader.GetFloat();
            }
        }
    }
}
