# Beatmap Converter

Converts MusicXML sheet music into the Virtual Band game's beatmap JSON
format (see `docs/beatmap_schema.md`).

## Setup

```
pip install music21 --break-system-packages
```

## MusicXML vs. MIDI input

Two separate scripts, same output schema:

- `convert.py` — reads MusicXML, either the plain XML form (`.musicxml`/
  `.xml`) or the compressed/zipped form (`.mxl`) that most sheet-music
  sites (MuseScore.com, IMSLP downloads, etc.) actually hand out — no extra
  steps needed, music21 picks the right reader off the file extension.
- `convert_midi.py` — reads MIDI (`.mid`/`.midi`). Use this one when a MIDI
  file was easier to find than a MusicXML file for a given song. Same
  `--mode band|piano`, same `--title`/`--composer`/`--source`, same
  multi-file merge support, same output filename/title version-tagging
  (see below) — it's a drop-in sibling, not a different tool to learn.

**The one thing that's genuinely different between them, and matters:**
MIDI note numbers always encode the pitch that actually sounds — there's no
"written pitch" concept in the MIDI format at all, unlike MusicXML, where a
transposing instrument's part is conventionally written in the performer's
own key (see the trumpet/saxophone note below). So a trumpet or sax track
in a MIDI file is concert pitch, full stop, no matter what the track is
named. `convert_midi.py` always treats trumpet/sax pitches as concert
pitch and transposes forward into the written key before the fingering
lookup — this is the *opposite* of what `convert.py`'s band mode does with
a MusicXML trumpet/sax part (which is already written pitch and must NOT
be re-transposed). You don't need to do anything about this yourself —
each script already handles its own format correctly — but if a trumpet
part's fingerings ever look transposed by a whole step from what you
expect, check which script/format you actually used.

MIDI files also don't reliably carry a title, composer, or clef the way
MusicXML does. Title/composer fall back to the input filename when missing
(same as MusicXML), and `convert_midi.py`'s piano mode picks the treble and
bass parts automatically by average pitch (highest-average-pitch
note-bearing part = treble, lowest = bass) instead of by clef, since MIDI
has no clef metadata to read — override with `--treble-track`/
`--bass-track` (0-indexed part numbers) if a file has more than two
note-bearing parts and it guesses wrong (it'll warn you which parts it
picked and which it skipped either way). Same as `convert.py`, the treble
part feeds trumpet, saxophone, AND violin simultaneously and the bass part
feeds trombone and drum-kit simultaneously — piano mode always produces
all 5 tracks. Band mode's per-track instrument
detection is also best-effort for MIDI specifically for drum/percussion
tracks: General MIDI percussion is conventionally selected by channel 10 +
note number rather than a program-change instrument name, so a percussion
track's instrument identity often doesn't survive a MIDI round trip the
way trumpet/sax/violin/trombone's does — `convert_midi.py` falls back to
matching common percussion-related words in the track's name (drum,
percussion, kit) to catch this.

## Usage

**Band mode** — a real instrument part (trumpet, sax, violin, trombone, or a
percussion/drum part) exported straight from notation software. If a score
has several parts for the same game instrument (a big-band chart's
Trumpet 1-4, or Alto/Tenor/Bari Sax), only the first one in score order is
used — the rest are skipped with a warning, since this project has one
controller per instrument family, not several:

```
python3 convert.py --mode band --input samples/twinkle_twinkle_trumpet.musicxml \
    --output output/twinkle_trumpet.json --source "where this came from"
```

