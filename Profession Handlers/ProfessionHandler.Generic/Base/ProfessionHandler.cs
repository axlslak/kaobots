using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using AOSharp.Common.Unmanaged.Imports;
using AOSharp.Common.GameData;
using AOSharp.Core;
using AOSharp.Core.Inventory;
using AOSharp.Core.UI;

namespace ProfessionHandler.Generic.Combat
{
    public class ProfessionHandler
    {
        private double perkDelay;
        private double Delay;
        protected Queue<CombatActionQueueItem> _actionQueue = new Queue<CombatActionQueueItem>();
        protected List<ItemRule> _itemRules = new List<ItemRule>();
        protected List<PerkRule> _perkRules = new List<PerkRule>();
        protected List<SpellRule> _spellRules = new List<SpellRule>();

        protected delegate bool ItemConditionProcessor(Item item, SimpleChar fightingTarget, ref (SimpleChar Target, bool ShouldSetTarget) actionTarget);
        protected delegate bool PerkConditionProcessor(PerkAction perkAction, SimpleChar fightingTarget, ref (SimpleChar Target, bool ShouldSetTarget) actionTarget);
        protected delegate bool SpellConditionProcessor(Spell spell, SimpleChar fightingTarget, ref (SimpleChar Target, bool ShouldSetTarget) actionTarget);

        public static ProfessionHandler Instance { get; private set; }

        private int LastQueue = 0;

        // New-client observations; engine selection is cached once for this handler.
        protected static readonly bool NewClientTraceEngine = Game.IsNewEngine;
        private double _nextNewClientTrace;
        private Dictionary<string, double> _newClientActionTrace;
        private static double _nextNewClientErrorTrace;
        protected bool NewClientTraceUpdate;
        protected string NewClientTraceStage = "update";

