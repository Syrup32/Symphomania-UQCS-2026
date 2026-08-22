using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Symphomania.Beatmaps;
using Symphomania.Controllers;
using Symphomania.Gameplay;
using Symphomania.Session;

namespace Symphomania.Menu
{
    /// <summary>
    /// The whole pre-gameplay flow in one place, all IMGUI (consistent with
    /// GameplayBootstrap's own HUD - no UGUI Canvas/EventSystem needed): Title
    /// -> Song Select -> Instrument Entry -> Playing -> Results -> back to
    /// Song Select, matching the project's own stated gameplay flow. Every
    /// screen is mouse-driven (GUI.Button/GUI clicks are pointer-driven
    /// natively - no keyboard input required anywhere in this file except as
    /// an optional shortcut); the song list scrolls via GUI's own
    /// BeginScrollView/EndScrollView, which already supports both mouse-wheel
    /// and scrollbar-drag scrolling with no extra work.
    ///
    /// Drop this on one empty GameObject in the game's persistent scene,
    /// press Play - no manual scene wiring, no prefabs, consistent with every
    /// other entry point in this project. Do not also drop a standalone
    /// GameplayBootstrap in the same scene with autoStartOnPlay left true -
    /// this component creates and drives its own GameplayBootstrap instance
    /// once a song actually starts (see BeginGameplay), and a second one
    /// auto-starting on its own would fight it for the AudioListener/camera.
    /// </summary>
    public class MenuFlowController : MonoBehaviour
    {
        public enum Difficulty { Easy, Medium, Hard, Whiplash }

        // Deliberately NOT named "Screen" - UnityEngine.Screen (used all over
        // this file for Screen.width/height) would otherwise be shadowed by a
        // nested type of the same name, and the compiler prefers the nested
        // type over the "using UnityEngine" import.
        enum MenuScreen { Title, SongSelect, InstrumentEntry, Playing, Results }

        /// <summary>The four difficulty presets from this delivery's spec, as hit-window half-widths (a note is judgeable from note_time-this to note_time+this - see HitJudge).</summary>
        static float HitWindowSecondsFor(Difficulty d) => d switch
        {
            Difficulty.Easy => 0.3f,
            Difficulty.Medium => 0.2f,
            Difficulty.Hard => 0.1f,
            Difficulty.Whiplash => 0.03f,
            _ => 0.2f,
        };

        static readonly InstrumentType[] AllInstruments =
        {
            InstrumentType.Trumpet, InstrumentType.Saxophone, InstrumentType.DrumKit,
            InstrumentType.Violin, InstrumentType.Trombone,
        };

        static readonly Difficulty[] AllDifficulties =
        {
            Difficulty.Easy, Difficulty.Medium, Difficulty.Hard, Difficulty.Whiplash,
        };

        [Tooltip("Seconds of scrolling lead-in passed to GameplayBootstrap once a song starts.")]
        public float leadInSeconds = 3f;

        [Tooltip("0-1 volume for the looping menu music track. Loaded from Resources/MenuMusic (drop a 10-second looping .wav at Assets/Resources/MenuMusic.wav) - plays during Song Select and Instrument Entry, silent on Title, Playing, and Results.")]
        public float menuMusicVolume = 0.6f;

        MenuScreen _screen = MenuScreen.Title;

        AudioSource _menuMusic;

        List<BeatmapListing> _songs = new List<BeatmapListing>();
        Vector2 _songScrollPos;
        string _selectedFileName;
        Beatmap _selectedBeatmap;
        Difficulty _selectedDifficulty = Difficulty.Medium;
        string _errorMessage;

        GameplayBootstrap _bootstrap;
        Dictionary<InstrumentType, (int perfect, int good, int miss)> _resultTally;
        Dictionary<InstrumentType, int> _resultScores;
        string _resultSongTitle;

