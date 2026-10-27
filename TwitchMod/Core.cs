using Drova_Modding_API.Access;
using Drova_Modding_API.Register;
using Drova_Modding_API.Systems.Coop;
using MelonLoader;
using TwitchMod.Integration;
using TwitchMod.Interactions;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(TwitchMod.Core), "TwitchMod", "1.0.0", "TrustNoOneElse")]
[assembly: MelonGame("Just2D", "Drova")]
[assembly: MelonAdditionalDependencies("Drova_Modding_API")]

namespace TwitchMod
{
    /// <summary>
    /// Mod entry point. Registers the dialogue poll input action and creates the <see cref="TwitchManager"/>.
    /// </summary>
    public class Core : MelonMod
    {
        private const string MainScene = "Scene_MainMenu";

        private bool _isInitialized;

        public override void OnInitializeMelon()
        {
            CoopAccess.Declare(CoopBehavior.AuthorityGated);
            CoopSpawns.Register();
            CoopElites.Register();

            InputActionRegister.OnInputActionsLoaded += () =>
            {
                InputAction? poll = InputActionRegister.Instance.AddActionToGameplayMap(PollManager.StartAction);
                if (poll != null)
                {
                    poll.AddBinding("<Keyboard>/period", null, null, "Keyboard;Mouse");
                    poll.AddBinding("<Gamepad>/leftStickPress", null, null, "Gamepad");
                }
            };
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName != MainScene || _isInitialized)
                return;

            GameObject gameObject = new();
            gameObject.AddComponent<TwitchManager>();
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            _isInitialized = true;
        }
    }
}
