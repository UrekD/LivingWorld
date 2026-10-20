using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Simulation time. Equals game time, except while history is generated before the landing: then it runs in the
    /// past (Offset &lt; 0), so wars, foundings and chronicle entries get their real (earlier) dates.
    /// </summary>
    public static class SimClock
    {
        public static int Offset;

        public static int Now => Find.TickManager.TicksGame + Offset;
        public static int TicksAbs => GenTicks.TicksAbs + Offset;
        public static bool InHistory => Offset < 0;
    }
}
