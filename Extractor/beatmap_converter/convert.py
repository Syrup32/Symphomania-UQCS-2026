#!/usr/bin/env python3
"""
Sheet music (MusicXML) -> Virtual Band beatmap JSON converter.

Two modes:

  band   Each MusicXML part is a real instrument part (e.g. a trumpet part,
         a percussion part). Parts are matched to controllers by their
         instrument name / MusicXML <score-instrument> name. --input accepts
         MULTIPLE files (or a directory of them) -- useful when you've found
         separate single-instrument scores for the same song (e.g. a
         trumpet-only MusicXML and a violin-only MusicXML) and want them
         merged into one beatmap. See "Merging multiple single-instrument
         files" below.

  piano  A single piano score. The treble clef staff feeds one of
         trumpet/saxophone/violin (pick with --treble-instrument), and the
         bass clef staff feeds both trombone and drum-kit simultaneously
         (drum hits are derived from the same bass-clef notes, bucketed by
         pitch height -- see config/drumkit_config.json). Exactly one input
         file only.

Usage:
  python3 convert.py --mode band  --input samples/song.musicxml --output output/song.json
  python3 convert.py --mode piano --input samples/song.musicxml --output output/song.json \
      --treble-instrument violin

Merging multiple single-instrument files (band mode only):
  python3 convert.py --mode band \
      --input samples/twinkle/trumpet.musicxml samples/twinkle/violin.musicxml \
      --output output/twinkle.json
  # or point at a directory containing one .musicxml/.xml per instrument:
  python3 convert.py --mode band --input samples/twinkle/ --output output/twinkle.json

Each file is parsed and instrument-detected independently (same per-part
detection band mode always does), then the resulting instrument tracks are
merged into one beatmap. Song-level metadata (title/composer/tempo/time
signature) comes from the FIRST file given (override with --title/
--composer if that file's own metadata is wrong or missing) -- so put
whichever file has the most complete metadata first. The converter checks
that every file agrees on tempo and time signature and warns loudly if they
don't, since that's exactly the kind of mismatch that would silently
desync two instruments that are supposed to share one timeline. If two
files both claim the same instrument, the first one's part wins and the
second is skipped with a warning.

Requires: music21 (pip install music21 --break-system-packages)
"""
import argparse
import json
import sys
from pathlib import Path

from music21 import converter as m21converter
from music21 import instrument as m21instrument
from music21 import note, chord

from common import (
    CONFIG_DIR, load_config, pname, Warnings,
    map_trumpet, map_saxophone, map_violin, map_trombone, bucket_drum_pad,
    get_tempo_changes, apply_version_tag, version_suffixed_path,
)

HERE = Path(__file__).resolve().parent

# ---------------------------------------------------------------------------
# Core extraction
# ---------------------------------------------------------------------------

def build_seconds_map(part):
    """Return list of (offsetSeconds, endTimeSeconds, element) for notes/chords/rests."""
    flat = part.flatten()
    try:
        smap = flat.secondsMap
    except Exception:
        smap = None
    if smap:
        return smap
    # Fallback: assume a flat 120bpm if no tempo info at all (music21 defaults to 120bpm anyway)
    return flat.secondsMap


def iter_notes(part):
    """Yield (music21 Note, measureNumber) for every sounding note/first chord pitch,
    skipping rests and chords collapse to their top pitch (monophonic controllers)."""
    for el in build_seconds_map(part):
        n = el["element"]
        if isinstance(n, chord.Chord):
            top = n.notes[-1]  # highest pitch in the chord
            yield top, n, el
        elif isinstance(n, note.Note):
            yield n, n, el


def notes_with_time(part):
    results = []
    for src_note, sounding_note, el in iter_notes(part):
        results.append({
            "measure": sounding_note.measureNumber if hasattr(sounding_note, "measureNumber") else None,
            "start_beat": float(sounding_note.offset),
            "start_time": round(float(el["offsetSeconds"]), 4),
            "duration_beats": float(sounding_note.duration.quarterLength),
            "duration_time": round(float(el["endTimeSeconds"] - el["offsetSeconds"]), 4),
            "pitch_obj": src_note.pitch,
        })
    return results


