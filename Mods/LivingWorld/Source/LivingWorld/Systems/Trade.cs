using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Merchant caravans between settlements: carry a commodity from where it is plentiful to where it is short,
    /// which evens prices out over time and makes trade partners friends.
    /// </summary>
    public static class Trade
    {
        public const float MaxRange = 14f;

        /// <summary>Price factors of every settlement for today (index = activeSettlements order).</summary>
        private static float[][] prices = new float[0][];
        private static readonly System.Collections.Generic.List<CompSettlementLife> near = new System.Collections.Generic.List<CompSettlementLife>();
        private static readonly System.Collections.Generic.Dictionary<CompSettlementLife, int> slot = new System.Collections.Generic.Dictionary<CompSettlementLife, int>();

        private static void CachePrices(LivingWorldComponent comp)
        {
            int n = comp.activeSettlements.Count, c = Commodities.Count;
            if (prices.Length < n) System.Array.Resize(ref prices, n);
            slot.Clear();
            for (int i = 0; i < n; i++)
            {
                CompSettlementLife s = comp.activeSettlements[i];
                slot[s] = i;
                if (prices[i] == null || prices[i].Length != c) prices[i] = new float[c];
                for (int k = 0; k < c; k++) prices[i][k] = s.PriceFactor(Commodities.All[k]);
            }
        }

        public static void DailyStep(LivingWorldComponent comp, float pace)
        {
            if (!LivingWorldMod.Settings.simulateEconomy) return;
            CachePrices(comp);
            foreach (CompSettlementLife s in comp.activeSettlements)
            {
                if (!s.Active) continue;
                FactionState fs = comp.StateFor(s.Settlement.Faction);
                if (fs == null || s.Settlement.Faction.def.permanentEnemy) continue;
                float chance = 0.1f * pace * fs.TraitFactor(t => t.tradeVolume) * s.Tier.tradeFactor * (0.5f + s.prosperity);
                if (!Rand.Chance(chance)) continue;
                if (!FindDeal(comp, s, out CompSettlementLife dest, out CommodityDef good, out float amount)) continue;
                Send(comp, s, dest, good, amount);
            }
        }

        private static bool FindDeal(LivingWorldComponent comp, CompSettlementLife from, out CompSettlementLife best, out CommodityDef bestGood, out float bestAmount)
        {
            best = null;
            bestGood = null;
            bestAmount = 0f;
            float bestScore = 0f;
            Faction f = from.Settlement.Faction;
            PlanetTile tile = from.Settlement.Tile;
            float capacity = 30f + from.population * 0.6f * from.Tier.tradeFactor;
            float[] fromPrices = slot.TryGetValue(from, out int fi) ? prices[fi] : null;
            comp.spatial.Near(tile, MaxRange, near);
            foreach (CompSettlementLife to in near)
            {
                if (to == from || !to.Active) continue;
                float d = Find.WorldGrid.ApproxDistanceInTiles(tile, to.Settlement.Tile);
                if (d > MaxRange) continue;
                Faction tf = to.Settlement.Faction;
                if (tf.HostileTo(f) || tf.def.permanentEnemy) continue;
                float[] toPrices = slot.TryGetValue(to, out int ti) ? prices[ti] : null;
                foreach (CommodityDef c in Commodities.All)
                {
                    float surplus = from.Stock(c) - from.TargetStock(c) * 1.1f;
                    if (surplus <= 1f) continue;
                    float gap = (toPrices?[c.index] ?? to.PriceFactor(c)) - (fromPrices?[c.index] ?? from.PriceFactor(c));
                    if (gap < 0.35f) continue;
                    float amount = Mathf.Min(surplus, capacity, to.TargetStock(c));
                    float score = gap * amount / (d + 4f);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = to;
                        bestGood = c;
                        bestAmount = amount;
                    }
                }
            }
            return best != null && Find.WorldReachability.CanReach(tile, best.Settlement.Tile);
        }

        private static void Send(LivingWorldComponent comp, CompSettlementLife from, CompSettlementLife to, CommodityDef good, float amount)
        {
            from.stock[good.index] -= amount;
            if (PartyBudget.CanLaunchNpc())
            {
                WorldParty p = WorldParty.Launch(LWPartyDefOf.LW_Merchants, from.Settlement.Faction, from.Settlement, to.Settlement);
                p.cargo[good.index] = amount;
                p.strength = 40f + amount * 0.3f;
                return;
            }
            Deliver(comp, from.Settlement, to, good.index, amount, out float profit);
            Earn(comp, from.Settlement, profit, amount);
        }

        /// <summary>Goods arrive: the buyer's stock rises; returns the seller's profit (silver).</summary>
        public static void Deliver(LivingWorldComponent comp, Settlement seller, CompSettlementLife buyer, int good, float amount, out float profit)
        {
            CommodityDef c = Commodities.All[good];
            float price = buyer.PriceFactor(c);
            buyer.stock[good] += amount;
            buyer.AddImport(c, amount);
            buyer.recentTrade += amount;
            profit = amount * Mathf.Max(0.1f, price - 0.8f);
            Diplomacy.AddTrade(seller?.Faction, buyer.Settlement.Faction, amount);
        }

        public static void Earn(LivingWorldComponent comp, Settlement home, float profit, float volume)
        {
            if (home == null) return;
            CompSettlementLife life = home.GetComponent<CompSettlementLife>();
            if (life != null && life.initialized) life.recentTrade += volume;
            FactionState fs = comp.StateFor(home.Faction);
            if (fs != null) fs.treasury += profit;
        }
    }
}
