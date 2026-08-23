#!/usr/bin/env python3
"""
Batch-convert every sheet-music file in a folder into beatmap JSON, without
running convert.py/convert_midi.py by hand once per file.

One song per file in --input-dir (not a directory-per-song merge -- for
files that are meant to be MERGED into one beatmap, e.g. separate
single-instrument scores of the same song, keep using convert.py's/
convert_midi.py's own --input <directory> multi-file-merge feature
directly instead of this script).

*** How mode is picked, per file (no --mode flag here -- this is automatic) ***

For each .musicxml/.xml/.mxl file:
  1. Try real-instrument-part detection (the same detection
     `convert.py --mode hybrid` uses for its first pass).
  2. If that finds a real part for at least one of the 5 game instruments,
     write a HYBRID beatmap (Version H) -- real parts plus, for whichever
     instruments are still missing, a piano-clef fallback derived from the
     same file (exactly what `convert.py --mode hybrid` does standalone).
     If it finds NO real part at all, hybrid mode is skipped entirely for
     this file -- a file with zero real-instrument matches isn't behaving
     like a real multi-part arrangement, so hybrid mode has nothing to add
     over plain piano mode.
  3. Independently of step 2, if the file also has a usable 2-staff piano
     part, ALSO write a standalone PIANO beatmap (Version P) -- the normal
     full-coverage piano-clef conversion, same as `convert.py --mode
     piano`. This runs whether or not step 2 produced a hybrid beatmap.
  4. If neither step 2 nor step 3 produced anything (no real parts found
     AND no usable piano part), the file is skipped with a warning --
     there was nothing in it this converter could use.

So a real multi-part arrangement that also has a piano reduction (common
for orchestral/big-band scores) gets BOTH a Version H and a Version P
beatmap written; a plain 2-staff piano score gets only a Version P; a
real arrangement with no piano reduction at all gets only a Version H.

For each .mid/.midi file: MIDI has no hybrid mode (see convert_midi.py's
own docstring for why -- MIDI can't reliably tell "real instrument part"
from "piano reduction" the way MusicXML's instrument tags can), so the
same fallback PRINCIPLE is applied with convert_midi.py's two actual
modes instead: real-part detection decides whether to write a BAND
beatmap (Version S, MIDI-tagged) for step 1-2 above; a standalone PIANO
beatmap (Version P, MIDI-tagged) is attempted independently for step 3,
same rule as MusicXML. There's no piano-fills-in-the-gaps merge step for
MIDI, since convert_midi.py doesn't have one to call.

Every beatmap this script writes uses the source file's own embedded
title/composer (falling back to the filename if the file has none) --
there's no per-file --title/--composer override here. If a specific file
needs a metadata correction, convert the file individually with
convert.py/convert_midi.py's own --title/--composer flags instead.

Usage:
  python3 batch_convert.py --input-dir samples/downloaded --output-dir output
  python3 batch_convert.py --input-dir samples/downloaded --output-dir output --source "downloaded from IMSLP"
"""
import argparse
import json
import sys
from pathlib import Path

from music21 import converter as m21converter

import convert
import convert_midi
from common import Warnings, apply_version_tag, version_suffixed_path, get_tempo_changes

MUSICXML_EXTS = {".musicxml", ".xml", ".mxl"}
MIDI_EXTS = {".mid", ".midi"}


def build_beatmap(instruments_out, score, mode, source, format_tag=None):
    title = score.metadata.bestTitle if score.metadata and score.metadata.bestTitle else None
    if not title:
        title = "Untitled"
    title = apply_version_tag(title, mode, format_tag=format_tag)
    composer = score.metadata.composer if score.metadata and score.metadata.composer else "Unknown"
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

    return {
        "song": {
            "title": title,
            "composer": composer,
            "source": source,
            "time_signature": ts or "4/4",
            "tempo_changes": get_tempo_changes(score),
            "duration_seconds": duration,
        },
        "instruments": instruments_out,
    }


def write_beatmap(beatmap, out_dir, stem, mode, format_tag=None):
    out_path = version_suffixed_path(out_dir / f"{stem}.json", mode, format_tag=format_tag)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w") as f:
        json.dump(beatmap, f, indent=2)
    return out_path


