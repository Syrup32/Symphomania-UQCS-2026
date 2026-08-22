using System;
using UnityEngine;
using Symphomania.Controllers;
using Symphomania.Session;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// One instrument's whole runtime presence during a song: its own camera
    /// (positioned/rect'd per BandScreenLayout via the SessionSlot), its own
    /// HitJudge, and its own NoteLaneView. Ties directly into the session
    /// setup delivery - one GameplayLane per SessionSlot in a frozen
    /// SessionPlan.
    ///
    /// Split-screen without touching Project Settings' Layers: each lane's
    /// whole scene content lives at a distinct world-space X offset (see
    /// worldOffsetX in Initialize), far enough apart that a narrow orthographic
    /// camera looking only at its own offset never sees another lane's content
    /// - so no culling-mask/Layer setup is required for this to work correctly.
    /// </summary>
    public class GameplayLane : MonoBehaviour
    {
        public InstrumentType Instrument { get; private set; }
        public NoteLaneView View { get; private set; }

        HitJudge _judge;
        RhythmConductor _conductor;
        Action<InstrumentType, JudgeEvent> _onJudged;
        AudioSource _audio;

        const float WorldOffsetSpacing = 80f; // wide margin vs. any lane's orthographic view width, even for a 2-column-plus-trombone-banner aspect ratio on an ultrawide monitor

        public void Initialize(SessionSlot slot, RhythmConductor conductor, int laneIndex, float hitWindowSeconds,
                                Action<InstrumentType, JudgeEvent> onJudged)
        {
            Instrument = slot.Instrument;
            _conductor = conductor;
            _onJudged = onJudged;

            float worldOffsetX = laneIndex * WorldOffsetSpacing;
            transform.position = new Vector3(worldOffsetX, 0f, 0f);

            _judge = HitJudge.ForTrack(slot.Track, hitWindowSeconds);

            var camGO = new GameObject($"{Instrument}Camera");
            camGO.transform.SetParent(transform, false);
            camGO.transform.localPosition = new Vector3(0f, 0f, -10f);
            var cam = camGO.AddComponent<Camera>();
            cam.orthographic = true;

            // orthographicSize is scaled by the viewport's own height fraction
            // rather than a flat 5f for every lane. A fixed size assumes every
            // lane fills the full screen height - true for the tall vertical
            // column lanes, but the trombone's own strip (a full-width, short
            // band per BandScreenLayout) only gets a fraction of that height,
            // so the same size would look vertically stretched/squashed
            // there. Scaling by Viewport.height keeps world-units-per-pixel
            // consistent across every lane regardless of its screen shape.
            cam.orthographicSize = 5f * slot.Viewport.height;
            cam.rect = slot.Viewport;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f);
            cam.depth = laneIndex;

            var viewGO = new GameObject($"{Instrument}Lane");
            viewGO.transform.SetParent(transform, false);

            // Computed directly from Screen/Viewport rather than reading
            // cam.aspect, since that's a Camera-internal value that isn't
            // guaranteed to already reflect a rect assigned this same frame -
            // this is the same math, just not dependent on that timing.
            float aspect = (Screen.width * slot.Viewport.width) / (Screen.height * slot.Viewport.height);
            float visibleHalfHeight = cam.orthographicSize;
            float visibleHalfWidth = cam.orthographicSize * aspect;
            InstrumentBackdrop.Create(viewGO.transform, Instrument, visibleHalfWidth, visibleHalfHeight);

            View = viewGO.AddComponent<NoteLaneView>();

            // A separate copy from HitJudge's own internal list (HitJudge.ConvertTrack
            // returns a fresh List each call) - the view's copy is never mutated by
            // judging, so notes stay visible for their full scroll even after HitJudge
            // has already resolved and removed its own entry.
            var items = HitJudge.ConvertTrack(slot.Track);

            View.Initialize(conductor, items, conductor.GridLines, Instrument);

            // spatialBlend = 0 (2D) deliberately - lanes sit 80 world units
            // apart (see WorldOffsetSpacing) purely to keep cameras from
            // seeing each other, not as a real spatial arrangement. A 3D/
            // positional AudioSource here would attenuate or pan based on that
            // arbitrary distance from whatever's listening, which has nothing
            // to do with what the player should actually hear.
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f;
        }

        void Update()
        {
            if (_conductor == null || _judge == null) return;

            var live = VirtualBandInput.Sample(Instrument);
            var events = _judge.Update(live, _conductor.CurrentTime);

            foreach (var evt in events)
            {
                View.OnJudged(evt);
                _onJudged?.Invoke(Instrument, evt);

                // Per the project's design: only a correctly-timed, correctly-fingered
                // check counts as a hit, and that's exactly what a non-Miss JudgeEvent
                // means here - so this is also exactly the right moment to play the
                // note's own pitch back, confirming "you timed and keyed that correctly."
                if (evt.Judgement != NoteJudgement.Miss)
                {
                    // Prefer a real recorded/licensed sample if one's been
                    // dropped in for this instrument (see
                    // InstrumentSampleLibrary's doc comment for where those
                    // live); otherwise fall back to NoteAudio's synthesized
                    // voice for this instrument. AudioSource.pitch is reset
                    // every time since PlayOneShot honors whatever pitch is
                    // currently set - a stale pitch-shift from a previous
                    // sampled hit would otherwise leak into a later
                    // synthesized (or differently-shifted) one.
                    if (InstrumentSampleLibrary.TryGetNearest(Instrument, evt.Pitch, out var sampleClip, out var playbackPitch))
                    {
                        _audio.pitch = playbackPitch;
                        _audio.PlayOneShot(sampleClip);
                    }
                    else
                    {
                        _audio.pitch = 1f;
                        var clip = NoteAudio.ClipForPitch(Instrument, evt.Pitch);
                        if (clip != null) _audio.PlayOneShot(clip);
                    }
                }
            }
        }
    }
}