INSTRUMENT_MATCHERS = {
    "trumpet": (m21instrument.Trumpet,),
    "saxophone": (m21instrument.AltoSaxophone, m21instrument.Saxophone, m21instrument.SopranoSaxophone,
                  m21instrument.TenorSaxophone, m21instrument.BaritoneSaxophone),
    "violin": (m21instrument.Violin,),
    "trombone": (m21instrument.Trombone,),
    "drum_kit": (m21instrument.Percussion,),
}


def detect_instrument(part):
    inst = part.getInstrument(returnDefault=False)
    if inst is None:
        return None
    for key, classes in INSTRUMENT_MATCHERS.items():
        if isinstance(inst, classes):
            return key
    name = (inst.instrumentName or "").lower()
    for key in INSTRUMENT_MATCHERS:
        if key.replace("_", " ") in name:
            return key
    return None


def resolve_input_paths(inputs):
    """Expand any directory arguments into the .musicxml/.xml files inside them
    (sorted, non-recursive); leave file paths as-is."""
    paths = []
    for raw in inputs:
        p = Path(raw)
        if p.is_dir():
            found = sorted(list(p.glob("*.musicxml")) + list(p.glob("*.xml")))
            if not found:
                raise ValueError(f"directory {p} has no .musicxml/.xml files in it")
            paths.extend(found)
        else:
            paths.append(p)
    return paths


def convert_band_mode_multi(paths, warnings):
    """Parse and instrument-detect each file independently, then merge into one
    instruments_out dict. Returns (instruments_out, primary_score) where
    primary_score is the first file's parsed score, used for song-level
    metadata (title/composer/tempo/time signature)."""
    instruments_out = {}
    claimed_by = {}  # instrument key -> filename that supplied it
    primary_score = None
    primary_tempo = None
    primary_ts = None

    for path in paths:
        score = m21converter.parse(str(path))
        if primary_score is None:
            primary_score = score
            primary_tempo = get_tempo_changes(score)
            ts_obj = score.flatten().getElementsByClass("TimeSignature").first()
            primary_ts = ts_obj.ratioString if ts_obj else None
        else:
            this_tempo = get_tempo_changes(score)
            if this_tempo and primary_tempo and this_tempo[0]["bpm"] != primary_tempo[0]["bpm"]:
                warnings.add(f"merge: {path.name}'s tempo ({this_tempo[0]['bpm']} bpm) doesn't match "
                              f"{paths[0].name}'s ({primary_tempo[0]['bpm']} bpm) -- its notes may not "
                              f"actually line up in time with the other file(s)")
            ts_obj = score.flatten().getElementsByClass("TimeSignature").first()
            this_ts = ts_obj.ratioString if ts_obj else None
            if this_ts and primary_ts and this_ts != primary_ts:
                warnings.add(f"merge: {path.name}'s time signature ({this_ts}) doesn't match "
                              f"{paths[0].name}'s ({primary_ts})")

        file_instruments = convert_band_mode(score, warnings)
        for key, track in file_instruments.items():
            if key in instruments_out:
                warnings.add(f"merge: both {claimed_by[key]} and {path.name} have a '{key}' part -- "
                              f"keeping {claimed_by[key]}'s, ignoring {path.name}'s")
                continue
            instruments_out[key] = track
            claimed_by[key] = path.name

    return instruments_out, primary_score


