using Verse;

namespace LivingWorld
{
    public static class LWLog
    {
        public static void Message(string text) => Log.Message("[LivingWorld] " + text);
        public static void Warning(string text) => Log.Warning("[LivingWorld] " + text);
        public static void Error(string text) => Log.Error("[LivingWorld] " + text);
        public static void ErrorOnce(string text, int key) => Log.ErrorOnce("[LivingWorld] " + text, key);
    }
}
