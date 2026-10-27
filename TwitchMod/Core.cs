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
        private static readonly string[] ItemFiles = ["TwitchMod.json", "twitch_spezi.png"];

        private bool _isInitialized;

        public override void OnInitializeMelon()
        {
            CoopAccess.Declare(CoopBehavior.AuthorityGated);
            CoopSpawns.Register();
            CoopElites.Register();
            InstallItems();

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

        /// <summary>
        /// Writes the mod's custom item files into the Modding API's item folder, which it reads at the main menu.
        /// The folder is the API's own rule, so this does not wait for the API to initialize.
        /// </summary>
        private void InstallItems()
        {
            string folder = Path.Combine(Path.GetDirectoryName(MelonAssembly.Location)!, "Modding_API", "Items");
            try
            {
                Directory.CreateDirectory(folder);
                foreach (string name in ItemFiles)
                {
                    using Stream resource = typeof(Core).Assembly.GetManifestResourceStream(name)!;
                    using FileStream file = File.Create(Path.Combine(folder, name));
                    resource.CopyTo(file);
                }
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("Could not install the custom items in " + folder + ": " + e.Message);
            }
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
