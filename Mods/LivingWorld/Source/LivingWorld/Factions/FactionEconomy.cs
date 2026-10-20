using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>Daily faction finances: taxes in, garrison and overextension upkeep out; war weariness.</summary>
    public static class FactionEconomy
    {
        public static void DailyStep(FactionState fs, float pace)
        {
            LivingWorldTuningDef t = Tuning.T;
            float income = 0f, upkeep = 0f;
            foreach (CompSettlementLife s in fs.settlements)
            {
                income += s.population * t.taxPerCapitaPerDay * (0.4f + s.prosperity);
                upkeep += s.garrison * t.garrisonUpkeepPerPoint;
            }
            // Overextension: every settlement beyond the third costs more to hold together.
            int n = fs.settlements.Count;
            if (n > 3) upkeep *= 1f + 0.08f * (n - 3);
            fs.treasury = Mathf.Max(0f, fs.treasury + (income - upkeep) * pace);
            bool atWar = LivingWorldComponent.Instance.AtWar(fs);
            // Raiders who are at war with everyone forever don't tire of it.
            float maxWeariness = fs.faction.def.permanentEnemy ? 0.3f : 1f;
            fs.warWeariness = Mathf.Clamp(fs.warWeariness + (atWar ? 0.004f : -0.01f) * pace, 0f, maxWeariness);
            for (int i = fs.grievances.Count - 1; i >= 0; i--)
            {
                Grievance g = fs.grievances[i];
                g.amount *= Mathf.Pow(0.99f, pace);
                if (g.amount < 1f) fs.grievances.RemoveAt(i);
            }
        }
    }
}
