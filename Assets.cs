using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AdvancedMedic
{
    /// <summary>
    /// Loads and caches the medical GUI PNGs (body masks + fallback item icons) from the deployed
    /// plugin's assets folder (the csproj Deploy step copies them there).
    /// </summary>
    internal static class Assets
    {
        private static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();
        private static string[] _roots;

        private static string[] Roots()
        {
            if (_roots != null) return _roots;
            var list = new List<string>();
            var pd = Paths.PluginDir();
            if (!string.IsNullOrEmpty(pd)) list.Add(Path.Combine(pd, "assets"));
            _roots = list.ToArray();
            return _roots;
        }

        // Body textures by BASE name. The overlay and the hit peek ask for ~20 of them per drawn frame, and
        // the relative path used to be concatenated ("body_image/" + name + ".png") on every ask just to
        // look it up; the path is now built once per name, on the first miss.
        private static readonly Dictionary<string, Texture2D> _body = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        /// <summary>Body mask/background by base name, e.g. Body("head") or Body("arm_left_s").</summary>
        public static Texture2D Body(string name)
        {
            if (name == null) return null;
            if (_body.TryGetValue(name, out var tex)) return tex;
            tex = Load("body_image/" + name + ".png");
            _body[name] = tex;   // null cached too (Load already never retries disk)
            return tex;
        }

        public static Texture2D Load(string rel)
        {
            if (_cache.TryGetValue(rel, out var cached)) return cached;

            Texture2D tex = null;
            foreach (var root in Roots())
            {
                try
                {
                    var path = Path.Combine(root, rel);
                    if (!File.Exists(path)) continue;
                    var bytes = File.ReadAllBytes(path);
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                    if (ImageConversion.LoadImage(t, bytes) && t.width > 2)
                    {
                        t.wrapMode = TextureWrapMode.Clamp;
                        tex = t;
                        if (Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"asset LOADED {rel} ({t.width}x{t.height}) from {root}");
                        break;
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning($"asset load {rel}: {e.GetType().Name} {e.Message}"); }
            }

            if (tex == null && Plugin.C.DebugLogging.Value) Plugin.L.LogWarning($"asset MISSING {rel}");
            _cache[rel] = tex;   // cache null too, so we don't retry disk every frame
            return tex;
        }
    }
}
