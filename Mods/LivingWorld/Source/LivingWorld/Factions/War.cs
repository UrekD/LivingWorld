using Verse;

namespace LivingWorld
{
    /// <summary>
    /// A war between two NPC factions. The relation itself is vanilla goodwill (hostile at ≤ -75); this record keeps
    /// what vanilla does not: when and why it started, battles, score, and how it ended. Reconciled daily against the
    /// real relation (see <see cref="Diplomacy"/>).
    /// </summary>
    public class War : IExposable
    {
        public int id;
        public int factionA = -1, factionB = -1;
        public string nameA, nameB;
        public int startTicks;
        public int endTicks;
        public bool ended;
        /// <summary>Positive favours A.</summary>
        public float score;
        public int battles;
        public string cause;
        /// <summary>True for hostilities that predate the simulation (worldgen feuds).</summary>
        public bool ancient;
        public int chronicleId = -1;

        public bool Active => !ended;

        public bool Involves(int factionId) => factionA == factionId || factionB == factionId;

        public int Other(int factionId) => factionA == factionId ? factionB : factionA;

        public bool Between(int a, int b) => (factionA == a && factionB == b) || (factionA == b && factionB == a);

        /// <summary>Score from one side's point of view.</summary>
        public float ScoreFor(int factionId) => factionA == factionId ? score : -score;

        public void AddScore(int winnerFactionId, float amount)
        {
            score += winnerFactionId == factionA ? amount : -amount;
            battles++;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref factionA, "a", -1);
            Scribe_Values.Look(ref factionB, "b", -1);
            Scribe_Values.Look(ref nameA, "nameA");
            Scribe_Values.Look(ref nameB, "nameB");
            Scribe_Values.Look(ref startTicks, "start");
            Scribe_Values.Look(ref endTicks, "end");
            Scribe_Values.Look(ref ended, "ended");
            Scribe_Values.Look(ref score, "score");
            Scribe_Values.Look(ref battles, "battles");
            Scribe_Values.Look(ref cause, "cause");
            Scribe_Values.Look(ref ancient, "ancient");
            Scribe_Values.Look(ref chronicleId, "chronicleId", -1);
        }
    }
}
