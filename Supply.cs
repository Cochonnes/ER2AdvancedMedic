using System;

namespace AdvancedMedic
{
    /// <summary>
    /// Optional spawn supply-floor: top the player up to a minimum medical kit. Gated on
    /// InjectSpawnKit AND configured item ids (blank id ⇒ skipped, since we can't add an
    /// unknown item). Counts and adds through the Inventory (Items.CountOf / GiveOne): the Lua
    /// countItems it used to ask always answers 0 here, so it topped up on every life whatever you
    /// carried. Runs once per player instance.
    /// </summary>
    internal static class Supply
    {
        private static IntPtr _doneFor;

        /// <summary>Session flush: a new mission's player may reuse the old pointer.</summary>
        public static void Reset() { _doneFor = IntPtr.Zero; }

        public static void EnsurePlayerKit(Soldier player)
        {
            var c = Plugin.C;
            if (!c.InjectSpawnKit.Value || player == null) return;

            var p = Interop.PtrOf(player);
            if (p == IntPtr.Zero || p == _doneFor) return;
            _doneFor = p;

            TopUp(player, c.IdBandage.Value, c.MinBandages.Value);
            TopUp(player, c.IdAspirin.Value, c.MinAspirin.Value);
        }

        private static void TopUp(Soldier s, string id, int min)
        {
            if (string.IsNullOrEmpty(id) || min <= 0) return;
            try
            {
                int have = Items.CountOf(s, id);
                for (int i = have; i < min; i++) Items.GiveOne(s, id);
                if (have < min && Plugin.C.DebugLogging.Value) Plugin.L.LogInfo($"[supply] topped {id} from {have} to {min}");
            }
            catch (Exception e) { Guard.Once("supply", e); }
        }
    }
}