        void Start()
        {
            // .wav files import as ordinary Unity AudioClips through Unity's
            // own asset pipeline - no custom parsing needed here (unlike the
            // StreamingAssets sample libraries, which read raw .wav bytes at
            // runtime specifically to bypass that pipeline for per-pitch
            // scanning). Just drop the file at Assets/Resources/MenuMusic.wav
            // and this picks it up by name.
            var clip = Resources.Load<AudioClip>("MenuMusic");
            if (clip == null)
            {
                Debug.LogWarning("[MenuFlowController] No menu music clip found at Resources/MenuMusic - " +
                                  "drop a 10-second looping .wav at Assets/Resources/MenuMusic.wav to enable menu music.");
                return;
            }

            _menuMusic = gameObject.AddComponent<AudioSource>();
            _menuMusic.clip = clip;
            _menuMusic.loop = true;
            _menuMusic.playOnAwake = false;
            _menuMusic.spatialBlend = 0f; // menu music isn't positioned in any one lane's world space
            _menuMusic.volume = menuMusicVolume;
        }

        void Update()
        {
            // Keeps the keyboard fallback's F1-F5/F12 working from every
            // screen too - instrument entry's mouse buttons below are the
            // primary way to pick an instrument, but the F-keys still work
            // as a shortcut, matching every other entry point in this
            // project (GameplayBootstrap, ControllerDiagnostics).
            KeyboardInstrumentFallback.PollInstrumentSwitch();

            UpdateMenuMusic();

            if (_screen == MenuScreen.SongSelect && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                _errorMessage = null;
                _screen = MenuScreen.Title;
                return;
            }

            if (_screen == MenuScreen.Playing && _bootstrap != null)
            {
                // Item 4 of this round's request: let a player bail out of a
                // song mid-performance and land back on song select, rather
                // than being stuck until the song finishes or fails to start.
                // Deliberately skips Results - an interrupted song has no
                // "how did I do" moment worth showing.
                if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                {
                    Destroy(_bootstrap.gameObject);
                    _bootstrap = null;
                    GameSessionContext.UnfreezePlan();
                    _errorMessage = null;
                    RefreshSongList();
                    _screen = MenuScreen.SongSelect;
                    return;
                }

                if (_bootstrap.FailedToStart)
                {
                    _errorMessage = "Couldn't start that song - check the Console for details.";
                    Destroy(_bootstrap.gameObject);
                    _bootstrap = null;
                    _screen = MenuScreen.InstrumentEntry;
                }
                else if (_bootstrap.IsSongComplete)
                {
                    _resultTally = new Dictionary<InstrumentType, (int, int, int)>(_bootstrap.Tally);
                    _resultScores = new Dictionary<InstrumentType, int>(_bootstrap.Scores);
                    Destroy(_bootstrap.gameObject);
                    _bootstrap = null;
                    _screen = MenuScreen.Results;
                }
            }
        }

        /// <summary>
        /// Keeps the looping menu track playing exactly on Song Select and
        /// Instrument Entry, silent everywhere else (Title, Playing,
        /// Results) - checked every frame against whatever _screen actually
        /// is, rather than started/stopped by hand at each individual
        /// transition. That's deliberate: this project has several different
        /// ways to land on Song Select (Title's button, finishing a song,
        /// the Back button from instrument entry, and now Escape from both
        /// instrument entry and song select itself) - a per-frame check here
        /// can't miss one of those paths the way scattering Play()/Stop()
        /// calls across every transition site could.
        /// </summary>
        void UpdateMenuMusic()
        {
            if (_menuMusic == null) return;

            bool shouldPlay = _screen == MenuScreen.SongSelect || _screen == MenuScreen.InstrumentEntry;
            if (shouldPlay && !_menuMusic.isPlaying)
                _menuMusic.Play(); // Stop() (below) resets playback position to 0, so this always restarts from the top
            else if (!shouldPlay && _menuMusic.isPlaying)
                _menuMusic.Stop();
        }

        // ---- GUIStyles, built once - OnGUI runs every IMGUI event, so
        // building fresh GUIStyle objects every call would just be avoidable
        // garbage (same reasoning as GameplayBootstrap's own EnsureGUIStyles). ----
        GUIStyle _titleStyle, _bigButtonStyle, _listButtonStyle, _headerStyle, _bodyStyle, _errorStyle, _toggleOnStyle, _toggleOffStyle;

