using System;

namespace AdvancedMedic
{
    /// <summary>
    /// Resolves the display name for the header. In a multiplayer room a player-owned soldier
    /// carries a PhotonView whose Owner has the player's lobby nickname; otherwise we fall back to
    /// the in-game (generated) character name.
    /// </summary>
    internal static class Names
    {
        public static string Display(Soldier s)
        {
            if (s == null) return "—";
            var nick = MpNick(s);
            if (!string.IsNullOrEmpty(nick)) return nick;
            return CharName(s);
        }

        private static string MpNick(Soldier s)
        {
            try
            {
                if (!Photon.Pun.PhotonNetwork.InRoom) return null;
                // AI soldiers are owned by the host's client, so their PhotonView.Owner.NickName is
                // the HOST's name — never the AI's. Only use the lobby nick for real player avatars.
                // (IsAI() alone is wrong on a client, where host-run AI read false; see IsOtherHuman.)
                if (Interop.IsPlayer(s)) return Photon.Pun.PhotonNetwork.NickName;
                if (!Interop.IsOtherHuman(s)) return null;
                var pv = s.GetComponent<Photon.Pun.PhotonView>();
                if (pv == null) return null;
                var owner = pv.Owner;
                if (owner == null) return null;              // scene object → no player
                return owner.NickName;
            }
            catch { return null; }
        }

        private static string CharName(Soldier s)
        {
            try { var l = Items.Lua(s); var n = l != null ? l.getName() : null; if (!string.IsNullOrEmpty(n)) return n; }
            catch { }
            return "Soldier";
        }
    }
}
