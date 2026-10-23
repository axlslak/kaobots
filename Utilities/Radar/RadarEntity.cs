using System.Globalization;
using AOSharp.Common.GameData;
using AOSharp.Core;

namespace Radar
{
    internal enum RadarEntityKind { Player, Npc, Other, Pet, Tower }

    // A display snapshot, never a retained native dynel wrapper.
    internal sealed class RadarEntity
    {
        private const int UnsetStat = 1234567890;
        public readonly Identity Identity;
        public readonly RadarEntityKind Kind;
        public readonly string Name;
        public readonly Profession Profession;
        public readonly int Level;
        public readonly int AILevel;
        private readonly string _health;

        public string Label
        {
            get
            {
                if (Kind == RadarEntityKind.Player)
                    return $"{Name} ({Format(Level)}/{Format(AILevel)})";

                string level = Level < 0 ? "" : Kind == RadarEntityKind.Other ?
                    $" (Lv/QL {Format(Level)})" : $" ({Format(Level)})";
                return Name + level;
            }
        }

        public string HealthLabel => _health == null ? "" : " HP " + _health;

        public RadarEntity(Dynel dynel, RadarEntityKind kind, Profession profession, bool details,
            int rosterLevel = -1)
        {
            Identity = dynel.Identity;
            Kind = kind;
            Profession = profession;
            Level = -1;
            AILevel = -1;
            if (!details)
                return;

            string name = dynel.Name;
            Name = string.IsNullOrWhiteSpace(name) ? Identity.Type + " " + Identity.Instance : name;
            int level = kind == RadarEntityKind.Player && rosterLevel >= 1 && rosterLevel <= 220 ?
                rosterLevel : dynel.GetStat(Stat.Level);
            if (kind == RadarEntityKind.Player)
            {
                Level = level >= 1 && level <= 220 ? level : -1;
                int aiLevel = dynel.GetStat(Stat.AlienLevel);
                AILevel = aiLevel >= 0 && aiLevel <= 30 ? aiLevel : -1;
            }
            else
            {
                // NPC levels can exceed 220; object Level can be template QL.
                Level = level > 0 && level < UnsetStat ? level : -1;
                _health = ReadHealth(dynel);
            }
        }

        public static RadarEntityKind KindOf(Dynel dynel)
        {
            // IsPlayer reads character-specific memory. Never call it on an
            // arbitrary door, terminal, corpse or other non-character dynel.
            if (dynel.Identity.Type != IdentityType.SimpleChar)
                return RadarEntityKind.Other;
            var character = new SimpleChar(dynel);
            if (character.IsPlayer)
                return RadarEntityKind.Player;
            // Use the engine's positive pet flag, even if its owner is not
            // currently loaded. PetOwnerId alone does not establish ownership.
            if (character.IsPet)
                return RadarEntityKind.Pet;
            return character.Flags.HasFlag(CharacterFlags.Tower) ? RadarEntityKind.Tower : RadarEntityKind.Npc;
        }

        public static string ReadHealth(Dynel dynel)
        {
            int current = dynel.GetStat(Stat.Health);
            int maximum = dynel.GetStat(Stat.MaxHealth);
            int minimum = dynel.Identity.Type == IdentityType.SimpleChar ? 0 : 1;
            // Omit missing/sentinel/inconsistent pools, including trivial object
            // defaults. Positive cached data still does not establish freshness.
            if (current < 0 || current >= UnsetStat || maximum <= minimum ||
                maximum >= UnsetStat || current > maximum)
                return null;
            return Format(current) + " / " + Format(maximum);
        }

        private static string Format(int value)
        {
            return value < 0 ? "?" : value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
