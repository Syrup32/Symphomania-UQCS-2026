using System.Collections.Generic;
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
        /// Instruments the player has manually switched off in instrument
        /// entry this round, even though they're otherwise playable (real
        /// controller connected, or standing in via the keyboard fallback) -
        /// e.g. five controllers plugged in, but tonight's session only wants
        /// three of them active. RefreshPlan removes anything in this set from
        /// the "available" list before intersecting it against the beatmap, so
        /// an excluded instrument reads exactly like it was never connected at
        /// all for scoring/screen-layout purposes - it just isn't unplugged.
        /// </summary>
        public static readonly HashSet<InstrumentType> ManuallyDisabled = new HashSet<InstrumentType>();

        /// <summary>True if the player has manually switched this instrument off for the current round.</summary>
        public static bool IsManuallyDisabled(InstrumentType instrument) => ManuallyDisabled.Contains(instrument);

        /// <summary>Toggle one instrument's manual on/off state - called from instrument entry's per-controller buttons.</summary>
        public static void SetManuallyDisabled(InstrumentType instrument, bool disabled)
        {
            if (disabled) ManuallyDisabled.Add(instrument);
            else ManuallyDisabled.Remove(instrument);
        }

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
            ManuallyDisabled.Clear();
        }

        /// <summary>
        /// Call from song select, AND from GameplayBootstrap.Begin() right
        /// before gameplay actually starts (it re-parses the same file into a
        /// fresh Beatmap instance and calls this again to be self-contained -
        /// see its own doc comment). Deliberately does NOT touch
        /// ManuallyDisabled: that field represents the player's instrument
        /// entry choice for the song about to be played, and Begin()'s
        /// re-call happens after that choice was made - clearing it here
        /// would silently discard the player's exclusions the instant they
        /// pressed Start Song. Call ClearManualOverridesForNewSong() instead,
        /// from the place a NEW song is actually being picked (song select),
        /// where "start fresh" is the correct behavior.
        /// </summary>
        public static void SetBeatmap(Beatmap beatmap)
        {
            CurrentBeatmap = beatmap;
            CurrentPlan = null;
            IsFrozen = false;
        }

        /// <summary>
        /// Call when the player picks a (possibly different) song from song
        /// select - resets manual per-instrument on/off toggles and the
        /// keyboard-fallback-standing-in choice isn't touched (that's a
        /// player/keyboard setting, not a per-song one), so instrument entry
        /// starts each new song with everything physically available switched
        /// on, rather than silently carrying over an exclusion picked for a
        /// different song. Do not call this from Begin()'s own SetBeatmap
        /// re-call - see SetBeatmap's doc comment for why.
        /// </summary>
        public static void ClearManualOverridesForNewSong()
        {
            ManuallyDisabled.Clear();
        }

        /// <summary>
        /// Rebuilds CurrentPlan from CurrentBeatmap against whichever
        /// instruments are playable right now, minus anything the player has
        /// manually switched off via SetManuallyDisabled. No-ops (logs a
        /// warning) if no beatmap has been chosen yet, or if the plan is
        /// already frozen - call UnfreezePlan first if you genuinely need to
        /// rebuild after freezing (e.g. the player backed out of the confirm
        /// screen).
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

            var source = includeKeyboardFallback
                ? VirtualBandInput.PlayableInstruments()
                : VirtualBandInput.ConnectedInstruments();

            List<InstrumentType> available = source;
            if (ManuallyDisabled.Count > 0)
            {
                available = new List<InstrumentType>(source.Count);
                foreach (var instrument in source)
                    if (!ManuallyDisabled.Contains(instrument))
                        available.Add(instrument);
            }

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