**Piano mode** — a 2-staff piano score. Treble clef feeds trumpet, sax,
*and* violin all at the same time (same melody line, all three
instruments — matching the project's own design, not a choice of one);
bass clef feeds trombone *and* drum-kit at the same time. A piano-mode
beatmap therefore always has all 5 instrument tracks:

```
python3 convert.py --mode piano --input samples/twinkle_twinkle_piano.musicxml \
    --output output/twinkle_piano.json
```

Try it now — both sample commands above run against the included Twinkle
Twinkle Little Star test files and work out of the box.

**Hybrid mode** — a real, multi-part score (a jazz/big-band/orchestral
arrangement downloaded as one `.mxl`, say) that has real parts for *some*
of the 5 game instruments but not all of them, and also happens to include
a piano part/reduction. Real parts win wherever they exist, exactly like
band mode (including: if a family has several parts, like a big-band
chart's Trumpet 1-4, only the first one is used, with a warning for the
rest — this project has one controller per instrument family, not four).
Whichever of the 5 instruments the score has *no* real part for get filled
in from that same file's own piano part instead, using the same
treble/bass-clef logic piano mode uses, restricted to just what's missing.
If an instrument is missing and the file has no usable piano part either,
that instrument is simply left out of the beatmap — same as band mode
already does when a score just doesn't have a part for something. One
input file only (real parts and the piano fallback need to live in the
same score):

```
python3 convert.py --mode hybrid --input samples/big_band_chart.mxl \
    --output output/big_band_chart.json
```

This is genuinely different from plugging the same file into band mode —
band mode alone would just silently drop whichever instruments the score
has no part for; hybrid mode tries the piano first.

**Full-length test songs** — `samples/twinkle_twinkle/`,
`samples/hot_cross_buns/`, and `samples/happy_birthday/` each hold a
complete (not excerpted) melody, in both forms: `<song>_piano.musicxml`
(2-staff piano) and `<song>_band.musicxml` (one file with all 5 parts —
trumpet and sax written in their own transposed key, violin/trombone/
drum-kit in concert pitch). Generated by `gen_full_songs.py`. All three are
traditional/public-domain melodies (Happy Birthday's US copyright claim was
invalidated by a 2015/2016 court ruling), and all three are hand-entered
approximations meant as converter test fixtures, not note-perfect
transcriptions — swap in a real downloaded MusicXML file for anything that
needs to be authoritative. Try them:

```
python3 convert.py --mode piano --input samples/happy_birthday/happy_birthday_piano.musicxml \
    --output output/happy_birthday_piano.json
python3 convert.py --mode band --input samples/happy_birthday/happy_birthday_band.musicxml \
    --output output/happy_birthday_band.json --source test
```

Every beatmap the converter writes is tagged with which source rendition it
came from, so beatmaps from different modes of the same song are never
mixed up in a song list: piano mode appends `(Version P)` to `song.title`
and `_Version_P` to the output filename; band mode appends `(Version S)` /
`_Version_S`; hybrid mode appends `(Version H)` / `_Version_H`. This
happens automatically — `--output output/twinkle.json` in piano mode
actually writes `output/twinkle_Version_P.json`, no extra flag needed. (If
your `--output` or `--title` already contains the tag, e.g. because you're
re-running the same command, it isn't duplicated.)

`convert_midi.py` adds one more tag on top of that, so a MIDI-derived
beatmap is also distinguishable from the MusicXML-derived beatmap of the
same song and mode: `python3 convert_midi.py --mode piano --input
hot_cross_buns.mid --output output/hot_cross_buns.json --title "Hot Cross
Buns"` writes `song.title` as `"Hot Cross Buns MIDI (Version P)"` and the
file as `output/hot_cross_buns_MIDI_Version_P.json`. `convert.py`
(MusicXML) never adds the `MIDI` tag — its output is unchanged, e.g.
`"Hot Cross Buns (Version P)"`.

## Piano mode: one melody line, three instruments at once

This is worth calling out because it's easy to assume otherwise: piano
mode doesn't pick *one* instrument for the treble clef. Per the project's
own design (treble clef → violin, trumpet, and saxophone; bass clef →
trombone and drum-kit), every treble-clef note is fed to trumpet,
saxophone, *and* violin simultaneously, and every bass-clef note is fed to
trombone *and* drum-kit simultaneously — so a piano-mode beatmap always
comes out with all 5 instrument tracks, all playing the same underlying
melody/bass line through their own instrument's fingering chart. (An
earlier version of this converter had `--treble-instrument` let you pick
just one of the three treble instruments per run — that's gone now; piano
mode always produces all three.)

Band mode on all three currently prints saxophone warnings for some notes
— that's the documented diatonic-chart limitation below, not a bug: these
melodies are in concert C (or G) major, which transposes to a written key
with sharps the simplified 8-switch sax chart doesn't cover.

**Merging separate single-instrument files (band mode only)** — if you find
two different scores of the same song at the same tempo (e.g. a
trumpet-only MusicXML and a violin-only MusicXML), point `--input` at both
and they'll be merged into one beatmap, sharing one timeline:

```
python3 convert.py --mode band \
    --input samples/twinkle_multi/trumpet.musicxml samples/twinkle_multi/violin.musicxml \
    --output output/twinkle_merged.json --source "trumpet.musicxml + violin.musicxml, found separately"
```

or point at a directory containing one file per instrument instead of
listing them individually:

```
python3 convert.py --mode band --input samples/twinkle_multi/ --output output/twinkle_merged.json
```

Recommended organization: one folder per song, one file per instrument
inside it (`samples/<song>/trumpet.musicxml`, `samples/<song>/violin.musicxml`,
...) — that's what `samples/twinkle_multi/` in this delivery demonstrates,
and it's what the directory form of `--input` expects. Song-level metadata
(title/composer/tempo/time signature) is taken from the *first* file listed
(or alphabetically-first file, in directory form) — put whichever file has
the most complete/correct metadata first, or just pass `--title`/
`--composer` explicitly. Each file's instrument is still auto-detected the
same way band mode always detects it (by MusicXML instrument name), so
there's no new per-file flag to say "this one is the violin" — it just
works out from the file's own contents, the same as a single multi-part
band-mode file would.

Two safety checks run automatically since "same song, same bpm" is doing a
lot of the trust here: if any file's tempo or time signature doesn't match
the first file's, you get a loud warning (their timelines likely don't
actually line up), and if two files both claim the same instrument, the
first one's part is kept and the second is skipped with a warning rather
than silently overwritten.

