using System.Collections.Generic;
using System.Linq;
using AOSharp.Common.GameData;
using AOSharp.Core;

namespace Radar
{
    // Managed roster values only. Membership never supplies a world position.
    internal sealed class RadarRoster
    {
        private readonly Dictionary<Identity, Member> _members = new Dictionary<Identity, Member>();
        private int _localTeam = -1;
        public bool HasTeam { get; private set; }
        public bool HasRaid { get; private set; }

        public void Capture(Identity localIdentity)
        {
            Clear();
            HasTeam = Team.IsInTeam;
            if (!HasTeam)
                return;
            HasRaid = Team.IsRaid;
            foreach (TeamMember member in Team.Members)
            {
                if (member.Identity.Type != IdentityType.SimpleChar ||
                    member.TeamIndex < 0 || member.TeamIndex >= 6)
                    continue;
                _members[member.Identity] = new Member(member.Profession, member.Level, member.TeamIndex);
                if (member.Identity == localIdentity)
                    _localTeam = member.TeamIndex;
            }
            // The ordinary team always uses roster0. In a raid, wait for the
            // local entry rather than guessing which of the six teams is ours.
            if (!HasRaid)
                _localTeam = 0;
        }

        public bool InTeam(Identity identity)
        {
            Member member;
            return HasTeam && _localTeam >= 0 && _members.TryGetValue(identity, out member) &&
                member.TeamIndex == _localTeam;
        }

        public bool InRaid(Identity identity) => HasRaid && _members.ContainsKey(identity);

        public int GroupRank(Identity identity) => InTeam(identity) ? 0 : InRaid(identity) ? 1 : 2;

        public int GroupIndex(Identity identity)
        {
            Member member;
            return HasRaid && _members.TryGetValue(identity, out member) ? member.TeamIndex : int.MaxValue;
        }

        public Profession ProfessionOf(Identity identity)
        {
            Member member;
            return _members.TryGetValue(identity, out member) &&
                ProfessionResolver.IsPlayerProfession(member.Profession) ? member.Profession : Profession.Unknown;
        }

        public int LevelOf(Identity identity)
        {
            Member member;
            return _members.TryGetValue(identity, out member) && member.Level >= 1 && member.Level <= 220 ?
                member.Level : -1;
        }

        public IEnumerable<Identity> TeamIdentities => _members.Keys.Where(InTeam);
        public IEnumerable<Identity> RaidIdentities => _members.Keys.Where(InRaid);

        public void Clear()
        {
            _members.Clear();
            _localTeam = -1;
            HasTeam = HasRaid = false;
        }

        private sealed class Member
        {
            public readonly Profession Profession;
            public readonly int Level;
            public readonly int TeamIndex;
            public Member(Profession profession, int level, int teamIndex)
            {
                Profession = profession;
                Level = level;
                TeamIndex = teamIndex;
            }
        }
    }
}
