#!/usr/bin/env python3
"""
MIDI (.mid/.midi) -> Virtual Band beatmap JSON converter.

Sibling of convert.py, which does the same job for MusicXML input. Use this
one when a MIDI file was easier to find than a MusicXML file for a given
song -- the two scripts share the same fingering/mapping logic (common.py)
and produce beatmaps in the exact same schema, just from a different source
format.

*** THE ONE THING TO KNOW BEFORE USING THIS SCRIPT ***
MIDI note numbers always encode the actual pitch that sounds -- there is no
"written pitch" concept in the MIDI spec at all, unlike MusicXML, where a
transposing instrument's part is conventionally written in the performer's
own key (see convert.py's docstring/comments). That means a trumpet or
saxophone track in a MIDI file is concert pitch, full stop, regardless of
what the track calls itself. This script always treats trumpet/sax pitches
as concert pitch before doing the fingering-chart lookup (transposing
forward into the instrument's written key first) -- this is DIFFERENT from
convert.py's MusicXML band mode, which does the opposite (treats the file's
pitches as already-written). If you ever see this script and convert.py
disagree on the fingering for what looks like "the same" trumpet part, this
is why -- check which format you actually fed it.

Two modes, matching convert.py:

  band   Each MIDI track is treated as one real instrument's part. Tracks
         are matched to controllers by whatever instrument music21 infers
         from the track's General MIDI program number / track name (e.g.
         program 56 "Trumpet", program 65 "Alto Sax", program 40 "Violin",
         program 57 "Trombone", channel 10 percussion). --input accepts
         MULTIPLE files (or a directory of them), same merge behavior as
         convert.py band mode, for when you've found separate
         single-instrument MIDI files for the same song.

  piano  A single MIDI file. Since MIDI has no clef concept, "treble" and
         "bass" parts are picked automatically by average pitch: whichever
         track has notes and the highest average pitch is treble, whichever
         has the lowest average pitch is bass. The treble part feeds
         trumpet, saxophone, AND violin all at once (same melody line,
         three instruments -- not a choice of one, matching convert.py),
         and the bass part feeds trombone + drum-kit simultaneously. Every
         piano-mode beatmap therefore always has all 5 instrument tracks.
         If the file has more than 2 note-bearing tracks, everything except
         the highest/lowest is skipped with a warning -- that's usually a
         percussion or doubling track in a real downloaded piano MIDI, but
         double check the warning to be sure nothing you wanted got
         dropped. Use --treble-track/--bass-track (0-indexed into the
         file's part list) to override the automatic pick if it guesses
         wrong.

Usage:
  python3 convert_midi.py --mode band  --input samples/song.mid --output output/song.json
  python3 convert_midi.py --mode piano --input samples/song.mid --output output/song.json

Merging multiple single-instrument files (band mode only), same as
convert.py:
  python3 convert_midi.py --mode band \\
      --input samples/twinkle/trumpet.mid samples/twinkle/violin.mid \\
      --output output/twinkle.json
  python3 convert_midi.py --mode band --input samples/twinkle/ --output output/twinkle.json

Requires: music21 (pip install music21 --break-system-packages)
"""
import argparse
import sys
from pathlib import Path

from music21 import converter as m21converter
from music21 import instrument as m21instrument
from music21 import note, chord

from common import (
    load_config, pname, Warnings,
    map_trumpet, map_saxophone, map_violin, map_trombone, bucket_drum_pad,
    get_tempo_changes, apply_version_tag, version_suffixed_path,
)

INSTRUMENT_MATCHERS = {
    "trumpet": (m21instrument.Trumpet,),
    "saxophone": (m21instrument.AltoSaxophone, m21instrument.Saxophone, m21instrument.SopranoSaxophone,
                  m21instrument.TenorSaxophone, m21instrument.BaritoneSaxophone),
    "violin": (m21instrument.Violin,),
    "trombone": (m21instrument.Trombone,),
    "drum_kit": (m21instrument.Percussion,),
}


# Extra name keywords per instrument, beyond the literal "trumpet"/"drum
# kit"/etc match -- General MIDI percussion in particular is conventionally
# selected by channel 10 + note number rather than a program-change
# instrument name, so a percussion track's Instrument object frequently
# doesn't survive a MIDI export/reimport round trip at all (unlike
# trumpet/sax/violin/trombone, which do carry a normal GM program). This
# widens the name-based fallback to catch the track-name text real DAWs and
# notation software commonly use for a drum/percussion track.
NAME_ALIASES = {
    "drum_kit": ("drum", "percussion", "kit", "woodblock"),
}


