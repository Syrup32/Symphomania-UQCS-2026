using System.Collections.Generic;
using UnityEngine;
using Symphomania.Controllers;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// Procedurally synthesizes short audio clips for hit feedback - no audio
    /// assets required. A sustained-note hit plays a tone at the note's own
    /// concert pitch (BeatmapNote.Pitch, e.g. "F#4"), voiced with a cheap
    /// per-instrument "timbre" (see Timbre.For) so four instruments hitting
    /// notes at once are tellable apart by ear, not just by which lane
    /// flashed. This is additive-harmonic synthesis approximating each
    /// instrument's brightness/attack character - it is NOT sampled
    /// instrument audio and isn't trying to sound like a recording. A drum
    /// hit plays a short noise "click" instead, since a pad strike has no
    /// pitch concept (see BeatmapHit - JudgeEvent.Pitch is null for drum
    /// hits). Swap in real samples later by changing what ClipForPitch/
    /// DrumClick return - nothing downstream needs to change, since
    /// GameplayLane just plays whatever clip comes back.
    /// </summary>
    public static class NoteAudio
    {
        const int SampleRate = 44100;
        const float ToneDuration = 0.35f;
        const float ClickDuration = 0.12f;

        /// <summary>
        /// Target length (before rounding to a whole number of fundamental
        /// periods - see BuildSustainClip) for a loopable hold-note/guide
        /// voice. Short is fine: AudioSource.loop just repeats it seamlessly
        /// for however long the note is actually held.
        /// </summary>
        const float SustainLoopSeconds = 0.25f;

        // Keyed by "Instrument|Pitch", not just pitch - the whole point is
        // that the same concert pitch sounds different per instrument, so
        // caching on pitch alone would have every instrument reuse whichever
        // one synthesized that pitch first.
        static readonly Dictionary<string, AudioClip> _toneCache = new Dictionary<string, AudioClip>();
        static readonly Dictionary<string, AudioClip> _pianoCache = new Dictionary<string, AudioClip>(); // separate from _toneCache - see PianoClip
        static readonly Dictionary<string, AudioClip> _sustainCache = new Dictionary<string, AudioClip>(); // see SustainClipForPitch
        static readonly Dictionary<string, AudioClip> _pianoSustainCache = new Dictionary<string, AudioClip>(); // see PianoSustainClip
        static AudioClip _drumClick;

        /// <summary>Returns this instrument's voice for this concert pitch (cached per instrument+pitch), or the drum click for the drum kit / a null-pitch event.</summary>
        public static AudioClip ClipForPitch(InstrumentType instrument, string pitch)
        {
            if (instrument == InstrumentType.DrumKit || string.IsNullOrEmpty(pitch)) return DrumClick();

            string key = instrument.ToString() + "|" + pitch;
            if (_toneCache.TryGetValue(key, out var cached) && cached != null) return cached;

            var clip = BuildToneClip(FrequencyForPitch(pitch), Timbre.For(instrument));
            if (clip != null) _toneCache[key] = clip; // don't cache a failure - let the next call retry
            return clip;
        }

        /// <summary>
        /// The background piano guide track's own voice (see
        /// PianoGuideTrack) - deliberately a separate cache and a separate
        /// Timbre from every band instrument's, so the reconstructed-piano
        /// reference track is never mistaken by ear for one of the live,
        /// judged instruments' own hit-confirmation tone.
        /// </summary>
        public static AudioClip PianoClip(string pitch)
        {
            if (string.IsNullOrEmpty(pitch)) return null;

            if (_pianoCache.TryGetValue(pitch, out var cached) && cached != null) return cached;

            var clip = BuildToneClip(FrequencyForPitch(pitch), Timbre.Piano);
            if (clip != null) _pianoCache[pitch] = clip;
            return clip;
        }

        public static AudioClip DrumClick() => _drumClick != null ? _drumClick : (_drumClick = BuildClickClip());

        /// <summary>
        /// A loopable version of this instrument's voice for this concert
        /// pitch - unlike ClipForPitch, this has no attack/decay baked in (see
        /// BuildSustainClip), so a caller can AudioSource.loop it for as long
        /// as a hold note is actually held and fade it out at release,
        /// instead of playing a fixed-length one-shot. Null for the drum kit
        /// or a null pitch - drums never hold (see Judgeable.IsHold, always
        /// false for a drum hit), so this should never actually be called for
        /// one.
        /// </summary>
        public static AudioClip SustainClipForPitch(InstrumentType instrument, string pitch)
        {
            if (instrument == InstrumentType.DrumKit || string.IsNullOrEmpty(pitch)) return null;

            string key = instrument.ToString() + "|" + pitch;
            if (_sustainCache.TryGetValue(key, out var cached) && cached != null) return cached;

            var clip = BuildSustainClip(FrequencyForPitch(pitch), Timbre.For(instrument));
            if (clip != null) _sustainCache[key] = clip;
            return clip;
        }

        /// <summary>
        /// The background piano guide track's own loopable sustain voice -
        /// same Timbre.Piano as PianoClip, but built for AudioSource.loop
        /// instead of a fixed-length one-shot, so PianoGuideTrack can hold
        /// each guide note audibly for its own real notated duration instead
        /// of a fixed-length blip regardless of how long the note actually is.
        /// </summary>
        public static AudioClip PianoSustainClip(string pitch)
        {
            if (string.IsNullOrEmpty(pitch)) return null;

            if (_pianoSustainCache.TryGetValue(pitch, out var cached) && cached != null) return cached;

            var clip = BuildSustainClip(FrequencyForPitch(pitch), Timbre.Piano);
            if (clip != null) _pianoSustainCache[pitch] = clip;
            return clip;
        }

        /// <summary>
        /// Scientific pitch notation ("C4", "F#4", "Bb2") -> Hz, via MIDI note
        /// number (A4 = MIDI 69 = 440Hz, matching beatmap_schema.md's "pitch is
        /// always concert pitch" convention).
        /// </summary>
        static float FrequencyForPitch(string pitch)
        {
            int i = 0;
            char letter = char.ToUpperInvariant(pitch[i++]);

            int accidental = 0;
            if (i < pitch.Length && (pitch[i] == '#' || pitch[i] == 'b'))
            {
                accidental = pitch[i] == '#' ? 1 : -1;
                i++;
            }

            if (!int.TryParse(pitch.Substring(i), out int octave))
                octave = 4; // malformed pitch string - fall back to a reasonable middle octave rather than throwing

            int semitoneFromC = letter switch
            {
                'C' => 0,
                'D' => 2,
                'E' => 4,
                'F' => 5,
                'G' => 7,
                'A' => 9,
                'B' => 11,
                _ => 0,
            };

            int midi = (octave + 1) * 12 + semitoneFromC + accidental;
            return 440f * Mathf.Pow(2f, (midi - 69) / 12f);
        }

        /// <summary>
        /// A cheap per-instrument "voice": a fixed set of harmonic amplitudes
        /// (relative to the fundamental), an attack time, a decay rate, and
        /// an optional vibrato. Tuned by ear to lean toward each instrument's
        /// real character (brassy/buzzy vs. reedy vs. bowed), not measured
        /// from any real recording - placeholder programmer-audio, same
        /// spirit as RuntimeSprite's placeholder programmer-art.
        /// </summary>
        struct Timbre
        {
            public float[] harmonicAmps; // harmonicAmps[0] = fundamental, [1] = 2nd harmonic, etc.
            public float attackSeconds;
            public float decayRate; // larger = faster exponential decay over ToneDuration
            public float vibratoDepth; // fraction of frequency; 0 = none
            public float vibratoRateHz;

            public static Timbre For(InstrumentType instrument) => instrument switch
            {
                // Bright and buzzy, fast tongued attack, rich upper harmonics.
                InstrumentType.Trumpet => new Timbre
                {
                    harmonicAmps = new[] { 1f, 0.8f, 0.6f, 0.5f, 0.35f, 0.2f },
                    attackSeconds = 0.01f,
                    decayRate = 4f,
                },
                // Reedy - odd harmonics emphasized over even ones, a touch of vibrato.
                InstrumentType.Saxophone => new Timbre
                {
                    harmonicAmps = new[] { 1f, 0.2f, 0.7f, 0.15f, 0.45f, 0.1f },
                    attackSeconds = 0.02f,
                    decayRate = 3.5f,
                    vibratoDepth = 0.006f,
                    vibratoRateHz = 5f,
                },
                // Sawtooth-ish body, slower bowed attack, the most noticeable vibrato.
                InstrumentType.Violin => new Timbre
                {
                    harmonicAmps = new[] { 1f, 0.5f, 0.35f, 0.25f, 0.2f, 0.15f, 0.1f },
                    attackSeconds = 0.05f,
                    decayRate = 2.5f,
                    vibratoDepth = 0.01f,
                    vibratoRateHz = 6f,
                },
                // Brassy like the trumpet but darker/rounder, slower slide-driven attack.
                InstrumentType.Trombone => new Timbre
                {
                    harmonicAmps = new[] { 1f, 0.55f, 0.3f, 0.18f, 0.1f },
                    attackSeconds = 0.03f,
                    decayRate = 3f,
                },
                _ => new Timbre { harmonicAmps = new[] { 1f, 0.5f, 0.25f }, attackSeconds = 0.01f, decayRate = 4f },
            };

            /// <summary>The background piano guide track's voice (see PianoGuideTrack/NoteAudio.PianoClip) - rounder and softer-onset than any of the 5 band instruments' timbres above, deliberately unlike all of them so the guide never gets mistaken for a live hit.</summary>
            public static readonly Timbre Piano = new Timbre
            {
                harmonicAmps = new[] { 1f, 0.35f, 0.5f, 0.15f, 0.2f, 0.08f },
                attackSeconds = 0.005f,
                decayRate = 2f,
            };
        }

        static AudioClip BuildToneClip(float frequency, Timbre timbre)
        {
            int samples = Mathf.CeilToInt(SampleRate * ToneDuration);
            var data = new float[samples];

            for (int n = 0; n < samples; n++)
            {
                float t = n / (float)SampleRate;

                float vibrato = timbre.vibratoDepth > 0f
                    ? 1f + timbre.vibratoDepth * Mathf.Sin(2f * Mathf.PI * timbre.vibratoRateHz * t)
                    : 1f;
                float f = frequency * vibrato;

                float sample = 0f;
                for (int h = 0; h < timbre.harmonicAmps.Length; h++)
                    sample += timbre.harmonicAmps[h] * Mathf.Sin(2f * Mathf.PI * f * (h + 1) * t);

                float attack = timbre.attackSeconds > 0f ? Mathf.Clamp01(t / timbre.attackSeconds) : 1f;
                float decay = Mathf.Exp(-timbre.decayRate * t / ToneDuration); // decays to ~0 by the clip's end, no click on stop
                float envelope = attack * decay;

                data[n] = sample * envelope * 0.18f; // headroom for several summed harmonics, keeps well under clipping
            }

            var clip = AudioClip.Create($"tone_{frequency:0}", samples, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>
        /// Builds a short, seamlessly loopable waveform for this frequency/
        /// timbre - rounded to a whole number of FUNDAMENTAL periods, so
        /// every harmonic (each an integer multiple of the fundamental) lines
        /// up in phase again exactly at the loop point, and AudioSource.loop
        /// can repeat it indefinitely with no audible click. Deliberately
        /// carries no attack/decay envelope, unlike BuildToneClip - baking a
        /// decay in here would make the volume visibly dip and rebuild every
        /// single loop cycle (an audible "pulsing"/tremolo artifact) once
        /// looped. The attack feel and the eventual cutoff at release are
        /// instead handled by the caller fading AudioSource.volume directly
        /// (see GameplayLane's hold-note playback and PianoGuideTrack's
        /// sustain voices) - this clip itself just holds at a constant level.
        /// </summary>
        static AudioClip BuildSustainClip(float frequency, Timbre timbre)
        {
            int periods = Mathf.Max(1, Mathf.RoundToInt(SustainLoopSeconds * frequency));
            int samples = Mathf.Max(2, Mathf.RoundToInt(periods * SampleRate / frequency));
            var data = new float[samples];

            for (int n = 0; n < samples; n++)
            {
                float t = n / (float)SampleRate;

                float vibrato = timbre.vibratoDepth > 0f
                    ? 1f + timbre.vibratoDepth * Mathf.Sin(2f * Mathf.PI * timbre.vibratoRateHz * t)
                    : 1f;
                float f = frequency * vibrato;

                float sample = 0f;
                for (int h = 0; h < timbre.harmonicAmps.Length; h++)
                    sample += timbre.harmonicAmps[h] * Mathf.Sin(2f * Mathf.PI * f * (h + 1) * t);

                data[n] = sample * 0.18f; // same headroom factor as BuildToneClip, for the same reason
            }

            var clip = AudioClip.Create($"sustain_{frequency:0}", samples, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static AudioClip BuildClickClip()
        {
            int samples = Mathf.CeilToInt(SampleRate * ClickDuration);
            var data = new float[samples];
            var rng = new System.Random(1); // fixed seed - same click every time, not that it matters audibly

            for (int n = 0; n < samples; n++)
            {
                float t = n / (float)SampleRate;
                float envelope = Mathf.Exp(-30f * t);
                data[n] = ((float)rng.NextDouble() * 2f - 1f) * envelope * 0.6f;
            }

            var clip = AudioClip.Create("drum_click", samples, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
