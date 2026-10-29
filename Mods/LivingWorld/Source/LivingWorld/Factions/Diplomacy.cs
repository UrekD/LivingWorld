using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>A number attached to a faction pair (by loadID), e.g. recent trade between them.</summary>
    public class PairValue : IExposable
    {
        public int a, b;
        public float value;

        public void ExposeData()
        {
            Scribe_Values.Look(ref a, "a");
            Scribe_Values.Look(ref b, "b");
            Scribe_Values.Look(ref value, "v");
        }
    }

    /// <summary>
    /// NPC↔NPC relations: daily goodwill drift from neighbourhood, trade, faith, grudges and common enemies; war
    /// declarations, alliances and peace. The truth is vanilla goodwill (so pawns on maps agree); <see cref="War"/>
    /// records are reconciled against it every day.
    /// </summary>
    public static class Diplomacy
    {
        private static readonly Dictionary<long, float> driftCarry = new Dictionary<long, float>();
        /// <summary>Nearest settlement distance per faction pair, refreshed every 10 days (borders rarely move).</summary>
        private static readonly Dictionary<long, (float dist, int tick)> nearestCache = new Dictionary<long, (float, int)>();

        public static void ResetStatics()
        {
            driftCarry.Clear();
            nearestCache.Clear();
        }

        /// <summary>The goodwill at which vanilla's relation turns hostile (at or below it).</summary>
        private const int WarGoodwill = -75;

        public static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        /// <summary>Can this pair's relation be changed by diplomacy (vanilla allows goodwill changes)?</summary>
        public static bool Diplomatic(Faction f) => f != null && f.HasGoodwill && !f.def.permanentEnemy && !f.defeated && !f.IsPlayer;

        public static War ActiveWar(LivingWorldComponent comp, Faction a, Faction b)
        {
            foreach (War w in comp.wars)
                if (w.Active && w.Between(a.loadID, b.loadID)) return w;
            return null;
        }

        public static float TradeBetween(LivingWorldComponent comp, Faction a, Faction b)
        {
            long k = Key(a.loadID, b.loadID);
            foreach (PairValue p in comp.tradeLinks)
                if (Key(p.a, p.b) == k) return p.value;
            return 0f;
        }

        public static void AddTrade(Faction a, Faction b, float value)
        {
            if (a == null || b == null || a == b) return;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            long k = Key(a.loadID, b.loadID);
            foreach (PairValue p in comp.tradeLinks)
                if (Key(p.a, p.b) == k)
                {
                    p.value += value;
                    return;
                }
            comp.tradeLinks.Add(new PairValue { a = a.loadID, b = b.loadID, value = value });
        }

        public static void DailyStep(LivingWorldComponent comp, float pace)
        {
            Reconcile(comp);
            if (!LivingWorldMod.Settings.simulateDiplomacy) return;
            var states = new List<FactionState>();
            foreach (FactionState fs in comp.factionStates)
                if (fs.faction != null && Diplomatic(fs.faction) && fs.settlements.Count > 0) states.Add(fs);
            for (int i = 0; i < states.Count; i++)
                for (int j = i + 1; j < states.Count; j++)
                    PairDay(comp, states[i], states[j], pace);
            foreach (PairValue p in comp.tradeLinks) p.value *= Mathf.Pow(0.97f, pace);
            comp.tradeLinks.RemoveAll(p => p.value < 1f);
        }

        /// <summary>War records follow the real relations: hostilities without a record get one, records without hostility end.</summary>
        private static void Reconcile(LivingWorldComponent comp)
        {
            foreach (War w in comp.wars)
            {
                if (!w.Active) continue;
                Faction a = comp.FactionById(w.factionA), b = comp.FactionById(w.factionB);
                if (a == null || b == null || a.defeated || b.defeated) EndWar(w, null);
                else if (!a.HostileTo(b)) EndWar(w, "LW_WarEnd_Relations".Translate().Resolve());
            }
            foreach (FactionState x in comp.factionStates)
            {
                if (x.faction == null || !Diplomatic(x.faction)) continue;
                foreach (FactionState y in comp.factionStates)
                {
                    if (y.faction == null || y.factionId <= x.factionId || !Diplomatic(y.faction) || !x.faction.HostileTo(y.faction)) continue;
                    if (ActiveWar(comp, x.faction, y.faction) != null) continue;
                    // First reconcile: old feuds from world generation. Later: hostility from outside (quests, other mods).
                    bool ancient = !comp.warsSeeded;
                    StartWar(comp, x.faction, y.faction, ancient ? "LW_WarCause_Ancient".Translate().Resolve() : "LW_WarCause_Unknown".Translate().Resolve(), ancient, announce: !ancient);
                }
            }
            comp.warsSeeded = true;
        }

        private static void PairDay(LivingWorldComponent comp, FactionState x, FactionState y, float pace)
        {
            Faction a = x.faction, b = y.faction;
            int gw = a.GoodwillWith(b);
            War war = ActiveWar(comp, a, b);
            // Natural rivalry: without reasons to be friends, neighbours drift apart.
            float drift = Rand.Range(-0.6f, 0.6f) - 0.1f;
            drift += NeighbourDrift(x, y);
            drift += Mathf.Min(0.5f, TradeBetween(comp, a, b) / 2000f);
            if (Mathf.Abs((int)KnowHow.Effective(a) - (int)KnowHow.Effective(b)) >= 2) drift -= 0.1f;
            Ideo ia = a.ideos?.PrimaryIdeo, ib = b.ideos?.PrimaryIdeo;
            if (ia != null && ib != null) drift += ia == ib ? 0.2f : -0.1f;
            drift -= GrudgeAgainst(x, b) * 0.02f + GrudgeAgainst(y, a) * 0.02f;
            if (CommonEnemy(comp, x, y)) drift += 0.4f;
            // Rulers' goals: a sworn enemy is pushed toward war, a ruler set on prosperity seeks good terms.
            if (x.GoalAgainst(b)) drift -= 0.3f;
            if (y.GoalAgainst(a)) drift -= 0.3f;
            if (x.goal == RulerGoalKind.Prosper) drift += 0.25f;
            if (y.goal == RulerGoalKind.Prosper) drift += 0.25f;
            drift -= 0.12f * (x.TraitFactor(t => t.warPropensity) + y.TraitFactor(t => t.warPropensity) - 2f);
            if (war != null) drift += -0.2f + 0.8f * (x.warWeariness + y.warWeariness);
            else if (gw > 60 && !CommonEnemy(comp, x, y)) drift -= 0.25f; // friendship fades without a reason
            FactionRelationKind before = a.RelationKindWith(b);
            ApplyDrift(a, b, drift * pace);
            gw = a.GoodwillWith(b);
            FactionRelationKind after = a.RelationKindWith(b);
            if (after != before)
            {
                // Relations crossed a threshold by themselves: friendship became alliance, tension became war.
                if (after == FactionRelationKind.Ally)
                {
                    ChronicleEventDef ad = LWChronicleDefOf.LW_AllianceFormed;
                    comp.chronicle.Record(ad, ad.text.Formatted(a.Name.Named("A"), b.Name.Named("B")).Resolve(), a, b);
                }
                else if (after == FactionRelationKind.Hostile && war == null)
                    war = StartWar(comp, a, b, "LW_WarCause_Escalation".Translate().Resolve(), ancient: false, announce: true);
            }
            if (!LivingWorldMod.Settings.simulateDiplomacy) return;
            if (war == null)
            {
                if (gw <= -40) ConsiderWar(comp, x, y, pace);
                else if (gw >= 45 && a.RelationKindWith(b) != FactionRelationKind.Ally && CommonEnemy(comp, x, y) && Rand.Chance(0.05f * pace))
                    Envoys.Send(comp, x, y, EnvoyMission.Alliance);
            }
            else ConsiderPeace(comp, war, x, y, pace);
        }

        /// <summary>
        /// Neighbours drift apart: lands that touch most, settlements close across water or mountains less, farther ones a little.
        /// Without borders (off, or no snapshot yet) the distance alone decides, as before.
        /// </summary>
        private static float NeighbourDrift(FactionState x, FactionState y)
        {
            bool borders = Territory.Ready;
            if (borders && Territory.SharedBorder(x.faction, y.faction) > 0) return -0.4f;
            float near = NearestDistance(x, y);
            return near < 6f ? (borders ? -0.25f : -0.4f) : near < 12f ? -0.15f : 0f;
        }

        internal static void ApplyDrift(Faction a, Faction b, float amount)
        {
            long k = Key(a.loadID, b.loadID);
            driftCarry.TryGetValue(k, out float carry);
            carry += amount;
            int whole = (int)carry;
            if (whole < 0 && (Tributaries.IsClient(a) || Tributaries.IsClient(b)) && !a.HostileTo(b))
            {
                // A free town the colony holds never drifts into a war by itself: it stops short of hostility.
                int room = a.GoodwillWith(b) - (WarGoodwill + 1);
                if (-whole > room)
                {
                    whole = -Mathf.Max(0, room);
                    carry = whole; // nothing carries over: the step below takes it out again
                }
            }
            if (whole != 0)
            {
                a.TryAffectGoodwillWith(b, whole, canSendMessage: false, canSendHostilityLetter: false);
                carry -= whole;
            }
            driftCarry[k] = carry;
        }

        private static float NearestDistance(FactionState x, FactionState y)
        {
            long k = Key(x.factionId, y.factionId);
            int now = SimClock.Now;
            if (nearestCache.TryGetValue(k, out var cached) && now - cached.tick < GenDate.TicksPerDay * 10 && now >= cached.tick) return cached.dist;
            float d = ComputeNearest(x, y);
            nearestCache[k] = (d, now);
            return d;
        }

        private static readonly List<CompSettlementLife> nearBuf = new List<CompSettlementLife>();

        /// <summary>Nearest distance between the factions' settlements, looked up in the spatial index (beyond its range = far).</summary>
        private static float ComputeNearest(FactionState x, FactionState y)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            float best = SpatialIndex.RangeTiles + 1f;
            FactionState small = x.settlements.Count <= y.settlements.Count ? x : y, other = small == x ? y : x;
            foreach (CompSettlementLife s in small.settlements)
            {
                comp.spatial.Near(s.Settlement.Tile, SpatialIndex.RangeTiles, nearBuf);
                foreach (CompSettlementLife t in nearBuf)
                {
                    if (t.Settlement.Faction != other.faction) continue;
                    float d = Find.WorldGrid.ApproxDistanceInTiles(s.Settlement.Tile, t.Settlement.Tile);
                    if (d < best) best = d;
                }
            }
            return best;
        }

        public static float GrudgeAgainst(FactionState fs, Faction other)
        {
            float g = 0f;
            foreach (Grievance gr in fs.grievances)
                if (gr.otherFactionId == other.loadID) g += gr.amount;
            return g;
        }

        public static void AddGrudge(FactionState fs, Faction other, float amount, string reason)
        {
            if (fs == null || other == null) return;
            foreach (Grievance gr in fs.grievances)
                if (gr.otherFactionId == other.loadID)
                {
                    gr.amount += amount;
                    gr.reason = reason;
                    return;
                }
            fs.grievances.Add(new Grievance { otherFactionId = other.loadID, amount = amount, reason = reason, sinceTicks = SimClock.Now });
        }

        private static bool CommonEnemy(LivingWorldComponent comp, FactionState x, FactionState y)
        {
            // Pirates are everyone's enemy: that unites nobody.
            foreach (FactionState z in comp.factionStates)
                if (z != x && z != y && z.faction != null && !z.faction.def.permanentEnemy && z.settlements.Count > 0
                    && x.faction.HostileTo(z.faction) && y.faction.HostileTo(z.faction)) return true;
            return false;
        }

        private static int WarsOf(LivingWorldComponent comp, Faction f)
        {
            int n = 0;
            foreach (War w in comp.wars)
                if (w.Active && w.Involves(f.loadID)) n++;
            return n;
        }

        internal static void ConsiderWar(LivingWorldComponent comp, FactionState x, FactionState y, float pace)
        {
            // A free town the colony holds never starts a war (two of them never fight each other); the other side still can.
            bool xClient = Tributaries.IsClient(x.faction), yClient = Tributaries.IsClient(y.faction);
            if (xClient && yClient) return;
            // Otherwise the more eager side decides.
            FactionState agg = xClient ? y : yClient ? x : x.TraitFactor(t => t.warPropensity) * (1f - x.warWeariness) >= y.TraitFactor(t => t.warPropensity) * (1f - y.warWeariness) ? x : y;
            FactionState def = agg == x ? y : x;
            if (WarsOf(comp, agg.faction) >= 2) return;
            float ratio = (agg.strength + 1f) / (def.strength + 1f);
            float chance = 0.03f * pace * agg.TraitFactor(t => t.warPropensity) * (1f - agg.warWeariness) * Mathf.Clamp(ratio, 0.3f, 2f);
            if (agg.GoalAgainst(def.faction)) chance *= 2f;
            else if (agg.goal == RulerGoalKind.Prosper) chance *= 0.5f;
            if (!Rand.Chance(chance)) return;
            string cause = agg.GoalAgainst(def.faction) && agg.faction.leader != null ? "LW_WarCause_RulerVow".Translate(agg.faction.leader.Name.ToStringShort, Rulers.GoalLabel(comp, agg)).Resolve()
                : GrudgeAgainst(agg, def.faction) > 20f ? "LW_WarCause_Grudge".Translate(def.faction.Name).Resolve()
                : agg.TraitFactor(t => t.expansion) > 1.2f ? "LW_WarCause_Land".Translate().Resolve()
                : Territory.Ready && Territory.SharedBorder(agg.faction, def.faction) == 0 ? "LW_WarCause_Escalation".Translate().Resolve()
                : "LW_WarCause_Border".Translate().Resolve();
            DeclareWar(comp, agg.faction, def.faction, cause);
        }

        public static War DeclareWar(LivingWorldComponent comp, Faction aggressor, Faction target, string cause)
        {
            aggressor.TryAffectGoodwillWith(target, aggressor.GoodwillToMakeHostile(target), canSendMessage: false, canSendHostilityLetter: false);
            if (!aggressor.HostileTo(target)) return null; // vanilla refused (quest lock, defeated, …)
            War w = StartWar(comp, aggressor, target, cause, ancient: false, announce: true);
            AddGrudge(comp.StateFor(target), aggressor, 30f, "LW_Grudge_DeclaredWar".Translate().Resolve());
            // Allies of the attacked keep their word, depending on their honour.
            foreach (FactionState ally in comp.factionStates)
            {
                if (ally.faction == null || ally.faction == aggressor || ally.faction == target || !Diplomatic(ally.faction) || Tributaries.IsClient(ally.faction)) continue;
                if (ally.faction.RelationKindWith(target) != FactionRelationKind.Ally || ally.faction.HostileTo(aggressor)) continue;
                if (!Rand.Chance(0.5f * ally.TraitFactor(t => t.treatyKeeping))) continue;
                ally.faction.TryAffectGoodwillWith(aggressor, ally.faction.GoodwillToMakeHostile(aggressor), canSendMessage: false, canSendHostilityLetter: false);
                if (!ally.faction.HostileTo(aggressor)) continue;
                War jw = StartWar(comp, ally.faction, aggressor, "LW_WarCause_Alliance".Translate(target.Name).Resolve(), ancient: false, announce: false);
                ChronicleEventDef jd = LWChronicleDefOf.LW_JoinedWar;
                comp.chronicle.Record(jd, jd.text.Formatted(ally.faction.Name.Named("ALLY"), target.Name.Named("FRIEND"), aggressor.Name.Named("ENEMY")).Resolve(),
                    ally.faction, aggressor, causeId: w?.chronicleId ?? -1);
                jw.chronicleId = w?.chronicleId ?? -1;
            }
            return w;
        }

        public static War StartWar(LivingWorldComponent comp, Faction a, Faction b, string cause, bool ancient, bool announce)
        {
            var w = new War
            {
                id = comp.NextId(),
                factionA = a.loadID,
                factionB = b.loadID,
                nameA = a.Name,
                nameB = b.Name,
                startTicks = SimClock.Now,
                cause = cause,
                ancient = ancient,
            };
            comp.wars.Add(w);
            if (!ancient) HistoryTotals.WarsStarted++;
            if (announce)
            {
                ChronicleEventDef def = LWChronicleDefOf.LW_WarDeclared;
                w.chronicleId = comp.chronicle.Record(def, def.text.Formatted(a.Name.Named("A"), b.Name.Named("B"), cause.Named("CAUSE")).Resolve(), a, b).id;
            }
            return w;
        }

        private static void ConsiderPeace(LivingWorldComponent comp, War war, FactionState x, FactionState y, float pace)
        {
            if (war.ancient && SimClock.Now - war.startTicks < GenDate.TicksPerQuadrum) return;
            float days = (SimClock.Now - war.startTicks) / (float)GenDate.TicksPerDay;
            if (days < 10f) return;
            float tired = x.warWeariness + y.warWeariness;
            float lopsided = Mathf.Clamp01(Mathf.Abs(war.score) / 2000f);
            float chance = (0.01f + 0.04f * tired + 0.03f * lopsided) * pace;
            if (x.GoalAgainst(y.faction) || y.GoalAgainst(x.faction)) chance *= 0.4f; // a sworn war is not ended lightly
            if (!Rand.Chance(chance)) return;
            // The side that is losing (or more tired) asks.
            FactionState asker = war.ScoreFor(x.factionId) < 0f || (war.score == 0f && x.warWeariness > y.warWeariness) ? x : y;
            FactionState other = asker == x ? y : x;
            Envoys.Send(comp, asker, other, EnvoyMission.Peace);
        }

        /// <summary>Peace between the pair: back to neutral, the loser pays, the record closes.</summary>
        public static void MakePeace(LivingWorldComponent comp, Faction asker, Faction other)
        {
            War war = ActiveWar(comp, asker, other);
            int gw = asker.GoodwillWith(other);
            asker.TryAffectGoodwillWith(other, 5 - gw, canSendMessage: false, canSendHostilityLetter: false);
            if (asker.HostileTo(other)) return;
            FactionState sa = comp.StateFor(asker), so = comp.StateFor(other);
            string terms = "";
            if (war != null && sa != null && so != null && war.ScoreFor(asker.loadID) < -300f)
            {
                float tribute = sa.treasury * 0.25f;
                sa.treasury -= tribute;
                so.treasury += tribute;
                terms = "LW_PeaceTribute".Translate(Mathf.RoundToInt(tribute)).Resolve();
            }
            if (sa != null) sa.warWeariness *= 0.5f;
            if (so != null) so.warWeariness *= 0.5f;
            if (war != null)
            {
                war.endTicks = SimClock.Now;
                war.ended = true;
            }
            ChronicleEventDef def = LWChronicleDefOf.LW_PeaceMade;
            comp.chronicle.Record(def, def.text.Formatted(asker.Name.Named("A"), other.Name.Named("B"), terms.Named("TERMS")).Resolve(), asker, other,
                causeId: war?.chronicleId ?? -1);
        }

        public static void MakeAlliance(LivingWorldComponent comp, Faction a, Faction b)
        {
            int gw = a.GoodwillWith(b);
            if (gw < 80) a.TryAffectGoodwillWith(b, 80 - gw, canSendMessage: false, canSendHostilityLetter: false);
            if (a.RelationKindWith(b) != FactionRelationKind.Ally) return;
            ChronicleEventDef def = LWChronicleDefOf.LW_AllianceFormed;
            comp.chronicle.Record(def, def.text.Formatted(a.Name.Named("A"), b.Name.Named("B")).Resolve(), a, b);
        }

        public static void EndWar(War w, string reason)
        {
            if (!w.Active) return;
            w.endTicks = SimClock.Now;
            w.ended = true;
            if (reason == null || w.ancient) return;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            ChronicleEventDef def = LWChronicleDefOf.LW_WarEnded;
            comp.chronicle.Record(def, def.text.Formatted(w.nameA.Named("A"), w.nameB.Named("B"), reason.Named("REASON")).Resolve(),
                comp.FactionById(w.factionA), comp.FactionById(w.factionB), causeId: w.chronicleId);
        }
    }
}
