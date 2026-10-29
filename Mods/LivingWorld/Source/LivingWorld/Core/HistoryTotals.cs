namespace LivingWorld
{
    /// <summary>
    /// Unsaved counters of the world events history produces, printed once as the history plan's <c>totals</c> line
    /// (<see cref="Testing.WorldChecks.LogTotals"/>) so balance targets can be compared with the golden files. They only count: no
    /// RNG, no state read by the simulation.
    /// </summary>
    public static class HistoryTotals
    {
        public static int Liberations, LiberationsOccupied, RisingsOccupied, WarsStarted, Battles, BattlesEmpire, Captures, Razes, Defeated;

        /// <summary>A new world: the counters start over (same boundary as <see cref="Expansion.ResetStatics"/>).</summary>
        public static void ResetStatics() =>
            Liberations = LiberationsOccupied = RisingsOccupied = WarsStarted = Battles = BattlesEmpire = Captures = Razes = Defeated = 0;
    }
}
