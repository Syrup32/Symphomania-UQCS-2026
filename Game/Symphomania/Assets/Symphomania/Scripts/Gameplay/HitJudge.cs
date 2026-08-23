using System.Collections.Generic;
using UnityEngine;
using Symphomania.Beatmaps;
using Symphomania.Controllers;

// ---------------------------------------------------------------------------
// "Next steps" item 3 from unity_input_layer.md, judging half: the
// window-and-compare approach beatmap_schema.md's "Hit detection & audio
// feedback" section recommends, tuned for a beginner game on hardware that
// isn't perfectly accurate:
//
//  1. GENEROUS, CONFIGURABLE TIMING WINDOW. Default is wide (see
//     GameplayLane's defaultHitWindowSeconds) rather than an esports-tight
//     tolerance - a beginner shouldn't fail a note just for imprecise timing
//     the hardware itself can't render more crisply anyway.
//
//  2. NO PENALTY FOR INPUT WHILE NOTHING IS DUE. A judge moment (breath
//     onset, bow activity, a drum strike) that happens while no note's
//     window has opened yet produces no event at all - not a miss, not a
//     broken combo. Mashing early costs nothing.
//
//  3. NO PENALTY FOR A WRONG ATTEMPT INSIDE THE WINDOW, EITHER. If a judge
//     moment happens while a note IS active but the fingering doesn't
//     match, that attempt is simply ignored and the note stays open for
//     another try - it only becomes a Miss once the window closes with no
//     match ever found. A beginner fumbling valves has the whole window to
//     correct themselves, not one shot.
//
//  4. SLURRING SUPPORT. For every instrument except the drum kit, a judge
//     moment is either a fresh check onset (CheckStarted) OR a fingering
//     change while the check is already held (Check && fingering changed
//     since last frame). That second case is what makes tied/slurred notes
//     playable at all: the hardware can only detect "is breath/bow active",
//     never "did the player re-tongue/re-bow for a new note", so under one
//     continuous breath or bow stroke the only signal a new note has begun
//     is the fingering itself changing.
//
//  5. HOLD NOTES. A sustained-note item (never the drum kit - a drum hit has
//     no duration concept at all) whose duration is a half note or longer
//     (DurationBeats >= HoldMinDurationBeats) becomes a hold: its head is
//     judged exactly like any other tap (rule 1-4 above, unchanged), and a
//     successful (non-Miss) head ARMS a second, separately-judged RELEASE at
//     head.Time + HoldDurationSeconds - so one hold note contributes TWO
//     JudgeEvents (and therefore two tally/score entries), one for pressing
//     it on time and one for letting go on time, distinguished by
//     JudgeEvent.IsHoldRelease. A release is detected either by the check
//     input physically going low, or by the fingering changing while still
//     blowing/bowing (the same signal rule 4 already uses for slurring into
//     a new note - moving on IS letting go of the old one); if neither ever
//     happens before the release's own timing window closes, that release is
//     judged a Miss. Only one hold can be active at a time per instrument,
//     which matches reality (a player has exactly one breath/bow to give).
//     A physical check drop doesn't resolve the release immediately - it
//     first opens a short grace window (HoldReleaseGraceSeconds, 150ms). If
//     the check comes back within that window still fingering the same
//     note, the drop is treated as a glitch and the hold continues as if it
//     never happened (no event, audio never stops). Only a fingering change
//     while still checking (a deliberate slur) skips the grace window
//     entirely, since that's an intentional move, not an accidental drop.
// ---------------------------------------------------------------------------

namespace Symphomania.Gameplay
{
    public enum NoteJudgement
    {
        Perfect,
        Good,
        Miss,
    }

    /// <summary>One judgeable event, converted from either a BeatmapNote or a BeatmapHit into a common shape.</summary>
    public struct Judgeable
    {
        public int Id;
        public float Time;
        public uint Mask;

        /// <summary>Concert pitch for audio playback, null for drum hits (see BeatmapHit - no pitch concept).</summary>
        public string Pitch;

        /// <summary>What to show the player so they know what to press - see NoteLabels. Display-only, never read for judging.</summary>
        public string Label;

        /// <summary>
        /// Seconds this note should be held for - 0 for a plain tap and always
        /// 0 for a drum hit (BeatmapHit has no duration field at all). See
        /// HitJudge.HoldMinDurationBeats for the cutoff. Non-zero only for a
        /// sustained-note instrument's half-note-or-longer note.
        /// </summary>
        public float HoldDurationSeconds;

