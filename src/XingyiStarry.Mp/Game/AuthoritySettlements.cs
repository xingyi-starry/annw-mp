using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ANNW;
using HarmonyLib;
using XingyiStarry.Mp.Protocol;
using XingyiStarry.Mp.Protocol.Wire;
using XingyiStarry.Mp.Session;
using Event = XingyiStarry.Mp.Protocol.Wire.SettlementEvent;

namespace XingyiStarry.Mp.Game;

// Synchronous native calls form event trees; detached work publishes new roots as it runs.
internal sealed class AttackSettlements
{
    internal static AttackSettlements? Current { get; private set; }
    private readonly Action<Event>? publish;
    private Call? call;
    private Event? invoking;
    private Exception? failure;
    private int pending;
    private bool canceled;
    internal bool Replaying => publish is null;
    internal bool SuppressDeathTail { get; private set; }
    internal bool HasPending => pending != 0;
    internal Call? ActiveCall => call;
    internal bool ApplyingExplicitEvent => invoking is not null;
    internal bool SelectingDerivedSteps => call?.Value.Pulse is not null || call?.Value.Effect?.EffectType is
        nameof(UnitEffect_ShockChain) or nameof(UnitEffect_CounterAtk) or nameof(UnitEffect_ExpOnSuicide) or nameof(UnitEffect_GenUnitOnDie);
    private AttackSettlements(Action<Event>? publish) => this.publish = publish;
    internal static void BeginRecording(Action<Event> publish) { Reset(); Current = new AttackSettlements(publish); }
    internal static void BeginReplay() { Reset(); Current = new AttackSettlements(null); }
    internal static void Reset() { if (Current is not null) Current.canceled = true; Current = null; NativeActionPlayback.Reset(); }
    internal void ThrowIfFailed() { if (failure is not null) throw new InvalidOperationException("Native settlement failed.", failure); }
    internal void CheckComplete() { ThrowIfFailed(); if (call is not null || pending != 0) throw new InvalidOperationException("Native settlements remain unfinished."); }

    // Rebuild the native callback list in host order at this callback point.
    // Local delegate ordering can change after save/load; it cannot select a
    // different effect instance for an otherwise identical owner/argument pair.
    internal bool ReplayCallbacks(Event proposed)
    {
        var callback = proposed.Effect;
        if (!Replaying || invoking is not null || call is null || callback is null || !callback.Callback.StartsWith("On", StringComparison.Ordinal)) return false;
        while (call.Next < call.Value.Children.Count)
        {
            var next = call.Value.Children[call.Next]; var expected = next.Effect;
            if (expected is null || next.TargetUnitId != proposed.TargetUnitId || !expected.Callback.StartsWith("On", StringComparison.Ordinal) ||
                expected.OtherUnitId != callback.OtherUnitId || expected.Damage != callback.Damage || expected.Value != callback.Value ||
                expected.Reason != callback.Reason || expected.Killed != callback.Killed || expected.X != callback.X || expected.Y != callback.Y) break;
            call.Next++; Apply(next);
        }
        return true;
    }

