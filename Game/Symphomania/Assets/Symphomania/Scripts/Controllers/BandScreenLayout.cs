using System.Collections.Generic;
using UnityEngine;

namespace Symphomania.Controllers
{
    /// <summary>
    /// Where each instrument's scrolling lane lives on screen.
    ///
    /// Per the project spec: left to right the screen splits vertically into
    /// Trumpet, Saxophone, Drum Kit, Violin, and the Trombone takes the bottom
    /// third for itself across the full width.
    ///
    /// Sessions can have any subset of controllers, so the columns divide only
    /// among the instruments actually playing - two players get half the width
    /// each, not two narrow strips beside three empty ones. If the trombone
    /// isn't playing, the columns take the full height instead of leaving a
    /// dead band at the bottom.
    /// </summary>
    public static class BandScreenLayout
    {
        /// <summary>Fraction of screen height the trombone banner occupies.</summary>
        public const float TromboneBannerHeight = 1f / 3f;

        /// <summary>
        /// Viewport rect for one instrument, in Unity's normalized y-up space -
        /// assign straight to Camera.rect for a split-screen camera.
        /// Returns Rect.zero if the instrument isn't in the active set.
        /// </summary>
        public static Rect GetViewportRect(InstrumentType instrument, IList<InstrumentType> active)
        {
            if (active == null || !active.Contains(instrument))
                return Rect.zero;

            bool tromboneActive = active.Contains(InstrumentType.Trombone);

            // Column instruments actually playing, left to right by ColumnOrder.
            var columns = new List<InstrumentType>();
            foreach (var candidate in InstrumentCatalog.ColumnsInOrder())
                if (active.Contains(candidate)) columns.Add(candidate);

            if (instrument == InstrumentType.Trombone)
            {
                // Solo trombone: nobody is using the columns, so take the whole
                // screen rather than sitting in a third of it with two-thirds
                // dead. Mirrors the columns taking full height when the trombone
                // is absent.
                return columns.Count == 0
                    ? new Rect(0f, 0f, 1f, 1f)
                    : new Rect(0f, 0f, 1f, TromboneBannerHeight);
            }

            int index = columns.IndexOf(instrument);
            if (index < 0 || columns.Count == 0) return Rect.zero;

            float columnWidth = 1f / columns.Count;
            float bottom = tromboneActive ? TromboneBannerHeight : 0f;
            float height = 1f - bottom;

            return new Rect(index * columnWidth, bottom, columnWidth, height);
        }

        /// <summary>
        /// Same layout in GUI pixel space (origin top-left, y increasing
        /// downward) for IMGUI / overlay drawing.
        /// </summary>
        public static Rect GetGuiRect(InstrumentType instrument, IList<InstrumentType> active,
                                      float screenWidth, float screenHeight)
        {
            var viewport = GetViewportRect(instrument, active);
            if (viewport == Rect.zero) return Rect.zero;

            return new Rect(
                viewport.x * screenWidth,
                (1f - viewport.y - viewport.height) * screenHeight,
                viewport.width * screenWidth,
                viewport.height * screenHeight);
        }
    }
}
