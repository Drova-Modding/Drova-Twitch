using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace TwitchMod.Interactions
{
    /// <summary>
    /// Spawn rewards in co-op. Only the world authority may spawn, so a streamer who joined someone else's world
    /// asks the host to spawn near them and waits for the answer. The co-op mod replicates what the host spawns.
    /// </summary>
    internal static class CoopSpawns
    {
        private const ushort RequestId = 200;
        private const ushort ResultId = 201;
        private const float AnswerTimeout = 10f;

        private static readonly Dictionary<int, (Action<bool> Done, float Deadline)> Pending = new();

        private static int _nextRequest;

        /// <summary>
        /// What a spawn reward sends.
        /// </summary>
        public enum Kind : byte
        {
            /// <summary>One creature.</summary>
            Monster,

            /// <summary>A bandit group.</summary>
            Bandits,
        }

        /// <summary>
        /// Binds the request and answer messages. Call once at startup.
        /// </summary>
        public static void Register()
        {
            NetworkAccess.RegisterWithId<SpawnRequest>(RequestId, OnRequest);
            NetworkAccess.RegisterWithId<SpawnResult>(ResultId, OnResult);
        }

        /// <summary>
        /// Spawns near <paramref name="player"/>, here or on the host. <paramref name="done"/> gets whether it spawned.
        /// </summary>
        public static void Spawn(Kind kind, Actor player, Action<bool> done)
        {
            if (!PlayerAccess.TryGetLevel(player, out int level))
            {
                MelonLogger.Warning("[Twitch] Spawn refunded: the player's level is not known.");
                done(false);
                return;
            }

            Vector2 origin = player.transform.position;
            if (CoopAccess.IsWorldAuthority)
            {
                bool spawned = SpawnHere(kind, origin, level);
                if (!spawned)
                    MelonLogger.Warning("[Twitch] Spawn refunded: no free spot near the player.");
                done(spawned);
                return;
            }

            if (!NetworkAccess.IsConnected)
            {
                MelonLogger.Warning("[Twitch] Spawn refunded: not connected to the host.");
                done(false);
                return;
            }

            int id = ++_nextRequest;
            Pending[id] = (done, Time.unscaledTime + AnswerTimeout);
            NetworkAccess.SendToAll(new SpawnRequest { Id = id, Kind = kind, X = origin.x, Y = origin.y, Level = level });
        }

        /// <summary>
        /// Refunds requests the host never answered, for example because it left.
        /// <paramref name="expireAll"/> refunds every open request, for when the game closes.
        /// </summary>
        public static void Tick(bool expireAll = false)
        {
            if (Pending.Count == 0)
                return;

            foreach ((int id, (Action<bool> done, float deadline)) in Pending)
            {
                if (!expireAll && deadline >= Time.unscaledTime)
                    continue;

                Pending.Remove(id);
                if (!expireAll)
                    MelonLogger.Warning("[Twitch] The host did not answer a spawn request.");
                done(false);
            }
        }

        private static bool SpawnHere(Kind kind, Vector2 origin, int level)
        {
            return kind == Kind.Monster ? RewardEffects.SpawnMonster(origin, level) : RewardEffects.SpawnBandits(origin, level);
        }

        private static void OnRequest(INetPeer peer, SpawnRequest request)
        {
            bool spawned = false;
            try
            {
                spawned = CoopAccess.IsWorldAuthority && SpawnHere(request.Kind, new Vector2(request.X, request.Y), request.Level);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Twitch] Spawn for a teammate failed: " + e);
            }

            NetworkAccess.Send(peer, new SpawnResult { Id = request.Id, Spawned = spawned });
        }

        private static void OnResult(INetPeer peer, SpawnResult result)
        {
            if (Pending.Remove(result.Id, out (Action<bool> Done, float Deadline) entry))
                entry.Done(result.Spawned);
        }

        private struct SpawnRequest : INetMessage
        {
            public int Id;
            public Kind Kind;
            public float X;
            public float Y;
            public int Level;

            public void Write(INetWriter writer)
            {
                writer.Put(Id);
                writer.Put((byte)Kind);
                writer.Put(X);
                writer.Put(Y);
                writer.Put(Level);
            }

            public void Read(INetReader reader)
            {
                Id = reader.GetInt();
                Kind = (Kind)reader.GetByte();
                X = reader.GetFloat();
                Y = reader.GetFloat();
                Level = reader.GetInt();
            }
        }

        private struct SpawnResult : INetMessage
        {
            public int Id;
            public bool Spawned;

            public void Write(INetWriter writer)
            {
                writer.Put(Id);
                writer.Put(Spawned);
            }

            public void Read(INetReader reader)
            {
                Id = reader.GetInt();
                Spawned = reader.GetBool();
            }
        }
    }
}