def convert_band_mode(score, warnings):
    instruments_out = {}
    bow_state = {"dir": "down"}
    for part in score.parts:
        key = detect_instrument(part)
        if key is None:
            warnings.add(f"band mode: could not match part '{part.partName}' to a known instrument, skipping")
            continue
        notes = notes_with_time(part)
        if key == "drum_kit":
            drum_cfg = load_config("drumkit_config.json")
            pads = drum_cfg["pads"]
            if notes:
                lo_midi = min(n["pitch_obj"].midi for n in notes)
                hi_midi = max(n["pitch_obj"].midi for n in notes)
            else:
                lo_midi = hi_midi = 60
            hits = []
            for i, n in enumerate(notes):
                pad = bucket_drum_pad(n["pitch_obj"], lo_midi, hi_midi, pads)
                hits.append({
                    "id": i + 1, "measure": n["measure"], "time": n["start_time"], "beat": n["start_beat"],
                    "pad": pad["index"], "pad_name": pad["name"], "velocity": 0.9,
                })
            instruments_out["drum_kit"] = {"controller": "drum_kit", "hits": hits}
            continue

        out_notes = []
        for i, n in enumerate(notes):
            p = n["pitch_obj"]
            sounding_p = p  # overridden below for transposing instruments
            if key == "trumpet":
                # band mode: a real trumpet part's pitches are already written
                # (not concert) -- input_is_concert_pitch=False.
                inp, sounding_p = map_trumpet(p, warnings, input_is_concert_pitch=False)
            elif key == "saxophone":
                inp, sounding_p = map_saxophone(p, warnings, input_is_concert_pitch=False)
            elif key == "violin":
                inp = map_violin(p, warnings, bow_state)
            elif key == "trombone":
                inp = map_trombone(p, warnings)
            else:
                continue
            out_notes.append({
                "id": i + 1, "measure": n["measure"],
                "start_beat": n["start_beat"], "start_time": n["start_time"],
                "duration_beats": n["duration_beats"], "duration_time": n["duration_time"],
                "pitch": pname(sounding_p), "input": inp,
            })
        instruments_out[key] = {"controller": key, "notes": out_notes}
    return instruments_out


