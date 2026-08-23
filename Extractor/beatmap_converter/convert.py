#!/usr/bin/env python3
"""
Sheet music (MusicXML, including .mxl) -> Virtual Band beatmap JSON converter.

Three modes:

  band   Each MusicXML part is a real instrument part (e.g. a trumpet part,
         a percussion part). Parts are matched to controllers by their
         instrument name / MusicXML <score-instrument> name. --input accepts
         MULTIPLE files (or a directory of them) -- useful when you've found
         separate single-instrument scores for the same song (e.g. a
         trumpet-only MusicXML and a violin-only MusicXML) and want them
         merged into one beatmap. See "Merging multiple single-instrument
         files" below. If a score has several parts for the same game
         instrument (a big-band chart's Trumpet 1-4, say), only the first
         one (in score order) is used -- the rest are skipped with a
         warning, since this project has exactly one controller per
         instrument family.

  piano  A single piano score. The treble clef staff feeds trumpet,
         saxophone, AND violin all at once (same melody line, three
         instruments -- not a choice of one), and the bass clef staff
         feeds both trombone and drum-kit simultaneously (drum hits are
         derived from the same bass-clef notes, bucketed by pitch height
         -- see config/drumkit_config.json). Every piano-mode beatmap
         therefore always has all 5 instrument tracks. Exactly one input
         file only.

  hybrid A single real multi-part score (e.g. a big-band/orchestral
         arrangement) that is NOT guaranteed to have a real part for every
         one of the 5 game instruments -- e.g. a jazz chart with trumpet,
         saxophone, and trombone parts but no violin. Real parts are used
         exactly like band mode wherever the score has them. Any of the 5
         instruments still missing after that are filled in from that same
         file's own piano part (if it has one -- most orchestral/band
         scores that include a piano reduction do), using the same
         treble/bass-clef logic as piano mode, restricted to just the
         missing instrument(s). If an instrument is missing AND the file
         has no usable piano part (or the piano part can't cover it), that
         instrument is simply left out of the beatmap -- same as band mode
         already does for an instrument the score just doesn't have.
         Exactly one input file only (the piano fallback needs the real
         parts and the piano part to live in the same file).

Usage:
  python3 convert.py --mode band   --input samples/song.musicxml --output output/song.json
  python3 convert.py --mode piano  --input samples/song.musicxml --output output/song.json
  python3 convert.py --mode hybrid --input samples/big_band_score.mxl --output output/song.json

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
from music21 import note, chord, percussion

from common import (
    CONFIG_DIR, load_config, pname, Warnings,
    map_trumpet, map_saxophone, map_violin, map_trombone, bucket_drum_pad, expand_drum_hit,
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
    """Yield (music21 Note-or-Unpitched, container, secondsMap-entry) for every
    sounding note/first chord pitch, skipping rests and chords collapse to
    their top pitch (monophonic controllers).

    Real percussion parts (as opposed to this project's own hand-built drum
    samples) come through music21 as note.Unpitched / percussion.
    PercussionChord, NOT note.Note / chord.Chord -- MusicXML notates a drum
    part by staff position (which line/space = which drum), not by a real
    sounding pitch, and music21 models that distinction with its own
    classes. Both cases are handled the same way as their pitched
    equivalents (PercussionChord -> take the highest notehead, same "one
    controller reads one note" rule as a pitched chord); _pitch_of() below
    is what lets notes_with_time() treat both kinds uniformly afterward.
    """
    for el in build_seconds_map(part):
        n = el["element"]
        if isinstance(n, percussion.PercussionChord):
            top = max(n.notes, key=lambda u: u.displayPitch().ps)
            yield top, n, el
        elif isinstance(n, chord.Chord):
            top = n.notes[-1]  # highest pitch in the chord
            yield top, n, el
        elif isinstance(n, (note.Note, note.Unpitched)):
            yield n, n, el


def _pitch_of(n):
    """note.Note has a real .pitch; note.Unpitched (percussion notation) has
    no such thing -- only a displayPitch() standing in for staff position.
    bucket_drum_pad only ever uses this as a relative-height signal to
    bucket into pads (see its docstring), so a notated staff position works
    exactly as well as a real pitch would for that purpose."""
    return n.displayPitch() if isinstance(n, note.Unpitched) else n.pitch


def notes_with_time(part):
    results = []
    for src_note, sounding_note, el in iter_notes(part):
        results.append({
            "measure": sounding_note.measureNumber if hasattr(sounding_note, "measureNumber") else None,
            "start_beat": float(sounding_note.offset),
            "start_time": round(float(el["offsetSeconds"]), 4),
            "duration_beats": float(sounding_note.duration.quarterLength),
            "duration_time": round(float(el["endTimeSeconds"] - el["offsetSeconds"]), 4),
            "pitch_obj": _pitch_of(src_note),
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
    """Expand any directory arguments into the .musicxml/.xml/.mxl files inside
    them (sorted, non-recursive); leave file paths as-is.

    .mxl (compressed MusicXML, the format most sheet-music sites actually
    hand out) works as a single --input file with no extra steps --
    music21's parser picks the right reader off the extension automatically.
    This glob just needs to know to pick .mxl files up too when --input
    points at a directory; a single file already worked before this was
    added, since directory-expansion was the only place the extension list
    was hardcoded.
    """
    paths = []
    for raw in inputs:
        p = Path(raw)
        if p.is_dir():
            found = sorted(list(p.glob("*.musicxml")) + list(p.glob("*.xml")) + list(p.glob("*.mxl")))
            if not found:
                raise ValueError(f"directory {p} has no .musicxml/.xml/.mxl files in it")
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
    claimed_by = {}  # instrument key -> partName that supplied it, within THIS score
    bow_state = {"dir": "down"}
    for part in score.parts:
        key = detect_instrument(part)
        if key is None:
            warnings.add(f"band mode: could not match part '{part.partName}' to a known instrument, skipping")
            continue
        if key in instruments_out:
            # A real score commonly has several parts for the same game
            # instrument (e.g. a big-band chart's Trumpet 1-4, or Alto/
            # Tenor/Bari Sax) -- this project only has one controller per
            # instrument family, so only the first part claims it; later
            # ones are skipped with a warning instead of silently
            # overwriting the first one's notes (which is what happened
            # here before this check existed).
            warnings.add(f"band mode: both '{claimed_by[key]}' and '{part.partName}' map to '{key}' -- "
                          f"keeping '{claimed_by[key]}''s notes, skipping '{part.partName}'")
            continue
        claimed_by[key] = part.partName
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
            for roll_group_id, n in enumerate(notes):
                pad = bucket_drum_pad(n["pitch_obj"], lo_midi, hi_midi, pads)
                hits.extend(expand_drum_hit(n, pad, roll_group_id))
            for i, h in enumerate(hits):
                h["id"] = i + 1
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


def convert_piano_mode_from_parts(treble_part, bass_part, warnings, only=None):
    """Do the actual treble->{trumpet,saxophone,violin} / bass->{trombone,
    drum_kit} conversion given two already-identified music21 parts. Split
    out from convert_piano_mode so the same logic can be reused as a
    per-instrument FALLBACK by convert_hybrid_mode (a real multi-part score
    that's missing one or two of the 5 game instruments as real parts) --
    not just for a standalone 2-staff piano file.

    `only`, when given, restricts which of the 5 instrument keys get
    computed at all -- e.g. hybrid mode passes only the keys it couldn't
    find a real part for, so it doesn't waste time (or emit fingering
    warnings) recomputing an instrument that already has real notes.
    Plain piano mode leaves this as None, meaning "all 5."
    """
    want = only if only is not None else {"trumpet", "saxophone", "violin", "trombone", "drum_kit"}
    instruments_out = {}
    bow_state = {"dir": "down"}

    if want & {"trumpet", "saxophone", "violin"}:
        treble_notes = notes_with_time(treble_part)
        trumpet_notes, sax_notes, violin_notes = [], [], []
        for i, n in enumerate(treble_notes):
            p = n["pitch_obj"]
            base = {
                "id": i + 1, "measure": n["measure"],
                "start_beat": n["start_beat"], "start_time": n["start_time"],
                "duration_beats": n["duration_beats"], "duration_time": n["duration_time"],
            }
            # piano mode: treble clef is concert pitch (piano doesn't
            # transpose), so input_is_concert_pitch=True for both
            # transposing instruments.
            if "trumpet" in want:
                trumpet_inp, _ = map_trumpet(p, warnings, input_is_concert_pitch=True)
                trumpet_notes.append({**base, "pitch": pname(p), "input": trumpet_inp})
            if "saxophone" in want:
                sax_inp, _ = map_saxophone(p, warnings, input_is_concert_pitch=True)
                sax_notes.append({**base, "pitch": pname(p), "input": sax_inp})
            if "violin" in want:
                violin_inp = map_violin(p, warnings, bow_state)
                violin_notes.append({**base, "pitch": pname(p), "input": violin_inp})
        if "trumpet" in want:
            instruments_out["trumpet"] = {"controller": "trumpet", "notes": trumpet_notes}
        if "saxophone" in want:
            instruments_out["saxophone"] = {"controller": "saxophone", "notes": sax_notes}
        if "violin" in want:
            instruments_out["violin"] = {"controller": "violin", "notes": violin_notes}

    if want & {"trombone", "drum_kit"}:
        bass_notes = notes_with_time(bass_part)

        if "trombone" in want:
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

        if "drum_kit" in want:
            drum_cfg = load_config("drumkit_config.json")
            pads = drum_cfg["pads"]
            if bass_notes:
                lo_midi = min(n["pitch_obj"].midi for n in bass_notes)
                hi_midi = max(n["pitch_obj"].midi for n in bass_notes)
            else:
                lo_midi = hi_midi = 48
            hits = []
            for roll_group_id, n in enumerate(bass_notes):
                pad = bucket_drum_pad(n["pitch_obj"], lo_midi, hi_midi, pads)
                hits.extend(expand_drum_hit(n, pad, roll_group_id))
            for i, h in enumerate(hits):
                h["id"] = i + 1
            instruments_out["drum_kit"] = {"controller": "drum_kit", "hits": hits}

    return instruments_out


def find_piano_treble_bass(score, warnings, context="piano mode"):
    """Locate the 2-staff piano part (treble, bass) inside a score, if any.
    Returns (treble_part, bass_part) or (None, None) if there isn't a usable
    one. Used by both plain piano mode (where the WHOLE score is expected to
    be just this) and hybrid mode (where it's one part pair among many
    others, used only as a fallback)."""
    piano_parts = [p for p in score.parts
                   if isinstance(p.getInstrument(returnDefault=False), m21instrument.Piano)]
    if not piano_parts:
        # Fall back to name-sniffing -- some exports don't set a proper
        # <score-instrument> Piano instrument object.
        piano_parts = [p for p in score.parts if "piano" in (p.partName or "").lower()]
    if len(piano_parts) < 2:
        if piano_parts:
            warnings.add(f"{context}: found only 1 piano staff/part ('{piano_parts[0].partName}'), "
                          f"need both treble and bass -- can't use it")
        return None, None
    if len(piano_parts) > 2:
        warnings.add(f"{context}: found {len(piano_parts)} piano parts, expected exactly 2 "
                      f"(treble + bass) -- using the first two in score order")
    return piano_parts[0], piano_parts[1]


def convert_piano_mode(score, warnings):
    # Per the project's own design (treble clef -> violin, trumpet, AND
    # saxophone simultaneously; bass clef -> trombone AND drum-kit
    # simultaneously), piano mode assigns every treble-clef note to all
    # three treble instruments at once and every bass-clef note to both
    # bass instruments at once -- it does NOT pick just one instrument per
    # clef. All 5 instrument tracks are always produced from one 2-staff
    # piano score.
    parts = list(score.parts)
    if len(parts) < 2:
        # Some piano MusicXML exports use one part with two voices/staves instead of two parts.
        raise ValueError(
            "Expected a 2-staff piano part (treble + bass) but found "
            f"{len(parts)} part(s). Re-export with treble and bass as separate staves/parts."
        )
    treble_part, bass_part = parts[0], parts[1]
    return convert_piano_mode_from_parts(treble_part, bass_part, warnings)


TARGET_INSTRUMENTS = ("trumpet", "saxophone", "violin", "trombone", "drum_kit")


def convert_hybrid_mode(score, warnings):
    """A single multi-part score (e.g. a real big-band/orchestral
    arrangement) that may not have a real part for every one of the 5 game
    instruments. Real parts are used wherever the score has them (same
    detection as band mode, including "first part wins" when a family has
    several, e.g. Trumpet 1-4); whichever of the 5 are still missing after
    that get filled in from the same file's own piano part (if it has one),
    using the same treble/bass-clef fallback piano mode uses. An instrument
    that's neither a real part nor coverable from a piano part in this file
    is simply left out, same as band mode already does -- it is NOT an
    error, just an instrument this particular score can't provide.
    """
    instruments_out = convert_band_mode(score, warnings)
    missing = [k for k in TARGET_INSTRUMENTS if k not in instruments_out]
    if not missing:
        return instruments_out

    treble_part, bass_part = find_piano_treble_bass(score, warnings, context="hybrid mode")
    if treble_part is None:
        for k in missing:
            warnings.add(f"hybrid mode: no '{k}' part in this file, and no usable piano part to fall "
                          f"back on -- '{k}' will be missing from this beatmap")
        return instruments_out

    fallback = convert_piano_mode_from_parts(treble_part, bass_part, warnings, only=set(missing))
    for k in missing:
        if k in fallback:
            instruments_out[k] = fallback[k]
            clef = "treble" if k in ("trumpet", "saxophone", "violin") else "bass"
            warnings.add(f"hybrid mode: no '{k}' part in this file -- filled in from the piano's "
                         f"{clef} clef instead")
        else:
            warnings.add(f"hybrid mode: no '{k}' part in this file, and the piano fallback didn't "
                         f"produce one either -- '{k}' will be missing from this beatmap")
    return instruments_out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--input", required=True, nargs="+",
                     help="Path to a MusicXML file. Band mode only: accepts multiple files "
                          "(or a directory containing several) to merge separate "
                          "single-instrument scores of the same song into one beatmap.")
    ap.add_argument("--output", required=True, help="Path to write beatmap JSON")
    ap.add_argument("--mode", choices=["band", "piano", "hybrid"], required=True)
    ap.add_argument("--title", help="Override song title (defaults to MusicXML metadata)")
    ap.add_argument("--composer", help="Override composer (defaults to MusicXML metadata)")
    ap.add_argument("--source", default="", help="Where this sheet music came from (for your own reference)")
    args = ap.parse_args()

    if args.mode in ("piano", "hybrid") and len(args.input) != 1:
        ap.error(f"{args.mode} mode takes exactly one --input file (multi-file merging is band mode only)")

    warnings = Warnings()

    if args.mode == "band":
        paths = resolve_input_paths(args.input)
        instruments_out, score = convert_band_mode_multi(paths, warnings)
        input_label = str(paths[0])
    elif args.mode == "hybrid":
        paths = resolve_input_paths(args.input)
        score = m21converter.parse(str(paths[0]))
        instruments_out = convert_hybrid_mode(score, warnings)
        input_label = str(paths[0])
    else:
        paths = resolve_input_paths(args.input)
        score = m21converter.parse(str(paths[0]))
        instruments_out = convert_piano_mode(score, warnings)
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
