using AOSharp.Common.GameData;
using AOSharp.Core.IPC;
using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

namespace ProfessionHandler.Keeper
{
    [AoContract((int)IPCOpcode.RootSnare)]
    public class RootSnareMessage : IPCMessage
    {
        public override short Opcode => (short)IPCOpcode.RootSnare;
        [AoMember(0)] public Identity Target { get; set; }
    }
    [AoContract(2350)]
    public class TargetControlMessage : IPCMessage, ProfessionHandler.Generic.ITargetControlRequest
    {
        public override short Opcode => 2350;
        [AoMember(0)] public Identity Target { get; set; }
        [AoMember(1)] public int Action { get; set; }
    }
}
