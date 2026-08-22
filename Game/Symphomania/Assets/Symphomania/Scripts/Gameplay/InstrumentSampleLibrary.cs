using System;
using System.Collections.Generic;
using System.IO;
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
    /// Layout: StreamingAssets/InstrumentSamples/<Instrument>/<Pitch>.wav -
    /// e.g. ".../InstrumentSamples/Trumpet/C4.wav", ".../Violin/F#3.wav". You
    /// don't need one file per semitone - one sample every minor third or so
    /// per instrument is plenty; TryGetNearest finds the closest available
    /// sample and reports the semitone distance so the caller can pitch-shift
    /// it (via AudioSource.pitch) to the exact note. An instrument with no
    /// folder (or an empty one) just falls back to NoteAudio's synthesized
    /// tone - this is entirely additive, never required.
    /// </summary>
    public static class InstrumentSampleLibrary
    {
        class Entry
        {
            public int Midi;
            public AudioClip Clip;
        }

        static readonly Dictionary<InstrumentType, List<Entry>> _library = new Dictionary<InstrumentType, List<Entry>>();
        static readonly HashSet<InstrumentType> _scanned = new HashSet<InstrumentType>();

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

        static void EnsureScanned(InstrumentType instrument)
        {
            if (_scanned.Contains(instrument)) return;
            _scanned.Add(instrument);

            string dir = Path.Combine(Application.streamingAssetsPath, "InstrumentSamples", instrument.ToString());
            if (!Directory.Exists(dir)) return; // no samples supplied for this instrument - fine, NoteAudio covers it

            var entries = new List<Entry>();
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

            if (entries.Count > 0)
            {
                _library[instrument] = entries;
                Debug.Log($"[InstrumentSampleLibrary] Loaded {entries.Count} real sample(s) for {instrument} from '{dir}'.");
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

            int semitoneFromC = letter switch
            {
                'C' => 0,
                'D' => 2,
                'E' => 4,
                'F' => 5,
                'G' => 7,
                'A' => 9,
                'B' => 11,
                _ => throw new FormatException(pitch),
            };

            return (octave + 1) * 12 + semitoneFromC + accidental;
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
