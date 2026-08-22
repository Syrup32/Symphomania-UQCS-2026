using System.Collections.Generic;
using UnityEngine;
using Symphomania.Beatmaps;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// The one shared playhead every lane scrolls and judges against - see
    /// beatmap_schema.md's "Session-time instrument availability" section on
    /// why a single shared clock (not five independent per-instrument clocks)
    /// is the whole point of one JSON file per song.
    ///
    /// Currently Time.deltaTime-driven, not synced to an AudioSource, because
    /// there's no backing-track audio playback built yet. That's the one
    /// thing to swap later: replace the accumulation in Update() with reading
    /// an AudioSource.time once music playback exists, and every lane/judge
    /// downstream keeps working unchanged since they only ever read CurrentTime.
    /// </summary>
    public class RhythmConductor : MonoBehaviour
    {
        /// <summary>Seconds since the song's beat 0 / start_time 0. Negative during the lead-in.</summary>
        public float CurrentTime { get; private set; }

        public bool IsPlaying { get; private set; }

        public BeatmapSong Song { get; private set; }

        /// <summary>Precomputed once per song in Initialize - see BeatGrid.</summary>
        public List<BeatGridLine> GridLines { get; private set; }

        /// <summary>
        /// Sets up the conductor for one song and rewinds to the lead-in start
        /// (CurrentTime = -leadInSeconds) without starting playback - call
        /// Play() separately once lanes/cameras are ready to receive input.
        /// </summary>
        public void Initialize(BeatmapSong song, float leadInSeconds = 3f, float gridCoverageMarginSeconds = 2f)
        {
            Song = song;
            CurrentTime = -Mathf.Abs(leadInSeconds);
            IsPlaying = false;
            GridLines = BeatGrid.Build(song, song.DurationSeconds + gridCoverageMarginSeconds);
        }

        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;

        /// <summary>Jump to an arbitrary time - mainly useful for testing a specific passage without waiting through the whole lead-in/song.</summary>
        public void Seek(float time) => CurrentTime = time;

        void Update()
        {
            if (!IsPlaying) return;
            CurrentTime += Time.deltaTime;
        }
    }
}
