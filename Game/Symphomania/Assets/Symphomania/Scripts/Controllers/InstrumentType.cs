using System.Collections.Generic;

namespace Symphomania.Controllers
{
    /// <summary>
    /// The five Virtual Band controllers. Values are stable; do not renumber
    /// (they are used for ordering and serialization).
    /// </summary>
    public enum InstrumentType
    {
        None = 0,
        Trumpet = 1,
        Saxophone = 2,
        DrumKit = 3,
        Violin = 4,
        Trombone = 5,
    }

    /// <summary>
    /// Static per-instrument metadata. THIS IS THE SINGLE SOURCE OF TRUTH for
    /// PID -> instrument -> screen section, per controller_hid_protocol.md
    /// ("PID -> screen section mapping should live in one place on the Unity
    /// side"). Nothing else in the codebase should hardcode a PID.
    /// </summary>
    public static class InstrumentCatalog
    {
        /// <summary>pid.codes shared VID used by every Virtual Band controller.</summary>
        public const int VendorId = 0x1209;

        public struct Info
        {
            /// <summary>Which instrument this is.</summary>
            public InstrumentType Instrument;

            /// <summary>USB product ID (pid.codes test range 0x0001-0x0009).</summary>
            public int ProductId;

            /// <summary>USB product string reported by the firmware.</summary>
            public string ProductString;

            /// <summary>Human-facing name for UI.</summary>
            public string DisplayName;

            /// <summary>
            /// Key under "instruments" in the beatmap JSON. Must match the
            /// Python converter's output exactly (see beatmap_schema.md).
            /// </summary>
            public string BeatmapKey;

            /// <summary>
            /// Left-to-right column order for the vertical split. Lower = further
            /// left. Trombone is -1 because it takes the bottom banner instead of
            /// a column.
            /// </summary>
            public int ColumnOrder;

            /// <summary>
            /// True if this instrument has a dedicated "check" input on button 1
            /// (breath for brass, bow rotation for violin). False for the drum
            /// kit, where the pad hit IS the check.
            /// </summary>
            public bool HasCheckButton;

            /// <summary>Label for the check input, for diagnostics UI.</summary>
            public string CheckLabel;

            /// <summary>
            /// How many fingering signals this instrument reports (excluding the
            /// check button). Trumpet 3 valves, sax 1 register + 7 holes = 8,
            /// violin 7 switches, trombone 7 slide positions, drums 7 pads.
            /// </summary>
            public int FingeringCount;

            /// <summary>
            /// Short labels for each fingering signal, index 0 = bit 0 of the
            /// canonical fingering mask. Used by the diagnostics overlay.
            /// </summary>
            public string[] FingeringLabels;

            /// <summary>
            /// True when only one fingering bit can legally be set at a time
            /// (trombone slide can't be in two positions at once).
            /// </summary>
            public bool FingeringIsOneHot;

            /// <summary>
            /// False for controllers whose hardware doesn't exist yet, so the
            /// diagnostics UI can say so instead of looking broken.
            /// </summary>
            public bool HardwareExists;
        }

