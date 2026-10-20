using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>An ally at war asks the colony to destroy an enemy settlement.</summary>
    public class WarRequest : IExposable
    {
        public int askerId = -1;
        public int targetId = -1;
        public int enemyId = -1;
        public string targetName;
        public int reward;
        public int deadlineTicks;
        public bool done;

        public void ExposeData()
        {
            Scribe_Values.Look(ref askerId, "asker", -1);
            Scribe_Values.Look(ref targetId, "target", -1);
            Scribe_Values.Look(ref enemyId, "enemy", -1);
            Scribe_Values.Look(ref targetName, "targetName");
            Scribe_Values.Look(ref reward, "reward");
            Scribe_Values.Look(ref deadlineTicks, "deadline");
            Scribe_Values.Look(ref done, "done");
        }
    }

    /// <summary>
    /// The world asks things of the colony: allies at war want an enemy settlement destroyed (silver and friendship),
    /// strong hostile neighbours demand tribute (pay and their raids go elsewhere for a while, refuse and they march).
    /// </summary>
    public static class Demands
    {
        public const float Range = 20f;

        public static void DailyCheck(LivingWorldComponent comp)
        {
            int now = Find.TickManager.TicksGame;
            foreach (WarRequest r in comp.warRequests)
            {
                // The town is gone or has changed hands (perhaps to the asker itself): the request lapses, no blame.
                if (r.done || (LivingWorldComponent.WorldObjectById(r.targetId) is Settlement t && t.Faction != null && (r.enemyId < 0 || t.Faction.loadID == r.enemyId))) continue;
                r.done = true;
                if (comp.FactionById(r.askerId) is Faction a) Messages.Message("LW_WarRequestMoot".Translate(a.Name, r.targetName), MessageTypeDefOf.NeutralEvent);
            }
            foreach (WarRequest r in comp.warRequests)
                if (!r.done && now > r.deadlineTicks && comp.FactionById(r.askerId) is Faction asker && !asker.defeated)
                {
                    // The ally notices the colony did nothing.
                    asker.TryAffectGoodwillWith(Faction.OfPlayer, -5, canSendMessage: false, canSendHostilityLetter: true, reason: LWHistoryDefOf.LW_IgnoredOurCall);
                    Messages.Message("LW_WarRequestExpired".Translate(asker.Name, r.targetName), MessageTypeDefOf.NegativeEvent);
                }
            comp.warRequests.RemoveAll(r => r.done || now > r.deadlineTicks);
            Map home = Neighbourhood.Home;
            if (home == null) return;
            if (comp.warRequests.Count == 0 && now - comp.lastWarRequestTick > GenDate.TicksPerDay * 15 && Rand.Chance(0.15f)) TryWarRequest(comp, home, now);
            if (now - comp.lastTributeTick > GenDate.TicksPerDay * 30 && Rand.Chance(0.06f)) TryTributeDemand(comp, home, now);
        }

        private static void TryWarRequest(LivingWorldComponent comp, Map home, int now)
        {
            foreach (War w in comp.wars)
            {
                if (!w.Active) continue;
                Faction a = comp.FactionById(w.factionA), b = comp.FactionById(w.factionB);
                if (a == null || b == null) continue;
                // The asker is friendly with the colony, the enemy hostile to it (so the colony can attack).
                Faction asker = a.PlayerGoodwill >= 40 && b.HostileTo(Faction.OfPlayer) ? a : b.PlayerGoodwill >= 40 && a.HostileTo(Faction.OfPlayer) ? b : null;
                if (asker == null || asker.HostileTo(Faction.OfPlayer)) continue;
                Faction enemy = asker == a ? b : a;
                Settlement target = null;
                float best = Neighbourhood.Radius(home.Tile, Range);
                foreach (Settlement s in Find.WorldObjects.Settlements)
                {
                    if (s.Faction != enemy || !SettlementOps.CanFall(s)) continue;
                    float d = Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Tile);
                    if (d < best)
                    {
                        best = d;
                        target = s;
                    }
                }
                if (target == null) continue;
                var req = new WarRequest
                {
                    askerId = asker.loadID,
                    targetId = target.ID,
                    enemyId = enemy.loadID,
                    targetName = target.Label,
                    reward = Mathf.RoundToInt(Mathf.Clamp(StorytellerUtility.DefaultThreatPointsNow(home) * 2f, 400f, 4000f) / 50f) * 50,
                    deadlineTicks = now + GenDate.TicksPerDay * 30,
                };
                comp.warRequests.Add(req);
                comp.lastWarRequestTick = now;
                Find.LetterStack.ReceiveLetter("LW_WarRequestLabel".Translate(asker.Name), "LW_WarRequestText".Translate(asker.NameColored.Named("ASKER"), enemy.NameColored.Named("ENEMY"),
                    target.Label.Named("TARGET"), req.reward.Named("REWARD"), 30.Named("DAYS")), LetterDefOf.NeutralEvent, target, asker);
                return;
            }
        }

        /// <summary>The colony destroyed a settlement: was it something an ally asked for (and still held by the enemy)?</summary>
        public static void Notify_SettlementDestroyed(int settlementId, Faction victim)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            foreach (WarRequest r in comp.warRequests)
            {
                if (r.done || r.targetId != settlementId) continue;
                r.done = true;
                Faction asker = comp.FactionById(r.askerId);
                Map home = Neighbourhood.Home;
                // Taken by someone else meanwhile (perhaps the asker itself): no reward for that.
                if (asker == null || home == null || victim == null || (r.enemyId >= 0 && victim.loadID != r.enemyId) || victim == asker) return;
                asker.TryAffectGoodwillWith(Faction.OfPlayer, 25, reason: LWHistoryDefOf.LW_FoughtOurEnemies);
                Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
                silver.stackCount = r.reward;
                DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(home), home, new List<Thing> { silver }, forbid: false, faction: asker);
                ChronicleEventDef def = LWChronicleDefOf.LW_WarRequestDone;
                comp.chronicle.Record(def, def.text.Formatted(r.targetName.Named("TARGET"), asker.Name.Named("ASKER"), r.reward.Named("REWARD")).Resolve(), Faction.OfPlayer, asker);
                return;
            }
        }

        /// <summary>A request to destroy the target town for the asker (a ruler's audience, …); paid on success.</summary>
        public static WarRequest AddWarRequest(Faction asker, Settlement target, int reward, int days)
        {
            var req = new WarRequest
            {
                askerId = asker.loadID, targetId = target.ID, enemyId = target.Faction?.loadID ?? -1, targetName = target.Label, reward = reward,
                deadlineTicks = Find.TickManager.TicksGame + GenDate.TicksPerDay * days,
            };
            LivingWorldComponent.Instance.warRequests.Add(req);
            return req;
        }

        /// <summary>Test hooks.</summary>
        public static void ForceWarRequest(Faction asker, Settlement target)
        {
            LivingWorldComponent.Instance.warRequests.Add(new WarRequest
            {
                askerId = asker.loadID, targetId = target.ID, enemyId = target.Faction.loadID, targetName = target.Label, reward = 500,
                deadlineTicks = Find.TickManager.TicksGame + GenDate.TicksPerDay * 30,
            });
        }

        /// <summary>Silver the colony could hand over: unforbidden silver lying on the home map (no trade beacon needed).</summary>
        public static int ColonySilver(Map map)
        {
            int n = 0;
            foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Silver))
                if (t.Spawned && !t.IsForbidden(Faction.OfPlayer)) n += t.stackCount;
            return n;
        }

        public static void TakeColonySilver(Map map, int amount)
        {
            foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Silver).ToArray())
            {
                if (amount <= 0) break;
                if (!t.Spawned || t.IsForbidden(Faction.OfPlayer)) continue;
                int take = Mathf.Min(amount, t.stackCount);
                t.SplitOff(take).Destroy();
                amount -= take;
            }
        }

        public static ChoiceLetter_TributeDemand ForceTribute(Faction f, Settlement near, int amount)
        {
            var letter = (ChoiceLetter_TributeDemand)LetterMaker.MakeLetter(LWLetterDefOf.LW_TributeDemand);
            letter.Label = "LW_TributeLabel".Translate(f.Name);
            letter.Text = "LW_TributeText".Translate(f.NameColored.Named("FACTION"), near.Label.Named("ORIGIN"), amount.Named("AMOUNT"));
            letter.faction = f;
            letter.amount = amount;
            letter.homeId = near.ID;
            Find.LetterStack.ReceiveLetter(letter);
            return letter;
        }

        private static void TryTributeDemand(LivingWorldComponent comp, Map home, int now)
        {
            float colonyPoints = StorytellerUtility.DefaultThreatPointsNow(home);
            foreach (FactionState fs in comp.factionStates)
            {
                Faction f = fs.faction;
                if (f == null || f.defeated || !f.HostileTo(Faction.OfPlayer) || fs.settlements.Count == 0) continue;
                bool bully = f.def.permanentEnemy || fs.TraitFactor(t => t.warPropensity) > 1.3f;
                if (!bully || fs.strength < colonyPoints * 2.5f || comp.TributeTruce(f)) continue;
                Settlement near = Bridge.NearestSettlement(f, home.Tile);
                if (near == null || Find.WorldGrid.ApproxDistanceInTiles(near.Tile, home.Tile) > Neighbourhood.Radius(home.Tile, Range)) continue;
                int amount = Mathf.RoundToInt(Mathf.Clamp(home.wealthWatcher.WealthTotal * 0.02f, 200f, 3000f) / 50f) * 50;
                comp.lastTributeTick = now;
                var letter = (ChoiceLetter_TributeDemand)LetterMaker.MakeLetter(LWLetterDefOf.LW_TributeDemand);
                letter.Label = "LW_TributeLabel".Translate(f.Name);
                letter.Text = "LW_TributeText".Translate(f.NameColored.Named("FACTION"), near.Label.Named("ORIGIN"), amount.Named("AMOUNT"));
                letter.lookTargets = near;
                letter.relatedFaction = f;
                letter.faction = f;
                letter.amount = amount;
                letter.homeId = near.ID;
                letter.StartTimeout(GenDate.TicksPerDay * 2);
                Find.LetterStack.ReceiveLetter(letter);
                return;
            }
        }
    }

    /// <summary>Pay the tribute (silver from the colony's stores) or refuse (they march).</summary>
    public class ChoiceLetter_TributeDemand : ChoiceLetter
    {
        public Faction faction;
        public int amount;
        public int homeId = -1;
        /// <summary>Paid or refused; a letter that times out or is dismissed unanswered counts as a refusal.</summary>
        public bool answered;

        public override bool CanDismissWithRightClick => false;

        public void Refuse()
        {
            answered = true;
            if (faction != null && !faction.defeated) Revenge.Schedule(faction, homeId, -1);
        }

        public override void Removed()
        {
            base.Removed();
            if (!answered) Refuse();
        }

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                if (ArchivedOnly)
                {
                    yield return Option_Close;
                    yield break;
                }
                Map home = Neighbourhood.Home;
                var pay = new DiaOption("LW_TributePay".Translate(amount))
                {
                    action = () =>
                    {
                        answered = true;
                        Demands.TakeColonySilver(home, amount);
                        LivingWorldComponent comp = LivingWorldComponent.Instance;
                        if (comp.StateFor(faction) is FactionState fs) fs.treasury += amount;
                        comp.SetTributeTruce(faction, Find.TickManager.TicksGame + GenDate.TicksPerQuadrum);
                        Find.LetterStack.RemoveLetter(this);
                    },
                    resolveTree = true,
                };
                if (home == null || Demands.ColonySilver(home) < amount) pay.Disable("LW_TributeNoSilver".Translate());
                yield return pay;
                yield return new DiaOption("LW_TributeRefuse".Translate())
                {
                    action = () =>
                    {
                        Refuse();
                        Find.LetterStack.RemoveLetter(this);
                    },
                    resolveTree = true,
                };
                yield return Option_Postpone;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref amount, "amount");
            Scribe_Values.Look(ref homeId, "homeId", -1);
            Scribe_Values.Look(ref answered, "answered");
        }
    }

    [DefOf]
    public static class LWLetterDefOf
    {
        public static LetterDef LW_TributeDemand;
        public static LetterDef LW_RansomOffer;
        public static LetterDef LW_RulerAudience;
        public static LetterDef LW_VisitorPlea;
        public static LetterDef LW_Commission;

        static LWLetterDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(LWLetterDefOf));
    }
}
