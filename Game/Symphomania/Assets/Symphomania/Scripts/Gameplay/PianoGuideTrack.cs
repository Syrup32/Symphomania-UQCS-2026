using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Symphomania.Beatmaps;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// Optional background "how it should sound" reference track. Two
    /// distinct beatmap origins trigger this - see GameplayBootstrap's
    /// HasGuideTrackSource, which is what decides whether to create this
    /// component at all:
    ///
    /// 1) Per beatmap_schema.md's "beatmap version tagging" section, a
    ///    beatmap converted FROM a single piano rendition is tagged
    ///    "Version P" in its title/filename - meaning every sustained-note
    ///    track in that file (the treble line for violin/trumpet/saxophone,
    ///    the bass line for trombone) really is that one original piano
    ///    piece, split apart by instrument. Recombining every such track's
    ///    notes reconstructs (an approximation of) the original piano
    ///    performance.
    ///
    /// 2) A MIDI-derived beatmap (title/filename tagged "MIDI" per
    ///    beatmap_schema.md's "2026-08-22: convert_midi.py added" note) gets
    ///    this too, even in "Version S" (band) mode - a MIDI source file
    ///    typically represents the actual full multi-instrument song, so
    ///    recombining every instrument track's notes here reconstructs a
    ///    "hear the whole arrangement" reference, not specifically a piano
    ///    reduction - it's the same mechanism either way (merge every
    ///    non-drum track's notes, play them back as one quiet background
    ///    voice), just a different reason for wanting it.
    ///
    /// Either way, this plays back underneath the player's own live, judged
    /// instrument(s) - never louder or more prominent than that.
    ///
    /// Deliberately excludes the drum kit: per beatmap_schema.md's "Drum pad
    /// numbering" section, piano-mode drum hits are derived by bucketing
    /// bass-register pitch height into pad categories, not by preserving an
    /// actual pitch - a BeatmapHit has no Pitch field at all - so there's no
    /// note left in a drum track to feed back into this reconstruction.
    /// Only the sustained-note (non-drum) tracks carry real pitch data.
    ///
    /// A non-MIDI "Version S" (band-mode) beatmap has no single-source
    /// rendition to recombine at all - it's real, separate per-instrument
    /// sheet music with no MIDI file behind it - so GameplayBootstrap simply
    /// never creates this component for one.
    /// </summary>
    public class PianoGuideTrack : MonoBehaviour
    {
        [Range(0f, 1f)]
        [Tooltip("Deliberately quiet - this is a background reference, not a 6th performer competing with the live instruments' own hit-confirmation audio. 0.245 = the original 0.35 default cut to 70% of itself, so players can hear their own performance better.")]
        public float volume = 0.245f;

        struct GuideNote
        {
            public float Time;
            public float Duration;
            public string Pitch;
        }

        /// <summary>
        /// One AudioSource "voice" that can be sustaining a single guide
        /// note at a time. A pool rather than one shared AudioSource because,
        /// unlike a one-shot, a sustained note occupies its source for its
        /// entire notated duration - a dense, multi-note-at-once chord (very
        /// common when several instrument tracks recombine into the same
        /// piano line) needs more than one voice actually playing at once,
        /// or later notes in the chord would cut off earlier ones.
        /// </summary>
        class Voice
        {
            public AudioSource Source;
            public Coroutine Routine;
            public float BusyUntil; // Time.time this voice expects to free up - used to pick a steal target
        }

        const int VoicePoolSize = 8;
        const float GuideAttackSeconds = 0.03f;
        const float GuideReleaseSeconds = 0.05f;

        RhythmConductor _conductor;
        List<GuideNote> _notes;
        int _nextIndex;
        List<Voice> _voices;

        public void Initialize(RhythmConductor conductor, Beatmap beatmap)
        {
            _conductor = conductor;

            _voices = new List<Voice>(VoicePoolSize);
            for (int i = 0; i < VoicePoolSize; i++)
            {
                var src = gameObject.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 0f; // this is a whole-session background track, not positioned in any one lane's world space
                src.loop = false; // PlayGuideNote sets this per-note (true only for the synthesized fallback - see its doc comment)
                src.volume = 0f;
                _voices.Add(new Voice { Source = src, Routine = null, BusyUntil = 0f });
            }

            _notes = new List<GuideNote>();
            foreach (var track in beatmap.Tracks.Values)
            {
                if (track.IsDrumTrack) continue; // no pitch data to recover - see class doc comment
                foreach (var note in track.Notes)
                    _notes.Add(new GuideNote { Time = note.StartTime, Duration = note.DurationTime, Pitch = note.Pitch });
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
            // duplicate just reinforces the same note. Each note now claims
            // its own voice from the pool and sustains for its own notated
            // duration, rather than a fixed-length PlayOneShot blip.
            while (_nextIndex < _notes.Count && _notes[_nextIndex].Time <= currentTime)
            {
                var note = _notes[_nextIndex];

                // Prefer a real Toy Keyboard (or similar) piano sample if
                // one's been dropped in (see InstrumentSampleLibrary's
                // TryGetNearestPianoGuide) - a real recording naturally
                // decays on its own, so it plays once, unlooped, letting
                // that natural decay carry the sustain. Only the
                // synthesized fallback (built specifically loop-safe - see
                // NoteAudio.BuildSustainClip) actually loops, since looping
                // an arbitrary real recording would click/repeat audibly.
                AudioClip clip;
                float playbackPitch;
                bool loop;
                if (InstrumentSampleLibrary.TryGetNearestPianoGuide(note.Pitch, out var realClip, out var realPitch))
                {
                    clip = realClip;
                    playbackPitch = realPitch;
                    loop = false;
                }
                else
                {
                    clip = NoteAudio.PianoSustainClip(note.Pitch);
                    playbackPitch = 1f;
                    loop = true;
                }

                if (clip != null)
                {
                    float duration = Mathf.Max(0.05f, note.Duration);
                    var voice = FindFreeVoice();
                    if (voice.Routine != null) StopCoroutine(voice.Routine);
                    voice.BusyUntil = Time.time + duration;
                    voice.Routine = StartCoroutine(PlayGuideNote(voice, clip, duration, playbackPitch, loop));
                }
                _nextIndex++;
            }
        }

        /// <summary>
        /// Picks an idle voice if one exists, otherwise steals whichever
        /// voice is expected to free up soonest - this is a quiet background
        /// reference track, not judged gameplay, so an occasional stolen
        /// voice cutting a still-sustaining note slightly short in a very
        /// dense chord is an acceptable trade rather than dropping the new
        /// note entirely.
        /// </summary>
        Voice FindFreeVoice()
        {
            Voice best = null;
            foreach (var v in _voices)
            {
                if (v.Routine == null) return v;
                if (best == null || v.BusyUntil < best.BusyUntil) best = v;
            }
            return best;
        }

        /// <summary>
        /// Sustains one guide note on its assigned voice for its own
        /// notated duration - fading in over GuideAttackSeconds and out over
        /// GuideReleaseSeconds (both scaled against the note's own duration
        /// so a very short note doesn't get an attack/release longer than
        /// the note itself), rather than a hard start/stop that would click.
        /// pitch is 1f for the synthesized sustain (already built at the
        /// exact target frequency) or the real sample's own pitch-shift
        /// ratio when a real Toy Keyboard-style clip was found nearest this
        /// note's pitch (see InstrumentSampleLibrary.TryGetNearestPianoGuide).
        /// loop is false for a real sample (its own natural decay carries
        /// the sustain - looping an arbitrary recording would click) and
        /// true only for the synthesized fallback, which is built
        /// specifically to loop without a seam.
        /// </summary>
        IEnumerator PlayGuideNote(Voice voice, AudioClip clip, float duration, float pitch, bool loop)
        {
            var src = voice.Source;
            src.Stop();
            src.clip = clip;
            src.pitch = pitch;
            src.loop = loop;
            src.time = 0f;
            src.volume = 0f;
            src.Play();

            float attack = Mathf.Min(GuideAttackSeconds, duration * 0.5f);
            float release = Mathf.Min(GuideReleaseSeconds, duration * 0.5f);
            float sustainEnd = Mathf.Max(attack, duration - release);

            float t = 0f;
            while (t < attack)
            {
                t += Time.deltaTime;
                src.volume = Mathf.Lerp(0f, volume, attack > 0f ? t / attack : 1f);
                yield return null;
            }
            src.volume = volume;

            while (t < sustainEnd)
            {
                t += Time.deltaTime;
                yield return null;
            }

            float fadeStart = src.volume;
            float ft = 0f;
            while (ft < release && src.isPlaying)
            {
                ft += Time.deltaTime;
                src.volume = Mathf.Lerp(fadeStart, 0f, release > 0f ? ft / release : 1f);
                yield return null;
            }

            src.Stop();
            src.volume = 0f;
            voice.Routine = null;
        }
    }
}
