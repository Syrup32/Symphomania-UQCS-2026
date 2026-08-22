"""
Generator for config/violin_fingering.json, built from the user's real
first-position fingering chart (Violinspiration reference).

Chart logic: 4 open strings G3 D4 A4 E5. Pressing button N (1-7) is not a
per-string action -- it presses down on the ENTIRE ROW at once, i.e. all
four strings simultaneously, N semitones above each string's open pitch.
No button pressed (button 0) = open strings. This is the primary structure
of the config below ("buttons": button number -> the 4-note row it
produces), matching how the physical controller actually works.

  Button 0 (open) -> G3  D4  A4  E5
  Button 1        -> G#3 D#4 A#4 F5
  Button 2        -> A3  E4  B4  F#5
  Button 3        -> A#3 F4  C5  G5
  Button 4        -> B3  F#4 C#5 G#5
  Button 5        -> C4  G4  D5  A5
  Button 6        -> C#4 G#4 D#5 A#5
  Button 7        -> D4  A4  E5  B5

Because a button produces 4 pitches at once (one per string) and the
hardware has no way to know which string is actually being bowed, a single
button is inherently ambiguous for playback purposes. A "notes" reverse
index (pitch name -> the input state that produces it) is derived from the
table above for the converter to use when it needs to go pitch -> input for
a specific chart note. Where a pitch appears in more than one row-position
(adjacent strings overlap by one note at the position boundary, e.g. D4 is
both button-0-on-D and button-7-on-G), the reverse index picks the lowest
button number as canonical (prefers open strings / lower positions) and
keeps the runner-up(s) in "alternatives" -- see docs/beatmap_schema.md
"Hit detection & audio feedback" for why the game never needs to reverse
this at play time.
"""
import json

STRINGS = ["G", "D", "A", "E"]
OPEN_MIDI = {"G": 55, "D": 62, "A": 69, "E": 76}  # G3 D4 A4 E5

SHARP = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
FLAT = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"]


def names_for(midi):
    pc = midi % 12
    octave = midi // 12 - 1
    names = {f"{SHARP[pc]}{octave}"}
    if SHARP[pc] != FLAT[pc]:
        names.add(f"{FLAT[pc]}{octave}")
    return names


def switches_for_button(button):
    return {str(i): (i == button) for i in range(1, 8)}  # all False when button == 0


# --- Primary structure: button -> the row of 4 notes it produces ----------
buttons = {}
for button in range(0, 8):
    row = {}
    for string_name in STRINGS:
        midi = OPEN_MIDI[string_name] + button
        pc, octave = midi % 12, midi // 12 - 1
        row[string_name] = f"{SHARP[pc]}{octave}"  # sharp spelling for display, consistently
    buttons[str(button)] = {"switches": switches_for_button(button), "row": row}

# --- Derived reverse index: pitch name -> canonical (button, string) ------
candidates_by_midi = {}
for string_name in STRINGS:
    for button in range(0, 8):
        midi = OPEN_MIDI[string_name] + button
        candidates_by_midi.setdefault(midi, []).append((button, string_name))

notes = {}
for midi, candidates in sorted(candidates_by_midi.items()):
    candidates.sort(key=lambda c: c[0])  # prefer smallest button (open strings / lower positions)
    button, string_name = candidates[0]
    entry = {
        "switches": switches_for_button(button),
        "string": string_name,
        "button": button,
    }
    if len(candidates) > 1:
        entry["alternatives"] = [{"button": b, "string": s} for b, s in candidates[1:]]
    for name in names_for(midi):
        notes[name] = entry

out = {
    "instrument": "violin",
    "description": (
        "Pressing a button holds down ALL FOUR strings at once, N semitones "
        "above each string's own open pitch -- it is a full-row action, not a "
        "per-string one. 'buttons' below is the primary/authoritative table: "
        "button number -> the exact 4-note row it produces (this is what the "
        "physical controller does). 'notes' is a DERIVED reverse index (pitch "
        "name -> canonical button+string) built from that table, for looking "
        "up what button to chart for a given target pitch. Because one button "
        "produces 4 pitches at once, and some pitches are reachable via more "
        "than one row-position (adjacent strings overlap by a note at the "
        "position boundary), 'notes' just records ONE canonical choice per "
        "pitch (preferring the lowest button number / open strings) plus any "
        "runner-up(s) in 'alternatives' -- it does not change what the "
        "hardware actually does, which is always press the whole row."
    ),
    "switch_count": 7,
    "bow_direction_rule": "alternate",
    "open_strings": OPEN_MIDI,
    "buttons": buttons,
    "notes": notes,
}

with open("config/violin_fingering.json", "w") as f:
    json.dump(out, f, indent=2)

print(f"wrote {len(buttons)} buttons (rows) and {len(notes)} pitch-name reverse-index entries")
for b, entry in buttons.items():
    print(f"  button {b}: {entry['row']}")
