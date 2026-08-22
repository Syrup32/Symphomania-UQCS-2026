using System.Collections.Generic;
using UnityEngine;
using Symphomania.Beatmaps;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// Optional background "how it should sound" reference track. Per
    /// beatmap_schema.md's "beatmap version tagging" section, a beatmap
    /// converted FROM a single piano rendition is tagged "Version P" in its
    /// title/filename - meaning every sustained-note track in that file
    /// (the treble line for violin/trumpet/saxophone, the bass line for
    /// trombone) really is that one original piano piece, split apart by
    /// instrument. So recombining every such track's notes and playing them
    /// back as plain piano tones reconstructs (an approximation of) the
    /// original piano performance, as a quiet guide track underneath the
    /// player's own live, judged instrument(s) - see GameplayBootstrap's
    /// IsPianoDerived, which is what decides whether to create this at all.
    ///
    /// Deliberately excludes the drum kit: per beatmap_schema.md's "Drum pad
    /// numbering" section, piano-mode drum hits are derived by bucketing
    /// bass-register pitch height into pad categories, not by preserving an
    /// actual pitch - a BeatmapHit has no Pitch field at all - so there's no
    /// note left in a drum track to feed back into a piano reconstruction.
    /// Only the sustained-note (non-drum) tracks carry real pitch data.
    ///
    /// A "Version S" (band-mode) beatmap has no single-piano origin at all -
    /// it's real, separate per-instrument sheet music - so GameplayBootstrap
    /// simply never creates this component for one.
    /// </summary>
    public class PianoGuideTrack : MonoBehaviour
    {
        [Range(0f, 1f)]
        [Tooltip("Deliberately quiet - this is a background reference, not a 6th performer competing with the live instruments' own hit-confirmation audio. 0.245 = the original 0.35 default cut to 70% of itself, so players can hear their own performance better.")]
        public float volume = 0.245f;

        struct GuideNote
        {
            public float Time;
            public string Pitch;
        }

        RhythmConductor _conductor;
        List<GuideNote> _notes;
        int _nextIndex;
        AudioSource _audio;

        public void Initialize(RhythmConductor conductor, Beatmap beatmap)
        {
            _conductor = conductor;

            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f; // this is a whole-session background track, not positioned in any one lane's world space
            _audio.volume = volume;

            _notes = new List<GuideNote>();
            foreach (var track in beatmap.Tracks.Values)
            {
                if (track.IsDrumTrack) continue; // no pitch data to recover - see class doc comment
                foreach (var note in track.Notes)
                    _notes.Add(new GuideNote { Time = note.StartTime, Pitch = note.Pitch });
            }

            // Sorted once up front so Update can just walk forward with a
            // single index, the same pattern NoteLaneView/HitJudge already
            // use for their own time-ordered lists - no per-frame scan.
            _notes.Sort((a, b) => a.Time.CompareTo(b.Time));
            _nextIndex = 0;

            Debug.Log($"[PianoGuideTrack] Reconstructed {_notes.Count} guide note(s) from {beatmap.Tracks.Count} non-drum-excluded track(s).");
        }

        void Update()
        {
            if (_conductor == null) return;
            float currentTime = _conductor.CurrentTime;

            // Multiple instruments' tracks can (and, in a full-band Version P
            // chart, often will) carry the exact same treble or bass line at
            // once - every one of them gets played here, so an overlapping
            // duplicate just reinforces the same note rather than causing
            // any problem. PlayOneShot supports this kind of overlap natively
            // on a single AudioSource, so no separate voice-pool is needed
            // even for a dense, multi-note-at-once chord moment.
            while (_nextIndex < _notes.Count && _notes[_nextIndex].Time <= currentTime)
            {
                var clip = NoteAudio.PianoClip(_notes[_nextIndex].Pitch);
                if (clip != null) _audio.PlayOneShot(clip);
                _nextIndex++;
            }
        }
    }
}
