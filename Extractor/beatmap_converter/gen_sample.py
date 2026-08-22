"""Build a small hand-entered sample MusicXML for Twinkle Twinkle Little Star,
as a 2-staff piano score (treble + bass), for testing the converter."""
from music21 import stream, note, meter, tempo, metadata, clef, instrument, interval, pitch

# Real trumpet/sax sheet music is written in the instrument's own transposed
# key, not concert pitch -- see convert.py's map_trumpet/map_saxophone.
# Band-mode trumpet fixtures below use this to write genuinely correct
# written pitch, matching what real downloaded trumpet sheet music looks
# like (rather than mislabeling concert-pitch notes as a trumpet part).
TRUMPET_CONCERT_TO_WRITTEN = interval.Interval("M2")


def written_trumpet(concert_name):
    return pitch.Pitch(concert_name).transpose(TRUMPET_CONCERT_TO_WRITTEN).nameWithOctave


melody = "C4 C4 G4 G4 A4 A4 G4 F4 F4 E4 E4 D4 D4 C4".split()
durations = [1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 1, 2]

bass = "C3 G2 C3 F2 C3 G2 C3".split()
bass_durations = [2, 2, 2, 2, 2, 2, 4]

score = stream.Score()
score.metadata = metadata.Metadata()
score.metadata.title = "Twinkle Twinkle Little Star"
score.metadata.composer = "Traditional"

treble = stream.Part()
treble.insert(0, instrument.Piano())
treble.insert(0, clef.TrebleClef())
treble.insert(0, meter.TimeSignature("4/4"))
treble.insert(0, tempo.MetronomeMark(number=100))
for pitch_name, dur in zip(melody, durations):
    n = note.Note(pitch_name)
    n.quarterLength = dur
    treble.append(n)

bass_part = stream.Part()
bass_part.insert(0, instrument.Piano())
bass_part.insert(0, clef.BassClef())
bass_part.insert(0, meter.TimeSignature("4/4"))
for pitch_name, dur in zip(bass, bass_durations):
    n = note.Note(pitch_name)
    n.quarterLength = dur
    bass_part.append(n)

score.insert(0, treble)
score.insert(0, bass_part)
score.write("musicxml", fp="samples/twinkle_twinkle_piano.musicxml")
print("wrote samples/twinkle_twinkle_piano.musicxml")

# Also a single trumpet-only "band mode" part for testing that path.
tscore = stream.Score()
tscore.metadata = metadata.Metadata()
tscore.metadata.title = "Twinkle Twinkle Little Star (Trumpet)"
tscore.metadata.composer = "Traditional"
tpart = stream.Part()
tpart.insert(0, instrument.Trumpet())
tpart.insert(0, meter.TimeSignature("4/4"))
tpart.insert(0, tempo.MetronomeMark(number=100))
for pitch_name, dur in zip(melody, durations):
    n = note.Note(written_trumpet(pitch_name))
    n.quarterLength = dur
    tpart.append(n)
tscore.insert(0, tpart)
tscore.write("musicxml", fp="samples/twinkle_twinkle_trumpet.musicxml")
print("wrote samples/twinkle_twinkle_trumpet.musicxml")

# A small "found two separate single-instrument scores for the same song"
# scenario, for testing --mode band's multi-file merge: a trumpet-only file
# and a violin-only file, same song, same bpm, in their own directory so
# `--input samples/twinkle_multi/` (directory form) has something to glob.
import os
os.makedirs("samples/twinkle_multi", exist_ok=True)

mscore_t = stream.Score()
mscore_t.metadata = metadata.Metadata()
mscore_t.metadata.title = "Twinkle Twinkle Little Star (Trumpet part, found separately)"
mscore_t.metadata.composer = "Traditional"
mpart_t = stream.Part()
mpart_t.insert(0, instrument.Trumpet())
mpart_t.insert(0, meter.TimeSignature("4/4"))
mpart_t.insert(0, tempo.MetronomeMark(number=100))
for pitch_name, dur in zip(melody, durations):
    n = note.Note(written_trumpet(pitch_name))
    n.quarterLength = dur
    mpart_t.append(n)
mscore_t.insert(0, mpart_t)
mscore_t.write("musicxml", fp="samples/twinkle_multi/trumpet.musicxml")
print("wrote samples/twinkle_multi/trumpet.musicxml")

mscore_v = stream.Score()
mscore_v.metadata = metadata.Metadata()
mscore_v.metadata.title = "Twinkle Twinkle Little Star (Violin part, found separately)"
mscore_v.metadata.composer = "Traditional"
mpart_v = stream.Part()
mpart_v.insert(0, instrument.Violin())
mpart_v.insert(0, meter.TimeSignature("4/4"))
mpart_v.insert(0, tempo.MetronomeMark(number=100))
for pitch_name, dur in zip(melody, durations):
    n = note.Note(pitch_name)
    n.quarterLength = dur
    mpart_v.append(n)
mscore_v.insert(0, mpart_v)
mscore_v.write("musicxml", fp="samples/twinkle_multi/violin.musicxml")
print("wrote samples/twinkle_multi/violin.musicxml")
