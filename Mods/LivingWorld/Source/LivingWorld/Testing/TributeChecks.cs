using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Tribute and loyalty (PLAN §4, step A4), the cases of the "agency" and "agencycore" plans that follow <see cref="TributaryChecks"/>' own: a town is held through the
    /// transfer (a yielded town, no map), then given a known state, and the day of tribute is driven by hand (<c>DailyStep(comp, pace, record)</c>, so a town another
    /// case left held is not billed under it). Everything a case spoils (the town, its client's treasury, the colony's stores) is disposable; the in-memory switches are put back.
    /// </summary>
    public static partial class TributaryChecks
    {
        private const float One = 1f;
        // The record a case leaves held across the halfway save, with a payment due on the first day after the load.
        private static int boundaryRecordId = -1, boundaryTownId = -1;
        private static string boundarySignature;

        // ---- Helpers ---------------------------------------------------------------------------------------

        /// <summary>The held town most cases share (one town for all of them: the world has few): the case that needs it first makes it, the others find it held.</summary>
        private static Case fixture;

        private static Case Fixture(string c)
        {
            if (fixture != null && fixture.record.state == TributaryState.Held && !fixture.town.Destroyed && fixture.town.Faction == ClientOf(fixture.record) && Tributaries.ColonyFor(fixture.town).map != null)
            {
                Fund(fixture);
                return fixture;
            }
            fixture = HeldTown(c);
            return fixture;
        }

        /// <summary>A held town with a colony to pay, in a known state: 300 people, prosperity 0.6, calm, a rich client, nothing in the stores (no goods), the normal rate.</summary>
        private static Case HeldTown(string c, Func<Settlement, bool> where = null)
        {
            // A convoy walks only where the colony can be reached: the cases that count on one need a town that can.
            Case x = Start(c, where ?? (t => Tributaries.ColonyFor(t).reachable), pump: true);
            if (x == null) return null;
            if (x.record.state != TributaryState.Held || ClientOf(x.record) == null)
            {
                problems.Add($"{c}: the town is {x.record.state} (no held town to charge)");
                return null;
            }
            if (Tributaries.ColonyFor(x.town).map == null)
            {
                Skip(c, "no colony the town could pay");
                Finish(x);
                return null;
            }
            Fund(x);
            return x;
        }

        private static void Fund(Case x)
        {
            CompSettlementLife life = Life(x);
            life.population = 300f;
            life.prosperity = 0.6f;
            life.unrest = 0.35f;
            life.recentTrade = 0f;
            life.specialty = null;
            for (int i = 0; i < life.stock.Length; i++) life.stock[i] = 0f;
            Comp.StateFor(ClientOf(x.record)).treasury = 5000f;
            Blank(x.record);
        }

        /// <summary>A case of the shared town is over: nothing of it stays on the road. The town stays held for the next.</summary>
        private static void Clean(Case x)
        {
            foreach (WorldParty p in WorldParty.All.Where(p => p.kind == LWPartyDefOf.LW_Tribute && p.recordId == x.record.id).ToList()) p.Destroy();
            Tributaries.TestRevolt = Tributaries.TestLiberate = null;
            Tributaries.TestNoColony = Tributaries.TestNoParty = Tributaries.TestPlaceFails = false;
        }

        private static void Blank(TributaryRecord r)
        {
            r.rate = TributeRate.Normal;
            r.accruedSilver = r.arrears = r.shareDays = r.accrualDays = r.paidTotal = 0f;
            r.payments = r.missed = 0;
            r.lastRateTick = r.lastGiftTick = r.lastWarningTick = FactionState.Never;
            r.rateDays = TributaryRecord.RateOpen;
            r.protectedUntilTick = r.abandonedUntilTick = FactionState.Never;
        }

        private static CompSettlementLife Life(Case x) => x.town.GetComponent<CompSettlementLife>();

        private static FactionState StateOf(Case x) => Comp.StateFor(ClientOf(x.record));

        private static Map ColonyOf(Case x) => Tributaries.ColonyFor(x.town).map;

        /// <summary>The case is over: nothing of it stays on the road, the town is let go and the budget is as it was.</summary>
        private static void Finish(Case x)
        {
            if (x.record.state == TributaryState.Held) Tributaries.Release(x.record); // sends what is accrued as a last payment
            foreach (WorldParty p in WorldParty.All.Where(p => p.kind == LWPartyDefOf.LW_Tribute && p.recordId == x.record.id).ToList()) p.Destroy();
            Comp.clientFactionsCreated = x.created;
        }

        /// <summary>Days of tribute, driven by hand with the revolt switched off (a case that tests a revolt sets it itself).</summary>
        private static void Days(Case x, int n, float pace = One, bool revolt = false)
        {
            Tributaries.TestRevolt = revolt ? (bool?)null : false;
            for (int i = 0; i < n; i++) Tributaries.DailyStep(Comp, pace, x.record);
            Tributaries.TestRevolt = null;
        }

        /// <summary>One day's tribute at a rate: its share of the town's income.</summary>
        private static float Due(CompSettlementLife life, TributeRate rate, float pace = One) =>
            Tributaries.Share(rate) * life.population * Tuning.T.taxPerCapitaPerDay * (0.4f + life.prosperity) * pace;

        private static WorldParty Convoy(TributaryRecord r) => WorldParty.All.FirstOrDefault(p => p.kind == LWPartyDefOf.LW_Tribute && p.recordId == r.id && !p.Destroyed);

        /// <summary>A convoy reaching its destination, as the party's own tick does it.</summary>
        private static void Arrive(WorldParty p)
        {
            if (p.kind.Worker.ArrivedAtDestination(p)) p.Disband();
        }

        private static int Count(Map map, ThingDef def) => map.listerThings.ThingsOfDef(def).Sum(t => t.stackCount);

        private static int Pods(Map map) => map.listerThings.ThingsOfDef(ThingDefOf.DropPodIncoming).Count;

        /// <summary>A payment on the road: 15 days at the normal rate with a surplus of food in the stores (goods in the cargo).</summary>
        private static WorldParty MakeConvoy(string c, Case x, bool goods)
        {
            CompSettlementLife life = Life(x);
            if (goods) life.stock[LWDefOf.LW_Food.index] = 1.2f * life.TargetStock(LWDefOf.LW_Food) + 400f;
            Days(x, Tuning.T.tributeIntervalDays);
            WorldParty p = Convoy(x.record);
            if (p == null) problems.Add($"{c}: no convoy was made (payments {x.record.payments}, parties {WorldParty.All.Count} of {LivingWorldMod.Settings.maxParties})");
            return p;
        }

        // ---- Accrual, payment ------------------------------------------------------------------------------

        /// <summary>15 daily steps: accrued is the sum of the daily dues and the client's treasury is down by as much; the payment falls due on the last, in tens, the rest stays.</summary>
        private static void Accrual()
        {
            const string c = "tribute: accrual";
            Case x = Fixture(c);
            if (x == null) return;
            try
            {
                TributaryRecord r = x.record;
                CompSettlementLife life = Life(x);
                FactionState fs = StateOf(x);
                float sum = 0f, start = fs.treasury;
                for (int i = 0; i < 14; i++)
                {
                    sum += Due(life, TributeRate.Normal);
                    Days(x, 1);
                }
                Expect(c, Math.Abs(r.accruedSilver - sum) <= 1f, $"accrued {r.accruedSilver:F2}, the daily dues add up to {sum:F2}");
                Expect(c, Math.Abs(start - fs.treasury - sum) <= 1f, $"the treasury went down by {start - fs.treasury:F2}, expected {sum:F2}");
                Expect(c, Math.Abs(r.accrualDays - 14f) < 0.001f && Math.Abs(r.shareDays - 7f) < 0.001f && r.arrears == 0f, $"accrualDays {r.accrualDays} shareDays {r.shareDays} arrears {r.arrears}");
                Expect(c, Convoy(r) == null && r.payments == 0, "a payment was made before it fell due");
                sum += Due(life, TributeRate.Normal);
                Days(x, 1);
                int paid = Mathf.FloorToInt(sum / 10f) * 10;
                Expect(c, r.accrualDays == 0f && r.shareDays == 0f && r.arrears == 0f, "the payment did not reset the period");
                Expect(c, Math.Abs(r.accruedSilver - (sum - paid)) <= 1f, $"{r.accruedSilver:F2} stays accrued after paying {paid} (the dues were {sum:F2})");
                WorldParty p = Convoy(r);
                if (p != null) Expect(c, Math.Abs(p.silver - paid) <= 1f, $"the convoy carries {p.silver} silver, expected {paid}");
                else Expect(c, r.payments == 1, "no convoy and no payment by pod");
                Expect(c, r.missed == 0, "a full payment counted as short");
            }
            finally
            {
                Clean(x);
            }
        }

        /// <summary>A treasury at the reserve cannot pay: it is left alone, what was due is arrears, and the payment is short (a message, a count, no debt carried over).</summary>
        private static void Short()
        {
            const string c = "tribute: short";
            Case x = Fixture(c);
            if (x == null) return;
            try
            {
                TributaryRecord r = x.record;
                CompSettlementLife life = Life(x);
                FactionState fs = StateOf(x);
                float reserve = Tuning.T.tributeTreasuryReserve, day = Due(life, TributeRate.Normal);
                fs.treasury = reserve + 30f;
                Days(x, 2);
                Expect(c, Math.Abs(r.accruedSilver - 30f) <= 0.01f && Math.Abs(fs.treasury - reserve) <= 0.01f, $"two days took {r.accruedSilver:F2} and left {fs.treasury:F2}, expected 30 and {reserve}");
                Expect(c, Math.Abs(r.arrears - (2f * day - 30f)) <= 0.01f, $"arrears are {r.arrears:F2}, expected {2f * day - 30f:F2}");
                Days(x, Tuning.T.tributeIntervalDays - 2);
                Expect(c, fs.treasury >= reserve - 0.01f, $"the treasury went below the reserve ({fs.treasury:F2})");
                Expect(c, r.missed == 1, $"the payment that fell short was counted {r.missed} times");
                Expect(c, r.arrears == 0f && r.accrualDays == 0f, "arrears or days were carried into the next period");
                WorldParty p = Convoy(r);
                Expect(c, p != null ? Math.Abs(p.silver - 30f) <= 0.01f : r.payments == 1, "the 30 silver that were paid did not make a payment");
                // Nothing at all: no convoy and no payment, but still a short one.
                Clean(x);
                Blank(r);
                fs.treasury = reserve;
                Days(x, Tuning.T.tributeIntervalDays);
                Expect(c, Convoy(r) == null && r.payments == 0 && r.missed == 1, $"a payment of nothing made a convoy or a payment ({r.payments} payments, {r.missed} missed)");
            }
            finally
            {
                Clean(x);
            }
        }

        /// <summary>
        /// The rate can be changed once in a stretch of days, so the unrest cannot be steered by switching at the right moment. A rate set ahead of time is what
        /// accrues: suspended for 14 days takes nothing and moves no due date; one day at the heavy rate is one day of its share; switching every day gives the
        /// sum of the daily shares; pace 2 doubles a day.
        /// </summary>
        private static void RateCase()
        {
            const string c = "tribute: rate";
            Case x = Fixture(c);
            if (x == null) return;
            try
            {
                TributaryRecord r = x.record;
                CompSettlementLife life = Life(x);
                FactionState fs = StateOf(x);
                int interval = Tuning.T.tributeIntervalDays;
                // The lock.
                Tributaries.SetRate(r, TributeRate.Heavy);
                Expect(c, r.rate == TributeRate.Heavy && r.lastRateTick == SimClock.Now && r.rateDays == 0f, "the first change of the rate did not go through");
                Expect(c, Tributaries.CannotSetRate(r) != null, "the rate can be changed again at once");
                Tributaries.SetRate(r, TributeRate.Light);
                Expect(c, r.rate == TributeRate.Heavy, "a second change within the stretch of days took effect");
                r.rateDays = interval - 0.5f;
                Expect(c, Tributaries.CannotSetRate(r) != null, "the rate can be changed half a day before the stretch is over");
                r.rateDays = interval;
                Expect(c, Tributaries.CannotSetRate(r) == null, "the rate cannot be changed when the stretch is over");
                Tributaries.SetRate(r, TributeRate.Suspended);
                Expect(c, r.rate == TributeRate.Suspended, "the rate could not be changed when the stretch was over");
                // The stretch is in simulated days (real days times the pace, as unrest moves): at pace 0.5 it is 30 real days, at pace 2 it is 7.5.
                Blank(r);
                Tributaries.SetRate(r, TributeRate.Heavy);
                Days(x, interval, pace: 0.5f);
                Expect(c, Tributaries.CannotSetRate(r) != null && Math.Abs(r.rateDays - interval * 0.5f) < 0.001f, $"the lock was over after {interval} days at pace 0.5 ({r.rateDays} simulated days)");
                Days(x, interval, pace: 0.5f);
                Expect(c, Tributaries.CannotSetRate(r) == null, $"the lock held after {2 * interval} days at pace 0.5 ({r.rateDays} simulated days)");
                Blank(r);
                Tributaries.SetRate(r, TributeRate.Heavy);
                Days(x, 8, pace: 2f);
                Expect(c, Tributaries.CannotSetRate(r) == null, $"the lock held after 8 days at pace 2 ({r.rateDays} simulated days)");
                // A payment a rounding error short of its day still falls due (a pace such as 0.3 adds up to 14.9999).
                Blank(r);
                Tributaries.TestNoParty = true;
                r.accruedSilver = 100f;
                r.accrualDays = interval - 1f - 0.0005f;
                Days(x, 1);
                Tributaries.TestNoParty = false;
                Expect(c, r.payments == 1 && r.accrualDays == 0f, $"a payment 0.0005 of a day short did not fall due ({r.payments} payments, {r.accrualDays} days)");
                Clean(x);
                // Suspended for 14 days, then one day at the heavy rate.
                Blank(r);
                r.rate = TributeRate.Suspended;
                float treasury = fs.treasury;
                Days(x, 14);
                Expect(c, r.accruedSilver == 0f && r.accrualDays == 0f && r.shareDays == 0f && fs.treasury == treasury, "a suspended tribute accrued or moved the due date");
                r.rate = TributeRate.Heavy;
                Days(x, 1);
                Expect(c, Math.Abs(r.accruedSilver - Due(life, TributeRate.Heavy)) <= 0.01f && r.accrualDays == 1f && r.payments == 0, $"one day of the heavy rate accrued {r.accruedSilver:F2} over {r.accrualDays} days (a payment can only follow 15 days of tribute)");
                // Switching every day: the sum of the daily shares, and the average share a payment's goods are made at.
                Blank(r);
                float expected = 0f, shares = 0f;
                for (int i = 0; i < interval - 1; i++)
                {
                    r.rate = i % 2 == 0 ? TributeRate.Heavy : TributeRate.Light;
                    expected += Due(life, r.rate);
                    shares += Tributaries.Share(r.rate);
                    Days(x, 1);
                }
                Expect(c, Math.Abs(r.accruedSilver - expected) <= 1f && Math.Abs(r.shareDays - shares) < 0.001f, $"switching every day accrued {r.accruedSilver:F2} with shares {r.shareDays:F2}, expected {expected:F2} and {shares:F2}");
                // Pace 2.
                Blank(r);
                r.rate = TributeRate.Normal;
                Days(x, 1, pace: 2f);
                Expect(c, Math.Abs(r.accruedSilver - Due(life, TributeRate.Normal, 2f)) <= 0.01f && r.accrualDays == 2f && r.shareDays == 1f, $"pace 2 accrued {r.accruedSilver:F2} over {r.accrualDays} days");
            }
            finally
            {
                Clean(x);
            }
        }

        // ---- Goods ----------------------------------------------------------------------------------------

        /// <summary>
        /// Goods come only from a surplus above 1.2 times the target stock: none below or at it, some above (never negative, never more than the cap, never
        /// more than the surplus); a non-convertible specialty is passed over; a value below one item stays in the stores; a payment debits exactly what it carries.
        /// </summary>
        private static void Goods()
        {
            const string c = "tribute: goods";
            Case x = Fixture(c);
            if (x == null) return;
            try
            {
                CompSettlementLife life = Life(x);
                TechLevel tech = KnowHow.Effective(ClientOf(x.record));
                CommodityDef food = LWDefOf.LW_Food;
                float limit = 1.2f * life.TargetStock(food);
                foreach (float stock in new[] { limit * 0.5f, limit, limit + 100f })
                {
                    life.stock[food.index] = stock;
                    bool any = Tributaries.PickGoods(life, 0.5f, tech, out CommodityDef goods, out TributeGoods.GoodsPlan plan, out float units);
                    float surplus = Mathf.Max(0f, stock - limit);
                    if (surplus <= 0.0001f) Expect(c, !any, $"goods from stock {stock:F1} (the limit is {limit:F1})");
                    else
                    {
                        Expect(c, any && goods == food && units > 0f, "no goods from a surplus of food");
                        Expect(c, units <= surplus * 0.5f + 0.001f && units <= Tuning.T.tributeGoodsMaxValue / food.silverPerUnit + 0.001f, $"{units:F2} units from a surplus of {surplus:F1}");
                        Expect(c, plan.Any && Math.Abs(plan.madeValue / food.silverPerUnit - units) < 0.001f && plan.madeValue <= surplus * 0.5f * food.silverPerUnit + 0.01f, "the units are not the value of the things planned");
                    }
                }
                // The cap: a huge surplus is worth at most the cap.
                life.stock[food.index] = limit + 100000f;
                Expect(c, Tributaries.PickGoods(life, 0.8f, tech, out _, out TributeGoods.GoodsPlan big, out float bigUnits) && big.madeValue <= Tuning.T.tributeGoodsMaxValue + 0.01f && bigUnits <= Tuning.T.tributeGoodsMaxValue / food.silverPerUnit + 0.001f, "the goods of a payment are worth more than the cap");
                // A specialty that cannot be made into things is passed over for the next candidate.
                CommodityDef animals = Commodities.All.FirstOrDefault(d => d.commissionGoods.NullOrEmpty());
                if (animals != null)
                {
                    life.specialty = animals;
                    life.stock[animals.index] = 1.2f * life.TargetStock(animals) + 1000f;
                    life.stock[food.index] = limit + 100f;
                    Expect(c, Tributaries.PickGoods(life, 0.5f, tech, out CommodityDef pick, out _, out _) && pick == food, $"a specialty with no goods was chosen or no other candidate ({pick?.defName})");
                    life.stock[animals.index] = 0f;
                    life.specialty = null;
                }
                // A specialty that can be made is preferred over a larger surplus.
                CommodityDef other = Commodities.All.FirstOrDefault(d => d != food && !d.commissionGoods.NullOrEmpty() && d.defName == "LW_Materials") ?? Commodities.All.First(d => d != food && !d.commissionGoods.NullOrEmpty());
                life.specialty = other;
                life.stock[other.index] = 1.2f * life.TargetStock(other) + 200f;
                life.stock[food.index] = limit + 5000f;
                Expect(c, Tributaries.PickGoods(life, 0.5f, tech, out CommodityDef preferred, out _, out _) && preferred == other, $"the specialty was not preferred ({preferred?.defName})");
                life.specialty = null;
                life.stock[other.index] = 0f;
                // No candidate: silver only. A value below one item stays in the stores.
                life.stock[food.index] = 0f;
                Expect(c, !Tributaries.PickGoods(life, 0.5f, tech, out _, out _, out _), "goods with nothing in the stores");
                CommodityDef parts = Commodities.All.FirstOrDefault(d => d.defName == "LW_Components");
                if (parts != null)
                {
                    life.stock[parts.index] = 1.2f * life.TargetStock(parts) + 0.05f;
                    Expect(c, !Tributaries.PickGoods(life, 0.5f, tech, out _, out _, out _), "goods worth less than one item were made");
                    life.stock[parts.index] = 0f;
                }
                // A payment: what is debited is what the convoy carries, in whole items.
                life.stock[food.index] = limit + 400f;
                float before = life.stock[food.index];
                WorldParty p = MakeConvoy(c, x, goods: false);
                if (p == null) return;
                float units2 = p.cargo[food.index];
                Expect(c, units2 > 0f, "the convoy carries no goods from a surplus of food");
                Expect(c, Math.Abs(before - life.stock[food.index] - units2) < 0.01f, $"{before - life.stock[food.index]:F2} units left the stores, the convoy carries {units2:F2}");
                TributeGoods.GoodsPlan carried = TributeGoods.Plan(food, units2 * food.silverPerUnit, tech);
                Expect(c, carried.Any && Math.Abs(carried.madeValue / food.silverPerUnit - units2) < 0.01f, "the cargo is not made of whole items");
            }
            finally
            {
                Clean(x);
            }
        }

        // ---- Delivery ---------------------------------------------------------------------------------------

        /// <summary>A convoy reaches the colony: the colony's silver and goods are up by what it carried, the record counts it once.</summary>
        private static void Delivery()
        {
            const string c = "tribute: delivery";
            Case x = Fixture(c);
            if (x == null) return;
            try
            {
                WorldParty p = MakeConvoy(c, x, goods: true);
                if (p == null) return;
                TributaryRecord r = x.record;
                Map colony = ColonyOf(x);
                int silver = Mathf.RoundToInt(p.silver);
                TechLevel tech = KnowHow.Effective(ClientOf(r));
                var wanted = new List<(ThingDef def, int count)>();
                float made = silver;
                foreach (CommodityDef d in Commodities.All)
                    if (p.cargo[d.index] > 0.0001f)
                    {
                        TributeGoods.GoodsPlan plan = TributeGoods.Plan(d, p.cargo[d.index] * d.silverPerUnit, tech);
                        if (plan.def == null) continue;
                        wanted.Add((plan.def, plan.count));
                        made += plan.madeValue;
                    }
                Expect(c, silver > 0 && wanted.Count > 0, $"the convoy carries {silver} silver and {wanted.Count} goods");
                Expect(c, p.Faction == ClientOf(r) && p.Origin == x.town && p.Destination == colony.Parent && p.recordId == r.id && r.convoyPartyId == p.ID, "the convoy is not the client's from the town to the colony");
                int silverBefore = Count(colony, ThingDefOf.Silver);
                var before = wanted.ToDictionary(w => w.def, w => Count(colony, w.def));
                int pods = Pods(colony);
                Arrive(p);
                Expect(c, p.Destroyed, "the convoy is still there after it arrived");
                Expect(c, Count(colony, ThingDefOf.Silver) - silverBefore == silver, $"the colony's silver is up by {Count(colony, ThingDefOf.Silver) - silverBefore}, the convoy carried {silver}");
                foreach ((ThingDef def, int count) in wanted) Expect(c, Count(colony, def) - before[def] == count, $"{def.defName} is up by {Count(colony, def) - before[def]}, the convoy carried {count}");
                Expect(c, Pods(colony) == pods, "pods came with a convoy that was delivered");
                Expect(c, r.payments == 1 && Math.Abs(r.paidTotal - made) <= 1f, $"{r.payments} payments, {r.paidTotal:F1} paid, expected {made:F1}");
            }
            finally
            {
                Clean(x);
            }
        }

        /// <summary>With no room for another party the payment comes by drop pods at once, through the same settlement (the same counters); what cannot be placed comes by pod too.</summary>
        private static void PodsCase()
        {
            const string c = "tribute: pods";
            Case x = Fixture(c);
            if (x == null) return;
            LivingWorldSettings stored = LivingWorldMod.Stored;
            int maxParties = stored.maxParties;
            try
            {
                TributaryRecord r = x.record;
                Map colony = ColonyOf(x);
                Life(x).stock[LWDefOf.LW_Food.index] = 1.2f * Life(x).TargetStock(LWDefOf.LW_Food) + 400f;
                stored.maxParties = WorldParty.All.Count;
                int pods = Pods(colony), silver = Count(colony, ThingDefOf.Silver);
                Days(x, Tuning.T.tributeIntervalDays);
                Expect(c, Convoy(r) == null, "a convoy was made with no room for a party");
                Expect(c, r.payments == 1 && r.paidTotal > 0f, $"{r.payments} payments by pod");
                Expect(c, Pods(colony) > pods, "no drop pod came");
                Expect(c, Count(colony, ThingDefOf.Silver) == silver, "silver was put down at once beside the pods");
                stored.maxParties = maxParties;
                // A convoy that arrives where nothing can be placed.
                Blank(r);
                WorldParty p = MakeConvoy(c, x, goods: true);
                if (p == null) return;
                Tributaries.TestPlaceFails = true;
                pods = Pods(colony);
                int silverNow = Count(colony, ThingDefOf.Silver), things = 1 + p.cargo.Count(u => u > 0f);
                Arrive(p);
                Tributaries.TestPlaceFails = false;
                Expect(c, Pods(colony) >= pods + things && Count(colony, ThingDefOf.Silver) == silverNow, $"{Pods(colony) - pods} pods for things that could not be placed (at least {things}), silver placed anyway: {Count(colony, ThingDefOf.Silver) != silverNow}");
                Expect(c, r.payments == 1, $"{r.payments} payments counted for a delivery by pod");
            }
            finally
            {
                stored.maxParties = maxParties;
                Tributaries.TestPlaceFails = false;
                Clean(x);
            }
        }

        /// <summary>The colony the convoy walked to is gone: another colony (none here, so by pod), or the way home with the payment owed again; a town with no colony to pay owes and accrues nothing.</summary>
        private static void ColonyGone()
        {
            const string c = "tribute: colony gone";
            Case x = Fixture(c);
            if (x == null) return;
            try
            {
                TributaryRecord r = x.record;
                CompSettlementLife life = Life(x);
                Map colony = ColonyOf(x);
                // The destination was lost and there is another way to the one colony: it is paid by pod.
                WorldParty p = MakeConvoy(c, x, goods: false);
                if (p == null) return;
                int pods = Pods(colony);
                p.kind.Worker.DestinationLost(p);
                Expect(c, p.Destroyed && r.payments == 1 && Pods(colony) > pods, $"a convoy that lost its way and has a colony: destroyed {p.Destroyed}, {r.payments} payments, {Pods(colony) - pods} pods");
                // No colony at all: the convoy turns back, and what it carries is owed again when it is home.
                Blank(r);
                life.stock[LWDefOf.LW_Food.index] = 1.2f * life.TargetStock(LWDefOf.LW_Food) + 400f;
                WorldParty q = MakeConvoy(c, x, goods: false);
                if (q == null) return;
                float silver = q.silver, units = q.cargo[LWDefOf.LW_Food.index], accrued = r.accruedSilver, stock = life.stock[LWDefOf.LW_Food.index];
                Tributaries.TestNoColony = true;
                q.kind.Worker.DestinationLost(q);
                Tributaries.TestNoColony = false;
                Expect(c, !q.Destroyed && q.phase == PartyPhase.Returning, "a convoy with no colony to go to did not turn back");
                q.kind.Worker.ArrivedHome(q);
                Expect(c, Math.Abs(r.accruedSilver - accrued - silver) <= 0.01f && Math.Abs(life.stock[LWDefOf.LW_Food.index] - stock - units) <= 0.01f, "what the convoy carried was not owed again when it came home");
                Expect(c, r.payments == 0 && r.paidTotal == 0f, "a convoy that turned back counted as a payment");
                q.Destroy();
                // A town with no colony to pay accrues nothing, and its due dates stand still.
                Blank(r);
                Tributaries.TestNoColony = true;
                Days(x, 10);
                Expect(c, r.accruedSilver == 0f && r.accrualDays == 0f && r.shareDays == 0f, "tribute accrued with no colony to pay");
                float term = Tributaries.UnrestTerm(life);
                Tributaries.TestNoColony = false;
                Expect(c, Math.Abs(term - Tuning.T.tributeUnrest[(int)TributeRate.Suspended]) < 0.0001f, $"the unrest term with no colony is {term}, not the suspended one");
            }
            finally
            {
                Tributaries.TestNoColony = false;
                Clean(x);
            }
        }

        /// <summary>The subsystem off for 10 days: no accrual and the due dates stand still; on again, it goes on from where it was.</summary>
        private static void SwitchedOff()
        {
            const string c = "tribute: setting off";
            Case x = Fixture(c);
            if (x == null) return;
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool was = stored.tributaries;
            try
            {
                TributaryRecord r = x.record;
                CompSettlementLife life = Life(x);
                Days(x, 3);
                float accrued = r.accruedSilver, days = r.accrualDays;
                Expect(c, days == 3f && accrued > 0f, "no tribute before the setting was switched off");
                stored.tributaries = false;
                int letters = Find.LetterStack.LettersListForReading.Count;
                Days(x, 10);
                Expect(c, r.accruedSilver == accrued && r.accrualDays == days, "tribute accrued with the setting off");
                Expect(c, Tributaries.UnrestTerm(life) == 0f && Tributaries.GarrisonFunding(life) == 1f, "the loyalty terms apply with the setting off");
                Expect(c, !Tributaries.Gizmos(x.town).Any(), "gizmos for a held town with the setting off");
                Expect(c, Find.LetterStack.LettersListForReading.Count == letters, "a letter was sent with the setting off");
                stored.tributaries = was;
                Days(x, 1);
                Expect(c, r.accrualDays == days + 1f && r.accruedSilver > accrued, "tribute did not go on from where it stood");
                Expect(c, Tributaries.Gizmos(x.town).Count() >= 3, "a held town has no rate, gift and release gizmos");
            }
            finally
            {
                stored.tributaries = was;
                Clean(x);
            }
        }

        /// <summary>What a convoy on the road does when the record changes: delivered after a release, recalled after a revolt or when the town is lost (at home, the silver goes to whoever rules the town).</summary>
        private static void ConvoyPolicy()
        {
            const string c = "tribute: convoys when the town changes";
            // Released: delivered, and the counters update on the released record.
            Case x = HeldTown(c + " (release)");
            if (x != null)
                try
                {
                    WorldParty p = MakeConvoy(c, x, goods: true);
                    if (p != null)
                    {
                        Map colony = ColonyOf(x);
                        int silver = Count(colony, ThingDefOf.Silver), carried = Mathf.RoundToInt(p.silver);
                        // Five days of tribute taken since the payment: released with the town, it is sent as a last payment and the rest goes back to the client.
                        Days(x, 5);
                        FactionState fs = StateOf(x);
                        float accrued = x.record.accruedSilver, treasury = fs.treasury, sent = Mathf.FloorToInt(accrued / 10f) * 10;
                        float onRoad = WorldParty.All.Where(q => q.kind == LWPartyDefOf.LW_Tribute && q.recordId == x.record.id).Sum(q => q.silver);
                        Expect(c, accrued > 10f, $"{accrued:F2} silver accrued in five days");
                        Tributaries.Release(x.record);
                        Expect(c, p.phase == PartyPhase.Outbound && !p.Destroyed, "a convoy was recalled when the town was released");
                        Expect(c, x.record.accruedSilver == 0f && x.record.arrears == 0f, $"{x.record.accruedSilver:F2} silver stayed accrued on a released record");
                        Expect(c, Math.Abs(fs.treasury - treasury - (accrued - sent)) <= 0.01f, $"the client's treasury is up by {fs.treasury - treasury:F2} after the release, expected the {accrued - sent:F2} that makes no ten");
                        float afterwards = WorldParty.All.Where(q => q.kind == LWPartyDefOf.LW_Tribute && q.recordId == x.record.id).Sum(q => q.silver);
                        Expect(c, Math.Abs(afterwards - onRoad - sent) <= 0.01f, $"{afterwards - onRoad:F2} silver went on the road after the release, expected {sent:F0}");
                        Arrive(p);
                        Expect(c, Count(colony, ThingDefOf.Silver) - silver == carried && x.record.state == TributaryState.Released && x.record.payments == 1, "the convoy was not delivered after the release");
                    }
                }
                finally
                {
                    Finish(x);
                }
            // Revolted: recalled, and the silver goes to its owner at home.
            x = HeldTown(c + " (revolt)");
            if (x != null)
                try
                {
                    WorldParty p = MakeConvoy(c, x, goods: true);
                    if (p != null)
                    {
                        FactionState fs = StateOf(x);
                        CompSettlementLife life = Life(x);
                        Days(x, 5);
                        float held = fs.treasury, accruedBefore = x.record.accruedSilver;
                        Tributaries.TestRevolt = true;
                        Tributaries.TestLiberate = false;
                        Tributaries.DailyStep(Comp, One, x.record);
                        Tributaries.TestRevolt = Tributaries.TestLiberate = null;
                        // What was taken from the treasury and not paid (that day's tribute too) is the client's again.
                        Expect(c, accruedBefore > 10f && x.record.accruedSilver == 0f && Math.Abs(fs.treasury - held - accruedBefore) <= 0.01f, $"after a revolt {x.record.accruedSilver:F2} silver stayed accrued and the treasury moved by {fs.treasury - held:F2}, expected {accruedBefore:F2}");
                        float silver = p.silver, treasury = fs.treasury, stock = life.stock[LWDefOf.LW_Food.index], units = p.cargo[LWDefOf.LW_Food.index];
                        Expect(c, x.record.state == TributaryState.Revolted, $"the town is {x.record.state} after a revolt");
                        Expect(c, !p.Destroyed && p.phase == PartyPhase.Returning, "a convoy was not recalled when the town revolted");
                        Map colony = ColonyOf(x);
                        int onMap = Count(colony, ThingDefOf.Silver);
                        p.kind.Worker.ArrivedHome(p);
                        Expect(c, x.record.payments == 0 && Count(colony, ThingDefOf.Silver) == onMap, "a recalled convoy was delivered");
                        Expect(c, Math.Abs(fs.treasury - treasury - silver) <= 0.01f && Math.Abs(life.stock[LWDefOf.LW_Food.index] - stock - units) <= 0.01f, "a recalled convoy's silver and goods did not go to the town's owner at home");
                    }
                }
                finally
                {
                    Tributaries.TestRevolt = Tributaries.TestLiberate = null;
                    Finish(x);
                }
            // Lost to another faction: recalled, and the silver goes to the new owner.
            x = HeldTown(c + " (lost)");
            if (x != null)
                try
                {
                    Faction third = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => FactionState.IsSimulated(f) && f != ClientOf(x.record) && f != x.old);
                    WorldParty p = third == null ? null : MakeConvoy(c, x, goods: true);
                    if (p != null)
                    {
                        CompSettlementLife life = Life(x);
                        Days(x, 5);
                        float accrued = x.record.accruedSilver;
                        float silver = p.silver, treasury = Comp.StateFor(third).treasury, stock = life.stock[LWDefOf.LW_Food.index], units = p.cargo[LWDefOf.LW_Food.index];
                        x.town.SetFaction(third);
                        Tributaries.Reconcile(Comp);
                        Expect(c, accrued > 10f && x.record.accruedSilver == 0f && Math.Abs(Comp.StateFor(third).treasury - treasury - accrued) <= 0.01f, $"the {accrued:F2} silver accrued before the town was lost did not go to its new owner (treasury up by {Comp.StateFor(third).treasury - treasury:F2}, {x.record.accruedSilver:F2} left on the record)");
                        treasury = Comp.StateFor(third).treasury;
                        Expect(c, x.record.state == TributaryState.Lost, $"the town is {x.record.state} after it changed hands");
                        Expect(c, !p.Destroyed && p.phase == PartyPhase.Returning, "a convoy was not recalled when the town was lost");
                        p.kind.Worker.ArrivedHome(p);
                        Expect(c, Math.Abs(Comp.StateFor(third).treasury - treasury - silver) <= 0.01f && Math.Abs(life.stock[LWDefOf.LW_Food.index] - stock - units) <= 0.01f, "a recalled convoy's silver and goods did not go to the new owner");
                        Expect(c, x.record.payments == 0, "a convoy of a lost town counted a payment");
                    }
                }
                finally
                {
                    Finish(x);
                }
        }

        // ---- Accounting ------------------------------------------------------------------------------------

        /// <summary>
        /// Where the silver and goods of tribute go at the edges: a convoy carries the tech its goods were planned with and is worth its goods at their price,
        /// one on its way home goes back to the town's holder when the save is prepared for removal, the estimate of the next payment stops at what the treasury
        /// holds above the reserve, nothing is demanded with the economy off, and a released town with no colony to pay gives the accrued silver back.
        /// </summary>
        private static void Accounting()
        {
            const string c = "tribute: accounting";
            Case x = Fixture(c);
            if (x == null) return;
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool economy = stored.simulateEconomy;
            try
            {
                TributaryRecord r = x.record;
                CompSettlementLife life = Life(x);
                FactionState fs = StateOf(x);
                WorldParty p = MakeConvoy(c, x, goods: true);
                if (p != null)
                {
                    Expect(c, p.mission == (byte)KnowHow.Effective(ClientOf(r)), $"the convoy carries tech {p.mission}, its goods were planned with {KnowHow.Effective(ClientOf(r))}");
                    float worth = p.silver;
                    foreach (CommodityDef d in Commodities.All) worth += p.cargo[d.index] * d.silverPerUnit;
                    Expect(c, p.cargo[LWDefOf.LW_Food.index] > 0f && Math.Abs(p.CargoValue - worth) <= 0.01f, $"the convoy is worth {p.CargoValue:F1}, its silver and goods at their price are {worth:F1}");
                    // On its way home when the save is prepared: back to the holder of the town, nothing destroyed with the party.
                    p.TurnBack();
                    float silver = p.silver, accrued = r.accruedSilver, units = p.cargo[LWDefOf.LW_Food.index], stock = life.stock[LWDefOf.LW_Food.index];
                    Tributaries.Notify_PartiesLeaving();
                    Expect(c, Math.Abs(r.accruedSilver - accrued - silver) <= 0.01f && Math.Abs(life.stock[LWDefOf.LW_Food.index] - stock - units) <= 0.01f && p.silver == 0f, "a convoy on its way home was destroyed with its silver and goods when the save was prepared for removal");
                    p.Destroy();
                }
                // The estimate of the next payment: nothing more comes from a treasury at its reserve.
                Blank(r);
                fs.treasury = Tuning.T.tributeTreasuryReserve;
                Expect(c, Tributaries.EstimateNextPayment(r, out _, out _) == 0, "the estimate counts silver a treasury at its reserve cannot pay");
                fs.treasury = 5000f;
                // The economy off: no tribute is demanded (the client earns nothing, and unrest never moves).
                stored.simulateEconomy = false;
                Days(x, 3);
                Expect(c, !Tributaries.Demanded(r, x.town) && r.accrualDays == 0f && r.accruedSilver == 0f, "tribute was demanded with the economy off");
                stored.simulateEconomy = economy;
            }
            finally
            {
                stored.simulateEconomy = economy;
                Clean(x);
            }
            // Released with no colony to pay: what accrued goes back to the client.
            Case y = HeldTown(c + " (release, no colony)");
            if (y == null) return;
            try
            {
                Days(y, 5);
                FactionState fy = StateOf(y);
                float accrued = y.record.accruedSilver, treasury = fy.treasury;
                Tributaries.TestNoColony = true;
                Tributaries.Release(y.record);
                Tributaries.TestNoColony = false;
                Expect(c, accrued > 10f && y.record.accruedSilver == 0f && Math.Abs(fy.treasury - treasury - accrued) <= 0.01f, $"{accrued:F2} silver accrued, {y.record.accruedSilver:F2} stayed on the released record, the treasury is up by {fy.treasury - treasury:F2}");
            }
            finally
            {
                Tributaries.TestNoColony = false;
                Finish(y);
            }
        }

        // ---- Loyalty ---------------------------------------------------------------------------------------

        /// <summary>The unrest term exactly per rate and modifier (defenders paid for, neglect, a road, nothing demanded), and the garrison funding factor exactly per rate.</summary>
        private static void Loyalty()
        {
            const string c = "loyalty: terms";
            Case x = Fixture(c);
            if (x == null) return;
            try
            {
                TributaryRecord r = x.record;
                CompSettlementLife life = Life(x);
                FactionState fs = StateOf(x);
                LivingWorldTuningDef t = Tuning.T;
                Map colony = ColonyOf(x);
                float road = Roads.Connected(colony, x.town) ? t.tributaryRoadTerm : 0f;
                fs.treasury = 1000f;
                foreach (TributeRate rate in new[] { TributeRate.Suspended, TributeRate.Light, TributeRate.Normal, TributeRate.Heavy })
                {
                    r.rate = rate;
                    Expect(c, Math.Abs(Tributaries.UnrestTerm(life) - (t.tributeUnrest[(int)rate] + road)) < 0.0001f, $"{rate}: the unrest term is {Tributaries.UnrestTerm(life):F4}, expected {t.tributeUnrest[(int)rate] + road:F4}");
                    Expect(c, Math.Abs(Tributaries.GarrisonFunding(life) - t.tributeGarrisonFunding[(int)rate]) < 0.0001f, $"{rate}: the garrison funding is {Tributaries.GarrisonFunding(life)}");
                    float funded = SettlementEconomy.TargetGarrison(life, fs), unfunded = SettlementEconomy.TargetGarrison(life, fs, includePay: false);
                    Expect(c, Math.Abs(funded - unfunded * t.tributeGarrisonFunding[(int)rate]) <= 0.001f * Mathf.Max(1f, unfunded), $"{rate}: the target garrison is {funded:F2} of {unfunded:F2}");
                }
                r.rate = TributeRate.Normal;
                r.protectedUntilTick = SimClock.Now + GenDate.TicksPerDay;
                Expect(c, Math.Abs(Tributaries.UnrestTerm(life) - (t.tributeUnrest[(int)TributeRate.Normal] + t.tributaryProtectedTerm + road)) < 0.0001f, "the term of paid defenders is wrong");
                r.abandonedUntilTick = SimClock.Now + GenDate.TicksPerDay;
                Expect(c, Math.Abs(Tributaries.UnrestTerm(life) - (t.tributeUnrest[(int)TributeRate.Normal] + t.tributaryProtectedTerm + t.tributaryAbandonedTerm + road)) < 0.0001f, "the term of neglect is wrong");
                r.protectedUntilTick = r.abandonedUntilTick = FactionState.Never;
                // A rate that is not demanded counts as suspended.
                r.rate = TributeRate.Heavy;
                Tributaries.TestNoColony = true;
                Expect(c, Math.Abs(Tributaries.UnrestTerm(life) - t.tributeUnrest[(int)TributeRate.Suspended]) < 0.0001f && Math.Abs(Tributaries.GarrisonFunding(life) - t.tributeGarrisonFunding[(int)TributeRate.Suspended]) < 0.0001f, "a rate with no colony to pay counts at its own value");
                Tributaries.TestNoColony = false;
                // Any other town: nothing.
                CompSettlementLife other = Comp.activeSettlements.FirstOrDefault(s => s != life && Tributaries.HeldFor(s.Settlement) == null);
                if (other != null) Expect(c, Tributaries.UnrestTerm(other) == 0f && Tributaries.GarrisonFunding(other) == 1f, "a town that is not held has a tributary term");
            }
            finally
            {
                Tributaries.TestNoColony = false;
                Clean(x);
            }
        }

        /// <summary>
        /// The real daily step of a held town, 120 days in a fed town with no trade (the stores are refilled before each day), against the closed form of the
        /// balance table at peace and at war: the unrest and the prosperity it settles at, within 0.03.
        /// </summary>
        private static void EngineAgainstTable()
        {
            const string c = "loyalty: engine against the balance table";
            Case x = Fixture(c);
            if (x == null) return;
            try
            {
                TributaryRecord r = x.record;
                CompSettlementLife life = Life(x);
                FactionState fs = StateOf(x);
                fs.treasury = 100000f;
                Map colony = ColonyOf(x);
                bool road = Roads.Connected(colony, x.town);
                float[] peaceTable = { 0.00f, 0.12f, 0.25f, 0.50f }, warTable = { 0.15f, 0.28f, 0.41f, 0.65f };
                foreach (TributeRate rate in new[] { TributeRate.Suspended, TributeRate.Light, TributeRate.Normal, TributeRate.Heavy })
                    foreach (bool war in new[] { false, true })
                    {
                        r.rate = rate;
                        life.prosperity = 0.6f;
                        life.unrest = 0.35f;
                        life.hunger = 0f;
                        for (int day = 0; day < 120; day++)
                        {
                            foreach (CommodityDef d in Commodities.All) life.stock[d.index] = life.TargetStock(d) * 1.5f;
                            life.recentTrade = 0f;
                            SettlementEconomy.DailyStep(life, fs, One, war);
                        }
                        float term = Tributaries.UnrestTerm(life);
                        float u = Mathf.Clamp01((term + (war ? 0.085f : -0.06f)) / 0.925f), p = (war ? 0.55f : 0.70f) - 0.25f * u;
                        Expect(c, Math.Abs(life.unrest - u) <= 0.03f && Math.Abs(life.prosperity - p) <= 0.03f, $"{rate} {(war ? "at war" : "at peace")}: unrest {life.unrest:F3} prosperity {life.prosperity:F3}, the closed form gives {u:F3} and {p:F3}");
                        float table = war ? warTable[(int)rate] : peaceTable[(int)rate];
                        if (!road) Expect(c, Math.Abs(life.unrest - table) <= 0.03f, $"{rate} {(war ? "at war" : "at peace")}: unrest {life.unrest:F3}, the balance table says {table:F2}");
                    }
            }
            finally
            {
                Clean(x);
            }
        }

        // ---- Revolt ----------------------------------------------------------------------------------------

        private static Quest LockPair(Faction a, Faction b)
        {
            Quest q = Quest.MakeRaw();
            q.SetInitiallyAccepted();
            var part = new QuestPart_FactionGoodwillLocked { faction1 = a, faction2 = b };
            q.AddPart(part);
            AccessTools.Field(typeof(QuestPartActivable), "state").SetValue(part, QuestPartState.Enabled);
            Find.QuestManager.Add(q);
            return q;
        }

        /// <summary>The gates of a revolt: a town with a map or a live quest does not rise (no roll), and a pair vanilla keeps from turning hostile (a quest lock) does not break.</summary>
        private static void RevoltGates()
        {
            const string c = "revolt: gates";
            Case x = Fixture(c);
            if (x == null) return;
            Quest quest = null;
            Map map = null;
            try
            {
                TributaryRecord r = x.record;
                Faction client = ClientOf(r), player = Faction.OfPlayer;
                int revolts = Chronicle(LWChronicleDefOf.LW_TributaryRevolted);
                void Rise() => Tributaries.DailyStep(Comp, One, r);
                Tributaries.TestRevolt = true;
                Tributaries.TestLiberate = false;
                // A live quest.
                x.town.questTags ??= new List<string>();
                x.town.questTags.Add("lwtest");
                Rise();
                Expect(c, r.state == TributaryState.Held && !client.HostileTo(player), "a town with a quest tag rose");
                x.town.questTags.Remove("lwtest");
                // A map.
                map = GetOrGenerateMapUtility.GetOrGenerateMap(x.town.Tile, new IntVec3(150, 1, 150), null);
                Rise();
                Expect(c, r.state == TributaryState.Held && !client.HostileTo(player), "a town with a map rose");
                Current.Game.DeinitAndRemoveMap(map, false);
                map = null;
                if (r.state != TributaryState.Held || client.HostileTo(player))
                {
                    problems.Add($"{c}: the town is {r.state} after a map, the quest-locked case cannot go on");
                    return;
                }
                // A pair vanilla keeps as it is.
                int letters = Find.LetterStack.LettersListForReading.Count;
                try
                {
                    quest = LockPair(player, client);
                }
                catch (Exception e)
                {
                    Skip(c + " (quest lock)", $"the quest lock could not be made: {e.GetType().Name}: {e.Message}");
                }
                if (quest != null)
                {
                    Expect(c, QuestUtility.IsGoodwillLockedByQuest(player, client), "the quest lock does not hold (the case cannot tell)");
                    Rise();
                    Expect(c, r.state == TributaryState.Held && !client.HostileTo(player), "a town whose pair is locked by a quest rose anyway");
                    Expect(c, Chronicle(LWChronicleDefOf.LW_TributaryRevolted) == revolts && Find.LetterStack.LettersListForReading.Count == letters, "a revolt that did not happen was chronicled or sent a letter");
                }
            }
            finally
            {
                Tributaries.TestRevolt = Tributaries.TestLiberate = null;
                if (quest != null) Find.QuestManager.Remove(quest);
                if (map != null && Current.Game.Maps.Contains(map)) Current.Game.DeinitAndRemoveMap(map, false);
                x.town.questTags?.Remove("lwtest");
                Clean(x);
            }
        }

        /// <summary>A town that breaks free: hostile to the colony, a letter and a chronicle entry (each once), the client's grudge, the record revolted; with conquest off it still breaks free (it changes no owner).</summary>
        private static void BreakFree()
        {
            const string c = "revolt: breaks free";
            Case x = HeldTown(c);
            if (x == null) return;
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool conquest = stored.allowConquest;
            try
            {
                TributaryRecord r = x.record;
                Faction client = ClientOf(r), player = Faction.OfPlayer;
                int revolts = Chronicle(LWChronicleDefOf.LW_TributaryRevolted), letters = Find.LetterStack.LettersListForReading.Count;
                stored.allowConquest = false; // the old masters cannot take it back, whatever the roll says
                Tributaries.TestRevolt = true;
                Tributaries.TestLiberate = true;
                Tributaries.DailyStep(Comp, One, r);
                Tributaries.TestRevolt = Tributaries.TestLiberate = null;
                stored.allowConquest = conquest;
                Expect(c, r.state == TributaryState.Revolted && client.HostileTo(player), $"the town is {r.state}, hostile {client.HostileTo(player)}");
                Expect(c, x.town.Faction == client, "a revolt with conquest off changed the owner of the town");
                Expect(c, Chronicle(LWChronicleDefOf.LW_TributaryRevolted) == revolts + 1, "the revolt was not chronicled exactly once");
                Expect(c, Find.LetterStack.LettersListForReading.Count == letters + 1, $"{Find.LetterStack.LettersListForReading.Count - letters} letters for the revolt");
                Expect(c, Diplomacy.GrudgeAgainst(Comp.StateFor(client), player) >= 29f, "the client holds no grudge against the colony");
                Expect(c, !Tributaries.IsClient(client) && Tributaries.WasClient(client), "a revolted client is still a client or no longer a former one");
                // Once it is over it is over.
                Tributaries.TestRevolt = true;
                Tributaries.DailyStep(Comp, One, r);
                Tributaries.TestRevolt = null;
                Expect(c, Chronicle(LWChronicleDefOf.LW_TributaryRevolted) == revolts + 1, "a revolted town rose again");
            }
            finally
            {
                stored.allowConquest = conquest;
                Tributaries.TestRevolt = Tributaries.TestLiberate = null;
                Finish(x);
            }
        }

        /// <summary>The old masters take the town back: founders own it and are not its occupiers, the client lost its only town and is defeated, the record is lost.</summary>
        private static void Liberation()
        {
            const string c = "revolt: liberation";
            Case x = HeldTown(c, t => SettlementOps.SettlementCount(t.Faction) >= 2 && !t.Faction.def.permanentEnemy);
            if (x == null) return;
            try
            {
                TributaryRecord r = x.record;
                Faction client = ClientOf(r);
                CompSettlementLife life = Life(x);
                int liberated = Chronicle(LWChronicleDefOf.LW_TownLiberated);
                if (x.old.defeated || !x.old.HostileTo(client))
                {
                    Skip(c, "the old masters are defeated or at peace with the client");
                    return;
                }
                Tributaries.TestRevolt = true;
                Tributaries.TestLiberate = true;
                Tributaries.DailyStep(Comp, One, r);
                Tributaries.TestRevolt = Tributaries.TestLiberate = null;
                Expect(c, x.town.Faction == x.old && life.originalFactionId == x.old.loadID && !life.Occupied, "the old masters do not own the town as its founders");
                Expect(c, client.defeated && SettlementOps.SettlementCount(client) == 0, "the client that lost its only town is not defeated");
                Expect(c, r.state == TributaryState.Lost, $"the record is {r.state}");
                Expect(c, Chronicle(LWChronicleDefOf.LW_TownLiberated) == liberated + 1, "the liberation was not chronicled exactly once");
                Expect(c, !Tributaries.IsClient(client) && Tributaries.WasClient(client), "a client that lost the town is still a client");
            }
            finally
            {
                Tributaries.TestRevolt = Tributaries.TestLiberate = null;
                Finish(x);
            }
        }

        /// <summary>The roll itself: no hazard below the line, a hazard above it (a thousand days of a very restless town do rise), and a warning at most every ten days.</summary>
        private static void RevoltHazard()
        {
            const string c = "revolt: hazard and warning";
            Case x = HeldTown(c);
            if (x == null) return;
            try
            {
                TributaryRecord r = x.record;
                CompSettlementLife life = Life(x);
                r.rate = TributeRate.Suspended; // no payments in these days: only the people's patience is tried
                // Below the line: never, however many days.
                life.unrest = Tuning.T.tributaryRevoltUnrest - 0.01f;
                for (int i = 0; i < 300 && r.state == TributaryState.Held; i++)
                {
                    life.unrest = Tuning.T.tributaryRevoltUnrest - 0.01f;
                    Days(x, 1, revolt: true);
                }
                Expect(c, r.state == TributaryState.Held, "a town below the revolt line rose");
                // The warning, at most once in ten days.
                life.unrest = Tuning.T.tributaryWarnUnrest + 0.02f;
                r.lastWarningTick = FactionState.Never;
                Days(x, 1);
                int warned = r.lastWarningTick;
                Expect(c, warned == SimClock.Now, "no warning from a restless town");
                r.lastWarningTick = SimClock.Now - 9 * GenDate.TicksPerDay;
                int nine = r.lastWarningTick;
                Days(x, 1);
                Expect(c, r.lastWarningTick == nine, "a second warning within ten days");
                r.lastWarningTick = SimClock.Now - 11 * GenDate.TicksPerDay;
                Days(x, 1);
                Expect(c, r.lastWarningTick == SimClock.Now, "no warning after ten days");
                life.unrest = Tuning.T.tributaryWarnUnrest - 0.05f;
                r.lastWarningTick = FactionState.Never;
                Days(x, 1);
                Expect(c, r.lastWarningTick == FactionState.Never, "a calm town warned");
                // Above the line, at 100 % unrest the daily hazard is 0.2 × 0.45: a town that is held there for 200 days rises.
                int days = 0;
                Tributaries.TestLiberate = false;
                while (r.state == TributaryState.Held && days < 200)
                {
                    life.unrest = 1f;
                    Days(x, 1, revolt: true);
                    days++;
                }
                Tributaries.TestLiberate = null;
                Expect(c, r.state == TributaryState.Revolted, $"a town at full unrest did not rise in {days} days");
            }
            finally
            {
                Tributaries.TestLiberate = null;
                Finish(x);
            }
        }

        // ---- Gifts -----------------------------------------------------------------------------------------

        /// <summary>A gift: silver from the colony's stores into the client's treasury, the town calmer by min(0.2, N / (10 pop)), once in ten days; refused with no silver or no colony.</summary>
        private static void Gift()
        {
            const string c = "tribute: gift";
            Case x = Fixture(c);
            if (x == null) return;
            try
            {
                TributaryRecord r = x.record;
                CompSettlementLife life = Life(x);
                FactionState fs = StateOf(x);
                Map colony = ColonyOf(x);
                Expect(c, Tributaries.GiftPrice(life) == 600, $"the price of a gift to 300 people is {Tributaries.GiftPrice(life)}, not 600");
                life.population = 100000f;
                Expect(c, Tributaries.CannotGift(r, x.town) != null, "a gift that costs more silver than the colony has was allowed");
                life.population = 300f;
                Tributaries.TestNoColony = true;
                Expect(c, Tributaries.CannotGift(r, x.town) != null, "a gift with no colony was allowed");
                Tributaries.TestNoColony = false;
                int price = Tributaries.GiftPrice(life);
                var silver = ThingMaker.MakeThing(ThingDefOf.Silver);
                silver.stackCount = price;
                GenPlace.TryPlaceThing(silver, DropCellFinder.TradeDropSpot(colony), colony, ThingPlaceMode.Near);
                Expect(c, Tributaries.CannotGift(r, x.town) == null, "a gift the colony can afford was refused: " + Tributaries.CannotGift(r, x.town));
                float treasury = fs.treasury;
                life.unrest = 0.5f;
                int stores = Demands.ColonySilver(colony);
                Tributaries.SendGift(r);
                Expect(c, stores - Demands.ColonySilver(colony) == price, $"the colony paid {stores - Demands.ColonySilver(colony)} silver, expected {price}");
                Expect(c, Math.Abs(fs.treasury - treasury - price) <= 0.01f, "the gift did not reach the client's treasury");
                Expect(c, Math.Abs(life.unrest - (0.5f - Mathf.Min(0.2f, price / (300f * 10f)))) < 0.0001f, $"the unrest is {life.unrest:F4} after the gift");
                Expect(c, r.lastGiftTick == SimClock.Now && Tributaries.CannotGift(r, x.town) != null, "a second gift was allowed within ten days");
                r.lastGiftTick = SimClock.Now - 11 * GenDate.TicksPerDay;
                Expect(c, (Tributaries.CannotGift(r, x.town) != null) == (Demands.ColonySilver(colony) < price), "the cooldown still holds after ten days");
            }
            finally
            {
                Tributaries.TestNoColony = false;
                Clean(x);
            }
        }

        // ---- A payment due across the save ----------------------------------------------------------------

        /// <summary>A held town is left in the world for the halfway save (prepared just before it, see <see cref="PrepareBoundary"/>): its payment falls due on the first day after the load.</summary>
        private static void BoundaryTown()
        {
            const string c = "tribute: due across the save";
            Case x = Fixture(c);
            if (x == null) return;
            boundaryRecordId = x.record.id;
            boundaryTownId = x.town.ID;
        }

        /// <summary>Just before the save: the record stands one day short of a payment, with a treasury that can pay and goods in the stores.</summary>
        private static void PrepareBoundary()
        {
            boundarySignature = null;
            if (boundaryRecordId < 0) return;
            TributaryRecord r = Tributaries.RecordById(boundaryRecordId);
            var s = LivingWorldComponent.WorldObjectById(boundaryTownId) as Settlement;
            if (r == null || r.state != TributaryState.Held || s == null)
            {
                Log.Message($"{Tag}the town held for the payment across the save is {r?.state.ToString() ?? "gone"}: not checked");
                boundaryRecordId = -1;
                return;
            }
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            Blank(r);
            r.accrualDays = Tuning.T.tributeIntervalDays - 1;
            r.shareDays = r.accrualDays * 0.5f;
            r.accruedSilver = 640.5f;
            r.arrears = 12.5f;
            r.rate = TributeRate.Normal;
            Comp.StateFor(Comp.FactionById(r.clientFactionId)).treasury = 5000f;
            life.stock[LWDefOf.LW_Food.index] = 1.2f * life.TargetStock(LWDefOf.LW_Food) + 400f;
            boundarySignature = Sign();
        }

        /// <summary>After the load: the record is as it was, the payment is made exactly once on the day it falls due, and not again the day after.</summary>
        private static void CheckBoundary(LivingWorldComponent c)
        {
            if (boundaryRecordId < 0 || boundarySignature == null) return;
            const string name = "tribute: due across the save";
            TributaryRecord r = c.tributaries.FirstOrDefault(t => t.id == boundaryRecordId);
            if (r == null || r.state != TributaryState.Held)
            {
                problems.Add($"{name}: the record is {r?.state.ToString() ?? "gone"} after the load");
                return;
            }
            Expect(name, Math.Abs(r.accruedSilver - 640.5f) < 0.01f && Math.Abs(r.accrualDays - (Tuning.T.tributeIntervalDays - 1)) < 0.001f && Math.Abs(r.arrears - 12.5f) < 0.01f && r.payments == 0, $"the period came back as {r.accruedSilver} accrued over {r.accrualDays} days with {r.payments} payments");
            Faction client = c.FactionById(r.clientFactionId);
            var s = LivingWorldComponent.WorldObjectById(r.settlementId) as Settlement;
            if (client == null || s == null || !Tributaries.Demanded(r, s))
            {
                problems.Add($"{name}: the town is not charged after the load");
                return;
            }
            // By pod, so that the payment is counted the day it is made (a convoy counts when it arrives).
            Tributaries.TestRevolt = false;
            Tributaries.TestNoParty = true;
            Tributaries.DailyStep(c, One, r);
            Expect(name, r.payments == 1 && r.accrualDays == 0f, $"{r.payments} payments after the day it fell due");
            int payments = r.payments;
            Tributaries.DailyStep(c, One, r);
            Tributaries.TestRevolt = null;
            Tributaries.TestNoParty = false;
            Expect(name, r.payments == payments && r.accrualDays == 1f, "a second payment the day after the first");
            Tributaries.Release(r);
            foreach (WorldParty p in WorldParty.All.Where(p => p.kind == LWPartyDefOf.LW_Tribute && p.recordId == r.id).ToList()) p.Destroy();
        }

        // ---- A4 additions to earlier checks ---------------------------------------------------------------

        /// <summary>The record's tribute state, for the round trip of the save (floats to the cent: the save writes seven digits).</summary>
        private static string TributeSign(TributaryRecord r) =>
            $"{(int)r.rate}:{r.accruedSilver:F1}:{r.arrears:F1}:{r.shareDays:F1}:{r.accrualDays:F1}:{r.payments}:{r.missed}:{r.paidTotal:F1}:{r.convoyPartyId}:{r.lastRateTick}:{r.rateDays:F1}:{r.lastGiftTick}:{r.lastWarningTick}:{r.protectedUntilTick}:{r.abandonedUntilTick}";

        /// <summary>Hand-built XML of a record: A4's fields load, the ones it lacks load as defaults, and a rate byte out of range reads as the normal rate.</summary>
        private static void TributeMigration(string dir)
        {
            const string c = "migration (tribute)";
            TributaryRecord a = Load(dir, "<tr_id>9</tr_id><tr_state>1</tr_state><tr_rate>3</tr_rate><tr_accruedSilver>42.5</tr_accruedSilver><tr_accrualDays>4</tr_accrualDays><tr_payments>2</tr_payments><tr_paidTotal>900</tr_paidTotal><tr_lastRate>1234</tr_lastRate><tr_rateDays>4.5</tr_rateDays>");
            Expect(c, a != null && a.rate == TributeRate.Heavy && Math.Abs(a.accruedSilver - 42.5f) < 0.001f && a.accrualDays == 4f && a.payments == 2 && a.paidTotal == 900f && a.lastRateTick == 1234 && a.rateDays == 4.5f, "a record with A4's fields did not load them");
            Expect(c, a != null && a.arrears == 0f && a.shareDays == 0f && a.missed == 0 && a.convoyPartyId == -1 && a.lastGiftTick == FactionState.Never && a.lastWarningTick == FactionState.Never
                && a.protectedUntilTick == FactionState.Never && a.abandonedUntilTick == FactionState.Never, "the fields the record lacks did not load as defaults");
            TributaryRecord b = Load(dir, "<tr_id>10</tr_id><tr_state>1</tr_state><tr_rate>99</tr_rate>");
            Expect(c, b != null && b.rate == TributeRate.Normal, "a rate byte out of range did not load as the normal rate");
            TributaryRecord d = Load(dir, "<tr_id>11</tr_id><tr_state>1</tr_state>");
            Expect(c, d != null && d.rate == TributeRate.Normal && d.accruedSilver == 0f && d.payments == 0 && d.rateDays == TributaryRecord.RateOpen, "a record from before tribute did not load with the normal rate, nothing owed and a rate that can be changed");
        }
    }
}
