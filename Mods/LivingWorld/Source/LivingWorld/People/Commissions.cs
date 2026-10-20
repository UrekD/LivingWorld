using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>
    /// A recurring merchant orders goods from the colony for their home town: what the town is short of (its market),
    /// so many by their next visit, at a better price. When they return the colony hands the goods over (silver from
    /// the town's treasury, the shortage eases, the colony's name rises there) or disappoints them. If the order is due
    /// and the merchant has not come, their town sends them.
    /// </summary>
    public static class Commissions
    {
        public const float PriceFactor = 1.6f;
        public const int DueDays = 20;
        public static int StatOffers, StatDelivered;
        /// <summary>Tests: offer on every visit.</summary>
        public static bool TestAlways;

        /// <summary>The merchant figure is on the colony map with their caravan: deliver an open order, or offer a new one.</summary>
        public static void Notify_MerchantOnMap(Figure f, Settlement home, Map map)
        {
            if (f.commissionDef != null)
            {
                Deliver(f, home, map);
                return;
            }
            if (TestAlways || Rand.Chance(0.5f + 0.05f * Legends.Tier(Legends.Renown(LivingWorldComponent.Instance)))) Offer(f, home);
        }

        /// <summary>The commodity the town needs most (with goods the colony could make), and one concrete good for it.</summary>
        public static CommissionGood PickGood(CompSettlementLife life)
        {
            CommodityDef need = null;
            float worst = 1.15f;
            foreach (CommodityDef c in Commodities.All)
            {
                if (c.commissionGoods.NullOrEmpty()) continue;
                float f = life.PriceFactor(c);
                if (f > worst)
                {
                    worst = f;
                    need = c;
                }
            }
            if (need == null && TestAlways) need = Commodities.All.FirstOrDefault(c => !c.commissionGoods.NullOrEmpty());
            return need?.commissionGoods.Where(g => g.thing != null).RandomElementWithFallback();
        }

        private static void Offer(Figure f, Settlement home)
        {
            CompSettlementLife life = home.GetComponent<CompSettlementLife>();
            CommissionGood good = life == null ? null : PickGood(life);
            if (good == null) return;
            int price = Mathf.RoundToInt(good.thing.BaseMarketValue * good.count * PriceFactor / 5f) * 5;
            StatOffers++;
            var letter = (ChoiceLetter_Commission)LetterMaker.MakeLetter(LWLetterDefOf.LW_Commission);
            letter.Label = "LW_Commission_OfferLabel".Translate(f.name);
            letter.Text = "LW_Commission_OfferText".Translate(f.pawn.Named("PAWN"), home.Label.Named("HOME"), good.count.Named("COUNT"), good.thing.label.Named("THING"),
                price.Named("SILVER"), DueDays.Named("DAYS"));
            letter.lookTargets = f.pawn;
            letter.relatedFaction = f.pawn.Faction;
            letter.figureId = f.id;
            letter.thing = good.thing;
            letter.count = good.count;
            letter.price = price;
            letter.delivery = false;
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>False when it cannot be done now (the letter stays, e.g. to unforbid the goods during the visit).</summary>
        public static bool Accept(ChoiceLetter_Commission l)
        {
            Figure f = Figures.ById(l.figureId);
            if (f == null || !f.Alive) return true; // the merchant is gone: nothing to do, drop the letter
            if (!l.delivery)
            {
                f.commissionDef = l.thing;
                f.commissionCount = l.count;
                f.commissionPrice = l.price;
                f.commissionDue = Find.TickManager.TicksGame + GenDate.TicksPerDay * DueDays;
                return true;
            }
            Map map = f.pawn?.MapHeld ?? Neighbourhood.Home;
            if (map == null || CountOn(map, l.thing) < l.count)
            {
                Messages.Message("LW_Commission_NotEnough".Translate(l.count, l.thing.label), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            Take(map, l.thing, l.count);
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            FactionState fs = comp.StateFor(f.pawn?.Faction);
            if (fs != null) fs.treasury = Mathf.Max(0f, fs.treasury - l.price);
            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = l.price;
            GenPlace.TryPlaceThing(silver, DropCellFinder.TradeDropSpot(map), map, ThingPlaceMode.Near);
            if (LivingWorldComponent.WorldObjectById(f.homeId)?.GetComponent<CompSettlementLife>() is CompSettlementLife home)
            {
                CommodityDef c = Commodities.Of(l.thing);
                if (c != null) home.AddImport(c, l.thing.BaseMarketValue * l.count / c.silverPerUnit);
                home.fame = Mathf.Clamp(home.fame + 0.2f, -1f, 1f);
            }
            StatDelivered++;
            ChronicleEventDef def = LWChronicleDefOf.LW_CommissionDelivered;
            comp.chronicle.Record(def, def.text.Formatted(Faction.OfPlayer.Name.Named("COLONY"), l.count.Named("COUNT"), l.thing.label.Named("THING"), f.name.Named("NAME"),
                (f.homeName ?? "?").Named("HOME")).Resolve(), Faction.OfPlayer, f.pawn?.Faction, LivingWorldComponent.WorldObjectById(f.homeId));
            f.ClearCommission();
            return true;
        }

        public static void Decline(ChoiceLetter_Commission l)
        {
            if (!l.delivery) return;
            Figure f = Figures.ById(l.figureId);
            if (f?.commissionDef == l.thing) Disappoint(f); // not when the order already lapsed
        }

        private static void Deliver(Figure f, Settlement home, Map map)
        {
            if (CountOn(map, f.commissionDef) < f.commissionCount)
            {
                // Early (the order is not due yet): they will come back for it, no harm done.
                if (Find.TickManager.TicksGame < f.commissionDue)
                {
                    Messages.Message("LW_Commission_NotYet".Translate(f.pawn.Named("PAWN"), f.commissionCount.Named("COUNT"), f.commissionDef.label.Named("THING")), f.pawn, MessageTypeDefOf.NeutralEvent, historical: false);
                    return;
                }
                Messages.Message("LW_Commission_Missing".Translate(f.pawn.Named("PAWN"), f.commissionCount.Named("COUNT"), f.commissionDef.label.Named("THING")), f.pawn, MessageTypeDefOf.NegativeEvent);
                Disappoint(f);
                return;
            }
            var letter = (ChoiceLetter_Commission)LetterMaker.MakeLetter(LWLetterDefOf.LW_Commission);
            letter.Label = "LW_Commission_DeliveryLabel".Translate(f.name);
            letter.Text = "LW_Commission_DeliveryText".Translate(f.pawn.Named("PAWN"), f.commissionCount.Named("COUNT"), f.commissionDef.label.Named("THING"), f.commissionPrice.Named("SILVER"));
            letter.lookTargets = f.pawn;
            letter.relatedFaction = f.pawn.Faction;
            letter.figureId = f.id;
            letter.thing = f.commissionDef;
            letter.count = f.commissionCount;
            letter.price = f.commissionPrice;
            letter.delivery = true;
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
        }

        private static void Disappoint(Figure f)
        {
            if (f == null) return;
            if (LivingWorldComponent.WorldObjectById(f.homeId)?.GetComponent<CompSettlementLife>() is CompSettlementLife home)
                home.fame = Mathf.Clamp(home.fame - 0.05f, -1f, 1f);
            f.ClearCommission();
        }

        /// <summary>Daily: an order that is due brings its merchant (their town sends a caravan with them).</summary>
        public static void DailyCheck(LivingWorldComponent comp)
        {
            int now = Find.TickManager.TicksGame;
            Map home = Neighbourhood.Home;
            if (home == null) return;
            foreach (Figure f in comp.figures)
            {
                if (f.commissionDef == null || now < f.commissionDue) continue;
                // Long overdue (the merchant never made it, or the letters were ignored): the order lapses.
                if (now > f.commissionDue + GenDate.TicksPerDay * DueDays)
                {
                    // The goods were waiting and nobody came: no one's fault, the order just lapses.
                    if (CountOn(home, f.commissionDef) >= f.commissionCount) f.ClearCommission();
                    else Disappoint(f);
                    continue;
                }
                if (now - f.commissionSummoned < GenDate.TicksPerDay * 5 || !f.Available) continue;
                Faction fac = f.pawn.Faction;
                if (fac == null || fac.HostileTo(Faction.OfPlayer)) continue;
                f.commissionSummoned = now;
                Bridge.PreferredTradeSource[fac.loadID] = f.homeId;
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentDefOf.TraderCaravanArrival.category, home);
                parms.faction = fac;
                bool ok = IncidentDefOf.TraderCaravanArrival.Worker.CanFireNow(parms) && IncidentDefOf.TraderCaravanArrival.Worker.TryExecute(parms);
                Bridge.PreferredTradeSource.Remove(fac.loadID);
                if (Testing.AutoTest.Active) LWLog.Message($"[commissions] {f.name} of {f.homeName} sent for the order: {ok}");
            }
        }

        /// <summary>The goods on the map, forbidden ones too: forbidding is how the colony keeps an order from being used up
        /// (colonists eat ordered meals) until the merchant comes.</summary>
        private static IEnumerable<Thing> Available(Map map, ThingDef def)
        {
            foreach (Thing t in map.listerThings.ThingsOfDef(def))
                if (t.Spawned && !t.Position.Fogged(map) && (t.Faction == null || t.Faction == Faction.OfPlayer)) yield return t;
        }

        public static int CountOn(Map map, ThingDef def) => Available(map, def).Sum(t => t.stackCount);

        private static void Take(Map map, ThingDef def, int count)
        {
            foreach (Thing t in Available(map, def).ToList())
            {
                if (count <= 0) break;
                int take = Mathf.Min(count, t.stackCount);
                t.SplitOff(take).Destroy();
                count -= take;
            }
        }
    }

    /// <summary>A merchant's order (accept or not) or its delivery (hand the goods over or not).</summary>
    public class ChoiceLetter_Commission : ChoiceLetter
    {
        public int figureId = -1;
        public ThingDef thing;

        public override bool CanDismissWithRightClick => false;
        public int count;
        public int price;
        public bool delivery;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                if (ArchivedOnly)
                {
                    yield return Option_Close;
                    yield break;
                }
                yield return new DiaOption((delivery ? "LW_Commission_Deliver" : "LW_Commission_Accept").Translate(price))
                {
                    action = () =>
                    {
                        // Short of goods during the visit: the letter stays, so they can still be handed over.
                        if (Commissions.Accept(this)) Find.LetterStack.RemoveLetter(this);
                    },
                    resolveTree = true,
                };
                yield return new DiaOption("RejectLetter".Translate())
                {
                    action = () =>
                    {
                        Commissions.Decline(this);
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
            Scribe_Values.Look(ref figureId, "figure", -1);
            Scribe_Defs.Look(ref thing, "thing");
            Scribe_Values.Look(ref count, "count");
            Scribe_Values.Look(ref price, "price");
            Scribe_Values.Look(ref delivery, "delivery");
        }
    }
}