def process_musicxml(path, out_dir, source_label):
    warnings = Warnings()
    written = []
    try:
        score = m21converter.parse(str(path))
    except Exception as e:
        print(f"  [skip] {path.name}: couldn't parse ({e})", file=sys.stderr)
        return written

    # Step 1-2: real parts, hybrid beatmap only if at least one matched.
    real_out = convert.convert_band_mode(score, warnings)
    if real_out:
        hybrid_out = dict(real_out)
        missing = [k for k in convert.TARGET_INSTRUMENTS if k not in hybrid_out]
        if missing:
            treble_part, bass_part = convert.find_piano_treble_bass(score, warnings, context="hybrid mode")
            if treble_part is not None:
                fallback = convert.convert_piano_mode_from_parts(treble_part, bass_part, warnings, only=set(missing))
                for k in missing:
                    if k in fallback:
                        hybrid_out[k] = fallback[k]
                        warnings.add(f"hybrid mode: no '{k}' part in {path.name} -- filled in from its piano part")
                    else:
                        warnings.add(f"hybrid mode: no '{k}' part in {path.name}, and the piano fallback "
                                      f"couldn't cover it either")
            else:
                for k in missing:
                    warnings.add(f"hybrid mode: no '{k}' part in {path.name}, and no usable piano part "
                                  f"to fall back on -- '{k}' will be missing from this beatmap")
        beatmap = build_beatmap(hybrid_out, score, "hybrid", source_label)
        written.append(write_beatmap(beatmap, out_dir, path.stem, "hybrid"))
    else:
        warnings.add(f"{path.name}: no real instrument parts matched any of the 5 game instruments -- "
                      f"skipping hybrid mode, trying piano mode only")

    # Step 3: standalone piano beatmap, independent of step 1-2.
    treble_part, bass_part = convert.find_piano_treble_bass(score, warnings, context="piano mode")
    if treble_part is not None:
        piano_out = convert.convert_piano_mode_from_parts(treble_part, bass_part, warnings)
        beatmap = build_beatmap(piano_out, score, "piano", source_label)
        written.append(write_beatmap(beatmap, out_dir, path.stem, "piano"))
    elif not real_out:
        print(f"  [skip] {path.name}: no real instrument parts AND no usable piano part -- "
              f"nothing in this file could be converted", file=sys.stderr)

    if warnings:
        print(f"  {len(warnings)} warning(s) for {path.name} -- see above.", file=sys.stderr)
    return written


def process_midi(path, out_dir, source_label):
    warnings = Warnings()
    written = []
    try:
        score = m21converter.parse(str(path))
    except Exception as e:
        print(f"  [skip] {path.name}: couldn't parse ({e})", file=sys.stderr)
        return written

    # MIDI has no hybrid mode -- band mode stands in for "use real parts
    # where they exist" (see module docstring).
    real_out = convert_midi.convert_band_mode(score, warnings)
    if real_out:
        beatmap = build_beatmap(real_out, score, "band", source_label, format_tag="MIDI")
        written.append(write_beatmap(beatmap, out_dir, path.stem, "band", format_tag="MIDI"))
    else:
        warnings.add(f"{path.name}: no real instrument parts matched any of the 5 game instruments -- "
                      f"skipping band mode, trying piano mode only")

    try:
        piano_out = convert_midi.convert_piano_mode(score, warnings, None, None)
        beatmap = build_beatmap(piano_out, score, "piano", source_label, format_tag="MIDI")
        written.append(write_beatmap(beatmap, out_dir, path.stem, "piano", format_tag="MIDI"))
    except ValueError as e:
        if not real_out:
            print(f"  [skip] {path.name}: no real instrument parts AND no usable piano-style treble/bass "
                  f"pair ({e}) -- nothing in this file could be converted", file=sys.stderr)
        else:
            warnings.add(f"{path.name}: piano mode not usable ({e}) -- only the band-mode beatmap was produced")

    if warnings:
        print(f"  {len(warnings)} warning(s) for {path.name} -- see above.", file=sys.stderr)
    return written


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--input-dir", required=True,
                     help="Folder of .musicxml/.xml/.mxl and/or .mid/.midi files -- one song per file")
    ap.add_argument("--output-dir", required=True, help="Folder to write beatmap JSON files into")
    ap.add_argument("--source", default="", help="Applied to every beatmap's song.source field")
    args = ap.parse_args()

    in_dir = Path(args.input_dir)
    out_dir = Path(args.output_dir)
    if not in_dir.is_dir():
        ap.error(f"{in_dir} is not a directory")

    files = sorted(p for p in in_dir.iterdir() if p.is_file())
    xml_files = [p for p in files if p.suffix.lower() in MUSICXML_EXTS]
    midi_files = [p for p in files if p.suffix.lower() in MIDI_EXTS]
    other_count = len(files) - len(xml_files) - len(midi_files)
    if other_count:
        skipped_exts = sorted({p.suffix for p in files
                                if p.suffix.lower() not in MUSICXML_EXTS | MIDI_EXTS})
        print(f"Ignoring {other_count} file(s) with unrecognized extension(s): {', '.join(skipped_exts)}")

    total_written = []
    for path in xml_files:
        print(f"\n{path.name} (MusicXML)")
        total_written.extend(process_musicxml(path, out_dir, args.source))
    for path in midi_files:
        print(f"\n{path.name} (MIDI)")
        total_written.extend(process_midi(path, out_dir, args.source))

    print(f"\nDone. {len(xml_files) + len(midi_files)} source file(s) processed, "
          f"{len(total_written)} beatmap JSON file(s) written to {out_dir}/:")
    for p in total_written:
        print(f"  {p}")


if __name__ == "__main__":
    main()
