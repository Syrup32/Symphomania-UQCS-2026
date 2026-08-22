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

        int _index;
        uint _prevFingering;
        bool _hasPrevFingering;

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
                    items.Add(new Judgeable { Id = hit.Id, Time = hit.Time, Mask = hit.PadMask, Pitch = null, Label = NoteLabels.ForHit(hit) });
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

            // Retire anything whose window fully closed without ever being hit.
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
                });
                _index++;
            }

            bool judgeMoment = _overlapMatch
                ? live.PadsStruck != 0
                : live.CheckStarted || (live.Check && _hasPrevFingering && live.Fingering != _prevFingering);

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
                    });

                    _items.RemoveAt(i);
                    break; // one match per judge moment
                }
            }

            _prevFingering = live.Fingering;
            _hasPrevFingering = true;

            return events;
        }
    }
}
