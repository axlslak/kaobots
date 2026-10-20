using System.Collections.Generic;
using System.Linq;
using AOSharp.Common.GameData;
using AOSharp.Core;

namespace Radar
{
    internal sealed class ProfessionResolver
    {
        private const int RequestIntervalMilliseconds = 500;
        private const int RetryMilliseconds = 5000;
        private const int MaxAttempts = 3;
        private readonly Dictionary<Identity, PlayerInfo> _players = new Dictionary<Identity, PlayerInfo>();
        private readonly HashSet<Identity> _seen = new HashSet<Identity>();
        private long _nextRequest;

        public void BeginRefresh()
        {
            _seen.Clear();
        }

        public Profession Observe(Identity identity, Profession profession)
        {
            _seen.Add(identity);
            PlayerInfo info;
            if (!_players.TryGetValue(identity, out info))
            {
                info = new PlayerInfo();
                _players.Add(identity, info);
            }

            // Profession is the real stat, separate from an Agent's VisualProfession.
            if (IsPlayerProfession(profession))
                info.Profession = profession;

            return info.Profession;
        }

        public void EndRefresh(long now)
        {
            // Retain identities only while they are loaded in this vicinity.
            foreach (Identity identity in _players.Keys.Where(identity => !_seen.Contains(identity)).ToArray())
                _players.Remove(identity);

            if (now < _nextRequest)
                return;

            // Give everybody a first request before retrying unanswered requests.
            var candidate = _players
                .Where(player => player.Value.Profession == Profession.Unknown &&
                    player.Value.Attempts < MaxAttempts && now >= player.Value.NextAttempt)
                .OrderBy(player => player.Value.Attempts)
                .ThenBy(player => player.Value.NextAttempt)
                .FirstOrDefault();
            if (candidate.Value == null)
                return;

            candidate.Value.Attempts++;
            candidate.Value.NextAttempt = now + RetryMilliseconds;
            _nextRequest = now + RequestIntervalMilliseconds;

            // Native info replies populate the stat read by Observe on later updates.
            // Requesting by identity neither changes the target nor opens an Info window.
            CharacterAction.InfoRequest(candidate.Key);
        }

        public void Clear()
        {
            _players.Clear();
            _seen.Clear();
            _nextRequest = 0;
        }

        private static bool IsPlayerProfession(Profession profession)
        {
            return (profession >= Profession.Soldier && profession <= Profession.Metaphysicist) ||
                profession == Profession.Keeper || profession == Profession.Shade;
        }

        private sealed class PlayerInfo
        {
            public Profession Profession = Profession.Unknown;
            public int Attempts;
            public long NextAttempt;
        }
    }
}
