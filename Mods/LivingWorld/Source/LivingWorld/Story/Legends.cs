using System.Collections.Generic;
using RimWorld;
using Verse;

namespace LivingWorld
{
    /// <summary>A colonist's notable deed (shown on the Legends page).</summary>
    public class Deed : IExposable
    {
        public Pawn pawn;
        public string name;
        public string text;
        public int ticksAbs;

        public void ExposeData()
        {
            // A pawn gone from the game entirely: the deed is kept under the saved name.
            if (Scribe.mode == LoadSaveMode.Saving && pawn != null && pawn.Discarded) pawn = null;
            Scribe_References.Look(ref pawn, "pawn", saveDestroyedThings: true);
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref text, "text");
            Scribe_Values.Look(ref ticksAbs, "ticks");
        }
    }

    /// <summary>
    /// The colony's legend: deeds of its people (a warlord slain, a fight abroad, a captive freed) and its renown,
    /// counted from the chronicle's entries about the colony.
    /// </summary>
    public static class Legends
    {
        public const int MaxDeeds = 200;

        public static void Add(Pawn colonist, string text)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || colonist == null || SimClock.InHistory) return;
            comp.deeds.Add(new Deed { pawn = colonist, name = colonist.Name?.ToStringShort ?? colonist.LabelShort, text = text, ticksAbs = SimClock.TicksAbs });
            if (comp.deeds.Count > MaxDeeds) comp.deeds.RemoveAt(0);
        }

        /// <summary>Renown: the colony's chronicle entries, weighted by importance.</summary>
        public static int Renown(LivingWorldComponent comp)
        {
            int score = 0;
            foreach (ChronicleEntry e in comp.chronicle.entries)
                if (e.involvesPlayer) score += e.def.importance == ChronicleImportance.Major ? 3 : e.def.importance == ChronicleImportance.Notable ? 2 : 1;
            return score;
        }

        /// <summary>How much more often friendly neighbours come: +10 % per renown tier (×1.4 for a legendary colony).</summary>
        public static float RenownFactor(LivingWorldComponent comp) => RenownFactor(Renown(comp));

        public static float RenownFactor(int renown) => 1f + 0.1f * Tier(renown);

        /// <summary>0 unknown, 1 known to neighbours, 2 known in the region, 3 renowned, 4 legendary.</summary>
        public static int Tier(int renown) => renown < 10 ? 0 : renown < 30 ? 1 : renown < 70 ? 2 : renown < 150 ? 3 : 4;

        /// <summary>The deeds of one colonist (or, for one gone from the game, of that name), newest first.</summary>
        public static List<Deed> Of(LivingWorldComponent comp, Pawn p, string name = null)
        {
            var list = new List<Deed>();
            for (int i = comp.deeds.Count - 1; i >= 0; i--)
            {
                Deed d = comp.deeds[i];
                if (p != null ? d.pawn == p : d.pawn == null && d.name == name) list.Add(d);
            }
            return list;
        }
    }
}
