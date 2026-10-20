using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Performance log (mod setting, or LIVINGWORLD_PROFILE in autotests): times named methods (ours, vanilla's, other
    /// mods' by type name) and writes one CSV line per in-game day: real seconds the day took, average tick and frame
    /// cost, and ms + calls per timed method. The file goes to the save-data folder so a player can send it in.
    /// </summary>
    public static class PerfLog
    {
        /// <summary>Timed by default: the whole tick and frame, our systems, all map components, and known heavy mods.</summary>
        public static readonly string[] DefaultTargets =
        {
            "Verse.TickManager:DoSingleTick",
            "Verse.Root_Play:Update",
            "LivingWorld.LivingWorldComponent:WorldComponentTick",
            "LivingWorld.WorldParty:TickInterval",
            "Verse.MapComponentUtility:MapComponentTick",
            "RimWorld.Planet.WorldComponentUtility:WorldComponentTick",
            "Spaceports.SpaceportsMapComp:MapComponentTick",
            "Spaceports.Buildings.Building_ShuttlePad:Tick",
            "Hospitality.Hospitality_MapComponent:MapComponentTick",
        };

        private class Slot
        {
            public string name;
            public long ticks;
            public int calls;
        }

        private static readonly Dictionary<MethodBase, Slot> slots = new Dictionary<MethodBase, Slot>();
        private static readonly List<Slot> order = new List<Slot>();
        private static Harmony harmony;
        private static string filePath;
        private static long lastStamp;
        private static int lastTick = -1;
        private static bool headerWritten;

        /// <summary>A game was started or loaded: the next line measures from now (not across the loading).</summary>
        public static void ResetClock()
        {
            lastStamp = Stopwatch.GetTimestamp();
            lastTick = -1;
            foreach (Slot s in order)
            {
                s.ticks = 0;
                s.calls = 0;
            }
        }

        public static bool Enabled => harmony != null;
        public static string FilePath => filePath;
        /// <summary>The last day's line (for the autotest's daily metrics).</summary>
        public static string LastLine { get; private set; }

        /// <summary>Starts timing (once per session; more targets can be added later). toFile: write the CSV.</summary>
        public static void Enable(IEnumerable<string> targets, bool toFile, bool warnMissing)
        {
            harmony ??= new Harmony("urekd.livingworld.perflog");
            var prefix = new HarmonyMethod(typeof(PerfLog), nameof(Prefix)) { priority = Priority.First };
            var postfix = new HarmonyMethod(typeof(PerfLog), nameof(Postfix)) { priority = Priority.Last };
            foreach (string entry in targets)
            {
                string[] parts = entry.Trim().Split(':');
                System.Type type = parts.Length == 2 ? AccessTools.TypeByName(parts[0]) : null;
                MethodInfo m = type == null ? null : AccessTools.Method(type, parts[1]);
                if (m == null)
                {
                    if (warnMissing) Log.Warning($"[LivingWorld:profile] not found: {entry}");
                    continue;
                }
                if (slots.ContainsKey(m)) continue;
                var slot = new Slot { name = type.Name + "." + m.Name };
                slots[m] = slot;
                order.Add(slot);
                harmony.Patch(m, prefix, postfix);
            }
            if (toFile && filePath == null)
            {
                string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "LivingWorld");
                Directory.CreateDirectory(dir);
                filePath = Path.Combine(dir, $"perf-{System.DateTime.Now:yyyyMMdd-HHmmss}.csv");
                headerWritten = false; // written with the first line, once every target is known
                LWLog.Message($"performance log: {filePath}");
            }
            lastStamp = Stopwatch.GetTimestamp();
            lastTick = Current.Game?.tickManager?.TicksGame ?? -1; // no game yet when enabled at startup
        }

        private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Postfix(long __state, MethodBase __originalMethod)
        {
            if (!slots.TryGetValue(__originalMethod, out Slot s)) return;
            s.ticks += Stopwatch.GetTimestamp() - __state;
            s.calls++;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>Once per in-game day: one line with everything since the last one, then reset.</summary>
        public static void Notify_Day(LivingWorldComponent comp)
        {
            if (!Enabled) return;
            long now = Stopwatch.GetTimestamp();
            int tick = Find.TickManager.TicksGame;
            double realSeconds = (now - lastStamp) / (double)Stopwatch.Frequency;
            Slot tickSlot = order.Find(s => s.name == "TickManager.DoSingleTick"), frameSlot = order.Find(s => s.name == "Root_Play.Update");
            int pawns = 0;
            foreach (Map m in Find.Maps) pawns += m.mapPawns.AllPawnsSpawnedCount;
            var line = new StringBuilder();
            line.Append(System.DateTime.Now.ToString("HH:mm:ss")).Append(',').Append(tick).Append(',').Append(GenDate.DaysPassed).Append(',')
                .Append(Find.TickManager.CurTimeSpeed).Append(',').Append(lastTick < 0 ? "" : realSeconds.ToString("F1")).Append(',')
                .Append(tickSlot == null || tickSlot.calls == 0 ? "" : (Ms(tickSlot.ticks) / tickSlot.calls).ToString("F3")).Append(',')
                .Append(frameSlot == null || frameSlot.calls == 0 ? "" : (Ms(frameSlot.ticks) / frameSlot.calls).ToString("F2")).Append(',')
                .Append(comp.lastDailyMs.ToString("F1")).Append(',').Append(WorldParty.All.Count).Append(',').Append(Find.Maps.Count).Append(',').Append(pawns);
            var summary = new StringBuilder("profile");
            foreach (Slot s in order)
            {
                line.Append(',').Append(Ms(s.ticks).ToString("F1")).Append(',').Append(s.calls);
                summary.Append(' ').Append(s.name).Append('=').Append(Ms(s.ticks).ToString("F1")).Append("ms/").Append(s.calls);
                s.ticks = 0;
                s.calls = 0;
            }
            LastLine = summary.Append($" realSecondsPerDay={realSeconds:F1}").ToString();
            if (filePath != null)
            {
                try
                {
                    if (!headerWritten)
                    {
                        var header = new StringBuilder("realTime,gameTick,day,speed,realSecondsPerDay,avgTickMs,avgFrameMs,lwDailyMs,worldParties,maps,pawnsSpawned");
                        foreach (Slot s in order) header.Append(',').Append(s.name).Append("_ms,").Append(s.name).Append("_calls");
                        File.WriteAllText(filePath, header.Append('\n').ToString());
                        headerWritten = true;
                    }
                    File.AppendAllText(filePath, line.Append('\n').ToString());
                }
                catch (IOException e)
                {
                    LWLog.Warning("performance log: " + e.Message);
                    filePath = null;
                }
            }
            lastStamp = now;
            lastTick = tick;
        }
    }
}
