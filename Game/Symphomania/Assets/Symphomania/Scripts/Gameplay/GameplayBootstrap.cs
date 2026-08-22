using System.Collections.Generic;
using UnityEngine;
using Symphomania.Beatmaps;
using Symphomania.Controllers;
using Symphomania.Session;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// Entry point for trying out scrolling + judging with nothing built yet
    /// besides the scripts in this drop - no manual scene wiring, no prefabs,
    /// no art. Drop this on one empty GameObject, press Play.
    ///
    /// What it does, in order: disables whatever camera is already in the
    /// scene (the lanes' own cameras cover 100% of the screen between them,
    /// per BandScreenLayout, so nothing else should render underneath),
    /// loads a beatmap, builds+freezes a SessionPlan against whatever's
    /// playable right now (real hardware plus the keyboard fallback's current
    /// instrument), and spins up one GameplayLane per active slot.
    ///
    /// This is a diagnostic/smoke-test entry point, not the real gameplay
    /// scene - pick your test instrument via the keyboard fallback's F1-F5
    /// BEFORE pressing Play (or just re-enter Play mode after switching), since
    /// the session is frozen once and doesn't reshuffle mid-test. See
    /// GameSessionContext's own doc comment for why that's deliberate rather
    /// than an oversight.
    /// </summary>
    public class GameplayBootstrap : MonoBehaviour
    {
        [Tooltip("Filename under StreamingAssets/Beatmaps.")]
        public string fileName = "twinkle_twinkle_sample.json";

        [Tooltip("Half-width of the generous, beginner-friendly hit window, in seconds. A note is judgeable from (note time - this) to (note time + this).")]
        public float hitWindowSeconds = 0.20f;

        [Tooltip("Seconds of scrolling lead-in before the first note reaches the judge line, so you can see notes/beat lines arrive rather than starting mid-song.")]
        public float leadInSeconds = 3f;

        RhythmConductor _conductor;
        readonly List<GameplayLane> _lanes = new List<GameplayLane>();
        readonly Dictionary<InstrumentType, (int perfect, int good, int miss)> _tally = new Dictionary<InstrumentType, (int, int, int)>();

        void Start()
        {
            // Disable just the rendering, not the whole GameObject - the default
            // scene camera usually carries the scene's one AudioListener, and
            // deactivating the whole object would silently take that with it,
            // leaving nothing able to hear any lane's PlayOneShot calls.
            if (Camera.main != null)
                Camera.main.enabled = false;

            // Belt and suspenders: guarantee exactly one listener exists even if
            // the scene's camera never had one (or there's no camera at all).
            if (FindAnyObjectByType<AudioListener>() == null)
                gameObject.AddComponent<AudioListener>();

            Beatmap beatmap;
            try
            {
                beatmap = BeatmapLoader.LoadFromStreamingAssets(fileName);
            }
            catch (BeatmapParseException e)
            {
                Debug.LogError($"[GameplayBootstrap] Failed to load '{fileName}': {e.Message}");
                enabled = false;
                return;
            }

            GameSessionContext.SetBeatmap(beatmap);
            var plan = GameSessionContext.RefreshPlan(includeKeyboardFallback: true);
            GameSessionContext.FreezePlan();

            if (plan == null || plan.Slots.Count == 0)
            {
                Debug.LogWarning("[GameplayBootstrap] No active slots - nothing playable overlaps this chart. " +
                                 "Plug in a controller, or check KeyboardInstrumentFallback.ActiveInstrument / F1-F5.");
                return;
            }

            var conductorGO = new GameObject("RhythmConductor");
            _conductor = conductorGO.AddComponent<RhythmConductor>();
            _conductor.Initialize(beatmap.Song, leadInSeconds);

            for (int i = 0; i < plan.Slots.Count; i++)
            {
                var slot = plan.Slots[i];
                var laneGO = new GameObject($"Lane_{slot.Instrument}");
                laneGO.transform.SetParent(transform, false);
                var lane = laneGO.AddComponent<GameplayLane>();
                lane.Initialize(slot, _conductor, i, hitWindowSeconds, OnJudged);
                _lanes.Add(lane);
                _tally[slot.Instrument] = (0, 0, 0);
            }

            Debug.Log($"[GameplayBootstrap] '{beatmap.Song.Title}' - {plan.Slots.Count} lane(s) active, {leadInSeconds}s lead-in, " +
                      $"±{hitWindowSeconds * 1000:0}ms hit window.");

            _conductor.Play();
        }

        void Update()
        {
            // F1-F5 (and F12) work here too, exactly like ControllerDiagnostics,
            // in case this scene doesn't also contain that diagnostics object.
            KeyboardInstrumentFallback.PollInstrumentSwitch();
        }

        void OnJudged(InstrumentType instrument, JudgeEvent evt)
        {
            var t = _tally.TryGetValue(instrument, out var existing) ? existing : (0, 0, 0);
            switch (evt.Judgement)
            {
                case NoteJudgement.Perfect: t.Item1++; break;
                case NoteJudgement.Good: t.Item2++; break;
                case NoteJudgement.Miss: t.Item3++; break;
            }
            _tally[instrument] = t;

            Debug.Log($"[Judge] {instrument} note id={evt.Id} pitch={evt.Pitch ?? "-"} -> {evt.Judgement} " +
                      $"(error={evt.TimingErrorSeconds * 1000:+0;-0}ms)");
        }

        void OnGUI()
        {
            if (_conductor == null) return;

            var style = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };
            var box = new GUIStyle(GUI.skin.box);

            GUILayout.BeginArea(new Rect(10, 10, 420, 30 + 24 * (_lanes.Count + 3)), box);
            GUILayout.Label($"t = {_conductor.CurrentTime:0.00}s   F1-F5 switch keyboard instrument", style);
            foreach (var lane in _lanes)
            {
                var t = _tally[lane.Instrument];
                GUILayout.Label($"{InstrumentCatalog.Get(lane.Instrument).DisplayName,-10} Perfect {t.perfect,3}  Good {t.good,3}  Miss {t.miss,3}", style);
            }
            GUILayout.Label("Each column = one button (digit shown under its ring). Hold the matching digit(s), tap SPACE at the line.", style);
            GUILayout.Label("Dot reaches its ring = hit window; green flash = hit, red flash = miss.", style);
            GUILayout.EndArea();
        }
    }
}
