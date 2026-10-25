using System;
using System.Linq;
using AOSharp.Common.GameData;
using AOSharp.Core;
using AOSharp.Core.IPC;
using AOSharp.Core.UI;
using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

namespace ProfessionHandler.Generic
{
    public interface ITargetControlRequest
    {
        Identity Target { get; }
        int Action { get; }
    }

    public partial class GenericProfessionHandler
    {
        private enum TargetControlAction { Root, Snare, Calm }

        [AoContract(2350)]
        private class TargetControlMessage : IPCMessage, ITargetControlRequest
        {
            public override short Opcode => 2350;
            [AoMember(0)] public Identity Target { get; set; }
            [AoMember(1)] public int Action { get; set; }
        }

        private void RegisterGroupCommands()
        {
            IPCChannel.RegisterCallback(2350, ReceiveTargetControl);
            Chat.RegisterCommand("root", (c, p, w) => SendTargetControl(TargetControlAction.Root));
            Chat.RegisterCommand("snare", (c, p, w) => SendTargetControl(TargetControlAction.Snare));
            Chat.RegisterCommand("calm", (c, p, w) => SendTargetControl(TargetControlAction.Calm));
            Chat.RegisterCommand("petcom", (c, p, w) =>
            {
                if (!GroupPetCommand(c, p, w))
                    Chat.WriteLine("/petcom wait | follow | attack | warp");
            });

            // Older Core DLLs still load this handler; only native /pet interception needs the new API.
            var registerOverride = typeof(Chat).GetMethod("RegisterCommandOverride",
                new[] { typeof(string), typeof(Func<string, string[], ChatWindow, bool>) });
            if (registerOverride != null)
                registerOverride.Invoke(null, new object[] { "pet", new Func<string, string[], ChatWindow, bool>(GroupPetCommand) });
            else
                Chat.WriteLine("Group /pet commands require AOSharp 2.7.28 or newer. Use /petcom on this version.");
        }

        private void SendTargetControl(TargetControlAction action)
        {
            try
            {
                var target = Targeting.Target as SimpleChar;
                if (target == null || !target.IsAlive)
                {
                    Chat.WriteLine("Select a living target first.");
                    return;
                }
                IPCChannel.Broadcast(new TargetControlMessage { Target = target.Identity, Action = (int)action });
                CastTargetControl(target.Identity, action);
            }
            catch (Exception ex) { ErrorCatch(ex); }
        }

        private void ReceiveTargetControl(int sender, IPCMessage message)
        {
            if (!(message is ITargetControlRequest request) || sender == Game.ClientInst) return;
            if (request.Action < (int)TargetControlAction.Root || request.Action > (int)TargetControlAction.Calm) return;
            try { CastTargetControl(request.Target, (TargetControlAction)request.Action); }
            catch (Exception ex) { ErrorCatch(ex); }
        }

        private void CastTargetControl(Identity identity, TargetControlAction action)
        {
            if (Game.IsZoning) return;
            var target = DynelManager.Characters.FirstOrDefault(c => c.Identity == identity);
            if (target == null || !target.IsAlive || !target.IsInLineOfSight) return;
            var line = action == TargetControlAction.Root ? NanoLine.Root
                : action == TargetControlAction.Snare ? NanoLine.Snare : NanoLine.Mezz;
            var spell = Spell.List.Where(s => s.Nanoline == line)
                .OrderByStackingOrder()
                .FirstOrDefault(s => s.IsReady && s.MeetsSelfUseReqs() && InCastRange(s, target));
            spell?.Cast(target, true);
        }

        private bool GroupPetCommand(string command, string[] parameters, ChatWindow window)
        {
            // Leave named-pet commands, quotes and every unsupported verb to AO unchanged.
            if (parameters.Length != 1) return false;
            switch (parameters[0].ToLowerInvariant())
            {
                case "wait": PetWaitClicked(null, null); return true;
                case "follow": PetFollowClicked(null, null); return true;
                case "attack": PetAttackClicked(null, null); return true;
                case "warp": PetWarpClicked(null, null); return true;
                default: return false;
            }
        }
    }
}
