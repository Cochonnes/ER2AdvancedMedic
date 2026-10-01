using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// The game's own item icons for the overlay's action buttons, as textures WE own.
    ///
    /// Ported from ER2ScoreTracker's ItemIcons. Three layers:
    /// - Request through the game's OWN async loader, <c>ResourcesManager.LoadItemIconAsync</c> — the
    ///   call the vanilla inventory panel makes, proven safe next to TextureReplacer / Asset Streaming
    ///   Overhaul. The old synchronous <c>ItemsDatabase.GetItemObject(id).GetIcon()</c> streamed a
    ///   texture on the main thread and crashed their sweep, which is why game icons used to be forced
    ///   off whenever such a mod was installed.
    /// - Copy the delivered Sprite into our own Texture2D (Blit + ReadPixels — the atlases aren't
    ///   CPU-readable). The game's sprite dies on a map change / streaming unload; our copy doesn't.
    /// - Save each copy as a PNG, so later sessions have the icon instantly with no request at all.
    ///
    /// Returns null until an icon arrives; the caller draws the bundled PNG meanwhile.
    /// </summary>
    internal static class Icons
    {
        private static readonly Dictionary<string, Texture2D> _baked = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Sprite> _pending = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _tries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _missingOnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Delegates handed to the game must outlive any in-flight native load: BOTH the managed
        // delegate and the converted one, forever — never cleared, not even on a scene change
        // (a collected half hard-crashes the game in the trampoline).
        private static readonly List<object> _keepAlive = new List<object>();

        private const int MaxTries = 3;   // requests per id per session — never hammer the loader
        private static float _nextPump;

        /// <summary>The icon for an item id as a texture we own, or null (not arrived / disabled).</summary>
        public static Texture2D ItemTex(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (!Plugin.C.UseGameIcons.Value) return null;

            if (_baked.TryGetValue(id, out var tex) && tex != null) return tex;

            var disk = LoadCached(id);
            if (disk != null) { _baked[id] = disk; return disk; }

            if (_pending.TryGetValue(id, out var sp))
            {
                _pending.Remove(id);
                var made = Bake(id, sp);
                if (made != null) { _baked[id] = made; SaveCached(id, made); return made; }
            }

            RequestAsync(id);
            return null;
        }

        /// <summary>Bake one delivered sprite per call, ticked from Effects so a sprite is copied
        /// within a fraction of a second of arriving — before a map change can unload it.</summary>
        public static void Pump()
        {
            if (_pending.Count == 0 || Time.unscaledTime < _nextPump) return;
            _nextPump = Time.unscaledTime + 0.2f;

            string id = null; Sprite sp = null;
            foreach (var kv in _pending) { id = kv.Key; sp = kv.Value; break; }
            if (id == null) return;
            _pending.Remove(id);

            var made = Bake(id, sp);
            if (made != null) { _baked[id] = made; SaveCached(id, made); }
        }

        private static void RequestAsync(string id)
        {
            _tries.TryGetValue(id, out int n);
            if (n >= MaxTries) return;
            _tries[id] = n + 1;

            try
            {
                // VirtualItem.Create builds the inventory-side item from its id — no prefab icon
                // fetch, no texture streaming. The loader does the streaming the safe way.
                var vi = VirtualItem.Create(id);
                if (vi == null) { Log($"[icon] '{id}': VirtualItem.Create returned null"); return; }

                var managed = new Action<Sprite>(s =>
                {
                    try { if (s != null) _pending[id] = s; } catch { }
                });
                var converted = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Sprite>>(managed);
                _keepAlive.Add(managed);
                _keepAlive.Add(converted);

                ResourcesManager.LoadItemIconAsync(vi, converted);
                Log($"[icon] '{id}': async request {n + 1}/{MaxTries}");
            }
            catch (Exception e) { Log($"[icon] '{id}' request failed: {e.GetType().Name} {e.Message}"); }
        }

        /// <summary>Copy a sprite (usually a sub-rect of an atlas) into a texture of our own.</summary>
        private static Texture2D Bake(string id, Sprite sp)
        {
            if (sp == null) return null;
            try
            {
                var src = sp.texture;
                if (src == null) return null;
                int tw = src.width, th = src.height;
                if (tw < 2 || th < 2) return null;   // dead / unloaded sprite — a later request retries

                // Sprite.textureRect can throw under Il2CppInterop: default to the whole texture and
                // narrow only when it reads back sane.
                float rx = 0f, ry = 0f, rw = tw, rh = th;
                try
                {
                    var tr = sp.textureRect;
                    if (tr.width >= 1f && tr.height >= 1f && tr.width <= tw && tr.height <= th)
                    { rx = tr.x; ry = tr.y; rw = tr.width; rh = tr.height; }
                }
                catch { }

                int w = Mathf.Clamp(Mathf.RoundToInt(rw), 1, 1024);
                int h = Mathf.Clamp(Mathf.RoundToInt(rh), 1, 1024);
                if (w < 2 || h < 2) return null;

                var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32);
                var prev = RenderTexture.active;
                try
                {
                    Graphics.Blit(src, rt);
                    RenderTexture.active = rt;
                    var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    outTex.ReadPixels(new Rect(rx, ry, w, h), 0, 0);
                    outTex.Apply();
                    outTex.hideFlags = HideFlags.HideAndDontSave;
                    outTex.wrapMode = TextureWrapMode.Clamp;
                    Log($"[icon] '{id}': baked {w}x{h} from {tw}x{th}");
                    return outTex;
                }
                finally
                {
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
            catch (Exception e) { Log($"[icon] '{id}' bake failed: {e.Message}"); return null; }
        }

        // ---- disk cache: <plugin>/icon_cache/<id>.png ----

        private static string CacheFile(string id)
        {
            var dir = Paths.PluginDir();
            if (dir == null) return null;
            var sb = new System.Text.StringBuilder(id.Length);
            foreach (var ch in id) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? char.ToLowerInvariant(ch) : '_');
            return System.IO.Path.Combine(dir, "icon_cache", sb + ".png");
        }

        private static Texture2D LoadCached(string id)
        {
            if (_missingOnDisk.Contains(id)) return null;
            try
            {
                var path = CacheFile(id);
                if (path == null || !System.IO.File.Exists(path)) { _missingOnDisk.Add(id); return null; }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path))) { _missingOnDisk.Add(id); return null; }
                tex.hideFlags = HideFlags.HideAndDontSave;
                tex.wrapMode = TextureWrapMode.Clamp;
                return tex;
            }
            catch { _missingOnDisk.Add(id); return null; }
        }

        private static void SaveCached(string id, Texture2D tex)
        {
            try
            {
                var path = CacheFile(id);
                if (path == null) return;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                var png = ImageConversion.EncodeToPNG(tex);
                if (png != null && png.Length > 0) { System.IO.File.WriteAllBytes(path, png); _missingOnDisk.Remove(id); }
            }
            catch (Exception e) { Log($"[icon] '{id}' cache write failed: {e.Message}"); }
        }

        private static void Log(string msg)
        {
            if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo(msg);
        }
    }
}