def detect_instrument(part):
    """Same idea as convert.py's detect_instrument, but MIDI tracks carry an
    instrument via General MIDI program-change events (music21 turns these
    into the same kind of Instrument objects a MusicXML <score-instrument>
    would produce) or, failing that, a track-name meta event. GM program
    mapping is a coarser signal than MusicXML's explicit instrument tags --
    e.g. some DAWs/exports use "Piccolo Trumpet" or a generic "Brass"
    program for what should be a trumpet part, and GM percussion tracks
    often carry no usable Instrument object at all after a MIDI round trip
    -- so this is best-effort; check the warnings for any part that doesn't
    get matched."""
    inst = part.getInstrument(returnDefault=False)
    if inst is not None:
        for key, classes in INSTRUMENT_MATCHERS.items():
            if isinstance(inst, classes):
                return key
        name = (inst.instrumentName or "").lower()
        for key in INSTRUMENT_MATCHERS:
            if key.replace("_", " ") in name or any(a in name for a in NAME_ALIASES.get(key, ())):
                return key
    # Fall back to the track/part name text (some MIDI exports only set a
    # track-name meta event, no program change at all -- this is the
    # common case for GM percussion, see above).
    name = (part.partName or "").lower()
    for key in INSTRUMENT_MATCHERS:
        if key.replace("_", " ") in name or any(a in name for a in NAME_ALIASES.get(key, ())):
            return key
    return None


# ---------------------------------------------------------------------------
# Core extraction (identical approach to convert.py -- built off music21's
# secondsMap, which is populated the same way for a MIDI-sourced Part as a
# MusicXML-sourced one, so this logic doesn't need to know which format the
# score came from).
# ---------------------------------------------------------------------------

def build_seconds_map(part):
    flat = part.flatten()
    try:
        smap = flat.secondsMap
    except Exception:
        smap = None
    if smap:
        return smap
    return flat.secondsMap


def iter_notes(part):
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


def resolve_input_paths(inputs):
    """Expand any directory arguments into the .mid/.midi files inside them
    (sorted, non-recursive); leave file paths as-is."""
    paths = []
    for raw in inputs:
        p = Path(raw)
        if p.is_dir():
            found = sorted(list(p.glob("*.mid")) + list(p.glob("*.midi")))
            if not found:
                raise ValueError(f"directory {p} has no .mid/.midi files in it")
            paths.extend(found)
        else:
            paths.append(p)
    return paths


def convert_band_mode(score, warnings):
    """Same structure as convert.py's convert_band_mode, but every trumpet/
    sax pitch is treated as concert pitch (input_is_concert_pitch=True) --
    see this module's docstring for why that's the correct call for MIDI
    and NOT for MusicXML."""
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
            sounding_p = p
            if key == "trumpet":
                inp, sounding_p = map_trumpet(p, warnings, input_is_concert_pitch=True)
            elif key == "saxophone":
                inp, sounding_p = map_saxophone(p, warnings, input_is_concert_pitch=True)
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


def convert_band_mode_multi(paths, warnings):
    instruments_out = {}
    claimed_by = {}
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


def pick_treble_bass_parts(score, warnings, treble_idx, bass_idx):
    """MIDI has no clef metadata, so treble/bass can't be read off the
    score the way convert.py reads parts[0]/parts[1] off a piano MusicXML
    export. Instead: rank every note-bearing part by its average MIDI pitch
    (highest = treble, lowest = bass). Explicit --treble-track/--bass-track
    indices (into score.parts, 0-indexed) override the guess entirely."""
    parts = list(score.parts)
    if treble_idx is not None or bass_idx is not None:
        if treble_idx is None or bass_idx is None:
            raise ValueError("--treble-track and --bass-track must both be given if either is")
        if treble_idx >= len(parts) or bass_idx >= len(parts):
            raise ValueError(f"--treble-track/--bass-track out of range -- this file has {len(parts)} part(s)")
        return parts[treble_idx], parts[bass_idx]

    scored = []
    for i, part in enumerate(parts):
        notes = notes_with_time(part)
        if not notes:
            continue
        avg = sum(n["pitch_obj"].midi for n in notes) / len(notes)
        scored.append((avg, i, part))

    if len(scored) < 2:
        raise ValueError(
            f"Expected at least 2 note-bearing parts (treble + bass) but found {len(scored)}. "
            "Piano mode needs a file with a melody part and a bass part; for a single-line MIDI "
            "file, use --mode band with --treble-track/--bass-track instead, or find a matching bass line."
        )
    scored.sort(key=lambda t: t[0], reverse=True)
    treble_avg, treble_i, treble_part = scored[0]
    bass_avg, bass_i, bass_part = scored[-1]
    if len(scored) > 2:
        skipped = [i for _, i, _ in scored[1:-1]]
        warnings.add(f"piano mode: {len(scored)} note-bearing parts found; using part {treble_i} "
                      f"(avg pitch {treble_avg:.1f}) as treble and part {bass_i} (avg pitch {bass_avg:.1f}) "
                      f"as bass, skipping part(s) {skipped} -- override with --treble-track/--bass-track "
                      f"if this guessed wrong")
    return treble_part, bass_part


