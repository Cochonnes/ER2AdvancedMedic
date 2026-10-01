using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>Small IMGUI helpers (solid-colour fills, labels) shared by overlay + debug.
    /// Named Gui to avoid colliding with MedicUI.Draw(). Uses the plain GUI.Label(Rect,string)
    /// overload (the GUIStyle overload misresolves under IL2CPP interop) with GUI.color.</summary>
    internal static class Gui
    {
        private static Texture2D _white;

        public static Texture2D White()
        {
            if (_white != null) return _white;
            _white = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            _white.hideFlags = HideFlags.HideAndDontSave;
            var px = new Color(1f, 1f, 1f, 1f);
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) _white.SetPixel(x, y, px);
            _white.Apply();
            return _white;
        }

        // Texture fills must only run during the Repaint event, so callers can be invoked on
        // every OnGUI event (letting GUI.Button handle its own layout/mouse phases).
        private static bool Repaint => Event.current != null && Event.current.type == EventType.Repaint;

        private static Texture2D _circle;
        public static Texture2D Circle()
        {
            if (_circle != null) return _circle;
            const int N = 32; float rad = N * 0.5f - 0.5f, cx = (N - 1) * 0.5f, cy = (N - 1) * 0.5f;
            _circle = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    float a = Mathf.Clamp01(rad - d);   // soft 1px edge
                    _circle.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            _circle.Apply();
            return _circle;
        }

        public static void Dot(float cx, float cy, float diameter, Color fill, Color edge)
        {
            if (!Repaint) return;
            var prev = GUI.color;
            GUI.color = edge; GUI.DrawTexture(new Rect(cx - diameter * 0.5f - 1.5f, cy - diameter * 0.5f - 1.5f, diameter + 3f, diameter + 3f), Circle());
            GUI.color = fill; GUI.DrawTexture(new Rect(cx - diameter * 0.5f, cy - diameter * 0.5f, diameter, diameter), Circle());
            GUI.color = prev;
        }

        public static void Rect(Rect r, Color col)
        {
            if (!Repaint) return;
            var prev = GUI.color;
            GUI.color = col;
            GUI.DrawTexture(r, White());
            GUI.color = prev;
        }

        public static void Tex(Rect r, Texture2D tex, Color tint)
        {
            if (!Repaint || tex == null) return;
            var prev = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(r, tex);
            GUI.color = prev;
        }

        /// <summary>Draw a texture aspect-fitted and centred in the rect (never stretched) — game item
        /// icons aren't square, unlike the bundled PNGs.</summary>
        public static void TexFit(Rect r, Texture2D tex, Color tint)
        {
            if (!Repaint || tex == null) return;
            float tw = tex.width, th = tex.height;
            if (tw <= 0 || th <= 0) return;
            float s = Mathf.Min(r.width / tw, r.height / th);
            float w = tw * s, h = th * s;
            Tex(new Rect(r.x + (r.width - w) * 0.5f, r.y + (r.height - h) * 0.5f, w, h), tex, tint);
        }

        // GUI.skin.label is two native lookups plus a style-pool round trip, so it is fetched once per
        // FRAME, not per label. Not once per session: GUI.Label draws with whatever skin is current, and a
        // skin swapped in later (a scene's own, another mod's) left the font size / alignment writes going
        // to a style nothing drew with any more.
        private static GUIStyle _labelStyle;
        private static int _styleFrame = -1;
        private static GUIStyle Style
        {
            get
            {
                int f = Time.frameCount;
                if (f == _styleFrame && _labelStyle != null) return _labelStyle;
                _styleFrame = f;
                try { _labelStyle = GUI.skin != null ? GUI.skin.label : null; } catch { _labelStyle = null; }
                return _labelStyle;
            }
        }

        public static void Label(Rect r, string text, Color col, int size = 13) => Label(r, text, col, size, false);

        /// <summary>A label. The game's skin centres its label text, so every label here is drawn
        /// LEFT-aligned unless it is a card title (centered:true).</summary>
        public static void Label(Rect r, string text, Color col, int size, bool centered)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Repaint only, like every other helper in this file. This was the one that wasn't:
            // OnGUI runs several times a frame (Layout, Repaint, one pass per input event) and each
            // of those passes was paying two native skin lookups and two font-size writes per label
            // — to draw nothing. Every Rect here is fixed, so no layout pass depends on them.
            if (!Repaint) return;

            // GUI.Label CLIPS to the rect, so a rect only as tall as the font cuts the descenders off
            // ("g", "y", "p"). Give every label the headroom its font size needs.
            float minH = size + 8f;
            if (r.height < minH) r = new Rect(r.x, r.y, r.width, minH);

            var prevCol = GUI.color;
            var st = Style;
            int prevSize = 0;
            TextAnchor prevAlign = TextAnchor.UpperLeft;
            var want = centered ? TextAnchor.UpperCenter : TextAnchor.UpperLeft;
            if (st != null)
            {
                prevSize = st.fontSize;
                if (prevSize != size) st.fontSize = size;   // most labels share a size
                prevAlign = st.alignment;
                if (prevAlign != want) st.alignment = want;
            }
            GUI.color = col;
            GUI.Label(r, text);
            GUI.color = prevCol;
            if (st != null)
            {
                if (st.fontSize != prevSize) st.fontSize = prevSize;
                if (st.alignment != prevAlign) st.alignment = prevAlign;
            }
        }
    }
}
