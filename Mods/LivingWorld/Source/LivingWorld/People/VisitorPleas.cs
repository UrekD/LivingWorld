using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    public enum PleaKind : byte { BuyFood, Refuge, Mercenary }

    /// <summary>
    /// Visitors act on their story: people from a starving town ask to buy food (meals, at a good price, paid by their
    /// faction), people from a sacked town ask that one of them may stay with the colony. A letter on the colony map with
    /// two answers; silence counts as no.
    /// </summary>
    public static class VisitorPleas
    {
        public const int Meals = 10;
        public const float PriceFactor = 1.5f;
        public static int StatPleas;

        /// <summary>Hourly, for each visiting group with a story: asks once, a few hours after arriving.</summary>
        public static void TryPlea(LivingWorldComponent comp, IncidentContext ctx, Lord lord)
        {
            if (ctx.pleaAsked || ctx.kind == ContextKind.Raid || Find.TickManager.TicksGame - ctx.createdTicks < GenDate.TicksPerHour * 3) return;
            var story = (HomeStory)ctx.story;
            if (story != HomeStory.Starving && story != HomeStory.Sacked && story != HomeStory.War) return;
            ctx.pleaAsked = true;
            if (!Rand.Chance(TestAlways ? 1f : 0.6f)) return;
            Map map = lord.Map;
            Faction f = lord.faction;
            if (story == HomeStory.Starving)
            {
                List<Thing> meals = ColonyMeals(map);
                int have = meals.Sum(t => t.stackCount);
                if (have < Meals) return;
                // Priced from the meals' base value (the cheapest kind the colony has), above the market.
                float price = meals.Min(t => t.def.BaseMarketValue) * Meals * PriceFactor;
                if (Testing.AutoTest.Active) LWLog.Message("[pleas] colony meals: " + string.Join(", ", meals.Select(t => $"{t.def.defName}x{t.stackCount}@{t.def.BaseMarketValue:F1}")));
                Send(PleaKind.BuyFood, lord, ctx, null, Mathf.RoundToInt(price / 5f) * 5);
            }
            else if (story == HomeStory.War)
            {
                // Their town is at war: they would hire the colony's best fighter for a few days.
                Pawn fighter = Mercenaries.BestFighter(map);
                if (fighter != null) Send(PleaKind.Mercenary, lord, ctx, fighter, Mercenaries.PayFor(map));
            }
            else
            {
                Pawn asker = lord.ownedPawns.Where(p => p.RaceProps.Humanlike && p.Spawned && !p.Downed && !p.mindState.wantsToTradeWithColony
                                                        && Figures.Of(p) == null && p.Faction?.leader != p && p.guest != null)
                    .OrderBy(p => p.ageTracker.AgeBiologicalYears).FirstOrDefault();
                if (asker != null) Send(PleaKind.Refuge, lord, ctx, asker, 0);
            }
        }

        /// <summary>Tests: every eligible group asks.</summary>
        public static bool TestAlways;

        private static List<Thing> ColonyMeals(Map map)
        {
            var list = new List<Thing>();
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
                // Proper meals only (pemmican counts as a "meal" by preference but is a sliver of one).
                if (t.def.ingestible != null && t.def.ingestible.IsMeal && t.GetStatValue(StatDefOf.Nutrition) >= 0.5f && t.Spawned && !t.IsForbidden(Faction.OfPlayer) && !t.Position.Fogged(map))
                    list.Add(t);
            return list;
        }

        private static void Send(PleaKind kind, Lord lord, IncidentContext ctx, Pawn asker, int silver)
        {
            StatPleas++;
            var letter = (ChoiceLetter_VisitorPlea)LetterMaker.MakeLetter(LWLetterDefOf.LW_VisitorPlea);
            Faction f = lord.faction;
            letter.Label = ("LW_Plea_" + kind + "Label").Translate(ctx.originName);
            letter.Text = ("LW_Plea_" + kind + "Text").Translate(f.NameColored.Named("FACTION"), (ctx.originName ?? "?").Named("HOME"), Meals.Named("COUNT"),
                silver.Named("SILVER"), (asker != null ? asker.Named("PAWN") : "?".Named("PAWN")));
            letter.lookTargets = asker != null ? new LookTargets(asker) : new LookTargets(lord.ownedPawns);
            letter.relatedFaction = f;
            letter.kind = kind;
            letter.faction = f;
            letter.asker = asker;
            letter.silver = silver;
            letter.homeId = ctx.originId;
            letter.map = lord.Map;
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
        }

        public static void Accept(ChoiceLetter_VisitorPlea l)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            CompSettlementLife home = LivingWorldComponent.WorldObjectById(l.homeId)?.GetComponent<CompSettlementLife>();
            if (l.kind == PleaKind.BuyFood)
            {
                Map map = l.map;
                if (map == null) return;
                int left = Meals;
                foreach (Thing t in ColonyMeals(map))
                {
                    if (left <= 0) break;
                    int take = Mathf.Min(left, t.stackCount);
                    t.SplitOff(take).Destroy();
                    left -= take;
                }
                if (left > 0) return; // the meals were eaten meanwhile
                FactionState fs = comp.StateFor(l.faction);
                if (fs != null) fs.treasury = Mathf.Max(0f, fs.treasury - l.silver);
                Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
                silver.stackCount = l.silver;
                GenPlace.TryPlaceThing(silver, DropCellFinder.TradeDropSpot(map), map, ThingPlaceMode.Near);
                if (home != null)
                {
                    home.AddImport(LWDefOf.LW_Food, l.silver / PriceFactor / LWDefOf.LW_Food.silverPerUnit);
                    home.fame = Mathf.Clamp(home.fame + 0.1f, -1f, 1f);
                }
            }
            else if (l.kind == PleaKind.Mercenary)
            {
                Pawn fighter = l.asker;
                if (fighter == null) return;
                Mercenaries.Hire(fighter, l.faction, LivingWorldComponent.WorldObjectById(l.homeId) as Settlement, l.silver);
            }
            else
            {
                Pawn p = l.asker;
                if (p == null || p.Dead || !p.Spawned || p.Faction != l.faction) return;
                p.GetLord()?.RemovePawn(p);
                p.SetFaction(Faction.OfPlayer);
                Legends.Add(p, "LW_Deed_Refuge".Translate(LivingWorldComponent.WorldObjectById(l.homeId)?.Label ?? "?").Resolve());
                if (home != null) home.fame = Mathf.Clamp(home.fame + 0.15f, -1f, 1f);
                l.faction?.TryAffectGoodwillWith(Faction.OfPlayer, 5, canSendMessage: false);
                Messages.Message("LW_Plea_RefugeJoined".Translate(p.Named("PAWN")), p, MessageTypeDefOf.PositiveEvent);
            }
        }
    }

    public class ChoiceLetter_VisitorPlea : ChoiceLetter
    {
        public PleaKind kind;
        public Faction faction;
        public Pawn asker;
        public int silver;
        public int homeId = -1;
        public Map map;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                if (ArchivedOnly)
                {
                    yield return Option_Close;
                    yield break;
                }
                yield return new DiaOption(kind == PleaKind.BuyFood ? "LW_Plea_BuyFoodAccept".Translate(silver)
                    : kind == PleaKind.Mercenary ? "LW_Plea_MercenaryAccept".Translate(asker?.LabelShort ?? "?", silver)
                    : "LW_Plea_RefugeAccept".Translate(asker?.LabelShort ?? "?"))
                {
                    action = () =>
                    {
                        VisitorPleas.Accept(this);
                        Find.LetterStack.RemoveLetter(this);
                    },
                    resolveTree = true,
                };
                yield return Option_Reject;
                yield return Option_Postpone;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref kind, "kind");
            Scribe_References.Look(ref faction, "faction");
            Scribe_References.Look(ref asker, "asker");
            Scribe_Values.Look(ref silver, "silver");
            Scribe_Values.Look(ref homeId, "home", -1);
            Scribe_References.Look(ref map, "map");
        }
    }
}
