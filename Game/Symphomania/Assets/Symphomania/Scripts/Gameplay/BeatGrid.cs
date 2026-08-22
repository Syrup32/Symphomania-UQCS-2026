using System.Collections.Generic;
using Symphomania.Beatmaps;

namespace Symphomania.Gameplay
{
    /// <summary>One scrolling reference line: a quarter-beat tick, or a bold measure line.</summary>
    public struct BeatGridLine
    {
        public int BeatIndex;
        public float Time;
        public bool IsMeasureStart;
    }

    /// <summary>
    /// Builds the DDR-style scrolling beat grid the player uses to hold tempo
    /// visually - a faint line every quarter beat, a bold line every measure.
    /// Pure data: NoteLaneView is what actually draws and scrolls these.
    /// </summary>
    public static class BeatGrid
    {
        /// <summary>
        /// One BeatGridLine per integer beat from 0 up to (and slightly past)
        /// <paramref name="coverageSeconds"/>, using the song's tempo map.
        /// "Quarter beat" here means one grid line per beat as counted by the
        /// time signature's bottom number (a beat IS a quarter note in 4/4) -
        /// finer subdivision than that isn't something the beatmap format
        /// tracks, so this is the finest grid available without guessing.
        /// </summary>
        public static List<BeatGridLine> Build(BeatmapSong song, float coverageSeconds)
        {
            var lines = new List<BeatGridLine>();
            int beatsPerMeasure = ParseBeatsPerMeasure(song.TimeSignature);

            int beatIndex = 0;
            while (true)
            {
                float time = TempoMap.BeatToTime(song.TempoChanges, beatIndex);
                lines.Add(new BeatGridLine
                {
                    BeatIndex = beatIndex,
                    Time = time,
                    IsMeasureStart = beatIndex % beatsPerMeasure == 0,
                });

                if (time > coverageSeconds) break;
                beatIndex++;

                // Safety valve: a corrupt/zero tempo map could make BeatToTime
                // never advance past coverageSeconds. Cap rather than hang.
                if (beatIndex > 100000) break;
            }

            return lines;
        }

        static int ParseBeatsPerMeasure(string timeSignature)
        {
            if (!string.IsNullOrEmpty(timeSignature))
            {
                var parts = timeSignature.Split('/');
                if (parts.Length == 2 && int.TryParse(parts[0], out int numerator) && numerator > 0)
                    return numerator;
            }
            return 4; // sensible default; every sample fixture so far is 4/4 anyway
        }
    }
}