        // Query the local combat target by identity; selection is not a fighting target.
        [DllImport("Gamecode.dll", EntryPoint = "?N3Msg_GetAttackingID@n3EngineClientAnarchy_t@@QBE_NAAVIdentity_t@@@Z", CallingConvention = CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool GetNewClientAttackingId(IntPtr engine, ref Identity target);

        private SimpleChar _newClientFightingTarget;
        private Identity _newClientFightingIdentity = Identity.None;
        private bool _newClientNativeAttacking;
        private bool _newClientTargetQueryUnavailable;
        private string _newClientFightingStatus = "not sampled";

        protected SimpleChar HandlerFightingTarget => NewClientTraceEngine
            ? _newClientFightingTarget : DynelManager.LocalPlayer.FightingTarget;

        private void RefreshNewClientFightingTarget()
        {
            _newClientFightingStatus = "idle";
            var local = DynelManager.LocalPlayer;
            if (local == null || !local.IsAttacking) return;
            if (_newClientTargetQueryUnavailable)
            {
                _newClientFightingStatus = "native query unavailable";
                return;
            }

            _newClientFightingStatus = "engine unavailable";
            var engine = N3Engine_t.GetInstance();
            if (engine == IntPtr.Zero) return;

            _newClientNativeAttacking = GetNewClientAttackingId(engine, ref _newClientFightingIdentity);
            _newClientFightingStatus = "native reports idle";
            if (!_newClientNativeAttacking) return;
            _newClientFightingStatus = "invalid or self target";
            if (_newClientFightingIdentity.Type != IdentityType.SimpleChar || _newClientFightingIdentity == local.Identity) return;

            _newClientFightingStatus = "target not loaded";
            var target = DynelManager.GetDynel<SimpleChar>(_newClientFightingIdentity);
            if (target != null)
            {
                _newClientFightingTarget = target;
                _newClientFightingStatus = "resolved by native combat identity";
            }
        }

        private void BeginNewClientTrace()
        {
            NewClientTraceUpdate = false;
            if (!NewClientTraceEngine) return;
            // Drop every previous frame's object before zoning/disabled/death early returns.
            _newClientFightingTarget = null;
            _newClientFightingIdentity = Identity.None;
            _newClientNativeAttacking = false;
            _newClientFightingStatus = "update gated";
            NewClientTraceStage = "update gates";
            var now = GenericProfessionHandler.Now;
            if (now < _nextNewClientTrace) return;
            _nextNewClientTrace = now + 3.0;
            NewClientTraceUpdate = true;
        }

        private static void WriteNewClientTrace(string message)
        {
            try { Chat.WriteLine($"[Handler new client] {message}"); }
            catch (Exception) { /* Diagnostic formatting must not interrupt the handler. */ }
        }

        protected void TraceNewClient(string message)
        {
            if (NewClientTraceUpdate) WriteNewClientTrace(message);
        }

        protected void TraceNewClientAction(string kind, object action, SimpleChar target, bool setTarget = false)
        {
            if (!NewClientTraceEngine) return;
            var now = GenericProfessionHandler.Now;
            if (_newClientActionTrace == null) _newClientActionTrace = new Dictionary<string, double>();
            if (_newClientActionTrace.TryGetValue(kind, out var next) && now < next) return;
            _newClientActionTrace[kind] = now + 1.0;
            try
            {
                var label = action is Spell nano ? $"{nano.Name} ({nano.Id})"
                    : action is PerkAction perk ? perk.Name
                    : action is Item item ? item.Name : action.ToString();
                var targetLabel = target == null ? "implicit self " + DescribeNewClientTarget(DynelManager.LocalPlayer) : DescribeNewClientTarget(target);
                WriteNewClientTrace($"Attempt {kind} {label} on {targetLabel}, setTarget={setTarget}");
            }
            catch (Exception error) { TraceNewClientReadError(error); }
        }

        internal static void TraceNewClientError(Exception error, string stage)
        {
            if (!NewClientTraceEngine) return;
            var now = GenericProfessionHandler.Now;
            if (now < _nextNewClientErrorTrace) return;
            _nextNewClientErrorTrace = now + 3.0;
            WriteNewClientTrace($"ERROR in {stage}: {error.GetType().Name}: {error.Message}; at {error.TargetSite}");
        }

        private static void TraceNewClientReadError(Exception error)
        {
            WriteNewClientTrace($"Diagnostic read failed: {error.GetType().Name}: {error.Message}");
        }

        protected void TraceNewClientCombatState()
        {
            try
            {
                var lp = DynelManager.LocalPlayer;
                WriteNewClientTrace($"Local={lp.Identity}; selected={DescribeNewClientTarget(Targeting.TargetChar)}; attacking={lp.IsAttacking}; fighting={DescribeNewClientTarget(HandlerFightingTarget)}; nativeFightId={_newClientFightingIdentity}; nativeAttacking={_newClientNativeAttacking}; targetRead={_newClientFightingStatus}; Specials={GenericProfessionHandler._settings["Specials"].AsBool()}; PetSync={GenericProfessionHandler._settings["SyncPets"] == null || GenericProfessionHandler._settings["SyncPets"].AsBool()}");
            }
            catch (Exception error) { TraceNewClientReadError(error); }
        }

        private void TraceNewClientScheduler(SimpleChar fightingTarget)
        {
            try
            {
                WriteNewClientTrace($"Scheduler target={DescribeNewClientTarget(fightingTarget)}; rules items={_itemRules.Count}, perks={_perkRules.Count}, nanos={_spellRules.Count}; queue={_actionQueue.Count}");
            }
            catch (Exception error) { TraceNewClientReadError(error); }
        }

        private void TraceNewClientPerkGates()
        {
            try
            {
                WriteNewClientTrace($"Perk gates: itemQueued={_actionQueue.Any(x => x.CombatAction is Item)}, pendingItem={Item.HasPendingUse}, delayReady={Time.AONormalTime > perkDelay}, active={PerkAction.List.Count(p => p.IsExecuting || p.IsPending)}, queuedPerks={_actionQueue.Count(x => x.CombatAction is PerkAction)}, limit={GenericProfessionHandler._settings["MAX_CONCURRENT_PERKS"].AsInt32()}");
            }
            catch (Exception error) { TraceNewClientReadError(error); }
        }

        private void TraceNewClientNanoGates()
        {
            try
            {
                WriteNewClientTrace($"Nano gates: delayReady={Time.AONormalTime >= Delay}, pendingCast={Spell.HasPendingCast}, movementAllowsCast={DynelManager.LocalPlayer.MovementStatePermitsCasting}");
            }
            catch (Exception error) { TraceNewClientReadError(error); }
        }

        protected void TraceNewClientPets(LocalPlayer localPlayer, Pet attackPet, Pet supportPet)
        {
            try
            {
                WriteNewClientTrace($"Pet gate: profession={localPlayer.Profession}, attackPresent={attackPet?.Character != null}, attackBusy={attackPet?.Character?.IsAttacking}, attackVelocity={attackPet?.Character?.Velocity}, supportPresent={supportPet?.Character != null}, supportBusy={supportPet?.Character?.IsAttacking}, supportVelocity={supportPet?.Character?.Velocity}");
            }
            catch (Exception error) { TraceNewClientReadError(error); }
        }

        protected static string DescribeNewClientTarget(SimpleChar target)
        {
            if (target == null) return "none";
            return $"{target.Identity} (ptr=0x{target.Pointer.ToInt64():X}, self={target.Identity == DynelManager.LocalPlayer.Identity}, player={target.IsPlayer}, npc={target.IsNpc})";
        }

        private sealed class NewClientRuleCounts
        {
            public int PerkContext, PerkMissing, PerkUnavailable, PerkRejected, PerkQueued;
            public int NanoContext, NanoMissing, NanoNotReady, NanoRequirements, NanoRejected, NanoAttempted;
        }

        public static void Set(ProfessionHandler ProfessionHandler)
        {
            Instance = ProfessionHandler;
        }

        protected void Update(object sender, float deltaTime)
        {
            BeginNewClientTrace();
            if (NewClientTraceUpdate)
                TraceNewClient($"Update {GetType().Name}, handler={GenericProfessionHandler.HandlerVersion:0.00}");
            if (GenericProfessionHandler.Now < GenericProfessionHandler._lastZonedTime)
            {
                if (NewClientTraceUpdate) TraceNewClient("Waiting for zoning/startup grace period.");
                return;
            }
            if (!DynelManager.LocalPlayer.IsAlive)
            {
                if (NewClientTraceUpdate) TraceNewClient("Update stopped: local player is not alive.");
                return;
            }
            if (Game.IsZoning)
            {
                if (NewClientTraceUpdate) TraceNewClient("Update stopped: zoning.");
                return;
            }
            if (!GenericProfessionHandler._settings["Enable"].AsBool())
            {
                if (NewClientTraceUpdate) TraceNewClient("Update stopped: handler Enable setting is off.");
                return;
            }

            GenericProfessionHandler.SkillLock.RemoveWhere(x => (int)Time.AONormalTime > x.Item2);

            if (NewClientTraceEngine)
            {
                try { RefreshNewClientFightingTarget(); }
                catch (Exception error)
                {
                    _newClientFightingTarget = null;
                    _newClientFightingStatus = "native query failed";
                    // An unavailable query must not cause a repeated exception on every update.
                    if (error is EntryPointNotFoundException || error is DllNotFoundException)
                        _newClientTargetQueryUnavailable = true;
                    TraceNewClientError(error, "native fighting-target query");
                    GenericProfessionHandler.ErrorCatch(error);
                }
            }

            OnUpdate(deltaTime);
        }

        protected virtual void OnUpdate(float deltaTime)
        {
            if (NewClientTraceEngine) NewClientTraceStage = "scheduler fighting-target read";
            var fightingTarget = HandlerFightingTarget;
            var trace = NewClientTraceUpdate ? new NewClientRuleCounts() : null;
            if (NewClientTraceUpdate)
                TraceNewClientScheduler(fightingTarget);

            if (fightingTarget != null && fightingTarget.IsPlayer)
            {
                if (NewClientTraceUpdate) TraceNewClient("Scheduler stopped: fighting target rejected by the existing target policy.");
                return;
            }

            if (NewClientTraceEngine) NewClientTraceStage = "item rules";
            foreach (var rule in _itemRules.OrderBy(r => r.Priority))
            {
                if (rule.ItemConditionProcessor == null) continue;
                if (rule.Context == RuleContext.InCombat && fightingTarget == null) continue;
                if (rule.Context == RuleContext.OutOfCombat && fightingTarget != null) continue;
                if (rule.Ids == null) continue;
                if (_actionQueue.Any(q => q.CombatAction is Item i && rule.Ids.Contains(i.Id))) continue;

                var item = Inventory.Items.Concat(Inventory.Backpacks.SelectMany(bp => Inventory.GetContainerItems(bp.Identity))).Where(x => rule.Ids.Contains(x.Id)).OrderByDescending(x => x.QualityLevel).FirstOrDefault(x => x.MeetsSelfUseReqs());

                if (item == null) continue;

                (SimpleChar Target, bool ShouldSetTarget) actionTarget = (null, false);

                if (rule.ItemConditionProcessor.Invoke(item, fightingTarget, ref actionTarget))
                {
                    var itemDelay = item.AttackDelay > 0 ? item.AttackDelay : 5f;
                    _actionQueue.Enqueue(new CombatActionQueueItem(item, actionTarget.Target, actionTarget.ShouldSetTarget, itemDelay));
                    perkDelay = Time.AONormalTime + 0.5;
                }
            }

            if (NewClientTraceEngine) NewClientTraceStage = "perk rules";
            if (NewClientTraceUpdate)
                TraceNewClientPerkGates();
            if (!_actionQueue.Any(x => x.CombatAction is Item))
            {
                if (Time.AONormalTime > perkDelay)
                {
                    if (PerkAction.List.Count(p => p.IsExecuting || p.IsPending) < GenericProfessionHandler._settings["MAX_CONCURRENT_PERKS"].AsInt32())
                    {
                        foreach (var rule in _perkRules.OrderBy(r => r.Priority))
                        {
                            if (Item.HasPendingUse) break;
                            if (_actionQueue.Any(x => x.CombatAction is Item)) break;
                            if (_actionQueue.Count(x => x.CombatAction is PerkAction) >= GenericProfessionHandler._settings["MAX_CONCURRENT_PERKS"].AsInt32()) break;
                            if (rule.PerkConditionProcessor == null) continue;
                            if (rule.Context == RuleContext.InCombat && fightingTarget == null) { if (trace != null) trace.PerkContext++; continue; }
                            if (rule.Context == RuleContext.OutOfCombat && fightingTarget != null) { if (trace != null) trace.PerkContext++; continue; }

                            if (!PerkAction.Find(rule.PerkHash, out PerkAction perk)) { if (trace != null) trace.PerkMissing++; continue; }
                            if (perk.IsPending || perk.IsExecuting || !perk.IsAvailable) { if (trace != null) trace.PerkUnavailable++; continue; }
                            if (_actionQueue.Any(x => x.CombatAction is PerkAction action && action == perk)) continue;

                            (SimpleChar Target, bool ShouldSetTarget) actionTarget = (null, false);

                            if (rule.PerkConditionProcessor.Invoke(perk, fightingTarget, ref actionTarget))
                            {
                                var perkDelayValue = perk.AttackDelay > 0  ? perk.AttackDelay : 5f;

                                _actionQueue.Enqueue(new CombatActionQueueItem(perk, actionTarget.Target, actionTarget.ShouldSetTarget, perkDelayValue));
                                if (trace != null) trace.PerkQueued++;
                                perkDelay = Time.AONormalTime + 0.5;
                            }
                            else if (trace != null) trace.PerkRejected++;
                        }
                    }
                }
            }

            if (NewClientTraceEngine) NewClientTraceStage = "nano and action-queue gates";
            if (NewClientTraceUpdate)
                TraceNewClientNanoGates();
            if (Time.AONormalTime >= Delay)
            {
                if (!Spell.HasPendingCast && DynelManager.LocalPlayer.MovementStatePermitsCasting)
                {
                    foreach (var spellRule in _spellRules.OrderBy(r => r.Priority))
                    {
                        if (spellRule.SpellConditionProcessor == null) continue;
                        if (spellRule.SpellGroup == null) continue;
                        if (spellRule.Context == RuleContext.InCombat && fightingTarget == null) { if (trace != null) trace.NanoContext++; continue; }
                        if (spellRule.Context == RuleContext.OutOfCombat && fightingTarget != null) { if (trace != null) trace.NanoContext++; continue; }

                        foreach (int spellId in spellRule.SpellGroup)
                        {
                            if (!Spell.Find(spellId, out Spell curSpell)) { if (trace != null) trace.NanoMissing++; continue; }
                            if (!curSpell.IsReady) { if (trace != null) trace.NanoNotReady++; continue; }
                            if (!curSpell.MeetsSelfUseReqs()) { if (trace != null) trace.NanoRequirements++; continue; }
                            if (Spell.HasPendingCast) break;

                            (SimpleChar Target, bool ShouldSetTarget) actionTarget = (null, false);

                            if (spellRule.SpellConditionProcessor.Invoke(curSpell, fightingTarget, ref actionTarget))
                            {
                                if (trace != null) trace.NanoAttempted++;
                                if (NewClientTraceEngine)
                                    TraceNewClientAction("nano", curSpell, actionTarget.Target, actionTarget.ShouldSetTarget);
                                curSpell.Cast(actionTarget.Target, actionTarget.ShouldSetTarget);
                            }
                            else if (trace != null) trace.NanoRejected++;
                        }
                    }
                }

                if (_actionQueue.Count > 0)
                {
                   var dequeueList = new List<CombatActionQueueItem>();

                    _actionQueue = new Queue<CombatActionQueueItem>( _actionQueue.Where(q => q.Timeout == 0 || Time.AONormalTime < q.Timeout));

                    var queAction = _actionQueue.Where(q => !q.Used).FirstOrDefault();

                    if (queAction != null)
                    {
                        switch (queAction.CombatAction)
                        {
                            case Item item:
                                {
                                    if (!Item.HasPendingUse && !PerkAction.List.Any(p => p.IsExecuting))
                                    {
                                        if (NewClientTraceEngine)
                                            TraceNewClientAction("item", item, queAction.Target, queAction.ShouldSetTarget);
                                        item.Use(queAction.Target, queAction.ShouldSetTarget);

                                        var lockValue = item.UseModifiers.FirstOrDefault(m => m.Function == SpellFunction.LockSkill) ?.Properties.TryGetValue(SpellPropertyOperator.Value, out var v) == true ? v : (int?)null;

                                        if (lockValue.HasValue)
                                            GenericProfessionHandler.SkillLock.Add((item, lockValue.Value + (int)Time.AONormalTime));

                                        queAction.Used = true;
                                        dequeueList.Add(queAction);
                                    }
                                }
                                break;
                            case PerkAction perk:
                                {
                                    if (!_actionQueue.Any(x => x.CombatAction is Item))
                                    {
                                        if (PerkAction.List.Count(p => p.IsExecuting || p.IsPending) < GenericProfessionHandler._settings["MAX_CONCURRENT_PERKS"].AsInt32())
                                        {
                                            if (perk.IsAvailable && !perk.IsExecuting && !perk.IsPending && !Item.HasPendingUse)
                                            {
                                                if (NewClientTraceEngine)
                                                    TraceNewClientAction("perk", perk, queAction.Target, queAction.ShouldSetTarget);
                                                perk.Use(queAction.Target, queAction.ShouldSetTarget);

                                                queAction.Used = true;
                                                dequeueList.Add(queAction);
                                            }
                                        }
                                    }
                                }
                                break;
                        }
                    }

                    _actionQueue = new Queue<CombatActionQueueItem>(_actionQueue.Where(x => !dequeueList.Contains(x)));
                }

                Delay = Time.AONormalTime + 0.15;
            }
            if (trace != null)
            {
                TraceNewClient($"Perk rules this update: contextSkipped={trace.PerkContext}, missing={trace.PerkMissing}, unavailable={trace.PerkUnavailable}, conditionFalse={trace.PerkRejected}, queued={trace.PerkQueued}");
                TraceNewClient($"Nano rules this update: contextSkipped={trace.NanoContext}, missing={trace.NanoMissing}, notReady={trace.NanoNotReady}, selfReqsFailed={trace.NanoRequirements}, conditionFalse={trace.NanoRejected}, attempted={trace.NanoAttempted}");
            }
        }

        protected void RegisterItemProcessor(int id, ItemConditionProcessor conditionProcessor, CombatActionPriority priority = CombatActionPriority.NonCombat, RuleContext context = RuleContext.Both)
        {
            RegisterItemProcessor(new[] { id }, conditionProcessor, priority, context);
        }

        protected void RegisterItemProcessor(IEnumerable<int> ids, ItemConditionProcessor conditionProcessor, CombatActionPriority priority = CombatActionPriority.NonCombat, RuleContext context = RuleContext.Both)
        {
            var idsArr = ids as int[] ?? ids.ToArray();
            if (_itemRules.Any(r => r.Ids.Length == idsArr.Length && r.Ids.SequenceEqual(idsArr))) return;

            var rule = new ItemRule(idsArr, conditionProcessor, priority, context);

            int idx = -1;
            for (int i = 0; i < _itemRules.Count; i++)
            {
                var r = _itemRules[i];
                if ((int)r.Context > (int)rule.Context || ((int)r.Context == (int)rule.Context && (int)r.Priority > (int)rule.Priority))
                {
                    idx = i;
                    break;
                }
            }

            if (idx >= 0) _itemRules.Insert(idx, rule);
            else _itemRules.Add(rule);
        }

        protected void RegisterPerkProcessor(PerkHash perkHash, PerkConditionProcessor conditionProcessor, CombatActionPriority priority = CombatActionPriority.NonCombat, RuleContext context = RuleContext.Both)
        {
            if (_perkRules.Any(r => r.PerkHash == perkHash)) return;
            var rule = new PerkRule(perkHash, conditionProcessor, priority, context);
            int idx = -1;
            for (int i = 0; i < _perkRules.Count; i++)
            {
                var r = _perkRules[i];
                if ((int)r.Context > (int)rule.Context || ((int)r.Context == (int)rule.Context && (int)r.Priority > (int)rule.Priority)) { idx = i; break; }
            }
            if (idx >= 0) _perkRules.Insert(idx, rule); else _perkRules.Add(rule);
        }

        protected void RegisterSpellProcessor(Spell spell, SpellConditionProcessor conditionProcessor, CombatActionPriority priority = CombatActionPriority.NonCombat, RuleContext context = RuleContext.Both)
        {
            RegisterSpellProcessor(new[] { spell.Id }, conditionProcessor, priority, context);
        }

        protected void RegisterSpellProcessor(IEnumerable<Spell> spellGroup, SpellConditionProcessor conditionProcessor, CombatActionPriority priority = CombatActionPriority.NonCombat, RuleContext context = RuleContext.Both)
        {
            RegisterSpellProcessor(spellGroup.GetIds(), conditionProcessor, priority, context);
        }

        protected void RegisterSpellProcessor(int spellId, SpellConditionProcessor conditionProcessor, CombatActionPriority priority = CombatActionPriority.NonCombat, RuleContext context = RuleContext.Both)
        {
            RegisterSpellProcessor(new[] { spellId }, conditionProcessor, priority, context);
        }

        protected void RegisterSpellProcessor(int[] spellGroup, SpellConditionProcessor conditionProcessor, CombatActionPriority priority = CombatActionPriority.NonCombat, RuleContext context = RuleContext.Both)
        {
            if (spellGroup.Length == 0) return;
            if (_spellRules.Any(r => r.SpellGroup.Length == spellGroup.Length && r.SpellGroup.SequenceEqual(spellGroup))) return;

            var rule = new SpellRule(spellGroup, conditionProcessor, priority, context);

            int idx = -1;
            for (int i = 0; i < _spellRules.Count; i++)
            {
                var r = _spellRules[i];
                if ((int)r.Context > (int)rule.Context || ((int)r.Context == (int)rule.Context && (int)r.Priority > (int)rule.Priority))
                {
                    idx = i;
                    break;
                }
            }

            if (idx >= 0) _spellRules.Insert(idx, rule);
            else _spellRules.Add(rule);
        }

        protected class CombatActionQueueItem : IEquatable<CombatActionQueueItem>
        {
            public AOSharp.Core.Combat.ICombatAction CombatAction;
            public SimpleChar Target;
            public bool Used = false;
            public bool ShouldSetTarget = false;
            public double Timeout = 0;

            public CombatActionQueueItem(AOSharp.Core.Combat.ICombatAction action, SimpleChar target, bool shouldSetTarget, double timeoutOffset = 0)
            {
                CombatAction = action;
                Target = target;
                ShouldSetTarget = shouldSetTarget;
                Timeout = Time.AONormalTime + timeoutOffset;
            }

            public bool Equals(CombatActionQueueItem other)
            {
                if (other == null)
                    return false;

                if (CombatAction.GetType() != other.CombatAction.GetType())
                    return false;

                switch (CombatAction)
                {
                    case PerkAction perk:
                        return perk == ((PerkAction)other.CombatAction);
                    case Item item:
                        Item item1 = item;
                        Item item2 = (Item)other.CombatAction;
                        return item.Id == ((Item)other.CombatAction).Id && item.HighId == ((Item)other.CombatAction).HighId && item.QualityLevel == ((Item)other.CombatAction).QualityLevel;
                    case Spell spell:
                        return spell == ((Spell)other.CombatAction);
                    default:
                        return false;
                }
            }
        }

        protected enum CombatActionPriority { Ultra = 0, VeryHigh = 10, High = 20, Medium = 30, Low = 40, VeryLow = 50, NonCombat = 60 }

        protected enum RuleContext
        {
            Both = 0,
            InCombat = 1,
            OutOfCombat = 2
        }

        protected readonly struct ItemRule
        {
            public int[] Ids { get; }
            public ItemConditionProcessor ItemConditionProcessor { get; }
            public CombatActionPriority Priority { get; }
            public RuleContext Context { get; }

            public ItemRule(IEnumerable<int> ids, ItemConditionProcessor itemConditionProcessor, CombatActionPriority priority, RuleContext context)
            {
                Ids = ids.ToArray();
                ItemConditionProcessor = itemConditionProcessor;
                Priority = priority;
                Context = context;
            }
        }

        protected readonly struct PerkRule
        {
            public PerkHash PerkHash { get; }
            public PerkConditionProcessor PerkConditionProcessor { get; }
            public CombatActionPriority Priority { get; }
            public RuleContext Context { get; }

            public PerkRule(PerkHash perkHash, PerkConditionProcessor perkConditionProcessor, CombatActionPriority combatActionPriority, RuleContext context)
            {
                PerkHash = perkHash;
                PerkConditionProcessor = perkConditionProcessor;
                Priority = combatActionPriority;
                Context = context;
            }
        }

        protected readonly struct SpellRule
        {
            public int[] SpellGroup { get; }
            public SpellConditionProcessor SpellConditionProcessor { get; }
            public CombatActionPriority Priority { get; }
            public RuleContext Context { get; }

            public SpellRule(int[] spellGroup, SpellConditionProcessor spellConditionProcessor, CombatActionPriority priority, RuleContext context)
            {
                SpellGroup = spellGroup;
                SpellConditionProcessor = spellConditionProcessor;
                Priority = priority;
                Context = context;
            }
        }
    }
}
