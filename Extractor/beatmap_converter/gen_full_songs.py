"""Build full-length (not excerpt) MusicXML starting points for three songs,
each in two forms:

  samples/<song>/<song>_piano.musicxml  -- 2-staff piano score (treble+bass),
      for --mode piano.
  samples/<song>/<song>_band.musicxml   -- one multi-part score with all 5
      instruments (trumpet, saxophone, violin, trombone, drum-kit-as-pitch),
      for --mode band. Trumpet/sax parts are written in their own
      transposed key (see written_trumpet/written_sax below), matching how
      real downloaded trumpet/sax sheet music looks and how convert.py's
      band mode expects a transposing instrument's part to already be
      written pitch (see convert.py's map_trumpet/map_saxophone).

Songs: Twinkle Twinkle Little Star, Hot Cross Buns, Happy Birthday to You.
All three are traditional/public-domain melodies (Happy Birthday's US
copyright claim was invalidated by court ruling in 2015/2016), safe to use
as test fixtures.

These are hand-entered approximations meant as *starting points* to test
the converter end-to-end, not authoritative transcriptions -- swap in a
real downloaded MusicXML file for anything you need to be note-perfect.
"""
import os
from music21 import stream, note, meter, tempo, metadata, clef, instrument, interval, pitch

TRUMPET_CONCERT_TO_WRITTEN = interval.Interval("M2")
ALTO_SAX_CONCERT_TO_WRITTEN = interval.Interval("M6")


def written_trumpet(concert_name):
    return pitch.Pitch(concert_name).transpose(TRUMPET_CONCERT_TO_WRITTEN).nameWithOctave


def written_sax(concert_name):
    return pitch.Pitch(concert_name).transpose(ALTO_SAX_CONCERT_TO_WRITTEN).nameWithOctave


def notes_from(pitches, durations, transpose_fn=None):
    """Build a list of (pitch_name, duration) pairs, applying transpose_fn
    (if given) to each concert pitch name first."""
    out = []
    for p, d in zip(pitches, durations):
        out.append((transpose_fn(p) if transpose_fn else p, d))
    return out


def add_line(part, pitches, durations, transpose_fn=None):
    for p, d in zip(pitches, durations):
        name = transpose_fn(p) if transpose_fn else p
        n = note.Note(name)
        n.quarterLength = d
        part.append(n)


def build_piano(title, composer, time_sig, tempo_bpm, melody, mel_durs, bass, bass_durs, out_path):
    score = stream.Score()
    score.metadata = metadata.Metadata()
    score.metadata.title = title
    score.metadata.composer = composer

    treble = stream.Part()
    treble.insert(0, instrument.Piano())
    treble.insert(0, clef.TrebleClef())
    treble.insert(0, meter.TimeSignature(time_sig))
    treble.insert(0, tempo.MetronomeMark(number=tempo_bpm))
    add_line(treble, melody, mel_durs)

    bass_part = stream.Part()
    bass_part.insert(0, instrument.Piano())
    bass_part.insert(0, clef.BassClef())
    bass_part.insert(0, meter.TimeSignature(time_sig))
    add_line(bass_part, bass, bass_durs)

    score.insert(0, treble)
    score.insert(0, bass_part)
    score.write("musicxml", fp=out_path)
    print(f"wrote {out_path}")


def build_band(title, composer, time_sig, tempo_bpm, melody, mel_durs, bass, bass_durs, out_path):
    """Melody (concert pitch) drives trumpet/sax/violin. Bass (concert
    pitch) drives trombone and drum-kit (drum-kit just needs *some* pitch
    contour to bucket into pads -- it isn't reading real percussion
    notation yet, see README)."""
    score = stream.Score()
    score.metadata = metadata.Metadata()
    score.metadata.title = title
    score.metadata.composer = composer

    def base_part(inst):
        p = stream.Part()
        p.insert(0, inst)
        p.insert(0, meter.TimeSignature(time_sig))
        p.insert(0, tempo.MetronomeMark(number=tempo_bpm))
        return p

    trumpet_part = base_part(instrument.Trumpet())
    add_line(trumpet_part, melody, mel_durs, written_trumpet)

    sax_part = base_part(instrument.AltoSaxophone())
    add_line(sax_part, melody, mel_durs, written_sax)

    violin_part = base_part(instrument.Violin())
    add_line(violin_part, melody, mel_durs)

    trombone_part = base_part(instrument.Trombone())
    add_line(trombone_part, bass, bass_durs)

    drum_part = base_part(instrument.Woodblock())
    drum_part.partName = "Drum Kit"
    add_line(drum_part, bass, bass_durs)

    for p in (trumpet_part, sax_part, violin_part, trombone_part, drum_part):
        score.insert(0, p)
    score.write("musicxml", fp=out_path)
    print(f"wrote {out_path}")