## Fingering / mapping configs

`config/*.json` hold every instrument's pitch → controller-input mapping.
Nothing is hardcoded in `convert.py` — edit these files directly to fix or
extend fingerings:

- `trumpet_fingering.json` — standard Bb trumpet valve chart (generated by
  `gen_trumpet_chart.py`, written pitch, F#3–C6).
- `saxophone_fingering.json` — **simplified** but now matches the real
  hardware build's 8-switch role assignment (register key + 3 upper + 3
  lower main keys + low C key, confirmed against a real sax fingering
  chart — see `claude/controller_hid_protocol.md` in the project). Covering
  more holes lowers pitch (fixed 2026-08-22 — an earlier version had this
  backwards). Diatonic only (fixed to C major on its own written stave);
  chromatic notes get flagged. Note: converting a piano part written in a
  key other than concert D major will very likely trip these warnings once
  routed through the sax's transposition (alto sax reads a major 6th above
  concert, so concert C major becomes written A major, which needs 3
  sharps this diatonic-only chart doesn't have) — that's an inherent limit
  of the simplified 8-switch model, not a bug.
- `trombone_config.json` — real tenor trombone position chart (generated
  by `gen_trombone_chart.py` from a real reference chart, confirmed against
  `trombone_control_HID.ino` in the project), not a linear approximation.
  Covers E2-F4 across the instrument's 5 usable overtone partials; where a
  pitch is reachable via more than one (partial, position) pair — real,
  genuine trombone alternate fingerings, e.g. Bb3 at position 1 or 5 — the
  lowest position number is canonical, runner-up kept in `alternatives`,
  same convention as violin. The controller reports only a discrete 1-7
  slide position (confirmed by the firmware: 7 buttons, no continuous axis
  at all), so `input.slide_position` is the only field that gets judged —
  there's no analog value in the game at all now (the earlier
  `slide_value_hint` cosmetic field has been removed).
