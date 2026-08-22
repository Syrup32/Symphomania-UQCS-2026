using System.Collections.Generic;
using Symphomania.Beatmaps;
using Symphomania.Controllers;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// Turns a note's fingering mask (or a drum hit's pad) into a short,
    /// two-line label: the keyboard digit(s) to hold, matching
    /// KeyboardInstrumentFallback's 1-based digit-per-bit scheme exactly, with
    /// the catalog's descriptive name(s) underneath for context.
    ///
    /// Exists purely so a scrolling note can tell the player what to press -
    /// without it there's no way to playtest the timing judge without already
    /// knowing the chart by ear.
    /// </summary>
    public static class NoteLabels
    {
        /// <summary>
        /// e.g. "1+2\n(V1+V2)" for a trumpet note pressing valves 1 and 2, or
        /// "OPEN\n(no keys)" for a note with nothing held. Digit numbers are
        /// exactly what KeyboardInstrumentFallback reads (bit i = digit i+1),
        /// so this is a direct "press these keys" instruction, not just a
        /// description.
        /// </summary>
        public static string ForNote(InstrumentType instrument, uint fingering)
        {
            var info = InstrumentCatalog.Get(instrument);
            var digits = new List<string>();
            var names = new List<string>();

            for (int i = 0; i < info.FingeringCount && i < 8; i++)
            {
                if ((fingering & (1u << i)) == 0) continue;
                digits.Add((i + 1).ToString());
                names.Add(info.FingeringLabels[i]);
            }

            if (digits.Count == 0) return "OPEN\n(no keys)";
            return string.Join("+", digits) + "\n(" + string.Join("+", names) + ")";
        }

        /// <summary>e.g. "2\n(Snare)" - the drum kit's keyboard mapping is one digit per pad, no combos.</summary>
        public static string ForHit(BeatmapHit hit) => $"{hit.Pad}\n({hit.PadName})";
    }
}
