using UnityEngine;
using MelonLoader;
using TwitchMod.Interactions;

namespace TwitchMod.Integration
{
    [RegisterTypeInIl2Cpp]
    internal class TwitchManager(IntPtr ptr) : MonoBehaviour(ptr)
    {
        private const string ClientId = "eqgerh5ygvfckt1fy5yqqqv5o7li8r";

        /// <summary>
        /// The Twitch SDK. Null until the main menu has loaded once.
        /// </summary>
        public static UnityTwitch? Twitch { get; private set; }

        private void Start()
        {
            name = "TwitchManager";
            Twitch = new UnityTwitch(ClientId);
            gameObject.AddComponent<AuthManager>();
            gameObject.AddComponent<RewardManager>();
            gameObject.AddComponent<PollManager>();
            gameObject.AddComponent<PredictionManager>();
            MelonLogger.Msg("TwitchManager started.");
        }
    }
}
