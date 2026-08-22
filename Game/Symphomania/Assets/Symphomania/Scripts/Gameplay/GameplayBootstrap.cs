using System.Collections.Generic;
using UnityEngine;
using Symphomania.Beatmaps;
using Symphomania.Controllers;
using Symphomania.Session;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// The gameplay session runner: disables whatever camera is already in
    /// the scene (the lanes' own cameras cover 100% of the screen between
    /// them, per BandScreenLayout, so nothing else should render underneath),
    /// loads a beatmap, builds+freezes a SessionPlan against whatever's
    /// playable right now (real hardware plus the keyboard fallback's current
    /// instrument), and spins up one GameplayLane per active slot.
    ///
    /// Two ways to drive this:
    ///
    /// 1) MenuFlowController creates a fresh GameObject for each song, sets
    ///    autoStartOnPlay = false right after AddComponent (before Start()
    ///    fires), and calls Begin(fileName, hitWindowSeconds) itself once the
    ///    player confirms song + instruments + difficulty at instrument
    ///    entry - see that class for the "instrument entry" screen this
    ///    class's doc comment used to say wasn't built yet. Poll
    ///    IsSongComplete / FailedToStart afterward and destroy this
    ///    GameObject once the song's over (destroying it also tears down
    ///    every lane and the RhythmConductor, both parented under this
    ///    transform).
    ///
    /// 2) Standalone testing without going through the menu at all:
    ///    autoStartOnPlay defaults to true, so Start() just calls Begin()
    ///    itself with fileName/hitWindowSeconds as set in the Inspector -
    ///    pick your test instrument via the keyboard fallback's F1-F5 BEFORE
    ///    pressing Play (or just re-enter Play mode after switching), since
    ///    the session is frozen once and doesn't reshuffle mid-test. See
    ///    GameSessionContext's own doc comment for why that's deliberate
    ///    rather than an oversight.
    /// </summary>
    public class GameplayBootstrap : MonoBehaviour
    {
        [Tooltip("If true, Begin() runs automatically from Start() using fileName/hitWindowSeconds below - for quick standalone testing with no menu flow. MenuFlowController sets this false immediately after AddComponent and calls Begin(...) itself explicitly instead.")]
        public bool autoStartOnPlay = true;

        [Tooltip("Filename under StreamingAssets/Beatmaps.")]
        public string fileName = "twinkle_twinkle_sample.json";

        [Tooltip("Half-width of the generous, beginner-friendly hit window, in seconds. A note is judgeable from (note time - this) to (note time + this).")]
        public float hitWindowSeconds = 0.20f;

        [Tooltip("Seconds of scrolling lead-in before the first note reaches the judge line, so you can see notes/beat lines arrive rather than starting mid-song.")]
        public float leadInSeconds = 3f;

        /// <summary>True once the conductor's clock has reached the song's own duration - MenuFlowController polls this to know when to move to the results screen.</summary>
        public bool IsSongComplete { get; private set; }

        /// <summary>True if Begin() couldn't start a session at all (bad file, or no active slots) - MenuFlowController should treat this as "bounce back to instrument entry with an error", not "proceed to gameplay".</summary>
        public bool FailedToStart { get; private set; }

        /// <summary>Read-only view of the live per-instrument tally, for the results screen once IsSongComplete is true.</summary>
        public IReadOnlyDictionary<InstrumentType, (int perfect, int good, int miss)> Tally => _tally;

        /// <summary>Read-only view of the live per-instrument cumulative score (Perfect=300, Good=100, Miss=0 points per note), for the results screen and the in-lane HUD.</summary>
        public IReadOnlyDictionary<InstrumentType, int> Scores => _score;

        RhythmConductor _conductor;
        Beatmap _beatmap;
        readonly List<GameplayLane> _lanes = new List<GameplayLane>();
        readonly Dictionary<InstrumentType, (int perfect, int good, int miss)> _tally = new Dictionary<InstrumentType, (int, int, int)>();
        readonly Dictionary<InstrumentType, int> _score = new Dictionary<InstrumentType, int>();

        // The one scene camera Begin() disabled (if any), so it can be put
        // back exactly the way it was found once this session ends - see
        // OnDestroy. Without this, the scene's original camera (and whatever
        // else depends on Camera.main) stays permanently dark after every
        // single playthrough, since this bootstrap's own lane cameras -
        // the only thing rendering anything during the song - are destroyed
        // right along with the rest of this GameObject's hierarchy.
        Camera _disabledMainCamera;

        void Start()
        {
            if (autoStartOnPlay) Begin(fileName, hitWindowSeconds);
        }

        /// <summary>
        /// Loads beatmapFileName, builds+freezes a session plan, and spins up
        /// one GameplayLane per active slot using the given hit-window width.
        /// Safe to call at most once per GameplayBootstrap instance - create
        /// a fresh GameObject/component per song rather than reusing one.
        /// </summary>
        public void Begin(string beatmapFileName, float hitWindow)
        {
            fileName = beatmapFileName;
            hitWindowSeconds = hitWindow;

            // Disable just the rendering, not the whole GameObject - the default
            // scene camera usually carries the scene's one AudioListener, and
            // deactivating the whole object would silently take that with it,
            // leaving nothing able to hear any lane's PlayOneShot calls.
            // Remembered in _disabledMainCamera so OnDestroy can turn it back
            // on once this session's own lane cameras are gone.
            if (Camera.main != null)
            {
                _disabledMainCamera = Camera.main;
                _disabledMainCamera.enabled = false;
            }

            // Belt and suspenders: guarantee exactly one listener exists even if
            // the scene's camera never had one (or there's no camera at all).
            if (FindAnyObjectByType<AudioListener>() == null)
                gameObject.AddComponent<AudioListener>();

            try
            {
                _beatmap = BeatmapLoader.LoadFromStreamingAssets(fileName);
            }
            catch (BeatmapParseException e)
            {
                Debug.LogError($"[GameplayBootstrap] Failed to load '{fileName}': {e.Message}");
                FailedToStart = true;
                RestoreMainCamera(); // nothing else here will ever render - don't leave the scene dark until/unless something destroys this GameObject
                return;
            }

            GameSessionContext.SetBeatmap(_beatmap);
            var plan = GameSessionContext.RefreshPlan(includeKeyboardFallback: true);
            GameSessionContext.FreezePlan();

            if (plan == null || plan.Slots.Count == 0)
            {
                Debug.LogWarning("[GameplayBootstrap] No active slots - nothing playable overlaps this chart. " +
                                 "Plug in a controller, or check KeyboardInstrumentFallback.ActiveInstrument / F1-F5.");
                FailedToStart = true;
                RestoreMainCamera();
                return;
            }

            var conductorGO = new GameObject("RhythmConductor");
            conductorGO.transform.SetParent(transform, false); // so destroying this bootstrap's GameObject also tears down the conductor (and any PianoGuideTrack child of it)
            _conductor = conductorGO.AddComponent<RhythmConductor>();
            _conductor.Initialize(_beatmap.Song, leadInSeconds);

            if (IsPianoDerived(_beatmap))
            {
                conductorGO.AddComponent<PianoGuideTrack>().Initialize(_conductor, _beatmap);
                Debug.Log("[GameplayBootstrap] Piano-derived beatmap (\"Version P\") - background piano guide track enabled.");
            }

            for (int i = 0; i < plan.Slots.Count; i++)
            {
                var slot = plan.Slots[i];
                var laneGO = new GameObject($"Lane_{slot.Instrument}");
                laneGO.transform.SetParent(transform, false);
                var lane = laneGO.AddComponent<GameplayLane>();
                lane.Initialize(slot, _conductor, i, hitWindowSeconds, OnJudged);
                _lanes.Add(lane);
                _tally[slot.Instrument] = (0, 0, 0);
                _score[slot.Instrument] = 0;
            }

            Debug.Log($"[GameplayBootstrap] '{_beatmap.Song.Title}' - {plan.Slots.Count} lane(s) active, {leadInSeconds}s lead-in, " +
                      $"±{hitWindowSeconds * 1000:0}ms hit window.");

            _conductor.Play();
        }

        void Update()
        {
            // F1-F5 (and F12) work here too, exactly like ControllerDiagnostics
            // / MenuFlowController, in case this scene doesn't also contain
            // one of those - harmless to poll twice a frame if it does, since
            // wasPressedThisFrame reads are idempotent within one frame.
            KeyboardInstrumentFallback.PollInstrumentSwitch();

            if (_conductor != null && !IsSongComplete && _conductor.CurrentTime >= _beatmap.Song.DurationSeconds)
                IsSongComplete = true;
        }

        /// <summary>
        /// Puts back whichever scene camera Begin() disabled, if any -
        /// called both from the early-failure paths in Begin() (nothing else
        /// is going to render if this GameObject is never destroyed, e.g.
        /// standalone testing with autoStartOnPlay) and from OnDestroy (the
        /// normal path: song complete, or MenuFlowController tearing this
        /// down on Escape/failure). Safe to call more than once.
        /// </summary>
        void RestoreMainCamera()
        {
            if (_disabledMainCamera == null) return;
            _disabledMainCamera.enabled = true;
            _disabledMainCamera = null;
        }

        void OnDestroy()
        {
            // Without this, every single playthrough leaves the scene's
            // original camera disabled forever once this GameObject (and the
            // lane cameras that were the only thing actually rendering
            // anything) is destroyed - the game view goes dark and stays
            // dark for every subsequent menu screen, no matter how many more
            // songs get started afterward.
            RestoreMainCamera();
        }

        /// <summary>
        /// Both converter scripts append "Version P" (piano mode) or
        /// "Version S" (band mode) to song.title automatically - see
        /// beatmap_schema.md's "beatmap version tagging" section - so this is
        /// just reading that tag back, not re-deriving anything. Only a
        /// piano-mode beatmap has a real single-piano origin for
        /// PianoGuideTrack to reconstruct; a "Version S" chart is genuine
        /// separate per-instrument sheet music with no such source to play
        /// back, so this deliberately returns false for it.
        /// </summary>
        static bool IsPianoDerived(Beatmap beatmap) =>
            beatmap.Song.Title.Contains("Version P");

        /// <summary>Points awarded per judgement - Perfect 300, Good 100, Miss 0, matching this round's spec exactly.</summary>
        static int PointsFor(NoteJudgement judgement) => judgement switch
        {
            NoteJudgement.Perfect => 300,
            NoteJudgement.Good => 100,
            _ => 0,
        };

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

            int points = PointsFor(evt.Judgement);
            _score[instrument] = (_score.TryGetValue(instrument, out var s) ? s : 0) + points;

            Debug.Log($"[Judge] {instrument} note id={evt.Id} pitch={evt.Pitch ?? "-"} -> {evt.Judgement} " +
                      $"(error={evt.TimingErrorSeconds * 1000:+0;-0}ms, +{points} pts, total {_score[instrument]})");
        }

        // Cached GUIStyles - OnGUI runs every IMGUI event, so building fresh
        // GUIStyle objects here every call would just be avoidable garbage.
        GUIStyle _headerStyle;
        GUIStyle _statsStyle;
        GUIStyle _hudStyle;

        void EnsureGUIStyles()
        {
            if (_headerStyle != null) return;
            _headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _statsStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.UpperLeft, normal = { textColor = Color.white } };
            _hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(1f, 1f, 1f, 0.75f) } };
        }

        void OnGUI()
        {
            if (_conductor == null) return;
            EnsureGUIStyles();

            // Each lane draws its own instrument name + stats confined to its
            // own screen area (derived from that lane's own Viewport, the
            // same normalized rect its camera renders through) rather than
            // one shared box listing every player - so a glance at any one
            // player's own portion of the screen tells them their own name
            // and their own stats, nothing else's.
            foreach (var lane in _lanes)
            {
                var vp = lane.Viewport;
                // Viewport is bottom-left-origin (Camera.rect convention);
                // OnGUI's Rects are top-left-origin - flip Y once here so
                // everything drawn below lines up with what that camera
                // actually renders.
                var screenRect = new Rect(
                    vp.x * Screen.width,
                    (1f - vp.y - vp.height) * Screen.height,
                    vp.width * Screen.width,
                    vp.height * Screen.height);

                var t = _tally[lane.Instrument];
                int score = _score.TryGetValue(lane.Instrument, out var sc) ? sc : 0;
                string statsText = $"Perfect {t.perfect}   Good {t.good}   Miss {t.miss}   Score {score}";

                if (lane.Instrument == InstrumentType.Trombone)
                {
                    // The trombone's own lane scrolls sideways (see
                    // NoteLaneView) with its judgement line near the left
                    // edge of its strip - "top of the player's portion"
                    // doesn't read the same way there, so the name instead
                    // sits fixed to the left of that judgement line, rotated
                    // to run top-to-bottom alongside it.
                    var pivot = new Vector2(screenRect.x + 28f, screenRect.center.y);
                    var matrixBackup = GUI.matrix;
                    GUIUtility.RotateAroundPivot(-90f, pivot);
                    GUI.Label(new Rect(pivot.x - 100f, pivot.y - 14f, 200f, 28f), "Trombone", _headerStyle);
                    GUI.matrix = matrixBackup;

                    GUI.Label(new Rect(screenRect.x + 60f, screenRect.y + 6f, 320f, 24f), statsText, _statsStyle);
                }
                else
                {
                    GUI.Label(new Rect(screenRect.x, screenRect.y + 4f, screenRect.width, 28f),
                        InstrumentCatalog.Get(lane.Instrument).DisplayName, _headerStyle);
                    GUI.Label(new Rect(screenRect.x + 8f, screenRect.y + 32f, screenRect.width - 16f, 24f), statsText, _statsStyle);
                }
            }

            // Shared session HUD (timer, controls) isn't any one player's
            // information, so it stays in its own small corner overlay
            // rather than inside any lane's own screen area.
            GUI.Label(new Rect(8, Screen.height - 44, 620, 40),
                $"t = {_conductor.CurrentTime:0.00}s   F1-F5 switch keyboard instrument   " +
                "Hold the matching button(s), tap SPACE at the line.",
                _hudStyle);
        }
    }
}
