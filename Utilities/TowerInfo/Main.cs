using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AOSharp.Common.GameData;
using AOSharp.Core;
using AOSharp.Core.UI;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace TowerInfoUtility
{
    public sealed class TowerInfoPlugin : AOPluginEntry
    {
        private const string Version = "1.0.0";
        private const int MaxTowers = 4096;
        private readonly Dictionary<Identity, TowerRecord> _towers = new Dictionary<Identity, TowerRecord>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Identity _playfield = Identity.None;
        private Identity _pending = Identity.None;
        private long _deadline;
        private long _nextRequest;
        private bool _hasSnapshot;
        private bool _disposed;

        public override void Run()
        {
            Chat.RegisterCommand("tower", Command);
            Network.N3MessageReceived += OnMessage;
            Game.TeleportStarted += OnTeleportStarted;
            Game.OnUpdate += OnUpdate;
            Chat.WriteLine("Tower Info " + Version + " loaded. /tower info (selected tower, otherwise nearest known tower); /tower list");
        }

        private void Command(string command, string[] args, ChatWindow window)
        {
            if (_disposed) return;
            try
            {
                args = args.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
                if (args.Length > 1 || (args.Length == 1 && args[0] != "info" && args[0] != "list"))
                {
                    Chat.WriteLine("/tower info - inspect selected/nearest known tower; /tower list - nearby known towers.");
                    return;
                }
                if (Game.IsZoning || DynelManager.LocalPlayer == null)
                {
                    Chat.WriteLine("Tower Info: wait until zoning finishes.");
                    return;
                }
                EnsureScope();
                ObserveLoadedTowers();
                var player = DynelManager.LocalPlayer;
                Chat.WriteLine(string.Format("Tower Info {0}: {1} (PF {2}), your position {3:0.0} x {4:0.0}",
                    Version, Playfield.Name, Playfield.ModelIdentity.Instance, player.Position.X, player.Position.Z));
                if (args.Length == 1 && args[0] == "list")
                {
                    var list = _towers.Values.Where(t => t.HasPosition).OrderBy(t => Distance(t.Position, player.Position)).Take(20).ToArray();
                    Chat.WriteLine(string.Format("Known towers: {0}. {1} Showing nearest {2}.", _towers.Count,
                        _hasSnapshot ? "Playfield tower snapshot received this visit; not a site boundary map." : "Partial view only; zone with this plugin loaded to receive the playfield snapshot.", list.Length));
                    foreach (var t in list)
                        Chat.WriteLine(string.Format("{0} | {1} | {2} | {3:0}m | {4:0} x {5:0}",
                            t.Name ?? t.Id.ToString(), ClassName(t), t.Side.HasValue ? t.Side.ToString() : "side unknown",
                            Distance(t.Position, player.Position), t.Position.X, t.Position.Z));
                    return;
                }

                var selected = Targeting.Target as SimpleChar;
                TowerRecord record;
                bool selectedTower = selected != null && _towers.TryGetValue(selected.Identity, out record);
                if (selectedTower)
                    record = _towers[selected.Identity];
                else
                    record = _towers.Values.Where(t => t.HasPosition)
                        .OrderBy(t => Distance(t.Position, player.Position)).FirstOrDefault();
                if (record == null)
                {
                    Chat.WriteLine("No tower is known here. This does not prove that this site is empty or plantable.");
                    EmptySiteLimits();
                    return;
                }
                Chat.WriteLine(selectedTower ? "Selected tower:" : "Nearest known tower (not proof that it belongs to the site you are standing in):");
                ShowRecord(record, player.Position);
                if (record.Info != null)
                    ShowInfo(record, true);
                if (_clock.ElapsedMilliseconds < _nextRequest)
                {
                    Chat.WriteLine("Info request throttled; wait two seconds before requesting again.");
                    return;
                }
                _pending = record.Id;
                _deadline = _clock.ElapsedMilliseconds + 8000;
                _nextRequest = _clock.ElapsedMilliseconds + 2000;
                CharacterAction.InfoRequest(record.Id);
                Chat.WriteLine("Requesting fresh tower details from the server; target selection is unchanged.");
                EmptySiteLimits();
            }
            catch (Exception ex) { Chat.WriteLine("Tower Info: " + ex.Message); }
        }

        private static string ClassName(TowerRecord record)
        {
            return record.Class.HasValue ? record.Class.Value.ToString() : "tower class not received";
        }

        private static float Distance(Vector3 a, Vector3 b)
        {
            float x = a.X - b.X, z = a.Z - b.Z;
            return (float)Math.Sqrt(x * x + z * z);
        }

        private static void EmptySiteLimits()
        {
            Chat.WriteLine("Site peace/war state and exact planting time are not exposed by these tower replies. For an empty site, use the native Controller Build Check; no countdown is inferred.");
        }

        private void ShowRecord(TowerRecord record, Vector3 position)
        {
            Chat.WriteLine(string.Format("{0} | {1} | identity {2}", record.Name ?? "Tower", ClassName(record), record.Id));
            if (record.HasPosition)
                Chat.WriteLine(string.Format("Position {0:0.0} x {1:0.0}, {2:0.0}m away; side {3}", record.Position.X,
                    record.Position.Z, Distance(record.Position, position), record.Side.HasValue ? record.Side.ToString() : "unknown"));
            if (record.Level.HasValue)
                Chat.WriteLine("Loaded tower level: " + record.Level.Value);
            Chat.WriteLine("Tower location/class is not a verified site number, name, boundary or site level range.");
        }

        private void ShowInfo(TowerRecord record, bool cached)
        {
            var info = record.Info;
            Chat.WriteLine(string.Format("{0} tower reply for {1} ({2:0.0}s old)", cached ? "Cached" : "Fresh",
                record.Name ?? record.Id.ToString(), (_clock.ElapsedMilliseconds - record.InfoAt) / 1000.0));
            Chat.WriteLine(string.Format("QL {0}; owner organization ID {1}; health {2}/{3}; tower item IDs {4}/{5}",
                info.TowerQuality, info.OrganizationId, info.Health, info.MaxHealth, info.TowerLowId, info.TowerHighId));
            if (record.IsControlReply)
            {
                Chat.WriteLine("Control-tower timer field: " + (info.Timer.HasValue ? info.Timer.Value.ToString() : "not supplied") +
                    "; next gas field: " + (info.NextSuppressionGas.HasValue ? info.NextSuppressionGas.Value.ToString() : "not supplied"));
                Chat.WriteLine("Timer/gas values above are raw server fields; units/encoding are unverified. They are not an empty-site planting countdown.");
            }
            else
                Chat.WriteLine("This is a support tower reply; select the Control Tower for its timer/gas fields.");
        }

        private void ObserveLoadedTowers()
        {
            foreach (var tower in DynelManager.Characters.Where(c => c.IsAlive && c.Flags.HasFlag(CharacterFlags.Tower)))
            {
                var record = GetOrAdd(tower.Identity);
                if (record == null) continue;
                record.Name = tower.Name;
                record.Position = tower.Position;
                record.HasPosition = true;
                record.Side = tower.Side;
                record.Level = tower.Level;
            }
        }

        private TowerRecord GetOrAdd(Identity id)
        {
            if (id == Identity.None) return null;
            TowerRecord record;
            if (_towers.TryGetValue(id, out record)) return record;
            if (_towers.Count >= MaxTowers) return null;
            record = new TowerRecord { Id = id };
            _towers.Add(id, record);
            return record;
        }

        private void Observe(TowerInfo tower)
        {
            if (tower == null) return;
            var record = GetOrAdd(tower.TowerCharId);
            if (record == null) return;
            record.Placeholder = tower.PlaceholderId;
            record.Position = tower.Position;
            record.HasPosition = true;
            record.Side = tower.Side;
            record.Class = tower.Class;
        }

        private void OnMessage(object sender, N3Message message)
        {
            if (_disposed) return;
            // Ignore unrelated traffic; no per-frame tower enumeration or native proxy memory reads.
            if (!(message is PlayfieldAllTowersMessage) && !(message is PlayfieldTowerUpdateClientMessage) &&
                !(message is InfoPacketMessage)) return;
            var incomingInfo = message as InfoPacketMessage;
            if (incomingInfo != null && !(incomingInfo.Info is TowerInfoPacket)) return;
            try
            {
                EnsureScope();
                var all = message as PlayfieldAllTowersMessage;
                if (all != null)
                {
                    _towers.Clear();
                    _hasSnapshot = true;
                    foreach (var tower in (all.TowerInfo ?? new TowerInfo[0]).Take(MaxTowers)) Observe(tower);
                    return;
                }
                var update = message as PlayfieldTowerUpdateClientMessage;
                if (update != null)
                {
                    if (update.UpdateType == PlayfieldUpdateClientType.Destroyed)
                    {
                        foreach (var id in _towers.Where(t => t.Key == update.TowerId || t.Value.Placeholder == update.TowerId ||
                            (update.Tower != null && t.Key == update.Tower.TowerCharId)).Select(t => t.Key).ToArray())
                        {
                            _towers.Remove(id);
                            if (_pending == id) _pending = Identity.None;
                        }
                    }
                    else if (update.UpdateType == PlayfieldUpdateClientType.Planted) Observe(update.Tower);
                    return;
                }
                var packet = (InfoPacketMessage)message;
                var info = packet.Info as TowerInfoPacket;
                if (info == null) return;
                var record = GetOrAdd(packet.Identity);
                if (record == null) return;
                record.Info = info;
                record.InfoAt = _clock.ElapsedMilliseconds;
                record.IsControlReply = packet.Type == InfoPacketType.ControlTower;
                if (record.IsControlReply) record.Class = TowerClass.ControlTower;
                if (_pending == packet.Identity)
                {
                    _pending = Identity.None;
                    ShowInfo(record, false);
                }
            }
            catch (Exception ex) { Chat.WriteLine("Tower Info reply: " + ex.Message); }
        }

        private void EnsureScope()
        {
            var identity = Playfield.Identity;
            if (_playfield == identity) return;
            Clear();
            _playfield = identity;
        }

        private void OnTeleportStarted(object sender, EventArgs args) { Clear(); _playfield = Identity.None; }

        private void Clear()
        {
            _towers.Clear();
            _pending = Identity.None;
            _hasSnapshot = false;
        }

        private void OnUpdate(object sender, float deltaTime)
        {
            if (_disposed || _pending == Identity.None || _clock.ElapsedMilliseconds < _deadline) return;
            _pending = Identity.None;
            Chat.WriteLine("No matching tower-info reply after eight seconds. Move closer/select the tower and retry; no timer or planting state was guessed.");
        }

        public override void Teardown()
        {
            _disposed = true;
            Network.N3MessageReceived -= OnMessage;
            Game.TeleportStarted -= OnTeleportStarted;
            Game.OnUpdate -= OnUpdate;
            Clear();
        }

        private sealed class TowerRecord
        {
            public Identity Id;
            public Identity Placeholder;
            public Vector3 Position;
            public bool HasPosition;
            public string Name;
            public Side? Side;
            public int? Level;
            public TowerClass? Class;
            public TowerInfoPacket Info;
            public bool IsControlReply;
            public long InfoAt;
        }
    }
}