        /// <summary>True for a note long enough to be judged as a hold (see HoldDurationSeconds) - both HitJudge (arms a release) and NoteLaneView (draws a tail bar) key off this.</summary>
        public bool IsHold => HoldDurationSeconds > 0f;
    }

    public struct JudgeEvent
    {
        public NoteJudgement Judgement;
        public int Id;
        public string Pitch;

        /// <summary>The Judgeable's own fingering/pad mask - null concept for non-drum audio, but for a drum hit this is the struck pad(s), needed by GameplayLane to pick which real drum sample to play (see InstrumentSampleLibrary.TryGetForDrumPad).</summary>
        public uint Mask;

        /// <summary>Seconds late (positive) or early (negative) the matching input landed. 0 for a Miss.</summary>
        public float TimingErrorSeconds;

        /// <summary>
        /// True if this event is judging a hold note's RELEASE (how
        /// well-timed letting go was) rather than its head/start. A hold note
        /// fires two separate JudgeEvents over its lifetime - false (the
        /// head) then, later, true (the release) - both sharing the same Id,
        /// each contributing its own tally/score entry. Always false for a
        /// plain tap or a drum hit, and for a hold's own head event.
        /// </summary>
        public bool IsHoldRelease;

        /// <summary>
        /// True for BOTH of a hold note's events (head and release) - false
        /// for a plain tap or a drum hit. Distinguishes "this note is a hold
        /// at all" from IsHoldRelease, which distinguishes which of the two
        /// events this particular one is. GameplayLane uses this to decide
        /// whether a head hit should start a continuously-sustained audio
        /// voice (rather than a short one-shot) and whether a release should
        /// stop it.
        /// </summary>
        public bool IsHoldNote;
    }

    /// <summary>
    /// Judges one instrument's track against that instrument's live input,
    /// frame by frame. One instance per active lane - see GameplayLane.
    /// </summary>
    public class HitJudge
    {
        readonly List<Judgeable> _items;

        /// <summary>
        /// True for the drum kit (match = live.PadsStruck overlaps the hit's
        /// pad bit at all - several pads can be struck the same frame), false
        /// for every sustained-note instrument (match = fingering mask is
        /// exactly equal - a beginner-friendly window on *timing*, not on
        /// which buttons happen to also be pressed).
        /// </summary>
        readonly bool _overlapMatch;

        readonly float _windowSeconds;
        readonly float _perfectFraction;

        /// <summary>
        /// A note's duration (in beats, quarter note = 1 per beatmap_schema.md's
        /// duration_beats convention) has to be at least this long to become a
        /// hold note with its own separately-judged release. 2 beats = a half
        /// note; a whole note (4 beats) also qualifies. A quarter note, eighth
        /// note, or sixteenth note (1, 0.5, 0.25 beats) stays a plain tap.
        /// Never applies to the drum kit - a BeatmapHit has no duration field
        /// at all, so ConvertTrack's drum branch never sets HoldDurationSeconds.
        /// </summary>
        public const float HoldMinDurationBeats = 2f;

        int _index;
        uint _prevFingering;
        bool _hasPrevFingering;
        bool _prevCheck;
        bool _hasPrevCheck;

        // The one hold currently awaiting its release judgement, if any - see
        // the class doc comment's "HOLD NOTES" section. Only one at a time:
        // arming a new one always happens after the previous one's release
        // has already been resolved (see Update's ordering).
        bool _hasActiveHold;
        int _activeHoldId;
        string _activeHoldPitch;
        uint _activeHoldMask;
        float _activeHoldReleaseTime;

        /// <summary>
        /// A physical release (check input dropping) doesn't immediately end
        /// a hold - it starts this grace window instead, in case it's a
        /// glitchy/bouncy drop rather than the player actually letting go
        /// (e.g. a breath-detection microphone hiccup, or a rotary encoder
        /// misreading a bow stroke for a frame). If the check comes back
        /// within HoldReleaseGraceSeconds while still fingering the same
        /// note, the hold is treated as never having been released at all -
        /// no JudgeEvent fires and the sustained audio never stops. Only if
        /// the grace window expires with no recovery does the release
        /// actually resolve, using the ORIGINAL drop moment (not the later
        /// grace-expiry moment) for its timing error, so the grace period
        /// itself never costs the player any extra lateness.
        /// </summary>
        public const float HoldReleaseGraceSeconds = 0.15f;

        bool _releasePending;
        float _releasePendingSince;