def convert_piano_mode(score, treble_instrument, warnings):
    parts = list(score.parts)
    if len(parts) < 2:
        # Some piano MusicXML exports use one part with two voices/staves instead of two parts.
        raise ValueError(
            "Expected a 2-staff piano part (treble + bass) but found "
            f"{len(parts)} part(s). Re-export with treble and bass as separate staves/parts."
        )
    treble_part, bass_part = parts[0], parts[1]

    instruments_out = {}
    bow_state = {"dir": "down"}
    treble_notes = notes_with_time(treble_part)
    out_notes = []
    for i, n in enumerate(treble_notes):
        p = n["pitch_obj"]
        if treble_instrument == "trumpet":
            # piano mode: treble clef is concert pitch (piano doesn't transpose).
            inp, _ = map_trumpet(p, warnings, input_is_concert_pitch=True)
        elif treble_instrument == "saxophone":
            inp, _ = map_saxophone(p, warnings, input_is_concert_pitch=True)
        elif treble_instrument == "violin":
            inp = map_violin(p, warnings, bow_state)
        else:
            raise ValueError(f"--treble-instrument must be trumpet, saxophone, or violin, got {treble_instrument}")
        out_notes.append({
            "id": i + 1, "measure": n["measure"],
            "start_beat": n["start_beat"], "start_time": n["start_time"],
            "duration_beats": n["duration_beats"], "duration_time": n["duration_time"],
            "pitch": pname(p), "input": inp,
        })
    instruments_out[treble_instrument] = {"controller": treble_instrument, "notes": out_notes}

    bass_notes = notes_with_time(bass_part)
    trombone_notes = []
    for i, n in enumerate(bass_notes):
        p = n["pitch_obj"]
        inp = map_trombone(p, warnings)
        trombone_notes.append({
            "id": i + 1, "measure": n["measure"],
            "start_beat": n["start_beat"], "start_time": n["start_time"],
            "duration_beats": n["duration_beats"], "duration_time": n["duration_time"],
            "pitch": pname(p), "input": inp,
        })
    instruments_out["trombone"] = {"controller": "trombone", "notes": trombone_notes}

    drum_cfg = load_config("drumkit_config.json")
    pads = drum_cfg["pads"]
    if bass_notes:
        lo_midi = min(n["pitch_obj"].midi for n in bass_notes)
        hi_midi = max(n["pitch_obj"].midi for n in bass_notes)
    else:
        lo_midi = hi_midi = 48
    hits = []
    for i, n in enumerate(bass_notes):
        pad = bucket_drum_pad(n["pitch_obj"], lo_midi, hi_midi, pads)
        hits.append({
            "id": i + 1, "measure": n["measure"], "time": n["start_time"], "beat": n["start_beat"],
            "pad": pad["index"], "pad_name": pad["name"], "velocity": 0.9,
        })
    instruments_out["drum_kit"] = {"controller": "drum_kit", "hits": hits}
    return instruments_out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--input", required=True, nargs="+",
                     help="Path to a MusicXML file. Band mode only: accepts multiple files "
                          "(or a directory containing several) to merge separate "
                          "single-instrument scores of the same song into one beatmap.")
    ap.add_argument("--output", required=True, help="Path to write beatmap JSON")
    ap.add_argument("--mode", choices=["band", "piano"], required=True)
    ap.add_argument("--treble-instrument", choices=["trumpet", "saxophone", "violin"],
                     help="piano mode only: which instrument the treble clef line feeds")
    ap.add_argument("--title", help="Override song title (defaults to MusicXML metadata)")
    ap.add_argument("--composer", help="Override composer (defaults to MusicXML metadata)")
    ap.add_argument("--source", default="", help="Where this sheet music came from (for your own reference)")
    args = ap.parse_args()

    if args.mode == "piano" and not args.treble_instrument:
        ap.error("--treble-instrument is required in piano mode")
    if args.mode == "piano" and len(args.input) != 1:
        ap.error("piano mode takes exactly one --input file (multi-file merging is band mode only)")

    warnings = Warnings()

    if args.mode == "band":
        paths = resolve_input_paths(args.input)
        instruments_out, score = convert_band_mode_multi(paths, warnings)
        input_label = str(paths[0])
    else:
        paths = resolve_input_paths(args.input)
        score = m21converter.parse(str(paths[0]))
        instruments_out = convert_piano_mode(score, args.treble_instrument, warnings)
        input_label = str(paths[0])

    title = args.title or (score.metadata.bestTitle if score.metadata and score.metadata.bestTitle else Path(input_label).stem)
    composer = args.composer or (score.metadata.composer if score.metadata and score.metadata.composer else "Unknown")

    # Tag both the song title and the output filename with which source
    # rendition this beatmap was converted from, so a "piano-mode" beatmap
    # and a "whole-band-mode" beatmap of the same song are never mistaken
    # for each other downstream (e.g. sitting side by side in a song-select
    # list). Piano mode -> "Version P", band mode -> "Version S".
    title = apply_version_tag(title, args.mode)
    ts = None
    ts_obj = score.flatten().getElementsByClass("TimeSignature").first()
    if ts_obj:
        ts = ts_obj.ratioString

    all_end_times = []
    for inst in instruments_out.values():
        for n in inst.get("notes", []):
            all_end_times.append(n["start_time"] + n["duration_time"])
        for h in inst.get("hits", []):
            all_end_times.append(h["time"])
    duration = round(max(all_end_times), 4) if all_end_times else 0.0

    beatmap = {
        "song": {
            "title": title,
            "composer": composer,
            "source": args.source,
            "time_signature": ts or "4/4",
            "tempo_changes": get_tempo_changes(score),
            "duration_seconds": duration,
        },
        "instruments": instruments_out,
    }

    out_path = version_suffixed_path(Path(args.output), args.mode)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w") as f:
        json.dump(beatmap, f, indent=2)

    if args.mode == "band" and len(paths) > 1:
        print(f"\nMerged {len(paths)} files: {', '.join(p.name for p in paths)}")
    print(f"Wrote {out_path} ({len(instruments_out)} instrument track(s): "
          f"{', '.join(instruments_out.keys())}).")
    if warnings:
        print(f"{len(warnings)} warning(s) -- see above.", file=sys.stderr)


if __name__ == "__main__":
    main()
