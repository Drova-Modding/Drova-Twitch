using System.Text.RegularExpressions;
using Drova_Modding_API.Register;
#if DEBUG
using Drova_Modding_API.Systems.Testing;
#endif
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Il2CppDrova.GUI.Dialogue;
using Il2CppNodeCanvas.DialogueTrees;
using Il2CppNodeCanvas.DialogueTrees.UI;
using MelonLoader;
using TwitchMod.Interactions;
using TwitchSDK;
using TwitchSDK.Interop;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TwitchMod.Integration
{
    /// <summary>
    /// Lets chat pick a dialogue answer. The streamer presses the poll key while answers are shown, the answers
    /// become a Twitch poll and stay locked until the winner is selected.
    /// </summary>
    [RegisterTypeInIl2Cpp]
    internal class PollManager(IntPtr ptr) : MonoBehaviour(ptr)
    {
        /// <summary>
        /// Name of the gameplay input action that starts a dialogue poll.
        /// </summary>
        public const string StartAction = "TwitchPoll";

        /// <summary>
        /// Option key of the poll duration in seconds.
        /// </summary>
        public const string PollSecondsKey = "TwitchMod_PollSeconds";

        /// <summary>
        /// Poll duration before the options menu ever wrote one.
        /// </summary>
        public const int PollSecondsDefault = 30;

        private const string PollTitle = "Chat decides: what should I answer?";
        private const int MaxChoices = 5;
        private const int MaxChoiceLength = 25;

        private static readonly Regex RichTextTag = new("<.*?>");

        private static DS_DialogueUGUI? _ui;
        private static MultipleChoiceRequestInfo? _request;
        private static IntPtr _requestNode;

        private AuthManager? _authManager;
        private InputActionRegister? _register;
        private InputAction? _startAction;
        private GameTask<Poll>? _poll;
        private bool _voting;
        private string[] _titles = [];
        private int[] _values = [];

        private void Start()
        {
            _authManager = GetComponent<AuthManager>();

#if DEBUG
            TestAgent.RegisterCommand("twitch_poll", _ => BeginVote() ? string.Join(" | ", _titles) : throw new InvalidOperationException("no answers to vote on"));
            TestAgent.RegisterCommand("twitch_vote", rest =>
            {
                long[] votes = rest.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();
                Resolve(new PollInfo
                {
                    Status = PollStatus.Completed,
                    Choices = votes.Select([HideFromIl2Cpp] (count, i) => new PollChoiceInfo { Title = _titles[i], Votes = count }).ToArray()
                });
                return "voting=" + _voting;
            });
            TestAgent.RegisterCommand("twitch_locked", _ =>
            {
                DS_GUI_DialogueOptionsGroup? group = GetOptionsGroup();
                DS_DialogueWindow? window = GetWindow();
                InputActionRegister register = InputActionRegister.Instance;
                return "voting=" + _voting
                    + " interactable=" + (group != null && group.CanvasGroup.interactable)
                    + " raycast=" + (group != null && group.CanvasGroup.blocksRaycasts)
                    + " digits=" + (window != null && window.Controls != null && !window.Controls._choiceInvoked)
                    + " key=" + (register != null && register[StartAction].enabled);
            });
#endif
        }

        private void Update()
        {
            if (!_voting)
            {
                InputActionRegister register = InputActionRegister.Instance;
                if (!ReferenceEquals(register, _register))
                {
                    _startAction = register != null ? register[StartAction] : null;
                    _register = register;
                }
                if (_startAction != null && _startAction.WasPressedThisFrame())
                    StartPoll();
                return;
            }

            DS_GUI_DialogueOptionsGroup? group = GetOptionsGroup();
            if (group == null || !IsRequestCurrent())
            {
                MelonLogger.Msg("[Twitch] The dialogue moved on, poll canceled.");
                if (_poll is { IsCompleted: true, Exception: null })
                    _poll.MaybeResult.DeletePoll();
                EndVote(null);
                return;
            }

            SetLocked(group, true);

            if (_poll == null || !_poll.IsCompleted)
                return;

            if (_poll.Exception != null)
            {
                MelonLogger.Warning("[Twitch] Could not start the poll: " + _poll.Exception.InnerException?.Message);
                EndVote(group);
                return;
            }

            PollInfo info = _poll.MaybeResult.Info;
            if (info.Status != PollStatus.Active)
                Resolve(info);
        }

        /// <summary>
        /// Remembers the answers the dialogue window was just asked to show.
        /// </summary>
        public static void OnChoicesRequested(DS_DialogueUGUI ui, MultipleChoiceRequestInfo info)
        {
            _ui = ui;
            _request = info;
            DialogueTree tree = ui.DLGTree;
            _requestNode = tree != null && tree.currentNode != null ? tree.currentNode.Pointer : IntPtr.Zero;
        }

        private void StartPoll()
        {
            UnityTwitch? twitch = TwitchManager.Twitch;
            if (twitch == null || _request == null || !IsRequestCurrent())
                return;

            if (_authManager == null || !_authManager.IsLoggedIn)
            {
                MelonLogger.Warning("[Twitch] Log in before starting a poll.");
                return;
            }

            if (!BeginVote())
                return;

            int seconds = RewardManager.GetInt(PollSecondsKey, PollSecondsDefault);
            _poll = twitch.NewPoll(new PollDefinition { Title = PollTitle, Choices = _titles, Duration = seconds });
            MelonLogger.Msg("[Twitch] Poll started, chat decides the answer within " + seconds + " s.");
        }

        private bool BeginVote()
        {
            DS_GUI_DialogueOptionsGroup? group = GetOptionsGroup();
            if (_ui == null || _request == null || group == null || !IsRequestCurrent())
                return false;

            List<string> titles = [];
            List<int> values = [];
            foreach (Il2CppSystem.Collections.Generic.KeyValuePair<IStatement, int> option in _request.options)
            {
                string text = (titles.Count + 1) + ". " + RichTextTag.Replace(option.Key?.text ?? string.Empty, string.Empty).Trim();
                titles.Add(text.Length > MaxChoiceLength ? text.Substring(0, MaxChoiceLength - 1) + "…" : text);
                values.Add(option.Value);
            }

            if (titles.Count < 2 || titles.Count > MaxChoices)
            {
                MelonLogger.Warning("[Twitch] A poll needs 2 to " + MaxChoices + " answers, this one has " + titles.Count + ".");
                return false;
            }

            _titles = titles.ToArray();
            _values = values.ToArray();
            _voting = true;
            SetLocked(group, true);

            if (_request.availableTime > 0f)
            {
                _ui._isCountdownEnabled = false;
                foreach (CanvasRenderer canvasRenderer in group.GetRect().GetComponentsInChildren<CanvasRenderer>())
                    canvasRenderer.SetAlpha(1f);
            }
            return true;
        }

        [HideFromIl2Cpp]
        private void Resolve(PollInfo info)
        {
            DS_GUI_DialogueOptionsGroup? group = GetOptionsGroup();
            if (!_voting || group == null || _ui == null || _request == null)
                return;

            if (info.Status is not (PollStatus.Completed or PollStatus.Terminated) || info.Choices == null || info.Choices.Length == 0)
            {
                MelonLogger.Msg("[Twitch] Poll ended without a result, choose yourself.");
                EndVote(group);
                return;
            }

            long most = info.Choices.Max(choice => choice.Votes);
            int[] winners = info.Choices.Where(choice => choice.Votes == most)
                .Select(choice => Array.IndexOf(_titles, choice.Title))
                .Where(index => index >= 0)
                .ToArray();
            if (winners.Length == 0)
            {
                EndVote(group);
                return;
            }

            int winner = winners[UnityEngine.Random.Range(0, winners.Length)];
            MelonLogger.Msg("[Twitch] Chat chose " + _titles[winner] + " with " + most + " votes.");

            MultipleChoiceRequestInfo request = _request;
            DS_DialogueUGUI ui = _ui;
            EndVote(null);
            _request = null;
            group.OnChoiceSelected();
            ui.Finalize(request, _values[winner]);
        }

        private void EndVote(DS_GUI_DialogueOptionsGroup? unlock)
        {
            _voting = false;
            _poll = null;
            if (unlock != null)
                SetLocked(unlock, false);
        }

        private static void SetLocked(DS_GUI_DialogueOptionsGroup group, bool locked)
        {
            group.SetInteractable(!locked);
            group.SetBlocksRaycast(!locked);
            if (group.JoyStickSelection != null)
                group.JoyStickSelection.enabled = !locked;

            DS_DialogueWindow? window = GetWindow();
            if (window != null && window.Controls != null)
                window.Controls._choiceInvoked = locked;
        }

        private static DS_DialogueWindow? GetWindow()
        {
            return _ui != null ? _ui.WindowMgr?.GetCurrentWindow() : null;
        }

        private static DS_GUI_DialogueOptionsGroup? GetOptionsGroup()
        {
            DS_DialogueWindow? window = GetWindow();
            return window != null && window.OptionsGroup != null ? window.OptionsGroup : null;
        }

        private static bool IsRequestCurrent()
        {
            if (_ui == null || _requestNode == IntPtr.Zero)
                return false;

            DialogueTree tree = _ui.DLGTree;
            return tree != null && tree.currentNode != null && tree.currentNode.Pointer == _requestNode;
        }
    }

    [HarmonyPatch(typeof(DS_DialogueUGUI), nameof(DS_DialogueUGUI.OnMultipleChoiceRequest), [typeof(MultipleChoiceRequestInfo)])]
    internal static class DialogueChoicesPatch
    {
        private static void Postfix(DS_DialogueUGUI __instance, MultipleChoiceRequestInfo info)
        {
            PollManager.OnChoicesRequested(__instance, info);
        }
    }
}
