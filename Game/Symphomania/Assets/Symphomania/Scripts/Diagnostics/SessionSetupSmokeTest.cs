using System.Text;
using UnityEngine;
using Symphomania.Beatmaps;
using Symphomania.Controllers;
using Symphomania.Session;

namespace Symphomania.Diagnostics
{
    /// <summary>
    /// Live proof that session setup works with whatever's plugged in right
    /// now, no gameplay scene needed - the session-setup equivalent of
    /// BeatmapLoaderSmokeTest and ControllerDiagnostics.
    ///
    /// Loads a beatmap once on Start, then rebuilds the SessionPlan every
    /// frame against VirtualBandInput.PlayableInstruments() (real controllers
    /// plus whichever instrument the keyboard fallback currently stands in
    /// for) and logs a summary - but only when something actually changed, so
    /// it doesn't spam the Console every frame.
    ///
    /// Try it with nothing plugged in: only the keyboard fallback's current
    /// instrument (F1-F5, default Trumpet) shows up as an active slot,
    /// full-screen. Plug in (or simulate via ControllerDiagnostics) a second
    /// instrument, or press a different F-key, and watch the log update with
    /// a new viewport split and updated mismatch lists.
    /// </summary>
    public class SessionSetupSmokeTest : MonoBehaviour
    {
        [Tooltip("Filename under StreamingAssets/Beatmaps.")]
        public string fileName = "twinkle_twinkle_sample.json";

        [Tooltip("Include the keyboard stand-in alongside real controllers (VirtualBandInput.PlayableInstruments). Uncheck to test hardware-only (ConnectedInstruments).")]
        public bool includeKeyboardFallback = true;

        string _lastSignature;

        void Start()
        {
            Beatmap beatmap;
            try
            {
                beatmap = BeatmapLoader.LoadFromStreamingAssets(fileName);
            }
            catch (BeatmapParseException e)
            {
                Debug.LogError($"[SessionSetupSmokeTest] Failed to load '{fileName}': {e.Message}");
                enabled = false;
                return;
            }

            Debug.Log($"[SessionSetupSmokeTest] Loaded '{beatmap.Song.Title}' - watching session composition against " +
                      (includeKeyboardFallback ? "PlayableInstruments (hardware + keyboard fallback)." : "ConnectedInstruments (hardware only)."));

            GameSessionContext.SetBeatmap(beatmap);
        }

        void Update()
        {
            if (GameSessionContext.CurrentBeatmap == null) return;

            var plan = GameSessionContext.RefreshPlan(includeKeyboardFallback);
            if (plan == null) return;

            var signature = BuildSignature(plan);
            if (signature == _lastSignature) return;
            _lastSignature = signature;

            LogPlan(plan);
        }

        static string BuildSignature(SessionPlan plan)
        {
            var sb = new StringBuilder();
            foreach (var slot in plan.Slots)
                sb.Append((int)slot.Instrument).Append(slot.IsKeyboardFallback ? 'K' : 'H').Append('|');
            sb.Append("/nc:");
            foreach (var i in plan.ControllersWithoutTrack) sb.Append((int)i).Append(',');
            sb.Append("/nt:");
            foreach (var i in plan.TracksWithoutController) sb.Append((int)i).Append(',');
            return sb.ToString();
        }

        static void LogPlan(SessionPlan plan)
        {
            if (plan.Slots.Count == 0)
            {
                Debug.LogWarning("[SessionSetupSmokeTest] No active slots - nothing playable right now overlaps this chart's instruments.");
            }
            else
            {
                Debug.Log($"[SessionSetupSmokeTest] Session changed - {plan.Slots.Count} active slot(s):");
                foreach (var slot in plan.Slots)
                {
                    var info = InstrumentCatalog.Get(slot.Instrument);
                    var noteCount = slot.Track.IsDrumTrack ? slot.Track.Hits.Count : slot.Track.Notes.Count;
                    Debug.Log($"    {info.DisplayName,-10} {(slot.IsKeyboardFallback ? "[keyboard]" : "[hardware]"),-10} " +
                              $"viewport=({slot.Viewport.x:0.00},{slot.Viewport.y:0.00},{slot.Viewport.width:0.00},{slot.Viewport.height:0.00}) " +
                              $"{noteCount} event(s) in this track.");
                }
            }

            if (plan.ControllersWithoutTrack.Count > 0)
                Debug.Log($"[SessionSetupSmokeTest] Playable but no part in this chart (won't appear this round): {DescribeList(plan.ControllersWithoutTrack)}");

            if (plan.TracksWithoutController.Count > 0)
                Debug.Log($"[SessionSetupSmokeTest] In this chart but nothing playing them right now (silent/unscored this round): {DescribeList(plan.TracksWithoutController)}");
        }

        static string DescribeList(System.Collections.Generic.List<InstrumentType> list)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(InstrumentCatalog.Get(list[i]).DisplayName);
            }
            return sb.ToString();
        }
    }
}
