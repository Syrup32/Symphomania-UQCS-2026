using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Symphomania.Controllers;

// ---------------------------------------------------------------------------
// Parses beatmap JSON (see claude/beatmap_schema.md) into the runtime
// structures in BeatmapData.cs, precomputing each note's canonical fingering
// mask so hit-judging is a single integer compare at runtime rather than a
// per-frame walk of a JSON object.
//
// WHY NEWTONSOFT: the schema's `instruments` object is a dictionary keyed by
// instrument name, and every non-drum `input` object is itself a dictionary
// keyed by stringified digits ("1".."7"). Unity's built-in JsonUtility can't
// deserialize dictionaries and can't pick between a "notes" shape and a
// "hits" shape at parse time, so this reads the JSON as a JObject tree
// (Newtonsoft.Json.Linq) and builds the strongly-typed result by hand.
// Requires the "com.unity.nuget.newtonsoft-json" package - Window -> Package
// Manager -> Add package by name - see the delivery notes for this drop.
// ---------------------------------------------------------------------------

namespace Symphomania.Beatmaps
{
    /// <summary>Thrown for structural problems in the JSON itself (missing top-level objects, unparsable file). Per-note oddities are logged as warnings and worked around instead — see the class doc below.</summary>
    public class BeatmapParseException : Exception
    {
        public BeatmapParseException(string message) : base(message) { }
        public BeatmapParseException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Bare filename + peeked title, for populating a song-select list without
    /// fully parsing every file in the folder.
    /// </summary>
    public struct BeatmapListing
    {
        public string FileName;
        public string Title;
    }

    public static class BeatmapLoader
    {
        /// <summary>
        /// Loads "&lt;StreamingAssets&gt;/&lt;subfolder&gt;/&lt;fileName&gt;". This is the
        /// expected home for beatmap JSON on every platform Application.streamingAssetsPath
        /// supports, and (unlike a Resources folder) plain file APIs can list its
        /// contents at runtime for the song-select screen.
        /// </summary>
        public static Beatmap LoadFromStreamingAssets(string fileName, string subfolder = "Beatmaps")
        {
            var fullPath = Path.Combine(Application.streamingAssetsPath, subfolder, fileName);
            return LoadFromFile(fullPath);
        }

        public static Beatmap LoadFromFile(string fullPath)
        {
            string json;
            try
            {
                json = File.ReadAllText(fullPath);
            }
            catch (Exception e)
            {
                throw new BeatmapParseException($"Could not read beatmap file '{fullPath}': {e.Message}", e);
            }

            try
            {
                return Parse(json);
            }
            catch (BeatmapParseException e)
            {
                throw new BeatmapParseException($"{fullPath}: {e.Message}", e);
            }
        }

        /// <summary>
        /// Every *.json file directly under StreamingAssets/&lt;subfolder&gt;, with
        /// just song.title peeked out of each (no track parsing) - cheap enough
        /// to call once when building a song-select list.
        /// </summary>
        public static List<BeatmapListing> ListStreamingAssetsBeatmaps(string subfolder = "Beatmaps")
        {
            var dir = Path.Combine(Application.streamingAssetsPath, subfolder);
            var result = new List<BeatmapListing>();
            if (!Directory.Exists(dir)) return result;

            foreach (var path in Directory.GetFiles(dir, "*.json"))
            {
                string title = Path.GetFileNameWithoutExtension(path);
                try
                {
                    var root = JObject.Parse(File.ReadAllText(path));
                    title = (string)root["song"]?["title"] ?? title;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BeatmapLoader] Couldn't peek a title out of '{path}' - listing it under its filename instead. ({e.Message})");
                }
                result.Add(new BeatmapListing { FileName = Path.GetFileName(path), Title = title });
            }
            return result;
        }

        /// <summary>Parses an already-loaded JSON string. Exposed directly so tests/tools don't need a file on disk.</summary>
        public static Beatmap Parse(string json)
        {
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception e)
            {
                throw new BeatmapParseException($"beatmap JSON is malformed: {e.Message}", e);
            }

            var beatmap = new Beatmap();
            ParseSong(root, beatmap.Song);

