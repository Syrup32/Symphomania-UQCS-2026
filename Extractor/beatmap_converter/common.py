"""Shared, format-agnostic pieces of the beatmap converter: fingering/mapping
lookups, config loading, and small utilities used by both convert.py
(MusicXML input) and convert_midi.py (MIDI input).

Nothing in this file cares whether the notes it's mapping came from a
MusicXML file or a MIDI file -- that's the whole point of splitting it out.
The one place the *caller* has to think about the difference is the
input_is_concert_pitch flag on map_trumpet/map_saxophone -- see the big
comment on map_trumpet below, and convert_midi.py's module docstring, for
why MIDI and MusicXML disagree about that by default.
"""
import json
import sys
from pathlib import Path

from music21 import interval

HERE = Path(__file__).resolve().parent
CONFIG_DIR = HERE / "config"

# Bb trumpet written pitch sounds a major second LOWER than concert pitch,
# i.e. to go from concert pitch to trumpet's written pitch, transpose UP a
# major second.
TRUMPET_CONCERT_TO_WRITTEN = interval.Interval("M2")
# Alto sax is an Eb instrument, written a major sixth above concert pitch.
ALTO_SAX_CONCERT_TO_WRITTEN = interval.Interval("M6")


def load_config(name):
    with open(CONFIG_DIR / name) as f:
        return json.load(f)


def pname(p):
    """Scientific pitch name without micro-accidentals, e.g. 'F#4'."""
    return p.nameWithOctave.replace("-", "b")


class Warnings(list):
    def add(self, msg):
        self.append(msg)
        print(f"  [warn] {msg}", file=sys.stderr)


# ---------------------------------------------------------------------------
# Per-instrument note -> input mapping
# ---------------------------------------------------------------------------

def map_trumpet(p, warnings, input_is_concert_pitch):
    # input_is_concert_pitch=True: p is concert/sounding pitch and needs
    # converting to the trumpet's own written key before chart lookup.
    # This is the right flag for: piano mode's treble clef (piano doesn't
    # transpose), and EVERY MIDI input (MIDI note numbers always encode the
    # actual sounding pitch, regardless of what instrument the track claims
    # to be -- there's no "written pitch" concept in MIDI at all).
    #
    # input_is_concert_pitch=False: p is ALREADY the trumpet's written pitch.
    # This is the right flag for MusicXML band mode ONLY: standard MusicXML
    # convention stores a transposing instrument's part at the pitch the
    # player actually reads, with the transposition interval carried as
    # separate metadata, so no further transposition should be applied --
    # doing so would double-transpose a real trumpet part.
    #
    # Returns (input_dict, concert_pitch). The chart lookup itself is keyed
    # by WRITTEN pitch (that's what determines the actual fingering), but
    # the returned pitch is always CONCERT/sounding pitch, mode-independent
    # -- so the beatmap's "pitch" field means "the note that actually
    # sounds" consistently no matter which input format or mode produced it.
    chart = load_config("trumpet_fingering.json")["notes"]
    if input_is_concert_pitch:
        written, concert = p.transpose(TRUMPET_CONCERT_TO_WRITTEN), p
    else:
        written, concert = p, p.transpose(TRUMPET_CONCERT_TO_WRITTEN.reverse())
    key = pname(written)
    entry = chart.get(key)
    if entry is None:
        warnings.add(f"trumpet: no fingering for written pitch {key} (concert {pname(concert)}) -- "
                      f"out of chart range")
        return {"valves": {"1": False, "2": False, "3": False}, "breath": True}, concert
    return dict(entry), concert


def map_saxophone(p, warnings, input_is_concert_pitch):
    # Same convention as map_trumpet -- see its comment.
    chart = load_config("saxophone_fingering.json")["notes"]
    if input_is_concert_pitch:
        written, concert = p.transpose(ALTO_SAX_CONCERT_TO_WRITTEN), p
    else:
        written, concert = p, p.transpose(ALTO_SAX_CONCERT_TO_WRITTEN.reverse())
    key = pname(written)
    entry = chart.get(key)
    if entry is None:
        # fall back to nearest diatonic pitch class in the same octave
        warnings.add(f"saxophone: no fingering for written pitch {key} (concert {pname(concert)}) -- "
                      f"likely a chromatic note the simplified 8-switch chart can't express")
        return {"holes": {str(i): False for i in range(1, 8)}, "register": False, "breath": True}, concert
    return dict(entry), concert


def map_violin(p, warnings, bow_state):
    # Per config/controller_hid_protocol.md (2026-08-22): the violin's rotary
    # encoder is read as activity-only -- "is it currently turning" -- with no
    # direction or speed decoding. bow_active is therefore the only field that
    # is actually verified against hardware (it's the HID "check" bit, button
    # 1). suggested_bow_direction is a cosmetic alternate-per-note value for
    # Unity's bow animation/audio only -- it is NOT something the controller
    # reports and must never be used as part of hit judging.
    chart = load_config("violin_fingering.json")
    entry = chart["notes"].get(pname(p))
    rule = chart.get("bow_direction_rule", "alternate")
    if rule == "alternate":
        bow_state["dir"] = "up" if bow_state["dir"] == "down" else "down"
    direction = bow_state["dir"]
    if entry is None:
        warnings.add(f"violin: no fingering for {pname(p)} in config/violin_fingering.json -- out of chart range")
        return {"switches": {str(i): False for i in range(1, 8)}, "bow_active": True,
                "suggested_bow_direction": direction}
    return {"switches": entry["switches"], "bow_active": True, "suggested_bow_direction": direction}


