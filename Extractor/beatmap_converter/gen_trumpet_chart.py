"""
One-off generator for config/trumpet_fingering.json.

Standard Bb trumpet fingering follows a 12-note chromatic pattern (by pitch
class) that repeats every octave across the normal playable range. This
script builds that pattern programmatically instead of hand-typing ~30
entries. Pitches are WRITTEN pitch (i.e. what's printed in a trumpet part),
not concert/sounding pitch -- the converter is responsible for transposing
concert pitch up a major second before doing this lookup.
"""
import json

# Standard ("first choice") fingering by pitch class, valves as [1,2,3] booleans.
# True = valve pressed down.
PATTERN = {
    0: [False, False, False],   # C
    1: [True,  True,  True],    # C#/Db
    2: [True,  False, True],    # D
    3: [False, True,  True],    # D#/Eb
    4: [True,  True,  False],   # E
    5: [True,  False, False],   # F
    6: [False, True,  False],   # F#/Gb
    7: [False, False, False],   # G
    8: [False, True,  True],    # G#/Ab
    9: [True,  True,  False],   # A
    10: [True, False, False],   # A#/Bb
    11: [False, True,  False],  # B
}

NAMES_SHARP = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]

chart = {}
# Practical written range for a beginner/intermediate part: F#3 up to C6.
for midi in range(54, 85):  # F#3 (54) .. C6 (84)
    pc = midi % 12
    octave = midi // 12 - 1
    name = f"{NAMES_SHARP[pc]}{octave}"
    chart[name] = {
        "valves": {"1": PATTERN[pc][0], "2": PATTERN[pc][1], "3": PATTERN[pc][2]},
        "breath": True,
    }

out = {
    "instrument": "trumpet",
    "description": (
        "Pitch is WRITTEN pitch for Bb trumpet (converter transposes concert "
        "pitch up a major second before lookup). valves.N = true means valve N "
        "is pressed. breath is always true for a sounding note -- the mic just "
        "needs to detect airflow above threshold while a note is active."
    ),
    "switch_count": 3,
    "notes": chart,
}

with open("config/trumpet_fingering.json", "w") as f:
    json.dump(out, f, indent=2)

print(f"wrote {len(chart)} entries")