        public int RemainingCount => _items.Count - _index;

        public HitJudge(List<Judgeable> items, bool overlapMatch, float windowSeconds, float perfectFraction = 0.4f)
        {
            _items = items;
            _overlapMatch = overlapMatch;
            _windowSeconds = Mathf.Max(0.01f, windowSeconds);
            _perfectFraction = Mathf.Clamp01(perfectFraction);
        }

        /// <summary>
        /// Converts a loaded track into the common Judgeable shape - shared by
        /// ForTrack (below) and by GameplayLane, which needs its own separate
        /// copy of the same list for display purposes (see NoteLaneView).
        /// Reads BeatmapNote.Fingering / BeatmapHit.PadMask, which were
        /// already precomputed by BeatmapLoader from only hardware-verified
        /// fields - this never re-reads raw JSON. Each call returns a fresh
        /// List instance, so the judge's copy and the view's copy never alias.
        /// </summary>
        public static List<Judgeable> ConvertTrack(BeatmapTrack track)
        {
            var items = new List<Judgeable>();

            if (track.IsDrumTrack)
            {
                foreach (var hit in track.Hits)
                    items.Add(new Judgeable { Id = hit.Id, Time = hit.Time, Mask = hit.PadMask, Pitch = null, Label = NoteLabels.ForHit(hit), HoldDurationSeconds = 0f });
                return items;
            }

            foreach (var note in track.Notes)
                items.Add(new Judgeable
                {
                    Id = note.Id,
                    Time = note.StartTime,
                    Mask = note.Fingering,
                    Pitch = note.Pitch,
                    Label = NoteLabels.ForNote(track.Instrument, note.Fingering),
                    HoldDurationSeconds = note.DurationBeats >= HoldMinDurationBeats ? note.DurationTime : 0f,
                });
            return items;
        }

        /// <summary>Builds a ready-to-use HitJudge straight from a loaded track.</summary>
        public static HitJudge ForTrack(BeatmapTrack track, float windowSeconds, float perfectFraction = 0.4f) =>
            new HitJudge(ConvertTrack(track), overlapMatch: track.IsDrumTrack, windowSeconds, perfectFraction);