        static readonly Dictionary<InstrumentType, Info> Table = new Dictionary<InstrumentType, Info>
        {
            [InstrumentType.Trumpet] = new Info
            {
                Instrument = InstrumentType.Trumpet,
                ProductId = 0x0001,
                ProductString = "VirtualBand Trumpet",
                DisplayName = "Trumpet",
                BeatmapKey = "trumpet",
                ColumnOrder = 0,
                HasCheckButton = true,
                CheckLabel = "BREATH",
                FingeringCount = 3,
                FingeringLabels = new[] { "V1", "V2", "V3" },
                FingeringIsOneHot = false,
                HardwareExists = true,
            },
            [InstrumentType.Saxophone] = new Info
            {
                Instrument = InstrumentType.Saxophone,
                ProductId = 0x0002,
                ProductString = "VirtualBand Saxophone",
                DisplayName = "Saxophone",
                BeatmapKey = "saxophone",
                ColumnOrder = 1,
                HasCheckButton = true,
                CheckLabel = "BREATH",
                FingeringCount = 8,
                // Bit 0 is the register/octave key, bits 1-7 are holes 1-7.
                FingeringLabels = new[] { "REG", "H1", "H2", "H3", "H4", "H5", "H6", "H7" },
                FingeringIsOneHot = false,
                HardwareExists = true,
            },
            [InstrumentType.DrumKit] = new Info
            {
                Instrument = InstrumentType.DrumKit,
                ProductId = 0x0003,
                ProductString = "VirtualBand DrumKit",
                DisplayName = "Drum Kit",
                BeatmapKey = "drum_kit",
                ColumnOrder = 2,
                HasCheckButton = false,
                CheckLabel = null,
                FingeringCount = 7,
                // Pad numbering is authoritative in beatmap_schema.md.
                FingeringLabels = new[] { "Kick", "Snare", "HiHat", "HiTom", "FlTom", "Crash", "Ride" },
                FingeringIsOneHot = false,
                HardwareExists = false, // not yet built
            },
            [InstrumentType.Violin] = new Info
            {
                Instrument = InstrumentType.Violin,
                ProductId = 0x0004,
                ProductString = "VirtualBand Violin",
                DisplayName = "Violin",
                BeatmapKey = "violin",
                ColumnOrder = 3,
                HasCheckButton = true,
                CheckLabel = "BOW",
                FingeringCount = 7,
                FingeringLabels = new[] { "S1", "S2", "S3", "S4", "S5", "S6", "S7" },
                FingeringIsOneHot = false,
                HardwareExists = true,
            },
            [InstrumentType.Trombone] = new Info
            {
                Instrument = InstrumentType.Trombone,
                ProductId = 0x0005,
                ProductString = "VirtualBand Trombone",
                DisplayName = "Trombone",
                BeatmapKey = "trombone",
                ColumnOrder = -1, // bottom banner, not a column
                HasCheckButton = true,
                CheckLabel = "BREATH",
                FingeringCount = 7,
                FingeringLabels = new[] { "P1", "P2", "P3", "P4", "P5", "P6", "P7" },
                FingeringIsOneHot = true, // slide can only be in one position
                HardwareExists = true,
            },
        };

        public static readonly InstrumentType[] All =
        {
            InstrumentType.Trumpet,
            InstrumentType.Saxophone,
            InstrumentType.DrumKit,
            InstrumentType.Violin,
            InstrumentType.Trombone,
        };

        /// <summary>
        /// Column instruments (everything except the trombone, which gets the
        /// bottom banner), sorted left to right by ColumnOrder. Layout code must
        /// use this rather than walking All and skipping the trombone, so
        /// ColumnOrder stays the actual authority it claims to be.
        /// </summary>
        public static List<InstrumentType> ColumnsInOrder()
        {
            var columns = new List<InstrumentType>();
            foreach (var instrument in All)
                if (Table[instrument].ColumnOrder >= 0)
                    columns.Add(instrument);

            columns.Sort((a, b) => Table[a].ColumnOrder.CompareTo(Table[b].ColumnOrder));
            return columns;
        }

        public static Info Get(InstrumentType instrument) => Table[instrument];

        public static bool TryGet(InstrumentType instrument, out Info info) =>
            Table.TryGetValue(instrument, out info);

        /// <summary>Reverse lookup from a USB product ID.</summary>
        public static InstrumentType FromProductId(int productId)
        {
            foreach (var kv in Table)
            {
                if (kv.Value.ProductId == productId)
                    return kv.Key;
            }
            return InstrumentType.None;
        }

        /// <summary>Reverse lookup from a beatmap JSON key ("drum_kit" etc).</summary>
        public static InstrumentType FromBeatmapKey(string key)
        {
            foreach (var kv in Table)
            {
                if (kv.Value.BeatmapKey == key)
                    return kv.Key;
            }
            return InstrumentType.None;
        }
    }
}
