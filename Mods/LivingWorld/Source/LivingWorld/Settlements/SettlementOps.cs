using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>Changes of ownership and existence: founding, capture, razing, faction defeat.</summary>
    public static partial class SettlementOps
    {
        private static readonly AccessTools.FieldRef<Settlement, Material> cachedMat = AccessTools.FieldRefAccess<Settlement, Material>("cachedMat");
        private static readonly AccessTools.FieldRef<WorldObject, Material> expandingMat = AccessTools.FieldRefAccess<WorldObject, Material>("expandingMaterial");

        /// <summary>Settlements the simulation must not take or destroy: player maps, towns a live quest refers to.</summary>
        public static bool Protected(Settlement s) => s.Destroyed || !s.Spawned || s.HasMap || QuestRelated(s);

        /// <summary>A faction whose last settlement must survive (required by the game or DLC content).</summary>
        public static bool MustSurvive(Faction f) => !Tributaries.WasClient(f) && (f.def.requiredCountAtGameStart > 0 || f.def.hidden || f.def.permanentEnemy);

        public static int SettlementCount(Faction f)
        {
            int n = 0;
            foreach (Settlement s in Find.WorldObjects.Settlements)
                if (s.Faction == f) n++;
            return n;
        }

        /// <summary>Could this settlement change hands or be destroyed now?</summary>
        public static bool CanFall(Settlement s) =>
            LivingWorldMod.Settings.allowConquest && !Protected(s)
            // Nobody is wiped out before the player arrives, and required factions never are.
            && !((SimClock.InHistory || MustSurvive(s.Faction)) && SettlementCount(s.Faction) <= 1);

        public static Settlement Found(Faction f, PlanetTile tile, float population)
        {
            var s = (Settlement)WorldObjectMaker.MakeWorldObject(tile.LayerDef.SettlementWorldObjectDef);
            s.SetFaction(f);
            s.Tile = tile;
            s.Name = SettlementNameGenerator.GenerateSettlementName(s);
            Find.WorldObjects.Add(s);
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            if (life != null)
            {
                SettlementEconomy.Seed(life, population);
                life.prosperity = 0.4f;
                life.garrison = population;
                for (int i = 0; i < life.stock.Length; i++) life.stock[i] *= 0.3f;
            }
            return s;
        }

        /// <summary>The settlement now belongs to the conqueror: keeps its id, name and people (who resent it).</summary>
        /// <summary>The faction's largest settlement (its seat).</summary>
        public static Settlement CapitalOf(Faction f)
        {
            FactionState fs = LivingWorldComponent.Instance?.StateFor(f);
            CompSettlementLife best = null;
            if (fs != null)
                foreach (CompSettlementLife s in fs.settlements)
                    if (s.Active && s.Settlement.Faction == f && (best == null || s.population > best.population)) best = s;
            return best?.Settlement;
        }

        /// <summary>A capital fell: its ruler may die with it. Quiet succession (no vanilla letters for distant rulers).</summary>
        public static void Notify_CapitalFell(Settlement s, Faction loser, Faction conqueror)
        {
            Pawn leader = loser?.leader;
            // Only a ruler living quietly off-map: not one held by the player, in a caravan or pod, or wanted by a quest.
            if (leader == null || leader.Dead || leader.Spawned || !Find.WorldPawns.Contains(leader) || Find.QuestManager.IsReservedByAnyQuest(leader)
                || !Rand.Chance(0.5f)) return;
            string name = leader.Name?.ToStringFull ?? leader.LabelShort;
            string title = loser.LeaderTitle;
            int oldId = leader.thingIDNumber;
            loser.leader = null; // no "leader died" letter: the chronicle tells it
            // Before the colony lands nobody is killed (history makes no pawn deaths); the ruler is deposed instead.
            if (!SimClock.InHistory) leader.Kill(null);
            Find.WorldPawns.ForcefullyKeptPawns.Remove(leader); // leaders are kept forever; a fallen one need not be
            loser.TryGenerateNewLeader();
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            ChronicleEventDef def = LWChronicleDefOf.LW_RulerFell;
            comp.chronicle.Record(def, def.text.Formatted(name.Named("NAME"), title.Named("TITLE"), loser.Name.Named("FACTION"), s.Label.Named("SETTLEMENT"),
                (conqueror?.Name ?? "?").Named("CONQUEROR")).Resolve(), loser, conqueror, s);
            FactionState fs = comp.StateFor(loser);
            if (fs != null)
            {
                // Narrative sees the new leader next day and chronicles the succession from this one.
                fs.lastLeaderThingId = oldId;
                fs.lastLeaderName = name;
                fs.warWeariness = UnityEngine.Mathf.Clamp01(fs.warWeariness + 0.2f);
            }
        }

        /// <param name="ownershipFirst">The town changes hands before the ruler's fall and the faction's progress are applied (a transaction that retries
        /// the capture sees the new owner and does not apply them twice). Everything else is the same.</param>
        public static void Capture(Settlement s, Faction conqueror, bool ownershipFirst = false)
        {
            Faction loser = s.Faction;
            bool capital = loser != null && !loser.IsPlayer && CapitalOf(loser) == s;
            if (ownershipFirst) s.SetFaction(conqueror);
            if (capital) Notify_CapitalFell(s, loser, conqueror);
            Rulers.Notify_Captured(LivingWorldComponent.Instance, conqueror, loser);
            KnowHow.Notify_Captured(LivingWorldComponent.Instance, conqueror, loser);
            if (!ownershipFirst) s.SetFaction(conqueror);
            cachedMat(s) = null;
            expandingMat(s) = null;
            s.trader?.TryDestroyStock();
            s.previouslyGeneratedInhabitants.Clear();
            Find.World.renderer.Notify_StaticWorldObjectPosChanged();
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            if (life != null && life.initialized)
            {
                life.unrest = Mathf.Clamp01(life.unrest + 0.5f);
                life.prosperity = Mathf.Clamp01(life.prosperity - 0.2f);
                life.population *= 0.85f;
                if (life.originalFactionId < 0 && loser != null) life.originalFactionId = loser.loadID;
                // Taken back by its founders: no longer occupied.
                life.occupiedSinceTicks = life.originalFactionId == conqueror.loadID ? -1 : SimClock.Now;
                SettlementEconomy.RecomputeProfile(life);
            }
            CheckDefeated(loser, conqueror);
        }

        public static void Raze(Settlement s, Faction destroyer)
        {
            Faction loser = s.Faction;
            if (loser != null && !loser.IsPlayer && CapitalOf(loser) == s) Notify_CapitalFell(s, loser, destroyer);
            PlanetTile tile = s.Tile;
            var ruin = (DestroyedSettlement)WorldObjectMaker.MakeWorldObject(tile.LayerDef.DestroyedSettlementWorldObjectDef);
            ruin.Tile = tile;
            ruin.SetFaction(loser);
            s.Destroy();
            Find.WorldObjects.Add(ruin);
            CheckDefeated(loser, destroyer);
        }

        /// <summary>No settlement left on any layer: the faction is defeated (vanilla only does this for player attacks).</summary>
        public static void CheckDefeated(Faction loser, Faction by)
        {
            if (loser == null || loser.defeated || SettlementCount(loser) > 0) return;
            loser.defeated = true;
            HistoryTotals.Defeated++;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            foreach (War w in comp.wars)
                if (w.Active && w.Involves(loser.loadID)) Diplomacy.EndWar(w, "LW_WarEnd_Destroyed".Translate(loser.Name).Resolve());
            ChronicleEventDef def = LWChronicleDefOf.LW_FactionDefeated;
            comp.chronicle.Record(def, def.text.Formatted(loser.Name.Named("FACTION"), (by?.Name ?? "?").Named("BY")).Resolve(), loser, by);
        }
    }
}
