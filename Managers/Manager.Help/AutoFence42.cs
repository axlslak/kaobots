using System;
using System.Linq;
using AOSharp.Common.GameData;
using AOSharp.Core;
using AOSharp.Core.Inventory;
using AOSharp.Core.UI;

namespace ManagerHelp
{
    public partial class ManagerHelp
    {
        private static readonly int[] FenceProtectionBuffs = { 257172, 277991 };
        private const float FenceInteractionRange = 5f;
        private double _nextFenceCheck;
        private double _nextFenceUse;
        private bool _fenceNcuWarning;

        private void UpdateAutoFence42()
        {
            if (!_settings["AutoFence42"].AsBool() || Game.IsZoning)
            {
                _nextFenceCheck = 0;
                _nextFenceUse = 0;
                _fenceNcuWarning = false;
                return;
            }

            double now = Time.AONormalTime;
            if (now < _nextFenceCheck) return;
            _nextFenceCheck = now + 0.25;

            var player = DynelManager.LocalPlayer;
            if (player == null || !player.IsValid || !player.IsAlive) return;

            // A use request is not proof of protection. Only the local buff is.
            if (player.Buffs.Contains(FenceProtectionBuffs))
            {
                _fenceNcuWarning = false;
                return;
            }

            // Resolve afresh each time: never keep using a stale object after
            // despawn, zoning or moving away. Do not navigate towards a fence.
            var transceiver = DynelManager.AllDynels
                .Where(d => d != null && d.IsValid &&
                    string.Equals(d.Name, "Biological Transceiver", StringComparison.OrdinalIgnoreCase) &&
                    player.DistanceFrom(d) <= FenceInteractionRange)
                .OrderBy(d => player.DistanceFrom(d))
                .FirstOrDefault();

            if (transceiver == null)
            {
                _fenceNcuWarning = false;
                return;
            }

            int freeNcu = player.GetStat(Stat.MaxNCU) - player.GetStat(Stat.CurrentNCU);
            if (freeNcu < 10)
            {
                if (!_fenceNcuWarning)
                {
                    Chat.WriteLine($"42 Autofence: {player.Name} needs 10 free NCU ({freeNcu} available). Fence protection is missing.");
                    _fenceNcuWarning = true;
                }
                return;
            }
            _fenceNcuWarning = false;

            // Being busy only delays this character's next attempt. There is
            // no retry limit and no assumption that another client's use worked.
            if (now < _nextFenceUse || Spell.HasPendingCast || Item.HasPendingUse) return;

            _nextFenceUse = now + 2;
            transceiver.Use();
        }
    }
}
