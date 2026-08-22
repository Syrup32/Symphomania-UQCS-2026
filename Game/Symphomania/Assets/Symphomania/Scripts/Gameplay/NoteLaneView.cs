using System.Collections.Generic;
using UnityEngine;
using Symphomania.Controllers;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// The visual half of "Next steps" item 3 - a DDR/piano-tiles-style
    /// scrolling lane: one column per fingering signal, in physical HID button
    /// order (see controller_hid_protocol.md - button 1 is the check input on
    /// every instrument but drums, so fingering bit 0 is button 2, and so on),
    /// each with its own color and its own static receptor ring at the judge
    /// line. This mirrors the real controllers, which have colored paddles on
    /// their physical buttons - the on-screen column colors and left-to-right
    /// order are meant to match those paddles so a player can glance at the
    /// lane the same way they'd glance at their own hands. A note is one
    /// filled dot per column its fingering mask has set, scrolling together
    /// and landing on the matching-colored receptor, with a static button-number
    /// label under each receptor for anyone still learning the mapping. A
    /// faint line every beat and a bold line every measure scroll underneath
    /// at the same speed, for a constant visual tempo reference.
    ///
    /// Column 0 is a dedicated neutral "open/rest" column for a note whose
    /// fingering mask is all-zero (e.g. an open trumpet valve combination) -
    /// otherwise a rest note would have nowhere to render at all. The drum kit
    /// never uses it (every hit has exactly one pad bit set), so it just sits
    /// unused for that instrument.
    ///
    /// Orientation: every instrument except the trombone scrolls vertically
    /// (top-down, per the project's spec) with columns spread left-to-right.
    /// The trombone gets its own sideways layout instead - one ROW per slide
    /// position, notes scrolling right-to-left onto a judge line near the
    /// left edge - matching the user's own sketch of a dedicated trombone
    /// play area rather than forcing it into the same vertical-column shape
    /// as everyone else. Internally this is done by treating one axis as
    /// "primary" (the scroll axis - Y for everyone else, X for the trombone)
    /// and the other as "secondary" (the column/row spread axis - X for
    /// everyone else, Y for the trombone), and mapping (primary, secondary)
    /// to a local Vector3 via LocalPos(). Every position/scroll calculation
    /// in this file is written in terms of primary/secondary so the same code
    /// drives both layouts - see LocalPos, ScrollPrimary, SecondaryPos.
    ///
    /// Placeholder programmer art throughout (RuntimeSprite's circles/square,
    /// tinted and scaled) - no texture assets required to see this run.
    ///
    /// All positioning here is in LOCAL space. The parent transform (set up by
    /// GameplayLane) is what actually separates one lane's world position from
    /// another's, so this component never needs to know its own lane's world
    /// offset.
    /// </summary>
    public class NoteLaneView : MonoBehaviour
    {
        [Header("Layout")]
        [Tooltip("Half-extent along the SECONDARY axis (the column/row spread) - X for a vertical (column) lane, Y for the trombone's horizontal (row) lane.")]
        public float laneHalfWidth = 2f;

        [Tooltip("Judge line position along the PRIMARY (scroll) axis - a Y coordinate for a vertical lane (near the bottom), an X coordinate for the trombone's horizontal lane (near the left edge, since it scrolls right-to-left). Auto-defaulted for the trombone in Initialize; override afterward if you need to retune it.")]
        public float judgeLinePos = -2.5f;

        /// <summary>
        /// +1 = notes travel toward increasing primary-axis coordinate as they
        /// approach the judge line (i.e. arrive from the negative side). -1 =
        /// the reverse (arrive from the positive side) - this is the default,
        /// which gives top-down scrolling for every vertical lane (notes
        /// spawn above and fall toward a judge line near the bottom, per the
        /// project's own "top-down scrolling rhythm game" spec) and,
        /// identically, right-to-left scrolling for the trombone's horizontal
        /// lane (notes spawn to the right and travel toward a judge line near
        /// the left) - the same sign works for both because "primary axis"
        /// already encodes which screen axis that is per orientation.
        /// </summary>
        public int scrollDirection = -1;

        public float scrollSpeed = 4f; // world units per second, along the primary axis

        /// <summary>How far ahead of the judge line (in world units, along the primary axis) a note becomes visible/spawns.</summary>
        public float spawnMarginPrimary = 6f;

        /// <summary>How far past the judge line (along the primary axis) a marker is allowed to travel before being force-cleaned-up (safety net only - judged notes are removed well before this).</summary>
        public float despawnMarginPrimary = 2f;

        public float circleDiameter = 0.4f;

        [Header("Colors")]
        public Color hitColor = new Color(0.3f, 1f, 0.3f);
        public Color missColor = new Color(1f, 0.3f, 0.3f);
        public Color openColumnColor = new Color(0.6f, 0.6f, 0.6f);
        public Color judgeLineBarColor = new Color(1f, 1f, 1f, 0.8f);
        public Color quarterBeatColor = new Color(1f, 1f, 1f, 0.12f);
        public Color measureColor = new Color(1f, 1f, 1f, 0.5f);
        public Color columnLabelColor = Color.white;

        [Header("Live input glow")]
        [Tooltip("Bright outline color shown around a column's receptor the instant that physical button/pad is pressed - completely independent of the beatmap/judging, so players can see their input is being read even when nothing is due.")]
        public Color inputGlowColor = new Color(1f, 1f, 1f, 0.95f);

        [Tooltip("How much bigger than the receptor ring the glow outline is drawn.")]
        public float glowScale = 1.4f;

        [Tooltip("Drum pads are edge-triggered (struck this frame only, not held) - hold their glow visible this long so a fast tap is still visible to the eye.")]
        public float drumGlowHoldSeconds = 0.12f;

        List<Judgeable> _items;
        List<BeatGridLine> _gridLines;
        RhythmConductor _conductor;
        InstrumentType _instrument;
        bool _isDrumKit;

        /// <summary>True only for the trombone - see the class doc comment's Orientation section.</summary>
        bool _horizontal;

        int _fingeringCount;
        int _totalColumns; // fingeringCount + 1 (the +1 is the open/rest column)
        Color[] _columnColors;

        // Live-input glow state. Index 0 (the open/rest column) is unused -
        // there's no physical button behind it, so it never glows.
        SpriteRenderer[] _glowRenderers;
        float[] _glowHoldUntil;

        int _nextSpawnIndex;
        readonly Dictionary<int, GameObject> _activeMarkers = new Dictionary<int, GameObject>();
        readonly List<(GameObject go, float expireAt)> _expiring = new List<(GameObject, float)>();

        GameObject[] _gridPool;
        const int GridPoolSize = 96; // generous: even a fast song rarely needs more simultaneous visible beat lines than this

        float LookAheadSeconds => spawnMarginPrimary / Mathf.Max(0.01f, scrollSpeed);

        public void Initialize(RhythmConductor conductor, List<Judgeable> items, List<BeatGridLine> gridLines, InstrumentType instrument)
        {
            _conductor = conductor;
            _items = items;
            _gridLines = gridLines;
            _nextSpawnIndex = 0;
            _instrument = instrument;
            _isDrumKit = instrument == InstrumentType.DrumKit;
            _horizontal = instrument == InstrumentType.Trombone;

            // The trombone's screen area (a full-width, short strip - see
            // BandScreenLayout) is a very different shape from everyone
            // else's tall narrow column, so the vertical-lane defaults above
            // don't fit it. These numbers are a reasonable starting point,
            // not a measured fit - retune scrollSpeed/laneHalfWidth/
            // judgeLinePos here if the trombone's strip ends up a different
            // aspect ratio than expected once you see it running.
            if (_horizontal)
            {
                laneHalfWidth = 1.4f;
                judgeLinePos = -6f;
                spawnMarginPrimary = 10f;
                scrollSpeed = 7f;
            }

            _fingeringCount = InstrumentCatalog.Get(instrument).FingeringCount;
            _totalColumns = _fingeringCount + 1;
            BuildColumnColors();

            _glowRenderers = new SpriteRenderer[_totalColumns];
            _glowHoldUntil = new float[_totalColumns];

            CreateJudgeLineBar();
            CreateReceptors();
            CreateGridPool();
        }

        // Real confirmed paddle colors for violin, saxophone, and the drum
        // kit (reported directly off the physical controllers) - trumpet and
        // trombone don't have a confirmed set yet, so they still fall back
        // to a placeholder rainbow spread (red first, progressing through
        // the hue wheel) below. Column 0 (open/rest) is grey for every
        // instrument that has one - openColumnColor - which was already the
        // right call and needed no change.
        static readonly Color PaddleRed = new Color(0.85f, 0.15f, 0.15f);
        static readonly Color PaddleOrange = new Color(1f, 0.55f, 0.05f);
        static readonly Color PaddleYellow = new Color(1f, 0.9f, 0.1f);
        static readonly Color PaddleGreen = new Color(0.15f, 0.8f, 0.25f);
        static readonly Color PaddleBlue = new Color(0.15f, 0.4f, 0.95f);
        static readonly Color PaddlePurple = new Color(0.55f, 0.15f, 0.85f);
        static readonly Color PaddlePink = new Color(1f, 0.45f, 0.75f);
        static readonly Color PaddleWhite = new Color(0.95f, 0.95f, 0.95f);
        static readonly Color PaddleGrey = new Color(0.6f, 0.6f, 0.6f);

        /// <summary>
        /// Real paddle colors in HID button order - index 0 is the FIRST
        /// FINGERING BIT, which is HID button 2 for violin/saxophone (button
        /// 1 is reserved for the check input on those - see
        /// controller_hid_protocol.md) but is button 1 for the drum kit,
        /// which has no reserved check button at all ("pad N = button N, no
        /// offset" - the drum kit's own exception, also why
        /// CreateReceptors labels its columns differently). Returns null for
        /// an instrument with no confirmed real colors yet (trumpet,
        /// trombone), so BuildColumnColors falls back to a placeholder
        /// rainbow for those.
        /// </summary>
        static Color[] FixedPaddleColors(InstrumentType instrument) => instrument switch
        {
            // Button 2=red, 3=orange, 4=yellow, 5=green, 6=blue, 7=purple, 8=pink.
            InstrumentType.Violin => new[] { PaddleRed, PaddleOrange, PaddleYellow, PaddleGreen, PaddleBlue, PaddlePurple, PaddlePink },
            // Button 2=white, 3=red, 4=orange, 5=yellow, 6=green, 7=blue, 8=purple, 9=pink.
            InstrumentType.Saxophone => new[] { PaddleWhite, PaddleRed, PaddleOrange, PaddleYellow, PaddleGreen, PaddleBlue, PaddlePurple, PaddlePink },
            // Crash=green, Snare=red, High Tom=white, Kick=yellow, Mid Tom=grey, Floor Tom=blue, Ride=purple - by PAD IDENTITY, not raw button number, since the button numbering itself just got corrected (see CreateReceptors) and pad identity is unambiguous either way.
            InstrumentType.DrumKit => new[] { PaddleGreen, PaddleRed, PaddleWhite, PaddleYellow, PaddleGrey, PaddleBlue, PaddlePurple },
            _ => null,
        };

        void BuildColumnColors()
        {
            _columnColors = new Color[_totalColumns];
            _columnColors[0] = openColumnColor;

            var fixedPalette = FixedPaddleColors(_instrument);
            for (int col = 1; col < _totalColumns; col++)
            {
                if (fixedPalette != null && col - 1 < fixedPalette.Length)
                {
                    _columnColors[col] = fixedPalette[col - 1];
                }
                else
                {
                    float hue = (float)(col - 1) / _fingeringCount; // spread the bit-columns evenly around the color wheel
                    _columnColors[col] = Color.HSVToRGB(hue, 0.85f, 1f);
                }
            }
        }

        /// <summary>Coordinate along the SECONDARY axis for this column/row index - X for a vertical lane, Y for the trombone's horizontal lane.</summary>
        float SecondaryPos(int column) =>
            -laneHalfWidth + (column + 0.5f) * (2f * laneHalfWidth / _totalColumns);

        /// <summary>Maps (primary, secondary) to a local Vector3, swapping X/Y per orientation - see the class doc comment.</summary>
        Vector3 LocalPos(float primary, float secondary) =>
            _horizontal ? new Vector3(primary, secondary, 0f) : new Vector3(secondary, primary, 0f);

        /// <summary>Which columns a fingering mask lights up - one per set bit, or just column 0 if the mask is all-zero (a rest/open note).</summary>
        List<int> ColumnsForMask(uint mask)
        {
            var columns = new List<int>();
            for (int bit = 0; bit < _fingeringCount; bit++)
                if ((mask & (1u << bit)) != 0) columns.Add(bit + 1);
            if (columns.Count == 0) columns.Add(0);
            return columns;
        }

        void CreateJudgeLineBar()
        {
            var go = new GameObject("JudgeLineBar");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = LocalPos(judgeLinePos, 0f);
            float span = laneHalfWidth * 2f;
            go.transform.localScale = _horizontal ? new Vector3(0.04f, span, 1f) : new Vector3(span, 0.04f, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = RuntimeSprite.WhiteSquare();
            sr.color = judgeLineBarColor;
            sr.sortingOrder = 9;
        }

        void CreateReceptors()
        {
            for (int col = 0; col < _totalColumns; col++)
            {
                // The drum kit never has an all-zero pad mask (every hit sets
                // exactly one pad bit), so its column 0 (the "open/rest"
                // column every other instrument uses for a rest note) is dead
                // space that never lights up - see ColumnsForMask. Drawing it
                // anyway showed an always-empty ring labeled "-" sitting right
                // next to the real pad rings, which reads as an 8th pad
                // option that doesn't exist rather than as "nothing." Skip it
                // entirely for the drum kit; every other instrument still
                // gets it, since a rest note genuinely needs somewhere to
                // land for them.
                if (_isDrumKit && col == 0) continue;

                var ring = new GameObject($"Receptor_{col}");
                ring.transform.SetParent(transform, false);
                ring.transform.localPosition = LocalPos(judgeLinePos, SecondaryPos(col));
                ring.transform.localScale = new Vector3(circleDiameter, circleDiameter, 1f);
                var sr = ring.AddComponent<SpriteRenderer>();
                sr.sprite = RuntimeSprite.RingCircle();
                sr.color = _columnColors[col];
                sr.sortingOrder = 10;

                // Static label alongside the receptor showing the PHYSICAL HID
                // button number that paddle sits on - controller_hid_protocol.md
                // reserves button 1 for the check input (breath/bow) on every
                // instrument but the drum kit, so fingering bit 0 is button 2,
                // bit 1 is button 3, and so on (column c = bit (c-1) = button
                // c+1). This is deliberately NOT the same numbering as
                // KeyboardInstrumentFallback's keyboard digits (which start at 1
                // for bit 0, since the keyboard's Space key stands in for the
                // check button instead of occupying a numbered key) - this label
                // is for reading the real colored paddles on the hardware, not
                // for the keyboard stand-in. Never moves, so unlike a label on a
                // scrolling note it's always legible regardless of contrast
                // against a moving background. Placed just beyond the judge
                // line on the far side from where notes spawn (below it for a
                // vertical lane, left of it for the trombone's lane).
                //
                // The drum kit does NOT get the +1 offset above - it has no
                // reserved check button at all ("pad N = button N, no
                // offset" per controller_hid_protocol.md's drum-kit section),
                // so column c IS button c directly (bit 0 = Crash = pad 1 =
                // button 1). This was previously wrong here (every drum
                // column showed one HID button number higher than the real
                // paddle it represents) until this fix.
                var labelGO = new GameObject("Label");
                labelGO.transform.SetParent(transform, false);
                labelGO.transform.localPosition = LocalPos(judgeLinePos - circleDiameter * 0.9f, SecondaryPos(col));
                var tm = labelGO.AddComponent<TextMesh>();
                tm.text = col == 0 ? "-" : (_isDrumKit ? col.ToString() : (col + 1).ToString());
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.characterSize = 0.1f;
                tm.fontSize = 32;
                tm.color = columnLabelColor;
                labelGO.GetComponent<MeshRenderer>().sortingOrder = 10;

                // Live-input glow ring: a bigger, brighter ring on top of the
                // receptor, hidden by default and toggled purely off raw
                // controller state in UpdateInputGlow - nothing to do with
                // whether a note is due or whether the press was "correct".
                // Column 0 (open/rest) has no physical button behind it, so
                // it never gets one.
                if (col > 0)
                {
                    var glow = new GameObject($"Glow_{col}");
                    glow.transform.SetParent(transform, false);
                    glow.transform.localPosition = LocalPos(judgeLinePos, SecondaryPos(col));
                    glow.transform.localScale = new Vector3(circleDiameter * glowScale, circleDiameter * glowScale, 1f);
                    var glowSr = glow.AddComponent<SpriteRenderer>();
                    glowSr.sprite = RuntimeSprite.RingCircle();
                    glowSr.color = inputGlowColor;
                    glowSr.sortingOrder = 11; // above the receptor ring/label
                    glowSr.enabled = false;
                    _glowRenderers[col] = glowSr;
                }
            }
        }

        void CreateGridPool()
        {
            _gridPool = new GameObject[GridPoolSize];
            for (int i = 0; i < GridPoolSize; i++)
            {
                var go = new GameObject("BeatLine");
                go.transform.SetParent(transform, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = RuntimeSprite.WhiteSquare();
                sr.sortingOrder = 0;
                go.SetActive(false);
                _gridPool[i] = go;
            }
        }

        /// <summary>Call this with every JudgeEvent HitJudge produces for this lane, so the matching note reacts immediately instead of just scrolling past unremarked.</summary>
        public void OnJudged(JudgeEvent evt)
        {
            if (!_activeMarkers.TryGetValue(evt.Id, out var go)) return; // never spawned (very tight lead-in) - nothing to react with, harmless

            _activeMarkers.Remove(evt.Id);

            var color = evt.Judgement == NoteJudgement.Miss ? missColor : hitColor;
            foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>())
                sr.color = color;

            _expiring.Add((go, Time.time + 0.2f));
        }

        void Update()
        {
            if (_conductor == null) return;
            float currentTime = _conductor.CurrentTime;

            UpdateNotes(currentTime);
            UpdateGrid(currentTime);
            UpdateExpiring();
            UpdateInputGlow();
        }

        /// <summary>
        /// Reads the controller directly, bypassing HitJudge entirely, and
        /// lights up each column's glow ring purely off whether that physical
        /// button/pad is pressed right now. Deliberately independent of the
        /// beatmap and of timing windows - this exists so a player can tell
        /// "my press was registered" even when nothing is due, which is a
        /// different question from "was that press a hit."
        /// </summary>
        void UpdateInputGlow()
        {
            var live = VirtualBandInput.Sample(_instrument);
            uint bits = _isDrumKit ? live.PadsStruck : live.Fingering;

            for (int bit = 0; bit < _fingeringCount; bit++)
            {
                int column = bit + 1;
                bool pressedNow = (bits & (1u << bit)) != 0;

                if (_isDrumKit)
                {
                    // PadsStruck is a one-frame edge, not a held state - a raw
                    // strike would otherwise flash for a single render frame,
                    // easy to miss. Hold the glow visible briefly instead.
                    if (pressedNow) _glowHoldUntil[column] = Time.time + drumGlowHoldSeconds;
                    pressedNow = Time.time < _glowHoldUntil[column];
                }

                var sr = _glowRenderers[column];
                if (sr != null) sr.enabled = pressedNow;
            }
        }

        void UpdateNotes(float currentTime)
        {
            float lookAhead = LookAheadSeconds;

            // Spawn anything newly within range.
            while (_nextSpawnIndex < _items.Count && _items[_nextSpawnIndex].Time - currentTime <= lookAhead)
            {
                SpawnNote(_items[_nextSpawnIndex]);
                _nextSpawnIndex++;
            }

            // Reposition everything still active (not yet judged). Each note's
            // dot(s) are children with their own fixed local secondary-axis
            // coordinate (their column/row) - only the group root's primary
            // coordinate needs updating here.
            List<int> toForceRemove = null;
            foreach (var kv in _activeMarkers)
            {
                float primary = ScrollPrimary(FindTime(kv.Key), currentTime);
                kv.Value.transform.localPosition = LocalPos(primary, 0f);

                // Safety net: should never actually fire, since HitJudge retires a
                // Miss the same frame its window closes and that always arrives
                // before a note could scroll this far past the line. "Past" means
                // beyond the judge line in the direction of travel (primary grows
                // as currentTime overtakes the note's own Time) - NOT the spawn
                // side, which is where every note starts out.
                if (scrollDirection * (primary - judgeLinePos) > despawnMarginPrimary)
                {
                    (toForceRemove ??= new List<int>()).Add(kv.Key);
                }
            }

            if (toForceRemove != null)
            {
                foreach (var id in toForceRemove)
                {
                    Destroy(_activeMarkers[id]);
                    _activeMarkers.Remove(id);
                }
            }
        }

        float FindTime(int id)
        {
            // Small linear scan over the still-relevant window; the active set
            // at any moment is tiny (usually 0-1, rarely a handful for dense
            // drum charts), so this is cheaper than maintaining a second index.
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Id == id) return _items[i].Time;
            return currentTimeFallback;
        }

        // Only reached if a marker's originating item somehow isn't found
        // (shouldn't happen - ids are unique per BeatmapLoader). Freezes the
        // marker in place rather than throwing.
        float currentTimeFallback => _conductor != null ? _conductor.CurrentTime : 0f;

        void SpawnNote(Judgeable item)
        {
            var go = new GameObject($"Note_{item.Id}");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = LocalPos(ScrollPrimary(item.Time, _conductor.CurrentTime), 0f);

            foreach (var column in ColumnsForMask(item.Mask))
            {
                var dot = new GameObject($"Dot_col{column}");
                dot.transform.SetParent(go.transform, false);
                dot.transform.localPosition = LocalPos(0f, SecondaryPos(column));
                dot.transform.localScale = new Vector3(circleDiameter, circleDiameter, 1f);
                var sr = dot.AddComponent<SpriteRenderer>();
                sr.sprite = RuntimeSprite.FilledCircle();
                sr.color = _columnColors[column];
                sr.sortingOrder = 5;
            }

            _activeMarkers[item.Id] = go;
        }

        void UpdateGrid(float currentTime)
        {
            int poolIndex = 0;
            foreach (var line in _gridLines)
            {
                float primary = ScrollPrimary(line.Time, currentTime);
                if (scrollDirection * (primary - judgeLinePos) < -spawnMarginPrimary) continue; // not visible yet (still approaching)
                if (scrollDirection * (primary - judgeLinePos) > despawnMarginPrimary) continue; // scrolled well past

                if (poolIndex >= _gridPool.Length) break; // pool exhausted - see GridPoolSize comment

                var go = _gridPool[poolIndex++];
                go.SetActive(true);
                go.transform.localPosition = LocalPos(primary, 0f);

                bool bold = line.IsMeasureStart;
                float span = laneHalfWidth * 2f;
                float thickness = bold ? 0.05f : 0.02f;
                go.transform.localScale = _horizontal ? new Vector3(thickness, span, 1f) : new Vector3(span, thickness, 1f);
                go.GetComponent<SpriteRenderer>().color = bold ? measureColor : quarterBeatColor;
            }

            for (int i = poolIndex; i < _gridPool.Length; i++)
                _gridPool[i].SetActive(false);
        }

        void UpdateExpiring()
        {
            for (int i = _expiring.Count - 1; i >= 0; i--)
            {
                if (Time.time < _expiring[i].expireAt) continue;
                if (_expiring[i].go != null) Destroy(_expiring[i].go);
                _expiring.RemoveAt(i);
            }
        }

        float ScrollPrimary(float eventTime, float currentTime) =>
            judgeLinePos - scrollDirection * (eventTime - currentTime) * scrollSpeed;
    }
}