            if (!(root["instruments"] is JObject instrumentsObj))
                throw new BeatmapParseException("missing the top-level \"instruments\" object.");

            foreach (var prop in instrumentsObj.Properties())
            {
                string key = prop.Name;
                var instrument = InstrumentCatalog.FromBeatmapKey(key);
                if (instrument == InstrumentType.None)
                {
                    Debug.LogWarning($"[BeatmapLoader] '{beatmap.Song.Title}': unrecognized instrument key '{key}' - no entry in InstrumentCatalog claims this BeatmapKey, so this track is skipped entirely.");
                    continue;
                }

                if (!(prop.Value is JObject trackObj))
                {
                    Debug.LogWarning($"[BeatmapLoader] '{beatmap.Song.Title}': instrument '{key}' entry is not a JSON object - skipped.");
                    continue;
                }

                beatmap.Tracks[instrument] = instrument == InstrumentType.DrumKit
                    ? ParseDrumTrack(trackObj, beatmap.Song.Title)
                    : ParseNoteTrack(instrument, trackObj, beatmap.Song.Title);
            }

            return beatmap;
        }

        static void ParseSong(JObject root, BeatmapSong song)
        {
            if (!(root["song"] is JObject s))
                throw new BeatmapParseException("missing the top-level \"song\" object.");

            song.Title = (string)s["title"] ?? "(untitled)";
            song.Composer = (string)s["composer"] ?? "";
            song.Source = (string)s["source"] ?? "";
            song.TimeSignature = (string)s["time_signature"] ?? "4/4";
            song.DurationSeconds = (float?)s["duration_seconds"] ?? 0f;

            song.TempoChanges.Clear();
            if (s["tempo_changes"] is JArray tempos)
            {
                foreach (var t in tempos)
                {
                    song.TempoChanges.Add(new TempoChange
                    {
                        Beat = (float?)t["beat"] ?? 0f,
                        Bpm = (float?)t["bpm"] ?? 120f,
                    });
                }
            }

            if (song.TempoChanges.Count == 0)
            {
                Debug.LogWarning($"[BeatmapLoader] '{song.Title}' has no tempo_changes - defaulting to a single 120bpm entry at beat 0. Every note's start_time is still read as-authored regardless, so this only matters if something later re-derives times from beats.");
                song.TempoChanges.Add(new TempoChange { Beat = 0f, Bpm = 120f });
            }

            song.TempoChanges.Sort((a, b) => a.Beat.CompareTo(b.Beat));
        }

        static BeatmapTrack ParseNoteTrack(InstrumentType instrument, JObject trackObj, string songTitle)
        {
            var track = new BeatmapTrack { Instrument = instrument, Notes = new List<BeatmapNote>() };

            if (!(trackObj["notes"] is JArray notesArr))
            {
                Debug.LogWarning($"[BeatmapLoader] '{songTitle}': {instrument} track has no \"notes\" array - treating it as empty.");
                return track;
            }

            foreach (var n in notesArr)
            {
                var note = new BeatmapNote
                {
                    Id = (int?)n["id"] ?? -1,
                    Measure = (int?)n["measure"] ?? 0,
                    StartBeat = (float?)n["start_beat"] ?? 0f,
                    StartTime = (float?)n["start_time"] ?? 0f,
                    DurationBeats = (float?)n["duration_beats"] ?? 0f,
                    DurationTime = (float?)n["duration_time"] ?? 0f,
                    Pitch = (string)n["pitch"],
                    Warning = (string)n["warning"],
                };

                if (n["input"] is JObject input)
                {
                    note.Fingering = ComputeFingeringMask(instrument, input, songTitle, note.Id);
                }
                else
                {
                    Debug.LogWarning($"[BeatmapLoader] '{songTitle}': {instrument} note id={note.Id} has no \"input\" object - fingering mask left at 0 (unhittable as loaded).");
                }

                track.Notes.Add(note);
            }

            // Sort by time, not trusting source order - the scrolling/judging
            // window (beatmap_schema.md's "Hit detection" section) walks each
            // track in time order.
            track.Notes.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            return track;
        }

