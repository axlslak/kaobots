using System.Collections.Generic;
using AOSharp.Common.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace Radar
{
    // Received combat results, never attack-state polling or guessed fire timers.
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
            else if (message is HealthDamageMessage health && health.Amount < 0)
            {
                // HealthDamage reverses the usual fields: Identity is the victim.
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
            else return;

            if (source.Type != IdentityType.SimpleChar || target.Type != IdentityType.SimpleChar ||
                source.Instance == 0 || target.Instance == 0 || source == target)
                return;
            Expire(now);
            if (_events.Count == Capacity) _events.Dequeue();
            _events.Enqueue(new CombatPulse(source, target, now));
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