        /// <summary>
        /// Advances judging by one frame. Call every frame regardless of
        /// whether input changed - retiring notes whose window has closed
        /// unhit needs to happen even on a frame with no input at all.
        /// </summary>
        public List<JudgeEvent> Update(InstrumentSnapshot live, float currentTime)
        {
            var events = new List<JudgeEvent>();

            // Retire anything whose HEAD window fully closed without ever
            // being hit. A missed head never arms a release (see below), so
            // this alone is still the right way to end a hold note nobody
            // ever started.
            while (_index < _items.Count && _items[_index].Time + _windowSeconds < currentTime)
            {
                var missed = _items[_index];
                events.Add(new JudgeEvent
                {
                    Judgement = NoteJudgement.Miss,
                    Id = missed.Id,
                    Pitch = missed.Pitch,
                    Mask = missed.Mask,
                    TimingErrorSeconds = 0f,
                    IsHoldRelease = false,
                    IsHoldNote = missed.IsHold,
                });
                _index++;
            }

            bool judgeMoment = _overlapMatch
                ? live.PadsStruck != 0
                : live.CheckStarted || (live.Check && _hasPrevFingering && live.Fingering != _prevFingering);

            // Resolve any hold currently awaiting its release BEFORE matching
            // new heads below - a fingering change this frame can be both "let
            // go of the held note" and "start the next one" at once (a slur
            // straight from one held note into another), and the old hold's
            // item has already been removed from _items (when its head was
            // judged), so there's no ordering conflict either way.
            if (_hasActiveHold && _releasePending)
            {
                // Already waiting out a grace period from a physical release
                // detected on an earlier frame - check first whether the
                // player recovered (still fingering the right note), then
                // whether the grace window has run out.
                bool recovered = live.Check && live.Fingering == _activeHoldMask;
                bool windowExpiredDuringGrace = currentTime > _activeHoldReleaseTime + _windowSeconds;
                if (recovered)
                {
                    _releasePending = false; // false alarm - the hold continues uninterrupted, no event fires
                }
                else if (currentTime - _releasePendingSince >= HoldReleaseGraceSeconds || windowExpiredDuringGrace)
                {
                    // Either the grace window ran out with no recovery, or -
                    // rare, only possible if the release lands right at the
                    // edge of the window - the whole release window closed
                    // while still waiting on grace. Either way this resolves
                    // against the original drop moment, same as normal.
                    ResolveHoldRelease(events, _releasePendingSince, wasReleased: true);
                    _releasePending = false;
                }
                // else still inside the grace window - keep waiting, no event this frame
            }

            if (_hasActiveHold && !_releasePending)
            {
                bool physicallyReleased = _hasPrevCheck && !live.Check;
                bool slurredAway = live.Check && _hasPrevFingering && live.Fingering != _prevFingering;
                bool windowExpired = currentTime > _activeHoldReleaseTime + _windowSeconds;

                if (slurredAway || windowExpired)
                {
                    // A slur straight into a new note is a deliberate move,
                    // not a glitchy drop - no grace period for that case.
                    // Likewise, once the release window has fully closed
                    // there's nothing left to wait out.
                    ResolveHoldRelease(events, currentTime, wasReleased: slurredAway);
                }
                else if (physicallyReleased)
                {
                    // Don't resolve yet - start the grace window and see if
                    // it comes back on a following frame.
                    _releasePending = true;
                    _releasePendingSince = currentTime;
                }
            }

            if (judgeMoment)
            {
                for (int i = _index; i < _items.Count; i++)
                {
                    var item = _items[i];

                    // This item's window hasn't opened yet - and since _items
                    // is time-sorted, nothing after it has either. Stop, don't
                    // penalize: an early check just means "nothing to do yet".
                    if (item.Time - _windowSeconds > currentTime) break;

                    bool matches = _overlapMatch
                        ? (item.Mask & live.PadsStruck) != 0
                        : item.Mask == live.Fingering;

                    if (!matches) continue; // wrong attempt inside the window - not penalized, keep waiting

                    float error = currentTime - item.Time;
                    var quality = Mathf.Abs(error) <= _windowSeconds * _perfectFraction
                        ? NoteJudgement.Perfect
                        : NoteJudgement.Good;

                    events.Add(new JudgeEvent
                    {
                        Judgement = quality,
                        Id = item.Id,
                        Pitch = item.Pitch,
                        Mask = item.Mask,
                        TimingErrorSeconds = error,
                        IsHoldRelease = false,
                        IsHoldNote = item.IsHold,
                    });

                    // A successfully-hit hold note (never the drum kit - see
                    // Judgeable.IsHold's doc comment) arms its release instead
                    // of ending here - see the class doc comment's "HOLD
                    // NOTES" section.
                    if (!_overlapMatch && item.IsHold)
                    {
                        _hasActiveHold = true;
                        _activeHoldId = item.Id;
                        _activeHoldPitch = item.Pitch;
                        _activeHoldMask = item.Mask;
                        _activeHoldReleaseTime = item.Time + item.HoldDurationSeconds;
                        _releasePending = false; // fresh hold - any earlier grace window is irrelevant
                    }

                    _items.RemoveAt(i);
                    break; // one match per judge moment
                }
            }

            _prevFingering = live.Fingering;
            _hasPrevFingering = true;
            _prevCheck = live.Check;
            _hasPrevCheck = true;

            return events;
        }

        /// <summary>
        /// Emits the release JudgeEvent for the currently-active hold and
        /// clears it. releaseTime is the moment the release is actually
        /// scored against - for a grace-period release this is when the
        /// check FIRST dropped (_releasePendingSince), not whenever the
        /// grace window happened to run out, so waiting out the grace period
        /// never itself counts as extra lateness. wasReleased is false only
        /// for the "held straight through the window, never let go at all"
        /// case, which is always a forced Miss regardless of timing.
        /// </summary>
        void ResolveHoldRelease(List<JudgeEvent> events, float releaseTime, bool wasReleased)
        {
            float releaseError = releaseTime - _activeHoldReleaseTime;
            NoteJudgement releaseQuality = wasReleased
                ? (Mathf.Abs(releaseError) <= _windowSeconds * _perfectFraction ? NoteJudgement.Perfect
                    : Mathf.Abs(releaseError) <= _windowSeconds ? NoteJudgement.Good
                    : NoteJudgement.Miss)
                : NoteJudgement.Miss;

            events.Add(new JudgeEvent
            {
                Judgement = releaseQuality,
                Id = _activeHoldId,
                Pitch = _activeHoldPitch,
                Mask = _activeHoldMask,
                TimingErrorSeconds = releaseError,
                IsHoldRelease = true,
                IsHoldNote = true,
            });

            _hasActiveHold = false;
        }
    }
}
