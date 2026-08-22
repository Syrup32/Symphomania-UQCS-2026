"""Build MIDI (.mid) test fixtures for convert_midi.py, reusing the same
melody/bass data as gen_full_songs.py (Twinkle Twinkle Little Star, Hot
Cross Buns, Happy Birthday to You).

Unlike gen_full_songs.py's band-mode MusicXML output, trumpet/sax notes
here are written at CONCERT pitch, not transposed into the instrument's own
written key -- because that's how real MIDI files actually work (MIDI note
numbers are always the pitch that sounds, there's no written-pitch concept
in the format at all). This is intentional and is exactly the behavior
convert_midi.py expects; see convert_midi.py's module docstring.
"""
import os
from music21 import stream, note, meter, tempo, metadata, instrument

from gen_full_songs import SONGS


def add_line(part, pitches, durations):
    for p, d in zip(pitches, durations):
        n = note.Note(p)
        n.quarterLength = d
        part.append(n)


def build_piano_midi(title, composer, time_sig, tempo_bpm, melody, mel_durs, bass, bass_durs, out_path):
    score = stream.Score()
    score.metadata = metadata.Metadata()
    score.metadata.title = title
    score.metadata.composer = composer

    treble = stream.Part()
    treble.insert(0, instrument.Piano())
    treble.insert(0, meter.TimeSignature(time_sig))
    treble.insert(0, tempo.MetronomeMark(number=tempo_bpm))
    add_line(treble, melody, mel_durs)

    bass_part = stream.Part()
    bass_part.insert(0, instrument.Piano())
    bass_part.insert(0, meter.TimeSignature(time_sig))
    add_line(bass_part, bass, bass_durs)

    score.insert(0, treble)
    score.insert(0, bass_part)
    score.write("midi", fp=out_path)
    print(f"wrote {out_path}")


def build_band_midi(title, composer, time_sig, tempo_bpm, melody, mel_durs, bass, bass_durs, out_path):
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
    add_line(trumpet_part, melody, mel_durs)  # concert pitch -- see module docstring

    sax_part = base_part(instrument.AltoSaxophone())
    add_line(sax_part, melody, mel_durs)  # concert pitch -- see module docstring

    violin_part = base_part(instrument.Violin())
    add_line(violin_part, melody, mel_durs)

    trombone_part = base_part(instrument.Trombone())
    add_line(trombone_part, bass, bass_durs)

    drum_part = base_part(instrument.Woodblock())
    drum_part.partName = "Drum Kit"
    add_line(drum_part, bass, bass_durs)

    for p in (trumpet_part, sax_part, violin_part, trombone_part, drum_part):
        score.insert(0, p)
    score.write("midi", fp=out_path)
    print(f"wrote {out_path}")


if __name__ == "__main__":
    for song in SONGS:
        folder = f"samples/{song['name']}"
        os.makedirs(folder, exist_ok=True)
        build_piano_midi(
            song["title"], song["composer"], song["time_sig"], song["tempo_bpm"],
            song["melody"], song["mel_durs"], song["bass"], song["bass_durs"],
            f"{folder}/{song['name']}_piano.mid",
        )
        build_band_midi(
            song["title"], song["composer"], song["time_sig"], song["tempo_bpm"],
            song["melody"], song["mel_durs"], song["bass"], song["bass_durs"],
            f"{folder}/{song['name']}_band.mid",
        )