- `drumkit_config.json` — pad numbering (1=Crash, 2=Snare, 3=High Tom,
  4=Kick, 5=Mid Tom, 6=Floor Tom, 7=Ride) is authoritative, matching the
  actual built controller's HID report layout (`drumkit_control_HID.ino`).
  **2026-08-22 update**: this replaces an earlier placeholder set (1=Kick,
  2=Snare, 3=Hi-Hat, 4=High Tom, 5=Floor Tom, 6=Crash, 7=Ride) decided
  before the physical 7-piezo kit was finalized — that kit has no hi-hat
  pad, so Mid Tom takes its place, and the numbering now follows the
  controller's physical left-to-right wiring order instead. Band mode
  doesn't yet match real percussion notation by name — it still buckets by
  (staff/notated) height instead, same as piano mode's bass clef.
  `band_mode_name_map` is prepared for that future work but isn't wired up
  yet.
- **2026-08-23: real percussion notation now actually produces hits.**
  A hand-built drum part (this project's own sample fixtures) uses ordinary
  `note.Note`/`chord.Chord` objects in music21, but a real percussion part
  exported from notation software (Finale, Sibelius, MuseScore, etc.) comes
  through as `note.Unpitched`/`percussion.PercussionChord` instead — MusicXML
  notates a drum part by staff position (which line/space = which drum
  sound), not by a real sounding pitch, and music21 mirrors that with its
  own classes. `convert.py`'s note-extraction only recognized the pitched
  classes before this fix, so a real percussion part silently produced
  *zero* hits — the part was "detected" (matched to `drum_kit` by
  instrument name) but contributed nothing. Fixed: unpitched notes/chords
  are now handled the same way as their pitched equivalents (a
  `PercussionChord`'s highest notehead, same "one controller reads one
  note" rule as a pitched chord), using the notated staff position in place
  of pitch for `bucket_drum_pad`'s height-based bucketing — which is exactly
  what that bucketing already treated pitch as a stand-in for. This affects
  band mode and hybrid mode only (piano mode's drum track is always derived
  from the bass clef's real pitches, and `convert_midi.py`'s percussion
  detection was already note-number-based, unaffected by this).

## Held drum notes ("rolls")

A drum-kit pad can't report a held press — the piezo sensors only ever
detect a momentary strike (firmware pulses the HID bit for `HOLD_MS` and
auto-releases). So when the source sheet music writes a held note on a
percussion line (a half note, whole note, etc.) — which the converter
already supports as a genuine sustained hold for the other 4 instruments
(valves/holes/switches/slide position stay pressed for the note's full
duration) — a drum hit can't just get a `duration` field, since there's
nothing on the hardware side that would ever be "held" to match it.

Instead, **2026-08-22**: a drum note longer than a quarter beat
(`DRUM_ROLL_THRESHOLD_BEATS` in `common.py`) is expanded into a *roll* — a
series of real, separate strikes spaced an eighth-note apart
(`DRUM_ROLL_INTERVAL_BEATS`), covering the note's full duration. Each
strike is still a completely normal, independent entry in the `hits`
array (same shape as always — `id`/`measure`/`time`/`beat`/`pad`/
`pad_name`/`velocity`), so hit-judging doesn't need any special-case logic
for drums at all; the player just has to actually keep hitting the pad
across the held duration, the same way a real drummer sustains a sound on
an instrument with no sustain pedal. The hits that make up one roll share
a `roll_group` id plus `roll_index`/`roll_length`, purely so Unity can
visually render them as one connected roll (or count a roll as one
combo/judged unit) rather than a string of unrelated single hits — none of
those three fields are present on an ordinary single hit, so existing code
that ignores them keeps working unchanged. A note at or under the
threshold (quarter note or shorter) is untouched — still exactly one
instant hit, same as before this change.
- `violin_fingering.json` — real first-position chart (generated by
  `gen_violin_chart.py`, MIDI 55–83 / G3–B5). Its primary structure is
  `buttons`: button number → the exact 4-note row it produces (button 0 =
  open strings G3/D4/A4/E5, button 1 = G#3/D#4/A#4/F5, ... button 7 =
  D4/A4/E5/B5), matching that a button presses the whole row across all
  four strings at once, not a single string. `notes` is a *derived* reverse
  index (pitch name → canonical button+string) built from that table, used
  when the converter needs to go from a target pitch to an input state.
  Where a pitch is reachable more than one way (string overlap at position
  boundaries, e.g. D4 as either open D or 7th button on G), `notes` picks
  the lowest button number as canonical and records the runner-up in
  `alternatives` — this doesn't change what the hardware does (always the
  whole row), it's only how the converter resolves "which button do I chart
  for this specific note." See `docs/beatmap_schema.md`'s "Hit detection &
  audio feedback" section for why the game never needs to reverse this at
  play time.

Any note the converter can't map (out of chart range, or — for sax —
chromatic notes the simplified switch set can't express) is still emitted
in the output JSON with a best-effort fallback input, and printed as a
warning to stderr so you can spot gaps without the beatmap breaking.

## Files

```
convert.py                  the converter (MusicXML input)
convert_midi.py             the converter (MIDI input) -- same schema, see "MusicXML vs. MIDI input" above
common.py                   fingering/mapping logic shared by convert.py and convert_midi.py
gen_trumpet_chart.py        one-off script that generated trumpet_fingering.json
gen_violin_chart.py         one-off script that generated violin_fingering.json
gen_trombone_chart.py       one-off script that generated trombone_config.json
gen_sample.py                builds the original 9-second Twinkle Twinkle test files, incl. samples/twinkle_multi/
gen_full_songs.py           builds the full-length MusicXML test songs (Twinkle Twinkle, Hot Cross Buns, Happy Birthday)
gen_midi_samples.py         builds the same full-length songs as MIDI, reusing gen_full_songs.py's melody/bass data
config/                     editable fingering/mapping charts
samples/                    test MusicXML and MIDI files (samples/twinkle_multi/ and samples/twinkle_multi_midi/
                             demonstrate the merge layout for each format; samples/twinkle_twinkle/,
                             samples/hot_cross_buns/, samples/happy_birthday/ each hold both a MusicXML and a
                             MIDI piano+band pair)
output/                     generated beatmap JSON lands here
docs/beatmap_schema.md      full schema writeup
```

## Where to get free sheet music

- **Twinkle Twinkle Little Star / Hot Cross Buns** — both are traditional,
  public domain melodies, so almost any source is fair game:
  - [MuseScore.com](https://musescore.com) — huge user-uploaded library,
    free scores can be exported straight to MusicXML (the format this
    script wants). Search the title, filter by instrument.
  - [8notes.com](https://www.8notes.com) — free arrangements across many
    instruments including trumpet, sax, violin, trombone.
  - [Mutopia Project](https://www.mutopiaproject.org) — public-domain
    sheet music distributed with source files, good for simple traditional
    tunes.
  - [IMSLP](https://imslp.org) — mainly historical/classical public-domain
    scores; less likely to have folk tunes like these but worth checking.

- **Autumn Leaves** — this one's different: it's a 1945 jazz standard
  (music by Joseph Kosma) and, unlike the two above, is *not* public
  domain. "Free lead sheet" sites (jazzleadsheet.com, MuseScore user
  uploads, fake-book scans) are common in the jazz community for personal
  practice/arranging use, but they're typically unlicensed fan
  transcriptions, not something you'd be clear to redistribute. Fine to use
  for your own beatmap testing; worth keeping in mind if you ever share the
  band's song library beyond personal use.

- **General tip**: whatever the source, look for a "Download MusicXML"
  option specifically (MuseScore offers this) rather than only PDF — MusicXML
  is what `convert.py` parses. If you can only get a PDF or scan, there's no
  reliable automated path from that to a beatmap; it'd need to be re-entered
  by ear/hand in a notation tool (e.g. free-and-quick in MuseScore itself)
  and then exported.
