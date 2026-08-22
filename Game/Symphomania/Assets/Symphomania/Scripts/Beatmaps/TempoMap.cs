using System.Collections.Generic;

namespace Symphomania.Beatmaps
{
    /// <summary>
    /// Beat -&gt; wall-clock-seconds conversion from a song's tempo_changes list
    /// (see beatmap_schema.md's "Time base" section). Notes already carry both
    /// start_beat and start_time as authored by the converter, so nothing in
    /// the loader needs this - it exists for anything that has to invent a
    /// time for an arbitrary beat position the converter never emitted, which
    /// is exactly what a scrolling beat-line grid needs (a line every quarter
    /// beat, not just at note onsets).
    /// </summary>
    public static class TempoMap
    {
        /// <summary>
        /// Converts a beat position to seconds by walking the piecewise-constant
        /// tempo map: each tempo_changes entry holds from its own beat until the
        /// next entry's beat (or forever, for the last one). Assumes
        /// <paramref name="tempoChanges"/> is sorted ascending by Beat - true for
        /// anything BeatmapLoader produced.
        /// </summary>
        public static float BeatToTime(List<TempoChange> tempoChanges, float beat)
        {
            if (tempoChanges == null || tempoChanges.Count == 0)
                return beat * (60f / 120f); // no tempo data at all - assume 120bpm rather than divide by zero

            float time = 0f;

            for (int i = 0; i < tempoChanges.Count; i++)
            {
                var change = tempoChanges[i];
                float bpm = change.Bpm > 0f ? change.Bpm : 120f;
                float secondsPerBeat = 60f / bpm;

                bool isLast = i == tempoChanges.Count - 1;
                float segmentEndBeat = isLast ? float.PositiveInfinity : tempoChanges[i + 1].Beat;

                if (beat <= segmentEndBeat)
                {
                    time += (beat - change.Beat) * secondsPerBeat;
                    return time;
                }

                time += (segmentEndBeat - change.Beat) * secondsPerBeat;
            }

            return time;
        }
    }
}