    internal bool Enter(Event proposed, out Call? scope)
    {
        scope = null;
        if (canceled) return false;
        if (Replaying)
        {
            Event? expected = null;
            if (invoking is not null) { expected = invoking; invoking = null; }
            else if (call is not null && call.Next < call.Value.Children.Count)
            {
                var next = call.Value.Children[call.Next];
                if (next.BodyCase == proposed.BodyCase && (proposed.Spawn is not null || next.TargetUnitId == proposed.TargetUnitId))
                { expected = next; call.Next++; }
            }
            if (expected is null) return false;
            if (expected.BodyCase != proposed.BodyCase || proposed.TargetUnitId != 0 && expected.TargetUnitId != proposed.TargetUnitId)
                throw new InvalidDataException("Native settlement target or kind mismatch.");
            proposed = expected;
        }
        else { proposed.RandomResult = new RandomResult(); call?.Value.Children.Add(proposed); }
        scope = new Call(this, call, proposed); call = scope;
        return true;
    }
    internal sealed class Call : IDisposable
    {
        internal readonly AttackSettlements Owner;
        internal readonly Call? Parent;
        internal readonly Event Value;
        internal int Next;
        private bool damageFiltersApplied;
        private readonly RandomResultContext random;
        private bool closed;
        internal Call(AttackSettlements owner, Call? parent, Event value)
        {
            Owner = owner; Parent = parent; Value = value;
            value.RandomResult ??= new RandomResult();
            random = new RandomResultContext(value.RandomResult, owner.Replaying, value.TargetUnitId,
                value.Wreck?.X ?? value.AttackCell?.TargetX ?? 0, value.Wreck?.Y ?? value.AttackCell?.TargetY ?? 0);
        }
        internal void Children() { while (Next < Value.Children.Count) Owner.Apply(Value.Children[Next++]); }
        internal void ApplyTargetFilters()
        {
            while (Next < Value.Children.Count && Value.Children[Next].Effect?.Callback is "PreAttack" or "PreDamage")
                Owner.EvaluateEffect(Value.Children[Next++]);
        }
        internal float DamageFilters(float damage)
        {
            if (damageFiltersApplied) return damage;
            damageFiltersApplied = true;
            while (Next < Value.Children.Count && Value.Children[Next].Effect?.Callback == "PreDamage")
                damage = (float)Owner.EvaluateEffect(Value.Children[Next++])!;
            return damage;
        }
        internal void Fail(Exception ex) { Owner.failure ??= ex; }
        public void Dispose()
        {
            if (closed) return; closed = true;
            try
            {
                if (Owner.Replaying && Owner.failure is null) { Children(); random.Complete(); }
                if (!Owner.Replaying && Owner.failure is null && Parent is null && !Owner.canceled) Owner.publish!(Value);
            }
            catch (Exception ex) { Owner.failure ??= ex; throw; }
            finally { random.Dispose(); Owner.call = Parent; }
        }
    }
    internal void Apply(Event value)
    {
        if (!Replaying || canceled) throw new InvalidOperationException("Settlement replay is not active.");
        ThrowIfFailed(); var previous = invoking; invoking = value;
        try
        {
            using (ExecutionContext.Enter(ExecutionOrigin.ClientReplay)) Dispatch(value);
            if (invoking is not null) throw new InvalidDataException("Settlement did not enter native adapter: " + value.BodyCase);
        }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        finally { invoking = previous; }
        ThrowIfFailed();
    }
    private static UnitData Unit(long id) => GS_Battle.self.all_unit.GetUnitByID(checked((int)id)) ?? throw new InvalidDataException("Missing settlement unit: " + id);
    private static UnitData? MaybeUnit(long id) => id == 0 ? null : Unit(id);
    private static Player PlayerAt(int index) => GS_Battle.self.all_player.players[index];
    internal static ActionData ResolveAction(Event value, string name)
    {
        if (value.SourceUnitId != 0) return Unit(value.SourceUnitId).actions.First(x => x.sd_action.name == name);
        return PlayerAt(value.PlayerIndex).co_data.skill_actions.First(x => x.sd_action.name == name);
    }
    private static void Invoke(object instance, string method, params object?[] args) => AccessTools.Method(instance.GetType(), method).Invoke(instance, args);
    private object? EvaluateEffect(Event value)
    {
        var previous = invoking; invoking = value;
        try
        {
            using (ExecutionContext.Enter(ExecutionOrigin.ClientReplay))
            {
                var result = InvokeEffect(value);
                if (invoking is not null) throw new InvalidDataException("Effect did not enter native adapter.");
                ThrowIfFailed();
                return result;
            }
        }
        finally { invoking = previous; }
    }
    private static object? InvokeEffect(Event e)
    {
        var f = e.Effect; var effect = EffectFor(e.TargetUnitId, e.PlayerIndex, f.EffectName, f.EffectIndex);
        var method = AccessTools.Method(effect.GetType(), f.Callback);
        return method.Invoke(effect, method.GetParameters().Select(p => EffectArgument(p, e)).ToArray());
    }
    private void Dispatch(Event e)
    {
        var target = e.BodyCase == Event.BodyOneofCase.Spawn ? null : MaybeUnit(e.TargetUnitId); var source = MaybeUnit(e.SourceUnitId);
        switch (e.BodyCase)
        {
            case Event.BodyOneofCase.AttackCell:
                var c = e.AttackCell;
                NativeActionSteps.Cell(ResolveAction(e, c.ActionName), GameTileData.Get(new Inctor2(c.TargetX, c.TargetY)), c.HasOriginX ? new Inctor2(c.OriginX, c.OriginY) : (Inctor2?)null, c.Data); break;
            case Event.BodyOneofCase.ActionPreparation:
                var preparation = e.ActionPreparation;
                NativeActionSteps.Prepare(ResolveAction(e, preparation.ActionName), GameTileData.Get(new Inctor2(preparation.TargetX, preparation.TargetY))); break;
            case Event.BodyOneofCase.Damage:
                var d = e.Damage;
                Invoke(target!, "Hurt", d.Damage, source, d.ActionName.Length == 0 ? null : ResolveAction(e, d.ActionName), d.HasCenterX ? new Inctor2(d.CenterX, d.CenterY) : (Inctor2?)null, d.UsePredamage, d.DoRandom, d.NoKill); break;
            case Event.BodyOneofCase.DeathStarted:
                var death = e.DeathStarted;
                Invoke(target!, "Die", (DieReason)death.Reason, death.Damage, source, death.ActionName.Length == 0 ? null : ResolveAction(e, death.ActionName), death.GenerateWreck, death.HasCenterX ? new Inctor2(death.CenterX, death.CenterY) : (Inctor2?)null); break;
            case Event.BodyOneofCase.DeathCommitted:
                if (e.DeathCommitted.DescendOnly) GameAPI.self.DescendUnit(target!);
                else target!.TriggerDieEvent((DieReason)e.DeathCommitted.Reason, source, e.DeathCommitted.KillerPlayer < 0 ? null : PlayerAt(e.DeathCommitted.KillerPlayer)); break;
            case Event.BodyOneofCase.PlayerDefeated: PlayerAt(e.PlayerDefeated.PlayerIndex).DoWipeOut(); break;
            case Event.BodyOneofCase.CleanupCompleted: Simple(e, () => Invoke(GS_Battle.self.all_reinforce, "RemoveByPlayer", PlayerAt(e.CleanupCompleted.PlayerIndex))); break;
            case Event.BodyOneofCase.Effect:
                InvokeEffect(e); break;
            case Event.BodyOneofCase.Pulse: Invoke(Unit(e.TargetUnitId).effect_host.effects.OfType<UnitEffect_ShockAura>().ElementAt(e.Pulse.EffectIndex), "OnEvent"); break;
            case Event.BodyOneofCase.Spawn:
                var spawn = e.Spawn;
                GameAPI.self.CreateUnit((CREATE_REASON)spawn.CreateReason, UnitTemplate.Acquire(spawn.TemplateId), spawn.HasPosition ? new Inctor2(spawn.X, spawn.Y) : (Inctor2?)null, PlayerAt(spawn.PlayerIndex), spawn.Building, spawn.Spawned, spawn.TriggerPassives); break;
            case Event.BodyOneofCase.Heal: Invoke(target!, "Heal", e.Heal.Amount); break;
            case Event.BodyOneofCase.Resource:
                var res = e.Resource; Invoke(PlayerAt(res.PlayerIndex), "AddMetalPower", (ResGetReason)res.Reason, res.Metal, res.Power); break;
            case Event.BodyOneofCase.CommanderPosition: Invoke(PlayerAt(e.CommanderPosition.PlayerIndex), "AutoSetCmdPos"); break;
            case Event.BodyOneofCase.Wreck:
                var wreck = e.Wreck; GameTileData.Get(new Inctor2(wreck.X, wreck.Y)).CreateWreck(wreck.Amount, wreck.UseRandom); break;
            case Event.BodyOneofCase.Shield: target!.wp_shield.AbsorbDam(e.Shield.Damage, e.Shield.Preview); break;
            case Event.BodyOneofCase.EffectMutation:
                var mutation = e.EffectMutation;
                if (mutation.Remove) EffectHostFor(e).Remove(EffectFor(e.TargetUnitId, e.PlayerIndex, mutation.EffectName, mutation.EffectIndex));
                else target!.AddUnitEffect(SDBase<SD_ANNW_EFFECT>.Get(mutation.EffectName), MaybeUnit(mutation.CasterUnitId), mutation.Layer, mutation.CasterPlayer < 0 ? null : PlayerAt(mutation.CasterPlayer)); break;
            case Event.BodyOneofCase.Experience:
                if (e.Experience.BasePercent) target!.AddExp_BasePercent(e.Experience.Amount); else target!.AddExp(e.Experience.Amount, e.Experience.UseFilter); break;
            case Event.BodyOneofCase.AttackStarted:
                Simple(e, () => NativeActionPlayback.Started(ResolveAction(e, e.AttackStarted.ActionName))); break;
            case Event.BodyOneofCase.ActionCallbacks:
                var callbackAction = ResolveAction(e, e.ActionCallbacks.ActionName);
                NativeActionSteps.ActionEnd(source!.Event_ActionEnd, callbackAction);
                BattleEventBus.self.TriggerUnitActioned(source, callbackAction.sd_action.cate);
                NativeActionPlayback.Finished(callbackAction);
                break;
            case Event.BodyOneofCase.AttackFinished:
                var finish = e.AttackFinished; var actionFinished = ResolveAction(e, finish.ActionName);
                if (finish.CheckAfterAction) source!.CheckAfterAction(actionFinished);
                else
                {
                    actionFinished.OnUnitActionEnd();
                    if (source is null) NativeActionPlayback.Finished(actionFinished);
                }
                break;
            case Event.BodyOneofCase.SkillFinished: Invoke(PlayerAt(e.SkillFinished.PlayerIndex).co_data, "AfterSkillCast"); break;
            case Event.BodyOneofCase.ForcedAttack:
                Simple(e, () => { });
                var forced = e.ForcedAttack;
                var action = source!.GetAction((ActionCate)forced.ActionCategory);
                GameController.self.StartCoroutine(Track(source!.DoActionWithAni(GameTileData.Get(new Inctor2(forced.TargetX, forced.TargetY)), action, false), waitForReplay: true));
                break;
            default: throw new InvalidDataException("Unknown settlement body: " + e.BodyCase);
        }
    }
    private static bool Observable(Inctor2 pos) => (bool)AccessTools.Method(typeof(GS_Battle), "CanObserve").Invoke(GS_Battle.self, new object[] { pos });
    private static UnityEngine.Vector3 World(Inctor2 pos, bool unit) => (UnityEngine.Vector3)AccessTools.Method(typeof(SS_ANNW_Game), unit ? "GetWPUnit" : "GetWP", new[] { typeof(Inctor2) }).Invoke(null, new object[] { pos });
    private static void Visual(string name, Inctor2 pos, bool unit)
    {
        if (!string.IsNullOrEmpty(name) && Observable(pos)) SingletonMonoAuto<VFX>.self.CreateVFX(name, World(pos, unit));
    }
    internal static void EffectVisual(Event e)
    {
        if (e.Pulse is not null && e.Children.Any(x => x.Damage is not null)) Visual("Unit_AutoAttack_OneShot", Unit(e.TargetUnitId).pos, true);
        if (e.Effect?.EffectType == nameof(UnitEffect_ShockChain))
        {
            var damage = e.Children.FirstOrDefault(x => x.Damage is not null);
            if (damage is null) return;
            var victim = Unit(e.Effect.OtherUnitId); var target = Unit(damage.TargetUnitId);
            if (!Observable(victim.pos) && !Observable(target.pos)) return;
            var vfx = SingletonMonoAuto<VFX>.self.CreateVFX("UE_ShockChain", World(victim.pos, true) + UnityEngine.Vector3.up * .3f, null, null, false);
            if (vfx is VFX_Beam beam) beam.SetTarget(World(target.pos, true) + UnityEngine.Vector3.up * .3f);
            vfx.Play();
        }
    }
    private void Simple(Event e, Action action) { if (!Enter(e, out var scope)) throw new InvalidOperationException("Missing explicit settlement."); using (scope) action(); }
    internal static EffectHost EffectHostFor(Event e) => e.TargetUnitId != 0 ? Unit(e.TargetUnitId).effect_host : PlayerAt(e.PlayerIndex).effect_host;
    internal static Effect EffectFor(long unit, int player, string name, int index)
    {
        var host = unit != 0 ? Unit(unit).effect_host : PlayerAt(player).effect_host;
        return host.effects.Where(x => x.sd_effect.name == name).ElementAt(index);
    }
    private static object? EffectArgument(ParameterInfo p, Event e)
    {
        var f = e.Effect;
        if (p.ParameterType == typeof(UnitData)) return MaybeUnit(f.OtherUnitId);
        if (p.ParameterType == typeof(float)) return p.Name == "damage" ? f.Damage : f.Value;
        if (p.ParameterType == typeof(bool)) return p.Name == "preview" ? f.Preview : f.Killed;
        if (p.ParameterType == typeof(DieReason)) return (DieReason)f.Reason;
        if (p.ParameterType == typeof(Inctor2)) return new Inctor2(f.X, f.Y);
        throw new InvalidDataException("Unsupported effect argument: " + p);
    }
    internal IEnumerator Track(IEnumerator original, Action? completed = null, bool deathAnimation = false, bool waitForReplay = false)
    {
        // Forced actions own native execution flags after DoActionAni returns.
        // Keep their replay scope alive through that tail; visual death work
        // continues to follow host death-commit events independently.
        var tracked = !Replaying || waitForReplay;
        if (tracked) pending++;
        return TrackInner(original, completed, deathAnimation, tracked);
    }
    private IEnumerator TrackInner(IEnumerator original, Action? completed, bool deathAnimation, bool tracked)
    {
        using var stack = new CoroutineStack(original);
        try
        {
            while (!canceled)
            {
                bool more; var previous = SuppressDeathTail;
                try
                {
                    SuppressDeathTail = Replaying && deathAnimation;
                    using (ExecutionContext.Enter(Replaying ? ExecutionOrigin.ClientReplay : ExecutionOrigin.HostAuthority)) more = stack.MoveNext();
                }
                catch (Exception ex) { failure ??= ex; yield break; }
                finally { SuppressDeathTail = previous; }
                if (!more) { if (!canceled) completed?.Invoke(); yield break; }
                yield return stack.Current;
            }
        }
        finally { if (tracked) pending--; }
    }
    internal void Emit(Event e) { if (!Enter(e, out var scope)) return; scope!.Dispose(); }
}