def map_trombone(p, warnings):
    # Per trombone_control_HID.ino (confirmed by the user 2026-08-22): the
    # HID report is 8 discrete buttons, no continuous axis at all -- the ADC
    # is bucketed into 7 positions on the firmware side before it ever
    # reaches HID, and the game is never meant to show an analog slide
    # value. slide_position is looked up from a real trombone position
    # chart (config/trombone_config.json, generated by
    # gen_trombone_chart.py) rather than a linear approximation across a
    # note range -- real trombone positions follow overtone-partial
    # physics, not a straight line.
    chart = load_config("trombone_config.json")["notes"]
    entry = chart.get(pname(p))
    if entry is None:
        warnings.add(f"trombone: no position for {pname(p)} -- out of the E2-F4 practical range this "
                      f"simplified slide-only controller can reach")
        return {"slide_position": 1, "breath": True}
    return {"slide_position": entry["slide_position"], "breath": True}


def bucket_drum_pad(p, lo_midi, hi_midi, pads):
    span = max(1, hi_midi - lo_midi)
    frac = (p.midi - lo_midi) / span
    frac = min(1.0, max(0.0, frac))
    idx = min(len(pads) - 1, int(frac * len(pads)))
    return pads[idx]


# A drum-kit pad has no way to report a held press -- the piezo sensors
# only ever detect a momentary strike (firmware pulses the HID bit high for
# HOLD_MS and auto-releases; see controller_hid_protocol.md). So a "held"
# note in the source sheet music (a half/whole note on a percussion line)
# can't become a single sustained hit the way a valve or slide position
# can stay held -- the only physically honest way to ask a player to
# sustain a drum sound is a roll: a series of real, separate strikes close
# together in time. DRUM_ROLL_THRESHOLD_BEATS is the cutoff above which a
# note gets rolled instead of staying a single instant hit (1 beat = a
# quarter note in most of this project's 4/4 test songs);
# DRUM_ROLL_INTERVAL_BEATS is the spacing between roll strikes once rolled
# (an eighth note -- fast enough to read as a sustained roll, slow enough
# to stay inside the ~150ms hit-judging timing window documented in
# beatmap_schema.md's "Hit detection & audio feedback" section).
DRUM_ROLL_THRESHOLD_BEATS = 1.0
DRUM_ROLL_INTERVAL_BEATS = 0.5


def expand_drum_hit(n, pad, roll_group_id):
    """Given one bucketed drum note (with start_beat/start_time/
    duration_beats/duration_time) and the pad it maps to, return a list of
    hit dicts (WITHOUT 'id' -- the caller numbers hits sequentially across
    the whole track after flattening). A note at or under
    DRUM_ROLL_THRESHOLD_BEATS is a single instant hit, same shape as
    before. A longer note becomes `roll_group_id` (all its
    hits share this token so the game can render/count them as one
    logical roll) and a real, discrete strike is emitted every
    DRUM_ROLL_INTERVAL_BEATS across the note's duration -- 'roll_index'/
    'roll_length' say where each strike falls in that sequence. Timing
    within the roll is linearly interpolated across the note's own
    start_time/duration_time, which is exact as long as tempo doesn't
    change mid-note (true for every note in practice)."""
    dur_beats = n["duration_beats"]
    base = {"measure": n["measure"], "pad": pad["index"], "pad_name": pad["name"], "velocity": 0.9}
    if dur_beats <= DRUM_ROLL_THRESHOLD_BEATS:
        return [{**base, "time": n["start_time"], "beat": n["start_beat"]}]

    num_hits = max(2, round(dur_beats / DRUM_ROLL_INTERVAL_BEATS))
    hits = []
    for i in range(num_hits):
        frac = i / num_hits
        hits.append({
            **base,
            "time": round(n["start_time"] + frac * n["duration_time"], 4),
            "beat": n["start_beat"] + frac * dur_beats,
            "roll_group": roll_group_id, "roll_index": i, "roll_length": num_hits,
        })
    return hits


def get_tempo_changes(score):
    changes = []
    for mm in score.flatten().getElementsByClass("MetronomeMark"):
        changes.append({"beat": float(mm.offset), "bpm": float(mm.getQuarterBPM())})
    if not changes:
        changes = [{"beat": 0.0, "bpm": 120.0}]
    return changes


def apply_version_tag(title, mode, format_tag=None):
    """Piano-derived beatmaps get '(Version P)' appended to the title;
    band-derived beatmaps get '(Version S)'. Shared by convert.py and
    convert_midi.py so a beatmap's source rendition is always tagged the
    same way regardless of which script produced it.

    format_tag, when given (convert_midi.py passes "MIDI"), is inserted
    right before the version tag -- e.g. "Hot Cross Buns MIDI (Version P)"
    -- so a MIDI-derived beatmap is also distinguishable at a glance from
    the MusicXML-derived beatmap of the same song/mode, not just piano vs.
    band. convert.py doesn't pass this (MusicXML is the original/default
    format), so its titles are unchanged: "Hot Cross Buns (Version P)".
    """
    tag = "Version P" if mode == "piano" else "Version S"
    if f"({tag})" not in title:
        if format_tag and format_tag not in title:
            title = f"{title} {format_tag}"
        title = f"{title} ({tag})"
    return title


def version_suffixed_path(out_path, mode, format_tag=None):
    suffix = "_Version_P" if mode == "piano" else "_Version_S"
    if suffix.lower() not in out_path.stem.lower():
        if format_tag and format_tag.lower() not in out_path.stem.lower():
            out_path = out_path.with_name(f"{out_path.stem}_{format_tag}{out_path.suffix}")
        out_path = out_path.with_name(f"{out_path.stem}{suffix}{out_path.suffix}")
    return out_path
