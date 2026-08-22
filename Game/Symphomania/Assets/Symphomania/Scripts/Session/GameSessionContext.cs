using UnityEngine;
using Symphomania.Beatmaps;
using Symphomania.Controllers;

namespace Symphomania.Session
{
    /// <summary>
    /// Static holder carrying the chosen song and its live SessionPlan across
    /// the menu flow (song select -> instrument entry -> gameplay). Nothing
    /// here loads a scene or drives UI - it's just the shared state those
    /// screens read and write, matching how VirtualBandInput is a static
    /// registry rather than a MonoBehaviour singleton.
    ///
    /// The instrument-entry screen is expected to call RefreshPlan() every
    /// frame (or on VirtualBandInput's connect/disconnect events) while
    /// players are plugging controllers in and picking up/putting down the
    /// keyboard fallback, then read CurrentPlan for its live display. Once the
    /// player confirms, gameplay reads the same CurrentPlan that was on screen
    /// when they confirmed - it does not silently change after that point,
    /// even if a controller is unplugged mid-transition, since a snapshot governs
    /// Slots for the rest of that session (matching VirtualBandInput's own
    /// note that Disabled/focus-loss is deliberately not a disconnect either).
    /// </summary>
    public static class GameSessionContext
    {
        /// <summary>The beatmap chosen at song select. Null before a song is chosen.</summary>
        public static Beatmap CurrentBeatmap { get; private set; }

        /// <summary>
        /// The most recent SessionPlan built for CurrentBeatmap. Live during
        /// instrument entry (call RefreshPlan to update it); treat it as a
        /// frozen snapshot once gameplay has started - see FreezePlan.
        /// </summary>
        public static SessionPlan CurrentPlan { get; private set; }

        /// <summary>True after FreezePlan() until the next SetBeatmap() call clears it.</summary>
        public static bool IsFrozen { get; private set; }

        /// <summary>
        /// Wipes all state at the start of every Play session, same reason
        /// VirtualBandInput does - with "Enter Play Mode Options" domain
        /// reload disabled, statics otherwise survive from the previous Play
        /// session and the second Play onward would start mid-song against a
        /// beatmap from last time.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            CurrentBeatmap = null;
            CurrentPlan = null;
            IsFrozen = false;
        }

        /// <summary>Call from song select. Clears any frozen plan from a previous song.</summary>
        public static void SetBeatmap(Beatmap beatmap)
        {
            CurrentBeatmap = beatmap;
            CurrentPlan = null;
            IsFrozen = false;
        }

        /// <summary>
        /// Rebuilds CurrentPlan from CurrentBeatmap against whichever
        /// instruments are playable right now. No-ops (logs a warning) if no
        /// beatmap has been chosen yet, or if the plan is already frozen -
        /// call UnfreezePlan first if you genuinely need to rebuild after
        /// freezing (e.g. the player backed out of the confirm screen).
        /// </summary>
        public static SessionPlan RefreshPlan(bool includeKeyboardFallback = true)
        {
            if (CurrentBeatmap == null)
            {
                Debug.LogWarning("[GameSessionContext] RefreshPlan called with no beatmap chosen - call SetBeatmap first.");
                return null;
            }

            if (IsFrozen)
            {
                Debug.LogWarning("[GameSessionContext] RefreshPlan called on a frozen plan - ignored. Call UnfreezePlan first if this is intentional.");
                return CurrentPlan;
            }

            var available = includeKeyboardFallback
                ? VirtualBandInput.PlayableInstruments()
                : VirtualBandInput.ConnectedInstruments();

            CurrentPlan = SessionSetup.Build(CurrentBeatmap, available);
            return CurrentPlan;
        }

        /// <summary>
        /// Call once, when the player confirms their instrument selection and
        /// gameplay is about to start. After this, RefreshPlan is a no-op
        /// until UnfreezePlan is called, so an alt-tab or a mid-song unplug
        /// can't reshuffle who's playing which lane after the song has begun -
        /// scrolling/judging should read a stable Slots list for the whole song.
        /// </summary>
        public static void FreezePlan()
        {
            IsFrozen = true;
        }

        /// <summary>Call if the player backs out of confirmation back to instrument entry.</summary>
        public static void UnfreezePlan()
        {
            IsFrozen = false;
        }
    }
}
