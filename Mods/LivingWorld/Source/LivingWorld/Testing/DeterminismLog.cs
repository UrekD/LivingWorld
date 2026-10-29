using System.Collections;
using System.Reflection;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// LIVINGWORLD_DETERMINISM=1: one <c>[LivingWorld:det]</c> line per simulated history day (and one for the state history starts from), so
    /// two runs of tools/history-golden.sh show the first day they part.
    /// <list type="bullet">
    /// <item><c>state</c> is a checksum of the sim state, made of <c>towns</c> (every town's numbers by their exact bits), <c>factions</c> (treasury,
    /// weariness, know-how, traits, the ruler's name, traits and goal), <c>wars</c> and <c>rel</c> (vanilla goodwill and relation kind of every
    /// pair). Raw thing ids are left out: the game's id counters are shared with everything it makes, so they are logged apart (below).</item>
    /// <item><c>seed</c> and <c>iter</c> are Verse.Rand's own state (the seed history pushed and the draws taken from it since), read directly;
    /// <c>stack</c> is the depth of Rand's state stack (history's push included).</item>
    /// </list>
    /// Once, before the first day, a <c>[LivingWorld:detids]</c> line logs the id counters and the leaders' thing ids: tools/history-golden.sh
    /// does not compare it, but it tells whether two runs started from other ids.
    /// Reads only: no RNG, no writes.
    /// </summary>
    public static class DeterminismLog
    {
        public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("LIVINGWORLD_DETERMINISM") == "1";

        // Rand's state (private): the current seed, the draws since it was set, and the stack PushState saves to.
        private static readonly FieldInfo randSeed = typeof(Rand).GetField("seed", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo randIterations = typeof(Rand).GetField("iterations", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo randStack = typeof(Rand).GetField("stateStack", BindingFlags.NonPublic | BindingFlags.Static);

        /// <param name="day">The history day just simulated (0 is the first), or -1 for the state before the first.</param>
        public static void Day(LivingWorldComponent comp, int day)
        {
            if (!Enabled || comp == null) return;
            if (day < 0) Ids(comp);
            uint towns = Fnv, factions = Fnv, wars = Fnv, rel = Fnv;
            int settlements = 0;
            double pop = 0d, garrison = 0d, stock = 0d, treasury = 0d;
            foreach (Settlement st in Find.WorldObjects.Settlements)
            {
                CompSettlementLife s = st.GetComponent<CompSettlementLife>();
                if (s == null || !s.initialized) continue;
                settlements++;
                towns = Mix(towns, st.Faction?.loadID ?? -1);
                towns = Mix(towns, st.Tile.tileId);
                towns = Mix(towns, s.population);
                towns = Mix(towns, s.garrison);
                towns = Mix(towns, s.prosperity);
                towns = Mix(towns, s.unrest);
                pop += s.population;
                garrison += s.garrison;
                foreach (float v in s.stock)
                {
                    towns = Mix(towns, v);
                    stock += v;
                }
            }
            foreach (FactionState fs in comp.factionStates)
            {
                factions = Mix(factions, fs.factionId);
                factions = Mix(factions, fs.treasury);
                factions = Mix(factions, fs.warWeariness);
                factions = Mix(factions, fs.techProgress);
                factions = Mix(factions, fs.techBonus);
                foreach (FactionTraitDef t in fs.traits) factions = Mix(factions, t.shortHash);
                factions = Mix(factions, fs.rulerThingId < 0 ? 0 : GenText.StableStringHash(fs.faction?.leader?.Name?.ToStringFull ?? "?"));
                foreach (RulerTraitDef t in fs.rulerTraits) factions = Mix(factions, t.shortHash);
                factions = Mix(factions, (int)fs.goal);
                factions = Mix(factions, fs.goalTargetId);
                factions = Mix(factions, fs.goalProgress);
                factions = Mix(factions, fs.grievances.Count);
                treasury += fs.treasury;
            }
            int active = 0;
            foreach (War w in comp.wars)
            {
                wars = Mix(wars, w.id);
                wars = Mix(wars, w.factionA);
                wars = Mix(wars, w.factionB);
                wars = Mix(wars, w.ended ? 1 : 0);
                wars = Mix(wars, w.score);
                if (w.Active) active++;
            }
            wars = Mix(wars, comp.chronicle.Count);
            for (int i = 0; i < comp.factionStates.Count; i++)
            {
                Faction a = comp.factionStates[i].faction;
                if (a == null) continue;
                for (int j = i + 1; j < comp.factionStates.Count; j++)
                {
                    Faction b = comp.factionStates[j].faction;
                    if (b == null || !a.HasGoodwill || !b.HasGoodwill) continue;
                    rel = Mix(rel, a.GoodwillWith(b));
                    rel = Mix(rel, (int)a.RelationKindWith(b));
                }
            }
            uint state = Mix(Mix(Mix(Mix(Fnv, (int)towns), (int)factions), (int)wars), (int)rel);
            state = Mix(state, Find.FactionManager.AllFactionsListForReading.Count);
            object seed = randSeed?.GetValue(null), iter = randIterations?.GetValue(null);
            int stack = (randStack?.GetValue(null) as ICollection)?.Count ?? -1;
            Log.Message($"[LivingWorld:det] day={(day < 0 ? "start" : day.ToString())} state={state:x8} seed={(seed is uint u ? u.ToString("x8") : "?")} iter={iter ?? "?"} stack={stack} " +
                        $"hTown={towns:x8} hFac={factions:x8} hWar={wars:x8} hRel={rel:x8} settlements={settlements} pop={pop:F0} garrison={garrison:F0} " +
                        $"stock={stock:F0} treasury={treasury:F0} factions={comp.factionStates.Count} chronicle={comp.chronicle.Count} wars={comp.wars.Count} active={active}");
        }

        /// <summary>The game's id counters and each simulated faction's leader (loadID:thingIDNumber) as history starts.</summary>
        private static void Ids(LivingWorldComponent comp)
        {
            UniqueIDsManager ids = Find.UniqueIDsManager;
            var sb = new StringBuilder("[LivingWorld:detids]");
            foreach (string field in new[] { "nextThingID", "nextWorldObjectID", "nextFactionID", "nextIdeoID", "nextHediffID" })
                sb.Append(' ').Append(field).Append('=').Append(ids == null ? "?" : typeof(UniqueIDsManager).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ids) ?? "?");
            sb.Append(" leaders=");
            foreach (FactionState fs in comp.factionStates)
                sb.Append(fs.factionId).Append(':').Append(fs.faction?.leader?.thingIDNumber.ToString() ?? "-").Append(' ');
            sb.Append("worldPawns=").Append(Find.WorldPawns?.AllPawnsAliveOrDead.Count ?? -1);
            Log.Message(sb.ToString());
        }

        private const uint Fnv = 2166136261u;

        // FNV-1a over the value's four bytes.
        private static uint Mix(uint h, int v)
        {
            unchecked
            {
                for (int i = 0; i < 4; i++)
                {
                    h ^= (uint)(v >> (8 * i)) & 0xffu;
                    h *= 16777619u;
                }
            }
            return h;
        }

        private static uint Mix(uint h, float v) => Mix(h, System.BitConverter.SingleToInt32Bits(v));
    }
}
