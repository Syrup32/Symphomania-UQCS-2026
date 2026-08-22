using System.Collections.Generic;
using UnityEngine;
using Symphomania.Beatmaps;
using Symphomania.Controllers;

// ---------------------------------------------------------------------------
// "Next steps" item 2 from unity_input_layer.md: intersect "instrument keys
// present in this beatmap" with "controllers actually playing right now", and
// hand the result to BandScreenLayout for a viewport rect per active slot.
//
// This is the Unity-side implementation of the intersection beatmap_schema.md
// calls for in "Session-time instrument availability": a session plays the
// instruments that are BOTH in the loaded chart AND connected/played right
// now. Neither half of a mismatch is an error -
//   - a controller with no matching track has nothing to play this round
//     (no screen section, no scoring - just not part of this song), and
//   - a track with no controller present is silent/unscored, not a factor.
// Both lists are still reported on SessionPlan, because beatmap_schema.md
// calls this out as a real UX question the instrument-entry screen needs to
// answer (e.g. "your saxophone chart part has no saxophone plugged in").
// ---------------------------------------------------------------------------

namespace Symphomania.Session
{
    /// <summary>One instrument's slot in an active session: its part of the chart, plus where it lives on screen.</summary>
    public struct SessionSlot
    {
        public InstrumentType Instrument;

        /// <summary>This instrument's part of the loaded beatmap. Never null for a slot that exists.</summary>
        public BeatmapTrack Track;

        /// <summary>Viewport rect (Camera.rect space) for this instrument's lane, from BandScreenLayout.</summary>
        public Rect Viewport;

        /// <summary>
        /// True if nothing physical is plugged in for this instrument right
        /// now and it's only playable via the keyboard stand-in. Read at
        /// build time from VirtualBandInput.IsConnected - if the caller built
        /// `available` from something other than VirtualBandInput (e.g. a
        /// unit test), this will always read true.
        /// </summary>
        public bool IsKeyboardFallback;
    }

    /// <summary>
    /// The result of intersecting one beatmap against one set of
    /// currently-playable instruments. Rebuild this whenever either side
    /// changes (a controller connects/disconnects, the keyboard fallback
    /// switches instrument, or a different song is chosen) - it's cheap pure
    /// computation, not something that needs to be cached across frames.
    /// </summary>
    public class SessionPlan
    {
        public Beatmap Beatmap;

        /// <summary>Active instruments only, already in left-to-right/bottom-banner screen order.</summary>
        public List<SessionSlot> Slots = new List<SessionSlot>();

        /// <summary>Playable right now, but this chart has no part for them - won't appear on screen this round.</summary>
        public List<InstrumentType> ControllersWithoutTrack = new List<InstrumentType>();

        /// <summary>This chart has a part for them, but nothing is playing them right now - silent/unscored this round.</summary>
        public List<InstrumentType> TracksWithoutController = new List<InstrumentType>();

        public bool TryGetSlot(InstrumentType instrument, out SessionSlot slot)
        {
            foreach (var s in Slots)
            {
                if (s.Instrument != instrument) continue;
                slot = s;
                return true;
            }
            slot = default;
            return false;
        }
    }

    public static class SessionSetup
    {
        /// <summary>
        /// Builds a SessionPlan from one beatmap and one list of instruments
        /// that are playable right now. Pass
        /// VirtualBandInput.ConnectedInstruments() for "real hardware only", or
        /// VirtualBandInput.PlayableInstruments() to include the keyboard
        /// stand-in (the default while hardware isn't fully built/available -
        /// see controller_hid_protocol.md's per-instrument build status).
        /// </summary>
        public static SessionPlan Build(Beatmap beatmap, IList<InstrumentType> available)
        {
            var plan = new SessionPlan { Beatmap = beatmap };
            var availableSet = new HashSet<InstrumentType>(available);

            // Walk InstrumentCatalog.All (not the beatmap's or the available
            // list's own order) so classification and screen order both come
            // from the one place that's supposed to own ordering.
            var active = new List<InstrumentType>();
            foreach (var instrument in InstrumentCatalog.All)
            {
                bool inChart = beatmap.Tracks.ContainsKey(instrument);
                bool inAvailable = availableSet.Contains(instrument);

                if (inChart && inAvailable)
                    active.Add(instrument);
                else if (inAvailable)
                    plan.ControllersWithoutTrack.Add(instrument);
                else if (inChart)
                    plan.TracksWithoutController.Add(instrument);
            }

            foreach (var instrument in active)
            {
                plan.Slots.Add(new SessionSlot
                {
                    Instrument = instrument,
                    Track = beatmap.Tracks[instrument],
                    Viewport = BandScreenLayout.GetViewportRect(instrument, active),
                    IsKeyboardFallback = !VirtualBandInput.IsConnected(instrument),
                });
            }

            return plan;
        }
    }
}
