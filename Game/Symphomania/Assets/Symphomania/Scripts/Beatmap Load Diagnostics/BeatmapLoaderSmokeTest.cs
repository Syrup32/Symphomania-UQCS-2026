using UnityEngine;
using Symphomania.Beatmaps;
using Symphomania.Controllers;

namespace Symphomania.Diagnostics
{
    /// <summary>
    /// The beatmap-loading equivalent of ControllerDiagnostics: a single
    /// MonoBehaviour, no scene wiring, that proves the loader works against a
    /// real file before any gameplay code depends on it. No hardware needed -
    /// per unity_input_layer.md's "Next steps", the beatmap loader is
    /// "testable immediately against your existing sample beatmaps."
    ///
    /// Drop this on any empty GameObject, set fileName (defaults to the sample
    /// fixture shipped alongside this script), press Play, read the Console.
    /// </summary>
    public class BeatmapLoaderSmokeTest : MonoBehaviour
    {
        [Tooltip("Filename under StreamingAssets/Beatmaps, e.g. twinkle_twinkle_sample.json")]
        public string fileName = "twinkle_twinkle_sample.json";

        [Tooltip("Also list every beatmap file found in StreamingAssets/Beatmaps, with its title, before loading fileName.")]
        public bool listAllFirst = true;

        void Start()
        {
            if (listAllFirst)
                LogListing();

            LoadAndLog();
        }

        [ContextMenu("Reload now")]
        void LoadAndLog()
        {
            Beatmap beatmap;
            try
            {
                beatmap = BeatmapLoader.LoadFromStreamingAssets(fileName);
            }
            catch (BeatmapParseException e)
            {
                Debug.LogError($"[BeatmapLoaderSmokeTest] Failed to load '{fileName}': {e.Message}");
                return;
            }

            var song = beatmap.Song;
            Debug.Log(
                $"[BeatmapLoaderSmokeTest] Loaded '{song.Title}' " +
                $"(composer: {(string.IsNullOrEmpty(song.Composer) ? "-" : song.Composer)}, " +
                $"{song.TimeSignature}, {song.TempoChanges.Count} tempo change(s), " +
                $"{song.DurationSeconds:0.##}s, {beatmap.Tracks.Count} track(s) present).");

            int totalWarnings = 0;

            foreach (var instrument in InstrumentCatalog.All)
            {
                if (!beatmap.Tracks.TryGetValue(instrument, out var track))
                {
                    Debug.Log($"    {instrument,-10} - not present in this beatmap.");
                    continue;
                }

                if (track.IsDrumTrack)
                {
                    Debug.Log($"    {instrument,-10} - {track.Hits.Count} hit(s). " +
                              $"First: {DescribeHit(track.Hits, 0)}  Last: {DescribeHit(track.Hits, track.Hits.Count - 1)}");
                    continue;
                }

                int warnings = 0;
                foreach (var note in track.Notes)
                {
                    if (string.IsNullOrEmpty(note.Warning)) continue;
                    warnings++;
                    // Logged individually rather than only via First/Last
                    // below - a flagged note in the middle of a track would
                    // otherwise never actually show up in the Console.
                    Debug.LogWarning($"    {instrument,-10} note id={note.Id} t={note.StartTime:0.00}s pitch={note.Pitch}: {note.Warning}");
                }
                totalWarnings += warnings;

                Debug.Log($"    {instrument,-10} - {track.Notes.Count} note(s), {warnings} flagged with a converter warning. " +
                          $"First: {DescribeNote(track.Notes, 0)}  Last: {DescribeNote(track.Notes, track.Notes.Count - 1)}");
            }

            if (totalWarnings > 0)
                Debug.LogWarning($"[BeatmapLoaderSmokeTest] {totalWarnings} note(s) across all tracks carry a converter warning - see per-note Warning fields (these are still playable, not dropped).");
        }

        void LogListing()
        {
            var listing = BeatmapLoader.ListStreamingAssetsBeatmaps();
            if (listing.Count == 0)
            {
                Debug.LogWarning("[BeatmapLoaderSmokeTest] No *.json files found under StreamingAssets/Beatmaps.");
                return;
            }

            Debug.Log($"[BeatmapLoaderSmokeTest] {listing.Count} beatmap file(s) in StreamingAssets/Beatmaps:");
            foreach (var entry in listing)
                Debug.Log($"    {entry.FileName}  ->  \"{entry.Title}\"");
        }

        static string DescribeNote(System.Collections.Generic.List<BeatmapNote> notes, int index)
        {
            if (notes.Count == 0) return "(none)";
            var n = notes[index];
            var warn = string.IsNullOrEmpty(n.Warning) ? "" : $" [WARNING: {n.Warning}]";
            return $"id={n.Id} t={n.StartTime:0.00}s pitch={n.Pitch} fingering=0x{n.Fingering:X2}{warn}";
        }

        static string DescribeHit(System.Collections.Generic.List<BeatmapHit> hits, int index)
        {
            if (hits.Count == 0) return "(none)";
            var h = hits[index];
            return $"id={h.Id} t={h.Time:0.00}s pad={h.Pad}({h.PadName}) mask=0x{h.PadMask:X2}";
        }
    }
}