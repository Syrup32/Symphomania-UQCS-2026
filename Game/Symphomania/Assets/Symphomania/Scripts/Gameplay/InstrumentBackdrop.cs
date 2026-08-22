using System.IO;
using UnityEngine;
using Symphomania.Controllers;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// The dark, tinted character art behind each lane's scrolling notes -
    /// pure gaze guidance, exactly the DDR trick (a big dimmed character
    /// portrait behind each player's own arrows, so a glance at the screen
    /// tells you which strip is yours before you even look at the notes) -
    /// never gameplay-relevant, never blocks or affects judging.
    ///
    /// Looks for StreamingAssets/InstrumentArt/<Instrument>.png (a
    /// transparent-background PNG you provide - e.g. ".../Trumpet.png") and,
    /// if found, centers it in the lane, scaled to fit within the camera's
    /// visible area (contain-fit - fully visible, not cropped), tinted dark
    /// via Tint so it never competes with the bright notes/receptors drawn on
    /// top of it. If the file is missing for a given instrument, that lane
    /// simply has no backdrop - this is optional set dressing, never required
    /// for the game to run, so there's no error for a missing file.
    /// </summary>
    public static class InstrumentBackdrop
    {
        /// <summary>Multiplied over the art's own colors/alpha - lower = darker/dimmer, matching the DDR reference (dim character, bright notes on top). Tune per-instrument by calling Create with different art if one image reads too dark/light relative to the others.</summary>
        public static Color Tint = new Color(0.35f, 0.35f, 0.35f, 1f);

        /// <summary>
        /// visibleHalfWidth/visibleHalfHeight are the camera's own visible
        /// half-extents in world units (orthographicSize for height, times
        /// aspect for width) - pass the same values GameplayLane computed for
        /// its camera so the art fills exactly what that lane's camera can
        /// actually see, whatever this lane's screen shape turns out to be
        /// (tall and narrow for most instruments, short and wide for the
        /// trombone's own strip).
        /// </summary>
        public static void Create(Transform parent, InstrumentType instrument, float visibleHalfWidth, float visibleHalfHeight)
        {
            string path = Path.Combine(Application.streamingAssetsPath, "InstrumentArt", instrument + ".png");
            if (!File.Exists(path)) return; // no art supplied for this instrument yet - fine, just skip it

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (IOException e)
            {
                Debug.LogWarning($"[InstrumentBackdrop] Couldn't read '{path}': {e.Message}");
                return;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes))
            {
                Debug.LogWarning($"[InstrumentBackdrop] '{path}' isn't a readable PNG.");
                return;
            }
            tex.filterMode = FilterMode.Bilinear;

            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);

            var go = new GameObject($"{instrument}Backdrop");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = Tint;
            sr.sortingOrder = -10; // beneath the beat grid (0), notes (5), judge line bar (9), receptors (10), glow rings (11)

            // Contain-fit: scale so the art is fully visible within the
            // camera's view without being cropped, even if its aspect ratio
            // doesn't match the lane's - a fully-visible smaller character
            // reads better as "who this lane is" than a cropped closeup.
            float artWorldWidth = tex.width / sprite.pixelsPerUnit;
            float artWorldHeight = tex.height / sprite.pixelsPerUnit;
            float scale = Mathf.Min((visibleHalfWidth * 2f) / artWorldWidth, (visibleHalfHeight * 2f) / artWorldHeight);
            go.transform.localScale = new Vector3(scale, scale, 1f);
        }
    }
}
