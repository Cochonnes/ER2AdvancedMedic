using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Hit peek: after the local player is wounded, a small, non-interactive
    /// body diagram shows for a few seconds — limbs shaded by how fast they bleed, bandaged limbs blue,
    /// broken limbs orange, tourniquets marked — then fades out.
    /// </summary>
    internal static class HitPeek
    {
        private static float _until;

        public static void OnHit()
        {
            if (!Plugin.C.PeekOnHit.Value) return;
            _until = Time.time + Mathf.Max(0.5f, Plugin.C.PeekSeconds.Value);
        }

        public static void Reset() { _until = 0f; }

        public static void Draw()
        {
            float now = Time.time;
            if (now >= _until || MedicUI.Open) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var p = Interop.Player();
            var w = p != null ? WoundState.Peek(p) : null;
            if (w == null) return;

            float alpha = Mathf.Clamp01((_until - now) / 0.6f);   // fade out over the last 0.6 s
            float size = Mathf.Max(60f, Plugin.C.PeekSize.Value);
            var r = new Rect(Screen.width - size - 24f, (Screen.height - size) * 0.5f, size, size);

            var bg = Assets.Body("background");
            if (bg != null) Gui.Tex(r, bg, new Color(1f, 1f, 1f, 0.55f * alpha));

            for (int i = 0; i < Body.Masks.Length; i++)
            {
                var (part, maskName) = Body.Masks[i];
                var mask = Assets.Body(maskName);
                if (mask == null) continue;
                float rate = Physiology.PartBleeding(w, part);
                Color col;
                if (rate > 0f) col = MedicUI.BleedColor(rate);
                else if (w.FractureState(part) == 1) col = new Color(0.95f, 0.55f, 0.10f, 0.6f);
                else if (w.CountBandaged(part) > 0) col = new Color(0.30f, 0.55f, 0.95f, 0.55f);
                else col = new Color(1f, 1f, 1f, 0.12f);
                col.a *= alpha;
                Gui.Tex(r, mask, col);

                if (w.HasTourniquet(part))
                {
                    var tq = Assets.Body(Body.TourniquetMasks[i]);
                    if (tq != null) Gui.Tex(r, tq, new Color(0.05f, 0.05f, 0.05f, 0.95f * alpha));
                }
            }
        }

    }
}
