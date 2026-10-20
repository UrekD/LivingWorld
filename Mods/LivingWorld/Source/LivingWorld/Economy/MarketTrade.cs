using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>
    /// Player trade meets the world economy: prices follow the trader's home market, completed deals move that
    /// market's stock, and trader stock leans toward what the home settlement has plenty of.
    /// </summary>
    public static class MarketTrade
    {
        private static ITrader cachedTrader;
        private static CompSettlementLife cachedOrigin;
        private static int cachedFrame = -1;

        /// <summary>Home market of the current trade partner (settlement, caravan or visitor from one), or null.</summary>
        public static CompSettlementLife CurrentOrigin()
        {
            ITrader trader = TradeSession.trader;
            if (trader == null) return null;
            if (trader == cachedTrader && cachedFrame == Time.frameCount) return cachedOrigin;
            cachedTrader = trader;
            cachedFrame = Time.frameCount;
            cachedOrigin = OriginOf(trader);
            return cachedOrigin;
        }

        public static CompSettlementLife OriginOf(ITrader trader)
        {
            Settlement s = trader switch
            {
                Settlement st => st,
                WorldParty p => p.Origin,
                Pawn pawn => OriginOf(pawn),
                _ => null,
            };
            CompSettlementLife life = s?.GetComponent<CompSettlementLife>();
            return life != null && life.Active ? life : null;
        }

        private static Settlement OriginOf(Pawn pawn)
        {
            if (pawn.Faction == null || !FactionState.IsSimulated(pawn.Faction)) return null;
            IncidentContext ctx = Bridge.ContextOf(pawn.GetLord());
            if (ctx != null && LivingWorldComponent.WorldObjectById(ctx.originId) is Settlement s) return s;
            return pawn.Map != null ? Bridge.NearestSettlement(pawn.Faction, pawn.Map.Tile) : null;
        }

        /// <summary>Multiplier on what the player pays and gets for a thing (softened scarcity of the home market).</summary>
        public static float PlayerPriceFactor(ThingDef def, out CompSettlementLife origin, out CommodityDef commodity)
        {
            origin = null;
            commodity = null;
            if (!LivingWorldMod.Settings.marketPrices) return 1f;
            commodity = Commodities.Of(def);
            if (commodity == null) return 1f;
            origin = CurrentOrigin();
            if (origin == null) return 1f;
            return Mathf.Pow(origin.PriceFactor(commodity), 0.75f);

        }

        /// <summary>Things (silver value) moved by a finished deal into or out of the home market.</summary>
        public static void ApplyDeal(CompSettlementLife origin, List<(CommodityDef c, float soldToTrader, float boughtFromTrader)> moves)
        {
            float volume = 0f;
            foreach ((CommodityDef c, float sold, float bought) in moves)
            {
                if (sold > 0f) AidRequests.Notify_Sold(origin, c, sold);
                float units = (sold - bought) / c.silverPerUnit;
                origin.stock[c.index] = Mathf.Max(0f, origin.stock[c.index] + units);
                volume += (sold + bought) / c.silverPerUnit;
            }
            origin.recentTrade += volume;
        }

        /// <summary>Scales a trader's stack sizes by the home market (scarce goods are kept back, plentiful ones offered).</summary>
        public static void BiasStock(IEnumerable<Thing> things, CompSettlementLife origin)
        {
            if (origin == null || !LivingWorldMod.Settings.marketPrices) return;
            foreach (Thing t in things)
            {
                if (t is Pawn || t.def == ThingDefOf.Silver || t.def.stackLimit <= 1) continue;
                CommodityDef c = Commodities.Of(t.def);
                if (c == null) continue;
                float f = origin.PriceFactor(c);
                float scale = f > 1.4f ? 0.4f : f < 0.75f ? 1.6f : 1f;
                if (scale != 1f) t.stackCount = Mathf.Clamp(Mathf.RoundToInt(t.stackCount * scale), 1, t.def.stackLimit * 3);
            }
        }
    }

    [HarmonyPatch(typeof(Tradeable), "InitPriceDataIfNeeded")]
    public static class Patch_Tradeable_InitPriceData
    {
        // Prices are cached per tradeable; scale them only when this call actually computed them.
        public static void Prefix(float ___pricePlayerBuy, out bool __state) => __state = ___pricePlayerBuy <= 0f;

        public static void Postfix(Tradeable __instance, bool __state, ref float ___pricePlayerBuy, ref float ___pricePlayerSell)
        {
            if (!__state || __instance.IsCurrency || ___pricePlayerBuy <= 0f) return;
            float f = MarketTrade.PlayerPriceFactor(__instance.ThingDef, out CompSettlementLife origin, out _);
            float fameBuy = Fame.PriceMultiplier(origin, playerBuys: true), fameSell = Fame.PriceMultiplier(origin, playerBuys: false);
            if (origin != null && origin.FairOn)
            {
                fameBuy *= Shocks.FairPriceMultiplier(true);
                fameSell *= Shocks.FairPriceMultiplier(false);
            }
            if (f == 1f && fameBuy == 1f) return;
            ___pricePlayerBuy *= f * fameBuy;
            ___pricePlayerSell *= f * fameSell;
            if (___pricePlayerSell >= ___pricePlayerBuy) ___pricePlayerSell = ___pricePlayerBuy;
        }
    }

    [HarmonyPatch(typeof(Tradeable), nameof(Tradeable.GetPriceTooltip))]
    public static class Patch_Tradeable_GetPriceTooltip
    {
        public static void Postfix(Tradeable __instance, ref string __result)
        {
            if (__instance.IsCurrency) return;
            float f = MarketTrade.PlayerPriceFactor(__instance.ThingDef, out CompSettlementLife origin, out CommodityDef c);
            if (origin == null || (Mathf.Abs(f - 1f) < 0.01f && Mathf.Abs(origin.fame) < 0.05f && !origin.FairOn)) return;
            string why = (f > 1f ? "LW_Market_Scarce" : "LW_Market_Plentiful").Translate(c.label);
            __result += "\n\n" + "LW_Market_Tooltip".Translate(origin.Settlement.Label, f.ToString("F2"), why);
            if (Mathf.Abs(origin.fame) > 0.05f) __result += "\n" + "LW_Market_Fame".Translate(origin.fame.ToStringPercent());
            if (origin.FairOn) __result += "\n" + "LW_Market_Fair".Translate();
        }
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.TryExecute))]
    public static class Patch_TradeDeal_TryExecute
    {
        // The deal resets its tradeables after executing: capture what moves first.
        public static void Prefix(TradeDeal __instance, out List<(CommodityDef, float, float)> __state)
        {
            __state = null;
            if (!LivingWorldMod.Settings.marketPrices || MarketTrade.CurrentOrigin() == null) return;
            __state = new List<(CommodityDef, float, float)>();
            foreach (Tradeable t in __instance.AllTradeables)
            {
                if (t.IsCurrency || t.CountToTransfer == 0) continue;
                CommodityDef c = Commodities.Of(t.ThingDef);
                if (c == null) continue;
                float value = t.AnyThing.MarketValue * Mathf.Abs(t.CountToTransfer);
                if (t.ActionToDo == TradeAction.PlayerSells) __state.Add((c, value, 0f));
                else if (t.ActionToDo == TradeAction.PlayerBuys) __state.Add((c, 0f, value));
            }
        }

        public static void Postfix(bool actuallyTraded, List<(CommodityDef, float, float)> __state)
        {
            if (!actuallyTraded || __state == null || __state.Count == 0) return;
            CompSettlementLife origin = MarketTrade.CurrentOrigin();
            if (origin != null) MarketTrade.ApplyDeal(origin, __state);
        }
    }

    [HarmonyPatch(typeof(Settlement_TraderTracker), "RegenerateStock")]
    public static class Patch_Settlement_TraderTracker_RegenerateStock
    {
        public static void Postfix(Settlement_TraderTracker __instance, ThingOwner<Thing> ___stock)
        {
            if (___stock == null) return;
            CompSettlementLife life = __instance.settlement?.GetComponent<CompSettlementLife>();
            if (life != null && life.Active) MarketTrade.BiasStock(___stock, life);
        }
    }
}