        void EnsureStyles()
        {
            if (_titleStyle != null) return;
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 42, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            _bigButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 22, fixedHeight = 56 };
            _listButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, alignment = TextAnchor.MiddleLeft, fixedHeight = 40 };
            _headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };
            _errorStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = new Color(1f, 0.4f, 0.4f) } };
            _toggleOnStyle = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 44, fontStyle = FontStyle.Bold, normal = { textColor = Color.black, background = GUI.skin.button.active.background } };
            _toggleOffStyle = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 44 };
        }

        void OnGUI()
        {
            EnsureStyles();

            switch (_screen)
            {
                case MenuScreen.Title: DrawTitle(); break;
                case MenuScreen.SongSelect: DrawSongSelect(); break;
                case MenuScreen.InstrumentEntry: DrawInstrumentEntry(); break;
                case MenuScreen.Playing: DrawPlayingOverlay(); break;
                case MenuScreen.Results: DrawResults(); break;
            }
        }

        // ==================== Title ====================

        void DrawTitle()
        {
            GUI.Label(new Rect(0, Screen.height * 0.3f, Screen.width, 80), "Symphomania", _titleStyle);

            var buttonRect = new Rect(Screen.width / 2f - 140, Screen.height * 0.55f, 280, 64);
            if (GUI.Button(buttonRect, "Click to Start", _bigButtonStyle))
            {
                RefreshSongList();
                _errorMessage = null;
                _screen = MenuScreen.SongSelect;
            }
        }

        // ==================== Song select ====================

        void RefreshSongList()
        {
            // Re-scans StreamingAssets/Beatmaps every time this screen is
            // entered - dropping a new .json file in there (item 4 of this
            // round's request) is all it takes for a song to show up here,
            // no rebuild/registration step.
            _songs = BeatmapLoader.ListStreamingAssetsBeatmaps();
        }

        void DrawSongSelect()
        {
            GUI.Label(new Rect(20, 16, 600, 40), "Select a Song", _headerStyle);

            if (GUI.Button(new Rect(Screen.width - 140, 16, 120, 32), "Rescan"))
                RefreshSongList();

            var listRect = new Rect(20, 70, Mathf.Min(700, Screen.width - 40), Screen.height - 110);
            var contentHeight = Mathf.Max(listRect.height, _songs.Count * 44);
            var contentRect = new Rect(0, 0, listRect.width - 24, contentHeight);

            // GUI.BeginScrollView/EndScrollView already handles mouse-wheel
            // and scrollbar-drag scrolling on its own - nothing extra needed
            // for item 3 of this round's request ("songs can be browsed by
            // scrolling").
            _songScrollPos = GUI.BeginScrollView(listRect, _songScrollPos, contentRect);

            if (_songs.Count == 0)
            {
                GUI.Label(new Rect(0, 0, contentRect.width, 30),
                    "No beatmap files found under StreamingAssets/Beatmaps. Drop a .json file there and hit Rescan.", _bodyStyle);
            }
            else
            {
                for (int i = 0; i < _songs.Count; i++)
                {
                    var entry = _songs[i];
                    var rowRect = new Rect(0, i * 44, contentRect.width, 40);
                    if (GUI.Button(rowRect, entry.Title, _listButtonStyle))
                        SelectSong(entry);
                }
            }

            GUI.EndScrollView();

            if (!string.IsNullOrEmpty(_errorMessage))
                GUI.Label(new Rect(20, Screen.height - 34, Screen.width - 40, 28), _errorMessage, _errorStyle);
        }

        void SelectSong(BeatmapListing entry)
        {
            try
            {
                _selectedBeatmap = BeatmapLoader.LoadFromStreamingAssets(entry.FileName);
            }
            catch (BeatmapParseException e)
            {
                _errorMessage = $"Couldn't load '{entry.FileName}': {e.Message}";
                Debug.LogError($"[MenuFlowController] {_errorMessage}");
                return;
            }

            _selectedFileName = entry.FileName;

            // Sets up GameSessionContext so instrument entry's live
            // RefreshPlan calls below have a beatmap to intersect against -
            // GameplayBootstrap.Begin() calls SetBeatmap again itself once
            // Start Song is pressed (see that method), which is harmless and
            // keeps GameplayBootstrap fully self-contained/testable on its
            // own. ClearManualOverridesForNewSong() is the one that actually
            // resets per-instrument on/off toggles - it must be called here,
            // at the "a new song was picked" moment, NOT from SetBeatmap
            // itself, since Begin()'s own SetBeatmap re-call happens AFTER
            // the player has already made their instrument entry choices.
            GameSessionContext.SetBeatmap(_selectedBeatmap);
            GameSessionContext.ClearManualOverridesForNewSong();
            _errorMessage = null;
            _screen = MenuScreen.InstrumentEntry;
        }

        // ==================== Instrument entry ====================

        void DrawInstrumentEntry()
        {
            // Live preview only - never frozen here. GameplayBootstrap.Begin()
            // does its own RefreshPlan+FreezePlan once Start Song is actually
            // pressed, reading whatever's true at that exact moment rather
            // than whatever this particular OnGUI frame happened to see.
            var plan = GameSessionContext.RefreshPlan(includeKeyboardFallback: true);

            GUI.Label(new Rect(20, 16, Screen.width - 260, 36),
                _selectedBeatmap != null ? _selectedBeatmap.Song.Title : "(no song selected)", _headerStyle);

            // Item 1 of this round's request: a mouse-driven on/off switch for
            // the keyboard fallback as a whole, sharing the same underlying
            // flag KeyboardInstrumentFallback's F12 shortcut already flips -
            // so the two stay in sync no matter which one a player uses.
            bool keyboardOn = VirtualBandInput.KeyboardFallbackEnabled;
            if (GUI.Button(new Rect(Screen.width - 240, 16, 220, 36),
                    keyboardOn ? "Keyboard Fallback: ON" : "Keyboard Fallback: OFF",
                    keyboardOn ? _toggleOnStyle : _toggleOffStyle))
                VirtualBandInput.KeyboardFallbackEnabled = !keyboardOn;

            GUI.Label(new Rect(20, 60, 400, 24), "Keyboard stands in for:", _bodyStyle);
            GUI.enabled = keyboardOn;
            for (int i = 0; i < AllInstruments.Length; i++)
            {
                var rect = new Rect(20 + i * 150, 88, 140, 44);
                bool isActive = KeyboardInstrumentFallback.ActiveInstrument == AllInstruments[i];
                if (GUI.Button(rect, InstrumentCatalog.Get(AllInstruments[i]).DisplayName, isActive ? _toggleOnStyle : _toggleOffStyle))
                    KeyboardInstrumentFallback.ActiveInstrument = AllInstruments[i];
            }
            GUI.enabled = true;

            // Item 2 of this round's request: every controller that's
            // actually playable right now (real hardware, or the keyboard
            // standing in per the row above) gets its own on/off button here,
            // independent of whether it's physically plugged in - so a table
            // full of connected controllers can still choose "just these
            // three tonight" without unplugging anything. Toggling one calls
            // GameSessionContext.SetManuallyDisabled, which RefreshPlan reads
            // every frame above.
            float y = 140f;
            GUI.Label(new Rect(20, y, 500, 24), "Controllers in this session:", _bodyStyle);
            y += 26f;
            var playable = VirtualBandInput.PlayableInstruments();
            for (int i = 0; i < AllInstruments.Length; i++)
            {
                var instrument = AllInstruments[i];
                bool isPlayable = playable.Contains(instrument);
                bool isEnabled = isPlayable && !GameSessionContext.IsManuallyDisabled(instrument);
                var rect = new Rect(20 + i * 150, y, 140, 44);

                GUI.enabled = isPlayable;
                string label = InstrumentCatalog.Get(instrument).DisplayName + (isPlayable ? (isEnabled ? "\n(in use)" : "\n(sitting out)") : "\n(not present)");
                if (GUI.Button(rect, label, isEnabled ? _toggleOnStyle : _toggleOffStyle))
                    GameSessionContext.SetManuallyDisabled(instrument, isEnabled);
                GUI.enabled = true;
            }
            y += 50f;

            GUI.Label(new Rect(20, y, 500, 24), "Active this round:", _bodyStyle);
            y += 26f;

            if (plan != null)
            {
                foreach (var slot in plan.Slots)
                {
                    string tag = slot.IsKeyboardFallback ? " (keyboard)" : " (controller)";
                    GUI.Label(new Rect(36, y, 500, 22), $"✓ {InstrumentCatalog.Get(slot.Instrument).DisplayName}{tag}", _bodyStyle);
                    y += 24f;
                }

                if (plan.Slots.Count == 0)
                {
                    GUI.Label(new Rect(36, y, 600, 22), "(nothing active yet - plug in a controller, or pick one above.)", _bodyStyle);
                    y += 24f;
                }

                if (plan.TracksWithoutController.Count > 0)
                {
                    y += 6f;
                    GUI.Label(new Rect(20, y, Screen.width - 40, 22),
                        "This chart also has a part for: " + string.Join(", ", NamesOf(plan.TracksWithoutController)) + " - not playing this round.",
                        _bodyStyle);
                    y += 24f;
                }
            }

            // Difficulty select.
            y += 16f;
            GUI.Label(new Rect(20, y, 300, 24), "Difficulty:", _bodyStyle);
            y += 26f;
            for (int i = 0; i < AllDifficulties.Length; i++)
            {
                var rect = new Rect(20 + i * 150, y, 140, 44);
                bool isSelected = _selectedDifficulty == AllDifficulties[i];
                string label = $"{AllDifficulties[i]}\n±{HitWindowSecondsFor(AllDifficulties[i]) * 1000:0}ms";
                if (GUI.Button(rect, label, isSelected ? _toggleOnStyle : _toggleOffStyle))
                    _selectedDifficulty = AllDifficulties[i];
            }

            if (GUI.Button(new Rect(20, Screen.height - 70, 160, 50), "Back", _bigButtonStyle))
            {
                _screen = MenuScreen.SongSelect;
                return;
            }

            bool canStart = plan != null && plan.Slots.Count > 0;
            GUI.enabled = canStart;
            if (GUI.Button(new Rect(Screen.width - 220, Screen.height - 70, 200, 50), "Start Song", _bigButtonStyle))
                BeginGameplay();
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(_errorMessage))
                GUI.Label(new Rect(20, Screen.height - 100, Screen.width - 40, 26), _errorMessage, _errorStyle);
        }

        static IEnumerable<string> NamesOf(List<InstrumentType> instruments)
        {
            foreach (var i in instruments) yield return InstrumentCatalog.Get(i).DisplayName;
        }

        void BeginGameplay()
        {
            var go = new GameObject("GameplaySession");
            _bootstrap = go.AddComponent<GameplayBootstrap>();
            _bootstrap.autoStartOnPlay = false; // set before Start() ever fires - AddComponent doesn't invoke Start() synchronously
            _bootstrap.leadInSeconds = leadInSeconds;
            _bootstrap.Begin(_selectedFileName, HitWindowSecondsFor(_selectedDifficulty));

            if (_bootstrap.FailedToStart)
            {
                _errorMessage = "Couldn't start that song - check the Console for details.";
                Destroy(go);
                _bootstrap = null;
                return;
            }

            _resultSongTitle = _selectedBeatmap != null ? _selectedBeatmap.Song.Title : null;
            _errorMessage = null;
            _screen = MenuScreen.Playing;
        }

        // ==================== Playing ====================

        void DrawPlayingOverlay()
        {
            // GameplayBootstrap draws its own in-lane HUD (headers/stats,
            // including each player's running score) through its own OnGUI -
            // nothing to draw here. This screen state exists so Update()
            // above has somewhere to poll IsSongComplete/FailedToStart/Escape
            // from.
            GUI.Label(new Rect(8, 8, 260, 24), "Esc: back to Song Select", _bodyStyle);
        }

        // ==================== Results ====================

        void DrawResults()
        {
            GUI.Label(new Rect(0, 60, Screen.width, 50),
                string.IsNullOrEmpty(_resultSongTitle) ? "Results" : $"Results - {_resultSongTitle}", _titleStyle);

            float y = 160f;
            if (_resultTally != null)
            {
                foreach (var kv in _resultTally)
                {
                    var t = kv.Value;
                    int score = _resultScores != null && _resultScores.TryGetValue(kv.Key, out var sc) ? sc : 0;
                    string line = $"{InstrumentCatalog.Get(kv.Key).DisplayName,-10}   Perfect {t.perfect,3}   Good {t.good,3}   Miss {t.miss,3}   Score {score,6}";
                    GUI.Label(new Rect(Screen.width / 2f - 260, y, 520, 28), line, _bodyStyle);
                    y += 30f;
                }
            }

            var buttonRect = new Rect(Screen.width / 2f - 160, Screen.height - 100, 320, 60);
            if (GUI.Button(buttonRect, "Return to Song Select", _bigButtonStyle))
            {
                RefreshSongList();
                _resultTally = null;
                _resultScores = null;
                _errorMessage = null;
                _screen = MenuScreen.SongSelect;
            }
        }
    }
}
