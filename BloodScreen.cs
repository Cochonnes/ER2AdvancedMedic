using UnityEngine;
using UnityEngine.UI;

namespace AdvancedMedic
{
    /// <summary>
    /// Blood-splash screen feedback for the LOCAL player being hit while bleeding. Modes (config
    /// BloodScreen/Mode):
    ///   Once       — trigger the game's own BloodSplashGUI splatter once per bleeding hit; it fades itself.
    ///   Persistent — grab the game's blood TEXTURE and draw it OURSELVES, full-screen, held at a steady
    ///                alpha the whole time you have an un-bandaged bleeding wound, then fade it out once the
    ///                last wound is bandaged. Drawing it ourselves is immune to the game deactivating /
    ///                animating its own splash object (which is why holding its alpha only lasted a moment).
    /// </summary>
    internal static class BloodScreen
    {
        private static bool _holding;      // a splash is currently shown/held
        private static float _fade = 1f;   // 1 while bleeding; ramps to 0 during the heal fade-out
        private static Texture _tex;       // the game's blood texture, captured so we can draw it ourselves

        // A failed capture (no BloodSplashGUI yet, or nothing on it) used to be retried every frame
        // while bleeding, from both the tick and the draw. Retry at most every CaptureRetry seconds.
        private static float _nextCapture;
        private const float CaptureRetry = 2f;

        /// <summary>Called when the local player takes a fresh bleeding wound.</summary>
        public static void OnBleedingHit(Soldier s)
        {
            var mode = Plugin.C.BloodScreenMode.Value;
            if (mode == BloodScreenMode.Off) return;
            if (!IsLocal(s)) return;

            PlayEffectOnce();   // paints a fresh splatter + populates the blood texture we capture
            if (mode == BloodScreenMode.Persistent) { _holding = true; _fade = 1f; CaptureTexture(); }
        }

        /// <summary>Per-frame (Effects.Tick). Persistent mode keeps the held splash alive while any bleeding
        /// wound remains, then fades it out once healed — no re-triggering.</summary>
        public static void Tick(Soldier player)
        {
            if (Plugin.C.BloodScreenMode.Value != BloodScreenMode.Persistent) { _holding = false; return; }
            if (player == null) { _holding = false; return; }

            var w = WoundState.Peek(player);
            bool bleeding = w != null && w.AnyBleeding();

            if (bleeding)
            {
                _holding = true;
                _fade = 1f;
                if (_tex == null && Time.unscaledTime >= _nextCapture) { PlayEffectOnce(); CaptureTexture(); }   // ensure we have a texture to draw
            }
            else if (_holding)
            {
                float dt = Time.deltaTime;
                _fade -= dt / Mathf.Max(0.05f, Plugin.C.BloodScreenFadeOut.Value);
                if (_fade <= 0f) { _fade = 0f; _holding = false; }
            }
        }

        /// <summary>Draw the held blood texture full-screen. Called from the OnGUI hook (Repaint only), so
        /// it renders regardless of what the game does with its own BloodSplashGUI object.</summary>
        public static void DrawOverlay()
        {
            if (Plugin.C.BloodScreenMode.Value != BloodScreenMode.Persistent) return;

            // Gate on Repaint and on "is there anything held at all" BEFORE touching the game.
            // Resolving the player and reading IsAlive are native calls, and they were happening on
            // every OnGUI pass even with no splash on screen to draw.
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (!_holding && _fade <= 0f) return;

            // No draw without a live player — otherwise a held splash lingers into the respawn menu /
            // after death. Interop.IsDead, NOT !IsAlive: a DOWNED player reads IsAlive=false at life 0,
            // and that wiped the splash of a player who went down still bleeding.
            var player = Interop.Player();
            if (player == null || Interop.IsDead(player)) { Reset(); return; }
            if (_tex == null && Time.unscaledTime >= _nextCapture) CaptureTexture();
            if (_tex == null) return;

            float a = Mathf.Clamp01(Plugin.C.BloodScreenPersistAlpha.Value) * Mathf.Clamp01(_fade);
            if (a <= 0.01f) return;

            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, a);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _tex, ScaleMode.StretchToFill);
            GUI.color = prev;
        }

        /// <summary>Clear all held state — called on session flush (scene change / player loss) and when
        /// the local player isn't alive, so the splash never lingers into a menu or the next life.
        /// forgetTexture (session flush): the captured texture belongs to the old scene's splash object.</summary>
        public static void Reset(bool forgetTexture = false)
        {
            _holding = false; _fade = 0f;
            if (forgetTexture) { _tex = null; _nextCapture = 0f; }
        }

        // The two game-UI touches below go through wrappers that catch: BloodSplashGUI is a game UI type,
        // and if an update removes a member, the JIT fails the method that names it at its CALL SITE.
        private static void PlayEffectOnce()
        {
            try { PlayEffectRaw(); } catch { }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PlayEffectRaw()
        {
            if (BloodSplashGUI.instance != null) BloodSplashGUI.PlayEffect();
        }

        private static bool _dbgLogged;

        /// <summary>Cache the game's current blood texture (RawImage.texture, or an Image sprite's texture)
        /// from the BloodSplashGUI object so we can render it ourselves.</summary>
        private static void CaptureTexture()
        {
            _nextCapture = Time.unscaledTime + CaptureRetry;
            try { CaptureRaw(); } catch { }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void CaptureRaw()
        {
            try
            {
                var gui = BloodSplashGUI.instance;
                if (gui == null) return;
                var go = gui.gameObject;

                var raw = go.GetComponentInChildren<RawImage>(true);
                if (raw != null && raw.texture != null) _tex = raw.texture;
                else
                {
                    var img = go.GetComponentInChildren<Image>(true);
                    if (img != null && img.sprite != null && img.sprite.texture != null) _tex = img.sprite.texture;
                }

                if (Plugin.C.DebugLogging.Value && !_dbgLogged)
                {
                    _dbgLogged = true;
                    Plugin.L.LogInfo($"[blood] capture tex={(_tex != null)} raw={(raw != null)} rootActive={go.activeInHierarchy}");
                }
            }
            catch { }
        }

        private static bool IsLocal(Soldier s)
        {
            try { return Interop.IsPlayer(s); } catch { return false; }
        }
    }
}
