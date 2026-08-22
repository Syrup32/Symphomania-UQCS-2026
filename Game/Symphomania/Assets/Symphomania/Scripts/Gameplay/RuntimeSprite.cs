using UnityEngine;

namespace Symphomania.Gameplay
{
    /// <summary>
    /// A handful of shared placeholder sprites, generated at runtime and
    /// tinted per use via SpriteRenderer.color - no texture assets required to
    /// see this system run. Swap in real art later by assigning sprites
    /// elsewhere instead of relying on this.
    /// </summary>
    public static class RuntimeSprite
    {
        static Sprite _whiteSquare;
        static Sprite _filledCircle;
        static Sprite _ringCircle;

        /// <summary>A 1x1 white square - used for the scrolling beat grid and the full-width judge line bar.</summary>
        public static Sprite WhiteSquare()
        {
            if (_whiteSquare != null) return _whiteSquare;

            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            tex.filterMode = FilterMode.Point;

            _whiteSquare = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            return _whiteSquare;
        }

        /// <summary>A soft-edged filled white circle, 1 world unit across at scale 1 - the scrolling note dots.</summary>
        public static Sprite FilledCircle()
        {
            if (_filledCircle != null) return _filledCircle;
            _filledCircle = BuildCircleSprite(filled: true);
            return _filledCircle;
        }

        /// <summary>A soft-edged hollow ring, 1 world unit across at scale 1 - the static receptor markers at the judge line.</summary>
        public static Sprite RingCircle()
        {
            if (_ringCircle != null) return _ringCircle;
            _ringCircle = BuildCircleSprite(filled: false);
            return _ringCircle;
        }

        static Sprite BuildCircleSprite(bool filled)
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var center = new Vector2((size - 1) / 2f, (size - 1) / 2f);
            float outerRadius = size / 2f - 1f;
            float innerRadius = outerRadius * 0.6f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), center);
                    float outerAlpha = Mathf.Clamp01(outerRadius - d + 1f); // 1px soft edge
                    float alpha = filled ? outerAlpha : Mathf.Min(outerAlpha, Mathf.Clamp01(d - innerRadius + 1f));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }
    }
}
