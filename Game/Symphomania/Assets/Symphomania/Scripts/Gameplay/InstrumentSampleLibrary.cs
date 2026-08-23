using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using Symphomania.Controllers;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// Optional real-audio path for hit feedback. NoteAudio's tones are
    /// synthesized (sine + harmonics) because I can't bundle actual recorded/
    /// sampled instrument audio here - that's copyrighted content, same as I
    /// wouldn't fabricate a real person's voice - so there was nothing
    /// legitimate to ship as "real trumpet/violin/etc. sound" out of the box.
    /// This is the plumbing for YOU to drop in whatever samples you have the
    /// rights to use: your own recordings, or a permissively-licensed library
    /// (the University of Iowa Musical Instrument Samples, VSCO2 Community
    /// Edition, and Sonatina Symphonic Orchestra are common free starting
    /// points, or freesound.org for one-off single notes). Once real samples
    /// exist under StreamingAssets, GameplayLane prefers them automatically -
    /// nothing else needs to change.
    ///
    /// Two ways to supply real audio, both scanned automatically - use
    /// whichever fits what you have:
    ///
    /// 1) Your own WAV recordings, dropped straight in the filesystem at
    ///    StreamingAssets/InstrumentSamples/<Instrument>/<Pitch>.wav - e.g.
    ///    ".../InstrumentSamples/Trumpet/C4.wav", ".../Violin/F#3.wav".
    ///
    /// 2) Already-imported Unity AudioClips - e.g. a purchased asset-store
    ///    instrument pack - placed (or left, if that's where they already
    ///    live) under Assets/Resources/InstrumentSamples/<Instrument>/, any
    ///    filename, as long as it ENDS in an underscore then a note letter
    ///    with an optional '#' (e.g. "Toy Keyboard Violin02_C#", matching
    ///    that kind of pack's own naming). These filenames don't carry an
    ///    octave number (a one-octave "toy keyboard" voice doesn't need one),
    ///    so every clip found this way is assumed to be the SAME octave - see
    ///    ResourceOctaveAssumption below; retune that constant if the pitch-
    ///    shifted result sounds off (too chipmunk-y = assumption too low, too
    ///    deep/muddy = too high).
    ///
    /// You don't need one file per semitone either way - one sample every
    /// minor third or so per instrument is plenty; TryGetNearest finds the
    /// closest available sample and reports the semitone distance so the
    /// caller can pitch-shift it (via AudioSource.pitch) to the exact note.
    /// An instrument with nothing in either location just falls back to
    /// NoteAudio's synthesized tone - this is entirely additive, never
    /// required.
    /// </summary>
    public static class InstrumentSampleLibrary
    {
        /// <summary>
        /// Assumed octave for any sample found via the Resources path (see
        /// class doc comment item 2) - those filenames don't carry an octave
        /// number, so this is a guess, not something read from the asset.
        /// Middle-of-the-keyboard toy/synth voices are commonly centered
        /// around here; adjust by ear if pitch-shifted notes sound off.
        /// </summary>
        const int ResourceOctaveAssumption = 4;

        // Matches a trailing "_<letter><optional #>" - e.g. "...02_C#" -> ("C", "#").
        static readonly Regex TrailingNoteLetter = new Regex(@"_([A-Ga-g])(#)?$", RegexOptions.Compiled);

        /// <summary>
        /// Fixed bit-index -> filename mapping for the drum kit, matching
        /// controller_hid_protocol.md's pad order exactly (bit 0 = Crash,
        /// bit 1 = Snare, ... bit 6 = Ride). A drum pad has no pitch to
        /// fuzzy-match against the way a melodic note does - it's a specific
        /// sound, not a note somewhere on a scale - so this expects an exact
        /// resource name per pad rather than TryGetNearest's nearest-available
        /// search. Put (or rename copies of) your percussion one-shots at
        /// Resources/InstrumentSamples/DrumKit/<Name>.wav using these exact
        /// names; any pad left out just falls back to NoteAudio's synthesized
        /// click for that pad.
        /// </summary>
        static readonly string[] DrumPadResourceNames =
        {
            "Crash", "Snare", "HighTom", "Kick", "MidTom", "FloorTom", "Ride",
        };

        static readonly Dictionary<int, AudioClip> _drumPads = new Dictionary<int, AudioClip>(); // bit index -> clip
        static bool _drumPadsScanned;

        /// <summary>
        /// Wipes every scan cache at the start of every Play session, same
        /// reason VirtualBandInput/GameSessionContext already do this - with
        /// "Enter Play Mode Options" domain reload disabled, these statics
        /// otherwise survive from the previous Play session. Without this,
        /// the FIRST Play session after adding this file (before any real
        /// samples existed under Resources/StreamingAssets) would scan an
        /// empty folder, cache "nothing found" into _scanned/_pianoScanned/
        /// _drumPadsScanned, and then every subsequent Play session would
        /// keep reusing that stale empty result forever - even long after
        /// real samples were dropped in - since EnsureScanned/
        /// EnsurePianoGuideScanned/EnsureDrumPadsScanned only ever check
        /// "have I scanned before", never "has anything changed since".
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _library.Clear();
            _scanned.Clear();
            _pianoLibrary.Clear();
            _pianoScanned = false;
            _drumPads.Clear();
            _drumPadsScanned = false;
        }

        class Entry
        {
            public int Midi;
            public AudioClip Clip;
        }

        static readonly Dictionary<InstrumentType, List<Entry>> _library = new Dictionary<InstrumentType, List<Entry>>();
        static readonly HashSet<InstrumentType> _scanned = new HashSet<InstrumentType>();

        /// <summary>
        /// The piano guide track's own real-sample cache, parallel to
        /// _library but keyed by a fixed folder name ("Piano") instead of an
        /// InstrumentType, since there's no InstrumentType for "the
        /// reconstructed piano/song guide track" - it isn't one of the five
        /// playable instruments. Scans the exact same two locations
        /// (StreamingAssets WAVs, Resources clips) via the folder-name
        /// overloads below, so dropping a Toy Keyboard-style piano voice
        /// pack at Resources/InstrumentSamples/Piano/ (same trailing
        /// "_C"/"_F#" naming convention as any other instrument) is picked
        /// up automatically.
        /// </summary>
        const string PianoGuideFolder = "Piano";
        static readonly List<Entry> _pianoLibrary = new List<Entry>();
        static bool _pianoScanned;

        /// <summary>
        /// Same lookup as TryGetNearest, but for PianoGuideTrack's
        /// reconstructed guide rendition rather than a live instrument's own
        /// hit-confirmation audio - see _pianoLibrary's doc comment. Returns
        /// false (leaving clip null) if no piano sample pack has been
        /// supplied, so the caller (PianoGuideTrack) should fall back to
        /// NoteAudio's synthesized piano sustain in that case.
        /// </summary>
        public static bool TryGetNearestPianoGuide(string pitch, out AudioClip clip, out float playbackPitch)
        {
            clip = null;
            playbackPitch = 1f;
            if (string.IsNullOrEmpty(pitch)) return false;

            EnsurePianoGuideScanned();
            if (_pianoLibrary.Count == 0) return false;

            int targetMidi;
            try { targetMidi = MidiForPitch(pitch); }
            catch (FormatException) { return false; }

            Entry best = null;
            int bestDistance = int.MaxValue;
            foreach (var e in _pianoLibrary)
            {
                int d = Mathf.Abs(e.Midi - targetMidi);
                if (d < bestDistance) { bestDistance = d; best = e; }
            }
            if (best == null) return false;

            clip = best.Clip;
            playbackPitch = Mathf.Pow(2f, (targetMidi - best.Midi) / 12f);
            return true;
        }

        static void EnsurePianoGuideScanned()
        {
            if (_pianoScanned) return;
            _pianoScanned = true;

            ScanStreamingAssetsWavs(PianoGuideFolder, _pianoLibrary);
            ScanResourcesClips(PianoGuideFolder, _pianoLibrary);

            if (_pianoLibrary.Count > 0)
                Debug.Log($"[InstrumentSampleLibrary] Loaded {_pianoLibrary.Count} real piano guide sample(s).");
        }

        /// <summary>
        /// Looks up the closest available real sample for this pitch. Returns
        /// false (leaving clip null) if this instrument has no sample folder
        /// at all, or the folder exists but nothing in it loaded - the caller
        /// should fall back to NoteAudio in that case. On success, set
        /// AudioSource.pitch to the returned playbackPitch before playing
        /// clip, so the one recorded sample sounds like the requested note
        /// rather than whatever note was actually recorded.
        /// </summary>
        public static bool TryGetNearest(InstrumentType instrument, string pitch, out AudioClip clip, out float playbackPitch)
        {
            clip = null;
            playbackPitch = 1f;
            if (string.IsNullOrEmpty(pitch)) return false;

            EnsureScanned(instrument);
            if (!_library.TryGetValue(instrument, out var entries) || entries.Count == 0)
                return false;

            int targetMidi;
            try { targetMidi = MidiForPitch(pitch); }
            catch (FormatException) { return false; } // malformed pitch string - let NoteAudio's own fallback parsing handle it

            Entry best = null;
            int bestDistance = int.MaxValue;
            foreach (var e in entries)
            {
                int d = Mathf.Abs(e.Midi - targetMidi);
                if (d < bestDistance) { bestDistance = d; best = e; }
            }
            if (best == null) return false;

            clip = best.Clip;
            playbackPitch = Mathf.Pow(2f, (targetMidi - best.Midi) / 12f);
            return true;
        }

        /// <summary>
        /// Looks up a real sample for a struck drum pad (or pads - see below),
        /// keyed by DrumPadResourceNames rather than pitch. If padMask has
        /// more than one bit set (a beatmap hit striking several pads at
        /// once), only the lowest-numbered pad WITH an available sample
        /// plays - simultaneous multi-pad real-sample layering isn't
        /// supported, a reasonable simplification since a JudgeEvent only
        /// carries one clip slot. Returns false (leaving clip null) if no
        /// pad in the mask has a loaded sample, so the caller can fall back
        /// to NoteAudio.DrumClick().
        /// </summary>
        public static bool TryGetForDrumPad(uint padMask, out AudioClip clip)
        {
            clip = null;
            if (padMask == 0) return false;

            EnsureDrumPadsScanned();

            for (int bit = 0; bit < DrumPadResourceNames.Length; bit++)
            {
                if ((padMask & (1u << bit)) == 0) continue;
                if (_drumPads.TryGetValue(bit, out clip)) return true;
            }
            return false;
        }

        static void EnsureDrumPadsScanned()
        {
            if (_drumPadsScanned) return;
            _drumPadsScanned = true;

            for (int bit = 0; bit < DrumPadResourceNames.Length; bit++)
            {
                var clip = Resources.Load<AudioClip>("InstrumentSamples/DrumKit/" + DrumPadResourceNames[bit]);
                if (clip != null) _drumPads[bit] = clip;
            }

            if (_drumPads.Count > 0)
                Debug.Log($"[InstrumentSampleLibrary] Loaded {_drumPads.Count}/{DrumPadResourceNames.Length} real drum pad sample(s).");
        }

        static void EnsureScanned(InstrumentType instrument)
        {
            if (_scanned.Contains(instrument)) return;
            _scanned.Add(instrument);

            var entries = new List<Entry>();
            ScanStreamingAssetsWavs(instrument.ToString(), entries);
            ScanResourcesClips(instrument.ToString(), entries);

            if (entries.Count > 0)
            {
                _library[instrument] = entries;
                Debug.Log($"[InstrumentSampleLibrary] Loaded {entries.Count} real sample(s) for {instrument}.");
            }
        }

        /// <summary>
        /// Source 1: your own raw WAV recordings, not run through Unity's
        /// import pipeline at all. Takes a plain folder name rather than an
        /// InstrumentType so this same scan also serves the piano guide
        /// track's own sample folder (see _pianoLibrary/PianoGuideFolder),
        /// which isn't one of the five playable InstrumentType values.
        /// </summary>
        static void ScanStreamingAssetsWavs(string folderName, List<Entry> entries)
        {
            string dir = Path.Combine(Application.streamingAssetsPath, "InstrumentSamples", folderName);
            if (!Directory.Exists(dir)) return; // no samples supplied this way - fine, either the Resources path or the synthesized fallback covers it

            foreach (var path in Directory.GetFiles(dir, "*.wav"))
            {
                string pitchName = Path.GetFileNameWithoutExtension(path);
                int midi;
                try { midi = MidiForPitch(pitchName); }
                catch (FormatException)
                {
                    Debug.LogWarning($"[InstrumentSampleLibrary] Skipping '{path}' - filename isn't a pitch like 'C4' or 'F#3'.");
                    continue;
                }

                var clip = LoadWav(path);
                if (clip != null) entries.Add(new Entry { Midi = midi, Clip = clip });
            }
        }

        /// <summary>
        /// Source 2: already-imported Unity AudioClips (e.g. a purchased
        /// instrument pack) under Resources/InstrumentSamples/&lt;folderName&gt;/.
        /// Takes a plain folder name for the same reason ScanStreamingAssetsWavs
        /// does - see that method's doc comment.
        /// </summary>
        static void ScanResourcesClips(string folderName, List<Entry> entries)
        {
            var clips = Resources.LoadAll<AudioClip>("InstrumentSamples/" + folderName);
            foreach (var clip in clips)
            {
                var match = TrailingNoteLetter.Match(clip.name);
                if (!match.Success)
                {
                    Debug.LogWarning($"[InstrumentSampleLibrary] Couldn't find a trailing note letter (like '_C' or '_F#') in Resources clip '{clip.name}' for {folderName} - skipping it.");
                    continue;
                }

                char letter = char.ToUpperInvariant(match.Groups[1].Value[0]);
                int accidental = match.Groups[2].Success ? 1 : 0; // this regex only ever captures '#', never 'b' - matches this pack's naming
                if (!TryLetterToSemitone(letter, out int semitoneFromC)) continue;

                int midi = (ResourceOctaveAssumption + 1) * 12 + semitoneFromC + accidental;
                entries.Add(new Entry { Midi = midi, Clip = clip });
            }
        }

        /// <summary>Scientific pitch notation ("C4", "F#4", "Bb2") -> MIDI note number (A4 = 69), same convention as NoteAudio.FrequencyForPitch.</summary>
        static int MidiForPitch(string pitch)
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
                throw new FormatException(pitch);

            if (!TryLetterToSemitone(letter, out int semitoneFromC))
                throw new FormatException(pitch);

            return (octave + 1) * 12 + semitoneFromC + accidental;
        }

        /// <summary>Shared by both MidiForPitch (full "C4" strings) and ScanResourcesClips (bare trailing letters) so the note-name convention lives in exactly one place.</summary>
        static bool TryLetterToSemitone(char letter, out int semitoneFromC)
        {
            switch (letter)
            {
                case 'C': semitoneFromC = 0; return true;
                case 'D': semitoneFromC = 2; return true;
                case 'E': semitoneFromC = 4; return true;
                case 'F': semitoneFromC = 5; return true;
                case 'G': semitoneFromC = 7; return true;
                case 'A': semitoneFromC = 9; return true;
                case 'B': semitoneFromC = 11; return true;
                default: semitoneFromC = 0; return false;
            }
        }

        /// <summary>
        /// Minimal synchronous 16-bit PCM WAV reader - deliberately not using
        /// UnityWebRequestMultimedia/a coroutine, to keep this a simple
        /// same-frame call like everything else in this file and in
        /// NoteAudio. Good enough for short one-shot instrument samples;
        /// doesn't handle compressed or 32-bit-float WAV variants - re-export
        /// as 16-bit PCM WAV if a sample fails to load.
        /// </summary>
        static AudioClip LoadWav(string path)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (IOException e)
            {
                Debug.LogWarning($"[InstrumentSampleLibrary] Couldn't read '{path}': {e.Message}");
                return null;
            }

            if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            {
                Debug.LogWarning($"[InstrumentSampleLibrary] '{path}' doesn't look like a WAV file.");
                return null;
            }

            int channels = BitConverter.ToInt16(bytes, 22);
            int sampleRate = BitConverter.ToInt32(bytes, 24);
            int bitsPerSample = BitConverter.ToInt16(bytes, 34);

            // Walk chunks to find "data" rather than assuming it's always at
            // byte 44 - some WAV files carry extra chunks (e.g. "fmt " extras,
            // "LIST") before it.
            int pos = 12;
            int dataOffset = -1, dataSize = 0;
            while (pos + 8 <= bytes.Length)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                int chunkSize = BitConverter.ToInt32(bytes, pos + 4);
                if (chunkId == "data") { dataOffset = pos + 8; dataSize = chunkSize; break; }
                pos += 8 + chunkSize + (chunkSize % 2); // chunks are word-aligned
            }

            if (dataOffset < 0 || bitsPerSample != 16)
            {
                Debug.LogWarning($"[InstrumentSampleLibrary] '{path}' isn't 16-bit PCM WAV (only format supported) or has no data chunk.");
                return null;
            }

            int sampleCount = dataSize / 2 / Mathf.Max(1, channels);
            var data = new float[sampleCount * channels];
            for (int n = 0; n < data.Length; n++)
            {
                int byteIndex = dataOffset + n * 2;
                if (byteIndex + 1 >= bytes.Length) break; // truncated/odd data chunk - stop rather than throw
                short s = BitConverter.ToInt16(bytes, byteIndex);
                data[n] = s / 32768f;
            }

            var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), sampleCount, Mathf.Max(1, channels), sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
