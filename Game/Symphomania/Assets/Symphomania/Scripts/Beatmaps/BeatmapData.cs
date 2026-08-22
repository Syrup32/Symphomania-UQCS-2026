using System.Collections.Generic;
using Symphomania.Controllers;

namespace Symphomania.Beatmaps
{
    /// <summary>One entry in song.tempo_changes.</summary>
    public struct TempoChange
    {
        public float Beat;
        public float Bpm;
    }

    public class BeatmapSong
    {
        public string Title = "(untitled)";
        public string Composer = "";
        public string Source = "";
        public string TimeSignature = "4/4";
        public List<TempoChange> TempoChanges = new List<TempoChange>();
        public float DurationSeconds;
    }

    /// <summary>
    /// One sustained-note event for a non-drum instrument (trumpet, saxophone,
    /// violin, trombone). <see cref="Fingering"/> is precomputed at load time by
    /// <see cref="BeatmapLoader"/> using only hardware-verified fields from the
    /// note's `input` object, using the exact same bit convention
    /// <c>VirtualBandInput</c> uses for live hardware — see that class's doc
    /// comment. That is what makes hit-judging a single integer compare:
    /// <c>live.Fingering == note.Fingering</c>.
    /// </summary>
    public class BeatmapNote
    {
        public int Id;
        public int Measure;
        public float StartBeat;
        public float StartTime;
        public float DurationBeats;
        public float DurationTime;

        /// <summary>
        /// Concert (sounding) pitch, e.g. "G4" — per beatmap_schema.md this is
        /// always concert pitch regardless of instrument/mode/input format.
        /// Debug/QA and the audio-playback sample key ONLY — never used for
        /// judging a hit.
        /// </summary>
        public string Pitch;

        /// <summary>
        /// Canonical fingering mask (see VirtualBandInput's doc comment),
        /// computed only from hardware-verified `input` fields. Cosmetic
        /// fields — e.g. violin's suggested_bow_direction — never influence
        /// this, per beatmap_schema.md's explicit warning that they must never
        /// be used for judging.
        /// </summary>
        public uint Fingering;

        /// <summary>
        /// Non-null when the Python converter flagged this note as
        /// unrepresentable on the real hardware (e.g. a chromatic pitch the
        /// 8-switch saxophone chart can't express, or a trombone note outside
        /// E2-F4) and emitted its best approximation anyway rather than
        /// dropping the beat. Surface this somewhere in a chart-authoring /
        /// QA view; it is not meant to block gameplay.
        /// </summary>
        public string Warning;
    }

    /// <summary>
    /// One drum pad strike. No sustain, no pitch, and no separate check input —
    /// the hit itself is the check, unlike every other instrument.
    /// </summary>
    public class BeatmapHit
    {
        public int Id;
        public int Measure;
        public float Time;
        public float Beat;

        /// <summary>1-7. See the authoritative pad table in beatmap_schema.md.</summary>
        public int Pad;
        public string PadName;
        public float Velocity;

        /// <summary>
        /// 1u &lt;&lt; (Pad-1). Compare this against VirtualBandInput's
        /// PadsStruck (an edge-triggered "struck this frame" mask), not
        /// Fingering (which is a held-state mask and wrong for an instant hit).
        /// </summary>
        public uint PadMask;
    }

    /// <summary>One instrument's part within a song. Exactly one of Notes/Hits is populated.</summary>
    public class BeatmapTrack
    {
        public InstrumentType Instrument;

        /// <summary>Null for the drum kit track.</summary>
        public List<BeatmapNote> Notes;

        /// <summary>Non-null only for the drum kit track.</summary>
        public List<BeatmapHit> Hits;

        public bool IsDrumTrack => Hits != null;
    }

    /// <summary>
    /// One fully-parsed beatmap file. <see cref="Tracks"/> only contains keys
    /// for instruments actually present in the source JSON — see "Session-time
    /// instrument availability" in beatmap_schema.md. Session setup is expected
    /// to intersect these keys with VirtualBandInput.ConnectedInstruments(); a
    /// track with no connected controller, or a connected controller with no
    /// track here, are both normal and not an error.
    /// </summary>
    public class Beatmap
    {
        public BeatmapSong Song = new BeatmapSong();
        public Dictionary<InstrumentType, BeatmapTrack> Tracks = new Dictionary<InstrumentType, BeatmapTrack>();
    }
}
