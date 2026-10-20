using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Plan "uitour" (windowed runs): steps through the mod's UI on a real-time timer and logs
    /// "[LivingWorld:ui] step N name" so tools/uitour.sh can screenshot each step. Quits at the end.
    /// </summary>
    public static class UiTour
    {
        private const float StepSeconds = 25f;
        private static int step = -1;
        private static float nextAt;
        private static readonly List<(string name, System.Action action)> steps = new List<(string, System.Action)>();

        public static void Update()
        {
            if (steps.Count == 0) Build();
            if (Time.realtimeSinceStartup < nextAt) return;
            step++;
            if (step >= steps.Count)
            {
                Log.Message("[LivingWorld:ui] done");
                Log.Message($"[LivingWorld:test] {(AutoTest.ErrorCount == 0 ? "PASS" : "FAIL")} plan=uitour errors={AutoTest.ErrorCount} {string.Join(" | ", AutoTest.FirstErrors)}");
                Root.Shutdown();
                return;
            }
            try
            {
                steps[step].action();
                Log.Message($"[LivingWorld:ui] step {step} {steps[step].name}");
            }
            catch (System.Exception e)
            {
                Log.Error($"[LivingWorld] ui tour step {steps[step].name} failed: {e}");
            }
            nextAt = Time.realtimeSinceStartup + StepSeconds;
        }

        private static void Build()
        {
            MainButtonDef chronicle = DefDatabase<MainButtonDef>.GetNamed("LW_Chronicle");
            steps.Add(("map", () =>
            {
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                // An envoy on its way, so the Factions page shows one.
                Faction foe = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => PlayerDiplomacy.CannotSend(f) == null && f.HostileTo(Faction.OfPlayer));
                WorldParty envoy = foe == null ? null : PlayerDiplomacy.Send(foe, EnvoyMission.PlayerPeace);
                Log.Message($"[LivingWorld:ui] envoy to {foe?.Name ?? "nobody"}: {(envoy != null ? PlayerDiplomacy.EnvoyStatus(envoy) : "not sent")}");
                // A blood feud with the faction the diplomacy dialog will show (People, Legends and the blood price offer).
                Faction feudFaction = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => PlayerDiplomacy.CannotSend(x) == null && x.HostileTo(Faction.OfPlayer));
                Settlement town = feudFaction == null ? null : Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction == feudFaction);
                Pawn slayer = Find.CurrentMap?.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                if (town != null && slayer != null)
                {
                    Pawn warlord = PawnGenerator.GeneratePawn(new PawnGenerationRequest(feudFaction.RandomPawnKind(), feudFaction, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                        fixedBiologicalAge: 50f, fixedChronologicalAge: 50f));
                    Find.WorldPawns.PassToWorld(warlord);
                    Figures.FindFor(town.ID, FigureRole.Warlord)?.pawn?.Kill(null);
                    Figures.Register(warlord, FigureRole.Warlord, town);
                    warlord.Kill(new DamageInfo(DamageDefOf.Cut, 500f, instigator: slayer));
                    Log.Message($"[LivingWorld:ui] feud with {feudFaction.Name}: {Feuds.HasOpenFeud(feudFaction)}");
                }
            }));
            string[] pages = { "news", "factions", "relations", "wars", "settlements", "markets", "people", "legends" };
            for (int i = 0; i < pages.Length; i++)
            {
                int index = i;
                steps.Add(("chronicle-" + pages[i], () =>
                {
                    Find.MainTabsRoot.SetCurrentTab(chronicle, playSound: false);
                    Find.WindowStack.WindowOfType<MainTabWindow_Chronicle>()?.SelectPage(index);
                }));
            }
            steps.Add(("diplomacy-dialog", () =>
            {
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                Faction f = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => PlayerDiplomacy.CannotSend(x) == null && x.HostileTo(Faction.OfPlayer))
                            ?? Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => PlayerDiplomacy.CannotSend(x) == null);
                if (f != null) Find.WindowStack.Add(new Dialog_Diplomacy(f));
            }));
            steps.Add(("world-warband", () =>
            {
                Find.WindowStack.TryRemove(typeof(Dialog_Diplomacy), doCloseSound: false);
            }));
            steps.Add(("world-warband-select", () =>
            {
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                WorldParty p = WorldParty.All.FirstOrDefault(x => x.kind == LWPartyDefOf.LW_WarBand) ?? WorldParty.All.FirstOrDefault();
                if (p != null) CameraJumper.TryJumpAndSelect(p);
                else CameraJumper.TryShowWorld();
            }));
            steps.Add(("world-settlement", () =>
            {
                Find.WorldSelector.ClearSelection();
                Map home = Find.AnyPlayerHomeMap;
                // The avenger's town (its tab shows the oath), else the nearest town.
                Figure avenger = LivingWorldComponent.Instance.figures.FirstOrDefault(x => x.FeudOpen && x.Alive);
                Settlement s = (avenger == null ? null : LivingWorldComponent.WorldObjectById(avenger.homeId) as Settlement)
                    ?? LivingWorldComponent.Instance.activeSettlements.Select(c => c.Settlement)
                    .OrderBy(x => home == null ? 0f : Find.WorldGrid.ApproxDistanceInTiles(home.Tile, x.Tile)).FirstOrDefault();
                if (s != null) CameraJumper.TryJumpAndSelect(s);
            }));
            steps.Add(("world-settlement-tab", () =>
            {
                WorldObject sel = Find.WorldSelector.SingleSelectedObject;
                if (sel != null) Find.World.UI.inspectPane.OpenTabType = typeof(WITab_SettlementLife);
            }));
            steps.Add(("world-overview", () =>
            {
                Find.WorldSelector.ClearSelection();
                CameraJumper.TryShowWorld();
            }));
            steps.Add(("settings", () =>
            {
                CameraJumper.TryHideWorld();
                Mod mod = LoadedModManager.GetMod<LivingWorldMod>();
                Find.WindowStack.Add(new Dialog_ModSettings(mod));
            }));
        }
    }
}
