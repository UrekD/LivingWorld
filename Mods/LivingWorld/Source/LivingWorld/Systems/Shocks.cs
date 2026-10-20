using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>Random fortunes and misfortunes of settlements: plague, blight, a great fair. Keeps the world from settling.</summary>
    public static class Shocks
    {
        public static void DailyStep(LivingWorldComponent comp, float pace)
        {
            if (!LivingWorldMod.Settings.simulateEconomy) return;
            foreach (CompSettlementLife s in comp.activeSettlements)
            {
                Settlement st = s.Settlement;
                BiomeDef biome = st.Tile.Tile?.PrimaryBiome;
                float wet = biome == null ? 0.5f : Mathf.Clamp01(biome.plantDensity);
                // Plague: crowded, wet places suffer most.
                if (Rand.Chance(0.00025f * pace * (0.5f + s.Tier.order * 0.5f) * (0.5f + wet)))
                {
                    float lost = s.population * Rand.Range(0.08f, 0.18f);
                    s.population = Mathf.Max(Tuning.T.minPopulation, s.population - lost);
                    s.prosperity = Mathf.Clamp01(s.prosperity - 0.12f);
                    s.garrison *= 0.85f;
                    Record(comp, LWChronicleDefOf.LW_Plague, st, Mathf.RoundToInt(lost));
                }
                // Blight: fields fail, food stores rot.
                else if (Rand.Chance(0.0004f * pace * (0.3f + wet)))
                {
                    s.stock[LWDefOf.LW_Food.index] *= 0.35f;
                    s.prosperity = Mathf.Clamp01(s.prosperity - 0.05f);
                    Record(comp, LWChronicleDefOf.LW_Blight, st, 0);
                }
                // The yearly fair: prosperous towns hold it in their harvest season and draw traders from all around.
                if (s.HoldsFairs && !s.FairOn && SimClock.Now - s.lastFairTicks > GenDate.TicksPerDay * 30)
                {
                    Twelfth now = s.TwelfthNow;
                    if (now == s.FairTwelfth) StartFair(comp, s);
                    else if (now.NextTwelfth() == s.FairTwelfth) AnnounceFair(s);
                }
            }
        }

        public const float FairInviteRange = 20f;

        /// <summary>A week-long fair: the town prospers, and a friendly colony nearby is invited to come and trade.</summary>
        public static void StartFair(LivingWorldComponent comp, CompSettlementLife s)
        {
            Settlement st = s.Settlement;
            s.lastFairTicks = SimClock.Now;
            s.prosperity = Mathf.Clamp01(s.prosperity + 0.05f);
            s.recentTrade += s.population * 0.5f;
            // Game time stands still during the pre-landing history: a fair then is over by the time the colony lands.
            if (SimClock.InHistory) return;
            s.fairUntilTicks = Find.TickManager.TicksGame + GenDate.TicksPerDay * 7;
            st.trader?.TryDestroyStock(); // the fair's market: fresh goods for the next visitor
            // Fairs are yearly routine: only one near the colony makes the chronicle (and invites a friendly colony).
            Map home = Neighbourhood.Home;
            if (home == null || Find.WorldGrid.ApproxDistanceInTiles(home.Tile, st.Tile) > Neighbourhood.Radius(home.Tile, FairInviteRange)) return;
            Record(comp, LWChronicleDefOf.LW_GreatFair, st, 0);
            if (st.Faction.HostileTo(Faction.OfPlayer)) return;
            Find.LetterStack.ReceiveLetter("LW_FairLabel".Translate(st.Label), "LW_FairText".Translate(st.Label.Named("SETTLEMENT"), st.Faction.NameColored.Named("FACTION"),
                7.Named("DAYS"), Mathf.RoundToInt((1f - FairPriceMultiplier(true)) * 100f).Named("PCT")), LetterDefOf.PositiveEvent, st, st.Faction);
        }

        /// <summary>A friendly town nearby will hold its fair in the next twelfth: tell the colony once (a message, not a letter).</summary>
        private static void AnnounceFair(CompSettlementLife s)
        {
            Settlement st = s.Settlement;
            if (SimClock.InHistory || SimClock.Now - s.fairNoticeTicks < GenDate.TicksPerDay * 30 || st.Faction.HostileTo(Faction.OfPlayer)) return;
            Map home = Neighbourhood.Home;
            if (home == null || Find.WorldGrid.ApproxDistanceInTiles(home.Tile, st.Tile) > Neighbourhood.Radius(home.Tile, FairInviteRange)) return;
            s.fairNoticeTicks = SimClock.Now;
            Messages.Message("LW_FairComing".Translate(st.Label.Named("SETTLEMENT"), st.Faction.Name.Named("FACTION")), st, MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>Fair prices for the player: buy cheaper, sell dearer.</summary>
        public static float FairPriceMultiplier(bool playerBuys) => playerBuys ? 0.9f : 1.1f;

        private static void Record(LivingWorldComponent comp, ChronicleEventDef def, Settlement s, int count) =>
            comp.chronicle.Record(def, def.text.Formatted(s.Label.Named("SETTLEMENT"), s.Faction.Name.Named("FACTION"), count.Named("COUNT")).Resolve(), s.Faction, wo: s);
    }
}
