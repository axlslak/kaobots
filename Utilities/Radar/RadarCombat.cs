using System.Collections.Generic;
using AOSharp.Common.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace Radar
{
    // Received directed activity, never attack-state polling or guessed fire timers.
    // The SDK dispatches N3MessageReceived on the engine update thread.
    internal sealed class RadarCombat
    {
        public const int DotCount = 3, DotSpacingMilliseconds = 55, TravelMilliseconds = 420;
        public const int LifetimeMilliseconds = 600;
        private const int Capacity = 128;
        private readonly Queue<CombatPulse> _events = new Queue<CombatPulse>(Capacity);
        public int Count => _events.Count;
        public IEnumerable<CombatPulse> Events => _events;

        public void Clear() => _events.Clear();

        public void Expire(long now)
        {
            while (_events.Count > 0 &&
                (now < _events.Peek().Started || now - _events.Peek().Started >= LifetimeMilliseconds))
                _events.Dequeue();
        }

        public void Observe(N3Message message, long now)
        {
            Identity source, target;
            if (message is AttackInfoMessage attack)
            {
                source = attack.Identity; target = attack.Target;
            }
            else if (message is SpecialAttackInfoMessage special)
            {
                source = special.Identity; target = special.Target;
            }
            else if (message is MissedAttackInfoMessage miss)
            {
                source = miss.Attacker; target = miss.Defender;
            }
            else if (message is HealthDamageMessage health)
            {
                // Covers damage and healing; Identity is the affected recipient.
                source = health.Target; target = health.Identity;
            }
            else if (message is ReflectAttackMessage reflect)
            {
                source = reflect.Target; target = reflect.Identity;
            }
            else if (message is ShieldAttackMessage shield)
            {
                source = shield.Target; target = shield.Identity;
            }
            else if (message is AttackMessage started)
            {
                source = started.Identity; target = started.Target;
            }
            else if (message is CharSecSpecAttackMessage queued)
            {
                source = queued.Identity; target = queued.Target;
            }
            else if (message is CastNanoSpellMessage cast)
            {
                // Native receipt starts casting on the header actor. The body
                // Caster/source field has unresolved semantics when it differs.
                source = cast.Identity; target = cast.Target;
            }
            else if (message is CharacterActionMessage action)
            {
                if (!ActionActors(action, out source, out target)) return;
            }
            else return;

            // All identity kinds can participate, but rendering requires BOTH
            // identities to resolve to valid loaded dynels. Keep self-targets.
            if (source.Type == IdentityType.None || target.Type == IdentityType.None)
                return;
            Expire(now);
            if (_events.Count == Capacity) _events.Dequeue();
            _events.Enqueue(new CombatPulse(source, target, now));
        }

        private static bool ActionActors(CharacterActionMessage action, out Identity source, out Identity target)
        {
            source = target = Identity.None;
            // Wire field roles are action-specific: Target can encode a nano
            // template or even an amount rather than another world entity.
            // Native reference: aorust docs/zone/actions.md and play/chat/log.rs.
            switch ((int)action.Action)
            {
                case 0x62: // Add/restore nano effect (SetNanoDuration).
                case 0xB1: // Refresh nano effect; same caster-instance encoding.
                    if (action.Target.Type != IdentityType.NanoProgram || action.Parameter1 == 0) return false;
                    source = new Identity(IdentityType.SimpleChar, action.Parameter1);
                    target = action.Identity;
                    return true;
                case 0x81: // Received health.
                case 0x8A: // Received nano.
                case 0x9B: // Detected by another character.
                case 0x9C: // Drained by another character.
                    source = action.Target; target = action.Identity;
                    return true;
                case 0x82: // Gave health.
                case 0x8B: // Gave nano.
                    source = action.Identity; target = action.Target;
                    return true;
                case 0xD0: // Drain result: Target.Type is amount, Instance is drainer.
                    if (action.Target.Instance == 0) return false;
                    source = new Identity(IdentityType.SimpleChar, action.Target.Instance);
                    target = action.Identity;
                    return true;
                default:
                    // Other action codes do not supply a verified actor pair.
                    // Never infer one from an overloaded Target field.
                    return false;
            }
        }
    }

    internal struct CombatPulse
    {
        public readonly Identity Source, Target;
        public readonly long Started;
        public CombatPulse(Identity source, Identity target, long started)
        {
            Source = source; Target = target; Started = started;
        }
    }
}