def convert_piano_mode(score, warnings, treble_idx, bass_idx):
    # Same rule as convert.py: treble feeds trumpet, saxophone, AND violin
    # all at once (not a choice of one instrument), bass feeds trombone AND
    # drum-kit at once. See this module's docstring.
    treble_part, bass_part = pick_treble_bass_parts(score, warnings, treble_idx, bass_idx)

    instruments_out = {}
    bow_state = {"dir": "down"}
    treble_notes = notes_with_time(treble_part)

    trumpet_notes, sax_notes, violin_notes = [], [], []
    for i, n in enumerate(treble_notes):
        p = n["pitch_obj"]
        base = {
            "id": i + 1, "measure": n["measure"],
            "start_beat": n["start_beat"], "start_time": n["start_time"],
            "duration_beats": n["duration_beats"], "duration_time": n["duration_time"],
        }
        trumpet_inp, _ = map_trumpet(p, warnings, input_is_concert_pitch=True)
        trumpet_notes.append({**base, "pitch": pname(p), "input": trumpet_inp})
        sax_inp, _ = map_saxophone(p, warnings, input_is_concert_pitch=True)
        sax_notes.append({**base, "pitch": pname(p), "input": sax_inp})
        violin_inp = map_violin(p, warnings, bow_state)
        violin_notes.append({**base, "pitch": pname(p), "input": violin_inp})

    instruments_out["trumpet"] = {"controller": "trumpet", "notes": trumpet_notes}
    instruments_out["saxophone"] = {"controller": "saxophone", "notes": sax_notes}
    instruments_out["violin"] = {"controller": "violin", "notes": violin_notes}

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
                     help="Path to a MIDI (.mid/.midi) file. Band mode only: accepts multiple "
                          "files (or a directory containing several) to merge separate "
                          "single-instrument MIDI files of the same song into one beatmap.")
    ap.add_argument("--output", required=True, help="Path to write beatmap JSON")
    ap.add_argument("--mode", choices=["band", "piano"], required=True)
    ap.add_argument("--treble-track", type=int, default=None,
                     help="piano mode only: override auto-pick -- 0-indexed part number to use as treble "
                          "(feeds trumpet, saxophone, and violin all at once)")
    ap.add_argument("--bass-track", type=int, default=None,
                     help="piano mode only: override auto-pick -- 0-indexed part number to use as bass "
                          "(feeds trombone and drum-kit at once)")
    ap.add_argument("--title", help="Override song title (MIDI files rarely carry one -- defaults to the filename)")
    ap.add_argument("--composer", help="Override composer (MIDI files rarely carry one)")
    ap.add_argument("--source", default="", help="Where this MIDI file came from (for your own reference)")
    args = ap.parse_args()

    if args.mode == "piano" and len(args.input) != 1:
        ap.error("piano mode takes exactly one --input file (multi-file merging is band mode only)")
    if args.mode == "band" and (args.treble_track is not None or args.bass_track is not None):
        ap.error("--treble-track/--bass-track are piano mode only")

    warnings = Warnings()

    paths = resolve_input_paths(args.input)
    if args.mode == "band":
        instruments_out, score = convert_band_mode_multi(paths, warnings)
        input_label = str(paths[0])
    else:
        score = m21converter.parse(str(paths[0]))
        instruments_out = convert_piano_mode(score, warnings, args.treble_track, args.bass_track)
        input_label = str(paths[0])

    title = args.title or (score.metadata.bestTitle if score.metadata and score.metadata.bestTitle else Path(input_label).stem)
    composer = args.composer or (score.metadata.composer if score.metadata and score.metadata.composer else "Unknown")
    # format_tag="MIDI" so a MIDI-derived beatmap's title/filename is
    # distinguishable from the MusicXML-derived beatmap of the same song
    # and mode, not just piano vs. band -- e.g. "Hot Cross Buns MIDI
    # (Version P)" / hot_cross_buns_MIDI_Version_P.json.
    title = apply_version_tag(title, args.mode, format_tag="MIDI")

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

    out_path = version_suffixed_path(Path(args.output), args.mode, format_tag="MIDI")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    import json
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