# ---------------------------------------------------------------------------
# Twinkle Twinkle Little Star -- full 6-line (12-measure) melody in 4/4.
# ---------------------------------------------------------------------------
twinkle_melody = (
    "C4 C4 G4 G4 A4 A4 G4 "       # Twinkle twinkle little star
    "F4 F4 E4 E4 D4 D4 C4 "       # How I wonder what you are
    "G4 G4 F4 F4 E4 E4 D4 "       # Up above the world so high
    "G4 G4 F4 F4 E4 E4 D4 "       # Like a diamond in the sky
    "C4 C4 G4 G4 A4 A4 G4 "       # Twinkle twinkle little star
    "F4 F4 E4 E4 D4 D4 C4"        # How I wonder what you are
).split()
twinkle_durs = ([1, 1, 1, 1, 1, 1, 2] * 6)

twinkle_bass = "C3 G2 C3 F2 C3 G2 C3 F2 C3 G2 C3 G2".split()
twinkle_bass_durs = [4] * 12

# ---------------------------------------------------------------------------
# Hot Cross Buns -- full 5-measure melody in 4/4.
# ---------------------------------------------------------------------------
hotcross_melody = "E4 D4 C4 E4 D4 C4 C4 C4 C4 C4 D4 D4 D4 D4 E4 D4 C4".split()
hotcross_durs = [1, 1, 2, 1, 1, 2, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 1, 1, 2]

hotcross_bass = "C3 G2 C3 C3 C3".split()
hotcross_bass_durs = [4, 4, 4, 4, 4]

# ---------------------------------------------------------------------------
# Happy Birthday to You -- 4 phrases, 3/4 (public domain since the 2015/2016
# Warner/Chappell copyright ruling; melody simplified to straight quarters/
# eighths for this fixture rather than exact original rhythm).
# ---------------------------------------------------------------------------
happy_melody = (
    "G4 G4 A4 G4 C5 B4 "     # Happy birthday to you
    "G4 G4 A4 G4 D5 C5 "     # Happy birthday to you
    "G4 G4 G5 E5 C5 B4 A4 "  # Happy birthday dear [name]
    "F5 F5 E5 C5 D5 C5"      # Happy birthday to you
).split()
happy_durs = [0.5, 0.5, 1, 1, 1, 2] * 2 + [0.5, 0.5, 1, 1, 1, 1, 2] + [0.5, 0.5, 1, 1, 1, 2]

happy_bass = "C3 G2 C3 G2 C3 F2 C3 G2 C3".split()
happy_bass_durs = [3] * 9


SONGS = [
    dict(name="twinkle_twinkle", title="Twinkle Twinkle Little Star", composer="Traditional",
         time_sig="4/4", tempo_bpm=100,
         melody=twinkle_melody, mel_durs=twinkle_durs,
         bass=twinkle_bass, bass_durs=twinkle_bass_durs),
    dict(name="hot_cross_buns", title="Hot Cross Buns", composer="Traditional",
         time_sig="4/4", tempo_bpm=100,
         melody=hotcross_melody, mel_durs=hotcross_durs,
         bass=hotcross_bass, bass_durs=hotcross_bass_durs),
    dict(name="happy_birthday", title="Happy Birthday to You", composer="Traditional (public domain)",
         time_sig="3/4", tempo_bpm=110,
         melody=happy_melody, mel_durs=happy_durs,
         bass=happy_bass, bass_durs=happy_bass_durs),
]

if __name__ == "__main__":
    for song in SONGS:
        folder = f"samples/{song['name']}"
        os.makedirs(folder, exist_ok=True)

        assert len(song["melody"]) == len(song["mel_durs"]), \
            f"{song['name']}: melody/duration length mismatch " \
            f"({len(song['melody'])} vs {len(song['mel_durs'])})"
        assert len(song["bass"]) == len(song["bass_durs"]), \
            f"{song['name']}: bass/duration length mismatch " \
            f"({len(song['bass'])} vs {len(song['bass_durs'])})"

        build_piano(
            song["title"], song["composer"], song["time_sig"], song["tempo_bpm"],
            song["melody"], song["mel_durs"], song["bass"], song["bass_durs"],
            f"{folder}/{song['name']}_piano.musicxml",
        )
        build_band(
            song["title"], song["composer"], song["time_sig"], song["tempo_bpm"],
            song["melody"], song["mel_durs"], song["bass"], song["bass_durs"],
            f"{folder}/{song['name']}_band.musicxml",
        )
