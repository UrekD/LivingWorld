using Verse;

namespace LivingWorld
{
    /// <summary>
    /// "1 battle" / "3 battles": a keyed string for the count, with a "_One" twin for exactly one
    /// (the way vanilla pairs Period1Day with PeriodDays).
    /// </summary>
    public static class CountLabel
    {
        public static string Of(string key, int count) => (count == 1 ? key + "_One" : key).Translate(count).Resolve();

        public static string Days(int days) => days == 1 ? "Period1Day".Translate().Resolve() : "PeriodDays".Translate(days).Resolve();
    }
}