        static BeatmapTrack ParseDrumTrack(JObject trackObj, string songTitle)
        {
            var track = new BeatmapTrack { Instrument = InstrumentType.DrumKit, Hits = new List<BeatmapHit>() };

            if (!(trackObj["hits"] is JArray hitsArr))
            {
                Debug.LogWarning($"[BeatmapLoader] '{songTitle}': drum_kit track has no \"hits\" array - treating it as empty.");
                return track;
            }

            foreach (var h in hitsArr)
            {
                int pad = (int?)h["pad"] ?? 0;
                if (pad < 1 || pad > 7)
                {
                    Debug.LogWarning($"[BeatmapLoader] '{songTitle}': drum hit id={(int?)h["id"] ?? -1} has out-of-range pad {pad} (expected 1-7 per the authoritative pad table in beatmap_schema.md) - skipped.");
                    continue;
                }

                track.Hits.Add(new BeatmapHit
                {
                    Id = (int?)h["id"] ?? -1,
                    Measure = (int?)h["measure"] ?? 0,
                    Time = (float?)h["time"] ?? 0f,
                    Beat = (float?)h["beat"] ?? 0f,
                    Pad = pad,
                    PadName = (string)h["pad_name"] ?? "",
                    Velocity = (float?)h["velocity"] ?? 1f,
                    PadMask = 1u << (pad - 1),
                });
            }

            track.Hits.Sort((a, b) => a.Time.CompareTo(b.Time));
            return track;
        }

        /// <summary>
        /// Reproduces VirtualBandInput's canonical fingering-mask bit
        /// convention from one note's `input` object, reading only
        /// hardware-verified fields. Cosmetic fields (violin's
        /// suggested_bow_direction) are never touched here, matching
        /// beatmap_schema.md's explicit warning that they must never be used
        /// for judging, and InstrumentSnapshot's deliberate omission of them.
        /// </summary>
        static uint ComputeFingeringMask(InstrumentType instrument, JObject input, string songTitle, int noteId)
        {
            switch (instrument)
            {
                case InstrumentType.Trumpet:
                {
                    // bit 0..2 = valve 1..3
                    uint m = 0;
                    var valves = input["valves"] as JObject;
                    for (int i = 1; i <= 3; i++)
                        if ((bool?)valves?[i.ToString()] == true) m |= 1u << (i - 1);
                    return m;
                }

                case InstrumentType.Saxophone:
                {
                    // bit 0 = register, bit 1..7 = hole 1..7
                    uint m = 0;
                    if ((bool?)input["register"] == true) m |= 1u << 0;
                    var holes = input["holes"] as JObject;
                    for (int i = 1; i <= 7; i++)
                        if ((bool?)holes?[i.ToString()] == true) m |= 1u << i;
                    return m;
                }

                case InstrumentType.Violin:
                {
                    // bit 0..6 = switch 1..7. suggested_bow_direction is cosmetic - never read here.
                    uint m = 0;
                    var switches = input["switches"] as JObject;
                    for (int i = 1; i <= 7; i++)
                        if ((bool?)switches?[i.ToString()] == true) m |= 1u << (i - 1);
                    return m;
                }

                case InstrumentType.Trombone:
                {
                    // bit 0..6 = slide position 1..7, one-hot.
                    int pos = (int?)input["slide_position"] ?? 0;
                    if (pos < 1 || pos > 7)
                    {
                        Debug.LogWarning($"[BeatmapLoader] '{songTitle}': trombone note id={noteId} has out-of-range slide_position {pos} (expected 1-7) - fingering mask left at 0.");
                        return 0;
                    }
                    return 1u << (pos - 1);
                }

                default:
                    // DrumKit never reaches here (parsed via ParseDrumTrack instead);
                    // this only fires for an instrument added to the catalog without
                    // a matching case added here.
                    Debug.LogWarning($"[BeatmapLoader] '{songTitle}': no fingering-mask rule implemented for {instrument} (note id={noteId}) - left at 0. Add a case in BeatmapLoader.ComputeFingeringMask.");
                    return 0;
            }
        }
    }
}
