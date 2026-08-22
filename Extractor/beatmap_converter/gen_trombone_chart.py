"""
Generator for config/trombone_config.json, built from a real tenor trombone
position chart (AMRO Music, provided by the user 2026-08-22) and cross-
checked against standard trombone overtone/position physics.

Trombone acoustics: each of the 7 slide positions on a given overtone
partial lowers the pitch by one semitone from position 1 (slide fully in)
to position 7 (slide fully out). The instrument has several usable
partials, each with its own position-1 pitch:

  Partial 2 (Bb): position 1 = Bb2
  Partial 3 (F):  position 1 = F3
  Partial 4 (Bb): position 1 = Bb3
  Partial 5 (D):  position 1 = D4
  Partial 6 (F):  position 1 = F4

Adjacent partials overlap in pitch (e.g. partial 4 position 1 = Bb3, same
pitch as partial 5 position 5), which is exactly why real trombone charts
show alternate positions for some notes -- this is genuine instrument
behavior, not an artifact of the simplified 8-switch/8-button controller.
Every note in the user's real chart checks out exactly against this table
(verified note-by-note while building this file), including the specific
alternates it lists: Bb3 at position 1 or 5, A3 at position 2 or 6, F3 at
position 1 or 6, D4 at position 1 or (position 4 of the next partial up).

Range covered: E2 (partial 2, position 7) through F4 (partial 6,
position 1) -- the full practical 7-position range. The user's chart shows
two notes above F4 (F#4, G4) that need an embouchure/lip adjustment beyond
straightforward slide position (marked with special footnotes on the
original chart) -- those are NOT included here since this simplified
hardware only reports a slide position, nothing else; they're out of range
the same way an out-of-chart note is for any other instrument.

Where a pitch is reachable at more than one (partial, position), this
generator picks the LOWEST position number as canonical -- this isn't just
convenience, it matches every primary/alternate pairing the real chart
itself lists (the chart's "primary" choice is always the lower position
number in every case that came up), and keeps the runner-up in
'alternatives' the same way trumpet/violin do.
"""
import json

PARTIALS = {
    2: 46,  # Bb2
    3: 53,  # F3
    4: 58,  # Bb3
    5: 62,  # D4
    6: 65,  # F4
}

SHARP = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
FLAT = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"]


def names_for(midi):
    pc, octave = midi % 12, midi // 12 - 1
    names = {f"{SHARP[pc]}{octave}"}
    if SHARP[pc] != FLAT[pc]:
        names.add(f"{FLAT[pc]}{octave}")
    return names


def name_for(midi):
    """Sharp spelling, for the min_note/max_note summary fields only."""
    pc, octave = midi % 12, midi // 12 - 1
    return f"{SHARP[pc]}{octave}"


candidates_by_midi = {}
for partial, pos1_midi in PARTIALS.items():
    for position in range(1, 8):
        midi = pos1_midi - (position - 1)
        candidates_by_midi.setdefault(midi, []).append((position, partial))

notes = {}
for midi in sorted(candidates_by_midi):
    candidates = sorted(candidates_by_midi[midi], key=lambda c: c[0])  # prefer lowest position
    position, partial = candidates[0]
    entry = {"slide_position": position, "partial": partial}
    if len(candidates) > 1:
        entry["alternatives"] = [{"slide_position": p, "partial": pt} for p, pt in candidates[1:]]
    for name in names_for(midi):
        notes[name] = entry

out = {
    "instrument": "trombone",
    "description": (
        "Built from a real tenor trombone position chart, not a linear "
        "approximation. The controller's HID report only ever exposes a "
        "discrete slide position 1-7 (one of 7 buttons -- see the firmware's "
        "trombone_control_HID.ino) with no continuous axis at all, so "
        "'slide_position' is the only field that needs to be judged against "
        "hardware; there is no analog value to show in-game. Covers E2-F4, "
        "the full practical range across partials 2-6. Where a pitch is "
        "reachable via more than one (position, partial) pair -- adjacent "
        "partials overlap by design on a real trombone -- the lowest "
        "position number is canonical (matches the real chart's own primary "
        "choice in every case checked), with the runner-up kept in "
        "'alternatives'. Two notes above this range (F#4, G4) need an "
        "embouchure/lip adjustment beyond plain slide position on a real "
        "horn and are deliberately left out -- this simplified controller "
        "has no way to express that, so they're out-of-chart the same as "
        "any other unreachable note."
    ),
    "position_count": 7,
    "min_note": name_for(min(candidates_by_midi)),
    "max_note": name_for(max(candidates_by_midi)),
    "notes": notes,
}

with open("config/trombone_config.json", "w") as f:
    json.dump(out, f, indent=2)

print(f"wrote {len(notes)} entries, range {out['min_note']}-{out['max_note']}")
