# Beatmap JSON schema (v1)

This is the format the Python converter emits and what the Unity game should
load. One file per song, containing every instrument's part together so a
session can pull out however many tracks it needs (solo drum, trumpet+sax
duo, full 5-piece band, etc.).

## Top level

```json
{
  "song": {
    "title": "Twinkle Twinkle Little Star",
    "composer": "Traditional",
    "source": "hand-built sample / <url if from a real score>",
    "time_signature": "4/4",
    "tempo_changes": [{"beat": 0.0, "bpm": 120}],
    "duration_seconds": 17.0
  },
  "instruments": {
    "trumpet": { "controller": "trumpet", "notes": [ ... ] },
    "saxophone": { "controller": "saxophone", "notes": [ ... ] },
    "violin": { "controller": "violin", "notes": [ ... ] },
    "trombone": { "controller": "trombone", "notes": [ ... ] },
    "drum_kit": { "controller": "drum_kit", "hits": [ ... ] }
  }
}
```

`instruments` only contains keys for instruments actually present in the
source (a solo drum chart has just `drum_kit`). Unity's session setup reads
whichever keys exist and only spins up controller slots for those.

## Time base

Every event carries both a beat position (useful for editing/quantizing) and
a wall-clock second position (what the game actually scrolls against),
computed from `tempo_changes`:

- `start_beat` / `start_time` (seconds)
- `duration_beats` / `duration_time` (seconds) -- omitted for drum hits

## Per-instrument note shape

Sustained-note instruments (trumpet, saxophone, violin, trombone) use a
`notes` array:

```json
{
  "id": 12,
  "measure": 3,
  "start_beat": 8.0,
  "start_time": 4.0,
  "duration_beats": 1.0,
  "duration_time": 0.5,
  "pitch": "G4",
  "input": { }
}
```

`input` is controller-specific -- it's the exact HID state the ESP32
controller should be reporting for this note to register as "hit":

| Instrument | `input` shape |
|---|---|
| trumpet | `{"valves": {"1": bool, "2": bool, "3": bool}, "breath": true}` |
| saxophone | `{"holes": {"1"..."7": bool}, "register": bool, "breath": true}` |
| violin | `{"switches": {"1"..."7": bool}, "bow_direction": "down"\|"up"}` |
| trombone | `{"slide_value": 0.0-1.0, "slide_position_estimate": 1-7, "breath": true}` |

Drum-kit has no sustain or pitch, so it uses a `hits` array instead:

```json
{"id": 4, "measure": 1, "time": 1.5, "beat": 3.0, "pad": 2, "pad_name": "snare", "velocity": 0.9}
```

## Design notes / open questions for the Unity side

- `pitch` is kept on every note (even though gameplay only needs `input`)
  purely for debugging/QA -- so you can eyeball a beatmap and know what note
  it's supposed to be.
- Fingering/mapping tables live in `config/*.json` next to the converter,
  **not** hardcoded in the script, specifically so they can be corrected
  without touching Python. Violin's config is a placeholder pending the
  real chart.
- Unresolvable notes (e.g. a chromatic note the simplified saxophone
  fingering can't express) are still emitted, tagged with
  `"warning": "..."` on that note, rather than dropped -- so the beatmap
  stays playable and gaps get caught by looking at the warnings list printed
  by the converter, not by silently missing beats.
