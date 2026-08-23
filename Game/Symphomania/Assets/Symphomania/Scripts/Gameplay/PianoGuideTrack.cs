using System.Collections;
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
                src.loop = true;
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
                var clip = NoteAudio.PianoSustainClip(note.Pitch);
                if (clip != null)
                {
                    float duration = Mathf.Max(0.05f, note.Duration);
                    var voice = FindFreeVoice();
                    if (voice.Routine != null) StopCoroutine(voice.Routine);
                    voice.BusyUntil = Time.time + duration;
                    voice.Routine = StartCoroutine(PlayGuideNote(voice, clip, duration));
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
        /// </summary>
        IEnumerator PlayGuideNote(Voice voice, AudioClip clip, float duration)
        {
            var src = voice.Source;
            src.Stop();
            src.clip = clip;
            src.pitch = 1f;
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
