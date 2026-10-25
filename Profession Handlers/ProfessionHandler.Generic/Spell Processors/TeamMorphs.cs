using System;
using System.Linq;
using AOSharp.Common.GameData;
using AOSharp.Core;

namespace ProfessionHandler.Generic
{
    public partial class GenericProfessionHandler
    {
        private double _nextTeamMorphCheck;

        // Auno's Adventurer catalogue retains these legacy Team/Other variants:
        // https://auno.org/ao/nanos.php?prof=adventurer
        // Match learned names, not inferred AOIDs or a blanket Polymorph nanoline.
        private sealed class TeamMorphForm
        {
            public readonly int[] SelfIds;
            public readonly string[] Names;
            public readonly bool HasTeamVariant;

            public TeamMorphForm(int[] selfIds, bool hasTeamVariant, params string[] names)
            {
                SelfIds = selfIds;
                Names = names; // Strongest legacy form first.
                HasTeamVariant = hasTeamVariant;
            }

            public bool Matches(Buff buff)
            {
                return SelfIds.Contains(buff.Id) || Names.Any(name =>
                    string.Equals(buff.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(buff.Name, name + " (Other)", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(buff.Name, name + " (Team)", StringComparison.OrdinalIgnoreCase));
            }

            public Spell LearnedVariant(string suffix)
            {
                foreach (string name in Names)
                {
                    Spell spell = Spell.List.FirstOrDefault(s =>
                        string.Equals(s.Name, name + suffix, StringComparison.OrdinalIgnoreCase));
                    if (spell != null)
                        return spell;
                }
                return null;
            }
        }

        private static readonly TeamMorphForm TeamDragonMorph = new TeamMorphForm(
            SpellID.DragonMorph, true, "Calia's Form: Pit Lizard");
        private static readonly TeamMorphForm TeamSaberMorph = new TeamMorphForm(
            SpellID.SaberMorph, true, "Calia's Form: Sabretooth", "Grinning Hunter");
        private static readonly TeamMorphForm TeamWolfMorph = new TeamMorphForm(
            SpellID.WolfMorph, true, "Calia's Form: Wolf", "Relation to Cerberus", "Playful Cub");
        private static readonly TeamMorphForm TeamLeetMorph = new TeamMorphForm(
            SpellID.LeetMorph, false, "Pronouncement of Greatness");

        private bool MaintainTeamMorph()
        {
            var player = DynelManager.LocalPlayer;
            if (player.Profession != Profession.Adventurer || !Team.IsInTeam ||
                !_settings["Buffing"].AsBool() || InCombat() || Now < _nextTeamMorphCheck)
                return false;

            _nextTeamMorphCheck = Now + 1.0;
            TeamMorphForm form;
            switch (_settings["MorphSelection"].AsInt32())
            {
                case 1: form = TeamDragonMorph; break;
                case 2: form = TeamSaberMorph; break;
                case 3: form = TeamWolfMorph; break;
                case 4: form = TeamLeetMorph; break;
                // None/Tree have no corresponding teammate morph to maintain.
                default: return false;
            }

            // A learned Team variant reserves this family; transient cast failures
            // must not alternate between Team and Other on subsequent updates.
            Spell spell = form.HasTeamVariant ? form.LearnedVariant(" (Team)") : null;
            bool teamCast = spell != null;
            if (spell == null)
                spell = form.LearnedVariant(" (Other)");
            if (spell == null || !CanCast(spell))
                return false;

            var members = Team.Members.ToArray();
            var localMember = members.FirstOrDefault(m => m.Identity == player.Identity);
            if (Team.IsRaid && localMember == null)
                return false;

            foreach (var member in members)
            {
                var target = member.Character;
                if (target == null || member.Identity == player.Identity ||
                    (Team.IsRaid && member.TeamIndex != localMember.TeamIndex) ||
                    !target.IsAlive || !target.IsInLineOfSight || !InCastRange(spell, target))
                    continue;

                var buffs = target.Buffs.ToArray();
                if (buffs.Any(b => form.Matches(b) && b.RemainingTime > 20f))
                    continue;
                // Do not repeatedly attempt to overwrite a stronger morph.
                if (buffs.Any(b => b.Nanoline == NanoLine.Polymorph && b.StackingOrder > spell.StackingOrder))
                    continue;

                var replacing = buffs.FirstOrDefault(form.Matches);
                int freeNCU;
                // Remote characters report free NCU over IPC; SimpleChar has no RemainingNCU property.
                if (!RemainingNCU.TryGetValue(target.Identity, out freeNCU))
                    continue;
                if (freeNCU + (replacing?.NCU ?? 0) < spell.NCU)
                    continue;

                spell.Cast(teamCast ? player : target, true);
                // Allow the cast/buff update to arrive before scanning for another attempt.
                _nextTeamMorphCheck = Now + 3.0;
                return true;
            }
            return false;
        }
    }
}
