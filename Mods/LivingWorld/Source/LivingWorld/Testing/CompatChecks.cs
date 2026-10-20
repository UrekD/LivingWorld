using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld.Planet;
using RimWorld;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Plan "spaceports": builds a powered spaceport (landing pad, beacon, power, conduits) at the start and expects
    /// visitors to come by shuttle (Spaceports' own arrivals, tagged by our contexts) instead of walking the world map.
    /// </summary>
    public static class CompatChecks
    {
        private static bool shuttleSeen, flown;
        private static int built;

        public static void Register()
        {
            if (AutoTest.Plan == "demands")
            {
                var problems = new List<string>();
                AutoTest.OnBegin.Add(() =>
                {
                    LivingWorldComponent comp = LivingWorldComponent.Instance;
                    comp.RebuildIndexes(seedMissing: true);
                    Map map = Find.CurrentMap;
                    Faction enemy = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => FactionState.IsSimulated(f) && f.HostileTo(Faction.OfPlayer) && Bridge.NearestSettlement(f, map.Tile) != null);
                    Faction friend = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => FactionState.IsSimulated(f) && !f.HostileTo(Faction.OfPlayer) && !f.def.permanentEnemy);
                    if (enemy == null || friend == null)
                    {
                        problems.Add("no enemy/friend faction");
                        return;
                    }
                    Settlement near = Bridge.NearestSettlement(enemy, map.Tile);
                    // Tribute: refuse → a vendetta is scheduled.
                    ChoiceLetter_TributeDemand letter = Demands.ForceTribute(enemy, near, 300);
                    DiaOption refuse = letter.Choices.ToList()[1]; // [0] pay, [1] refuse, [2] postpone
                    int before = comp.revenge.Count;
                    refuse.action();
                    Log.Message($"[LivingWorld:test] demands: tribute refused, vendettas {before}->{comp.revenge.Count}");
                    if (comp.revenge.Count <= before) problems.Add("refusing tribute scheduled no vendetta");
                    // A letter that times out (or is dismissed) unanswered counts as a refusal.
                    comp.revenge.Clear();
                    ChoiceLetter_TributeDemand ignored = Demands.ForceTribute(enemy, near, 300);
                    Find.LetterStack.RemoveLetter(ignored);
                    Log.Message($"[LivingWorld:test] demands: tribute ignored, vendettas {comp.revenge.Count}");
                    if (comp.revenge.Count == 0) problems.Add("ignoring tribute scheduled no vendetta");
                    // Paying works from silver lying in the colony (no trade beacon), and the truce keeps the faction out of raids.
                    comp.revenge.Clear();
                    Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
                    silver.stackCount = 400;
                    GenPlace.TryPlaceThing(silver, map.Center, map, ThingPlaceMode.Near);
                    int had = Demands.ColonySilver(map);
                    ChoiceLetter_TributeDemand paid = Demands.ForceTribute(enemy, near, 300);
                    DiaOption pay = paid.Choices.ToList()[0];
                    if (pay.disabled) problems.Add("tribute: pay disabled with " + had + " silver on the map");
                    else pay.action();
                    bool truce = comp.TributeTruce(enemy);
                    var raidParms = new IncidentParms { target = map, points = 300f };
                    bool truceSource = ((IncidentWorker_RaidEnemy)IncidentDefOf.RaidEnemy.Worker).FactionCanBeGroupSource(enemy, raidParms);
                    Log.Message($"[LivingWorld:test] demands: paid, silver {had}->{Demands.ColonySilver(map)}, truce {truce}, still raid source {truceSource}, vendettas {comp.revenge.Count}");
                    if (Demands.ColonySilver(map) != had - 300) problems.Add("tribute: silver not taken");
                    if (!truce || truceSource) problems.Add("tribute: truce faction can still raid");
                    if (comp.revenge.Count > 0) problems.Add("tribute: paying scheduled a vendetta");
                    comp.SetTributeTruce(enemy, -1);
                    // War request: the colony destroys the target → reward and chronicle.
                    Demands.ForceWarRequest(friend, near);
                    int chronicle = comp.chronicle.Count;
                    Demands.Notify_SettlementDestroyed(near.ID, enemy);
                    bool rewarded = comp.chronicle.Count > chronicle;
                    Log.Message($"[LivingWorld:test] demands: war request on {near.Label} for {friend.Name}: rewarded={rewarded}");
                    if (!rewarded) problems.Add("war request reward not given");
                    // ... but not when the town had changed hands meanwhile.
                    Demands.ForceWarRequest(friend, near);
                    chronicle = comp.chronicle.Count;
                    Demands.Notify_SettlementDestroyed(near.ID, friend);
                    if (comp.chronicle.Count > chronicle) problems.Add("war request rewarded for a town the asker held");
                    // A request whose target changed hands lapses without blame.
                    Settlement other = Find.WorldObjects.Settlements.FirstOrDefault(s => s.Faction == enemy && s != near && SettlementOps.CanFall(s));
                    if (other != null)
                    {
                        Demands.ForceWarRequest(friend, other);
                        int gw = friend.PlayerGoodwill;
                        SettlementOps.Capture(other, friend);
                        comp.warRequests[comp.warRequests.Count - 1].deadlineTicks = Find.TickManager.TicksGame - 1;
                        Demands.DailyCheck(comp);
                        Log.Message($"[LivingWorld:test] demands: request on {other.Label} after capture: goodwill {gw}->{friend.PlayerGoodwill}, pending {comp.warRequests.Count}");
                        if (friend.PlayerGoodwill < gw) problems.Add("war request: blamed for a town that changed hands");
                    }
                    // Preparing the save for removal switches this save off, not the player's settings, and removes every
                    // letter of the mod (an audience here) from the stack and the archive.
                    if (friend.leader != null && comp.StateFor(friend)?.settlements.Count > 0) RulerAudience.Open(friend.leader, friend, comp.StateFor(friend).settlements[0].Settlement);
                    LivingWorldMod.PrepareForRemoval();
                    if (Find.LetterStack.LettersListForReading.Any(l => l is ChoiceLetter_RulerAudience) || Find.Archive.ArchivablesListForReading.Any(a => a is ChoiceLetter_RulerAudience))
                        problems.Add("prepare for removal: an audience letter is left");
                    bool off = !LivingWorldMod.Settings.simulateEconomy && !LivingWorldMod.Settings.worldParties;
                    if (PlayerDiplomacy.CannotSend(friend) == null) problems.Add("prepare for removal: envoys can still be sent");
                    comp.preparedForRemoval = false;
                    bool back = LivingWorldMod.Settings.simulateEconomy && LivingWorldMod.Settings.worldParties;
                    Log.Message($"[LivingWorld:test] demands: prepared for removal off={off}, re-enabled={back}");
                    if (!off || !back) problems.Add("prepare for removal: per-save switch broken");
                });
                // A war band already marching when tribute is paid turns back on arrival.
                bool bandChecked = false;
                AutoTest.Hourly.Add(() =>
                {
                    if (bandChecked) return;
                    WorldParty band = WorldParty.All.FirstOrDefault(p => p.kind == LWPartyDefOf.LW_WarBand && p.deferredParms != null && p.Faction != null);
                    if (band == null) return;
                    bandChecked = true;
                    LivingWorldComponent comp = LivingWorldComponent.Instance;
                    comp.SetTributeTruce(band.Faction, Find.TickManager.TicksGame + GenDate.TicksPerDay * 10);
                    bool done = band.kind.Worker.ArrivedAtDestination(band);
                    Log.Message($"[LivingWorld:test] demands: war band of {band.Faction.Name} under truce: raided={done} returning={band.phase == PartyPhase.Returning}");
                    if (done || band.deferredParms != null) problems.Add("tribute truce: a marching war band still raided");
                    comp.SetTributeTruce(band.Faction, -1);
                });
                AutoTest.Checks.Add(() => problems);
                return;
            }
            if (AutoTest.Plan == "refugees")
            {
                AutoTest.OnBegin.Add(() =>
                {
                    Map map = Find.CurrentMap;
                    Settlement near = Find.WorldObjects.Settlements.Where(s => s.Faction != null && !s.Faction.IsPlayer)
                        .OrderBy(s => Find.WorldGrid.ApproxDistanceInTiles(map.Tile, s.Tile)).FirstOrDefault();
                    Refugees.Notify_Disaster(near, razed: true, force: true);
                    Log.Message($"[LivingWorld:test] refugees: disaster at {near?.Label} ({(near == null ? -1f : Find.WorldGrid.ApproxDistanceInTiles(map.Tile, near.Tile)):F1} tiles, history {SimClock.InHistory}, home {Find.AnyPlayerHomeMap != null}), pending {LivingWorldComponent.Instance.refugees.Count}");
                });
                AutoTest.Checks.Add(() => Refugees.StatQuests > 0 ? new string[0] : new[] { "no refugee quest" });
                return;
            }
            if (AutoTest.Plan != "spaceports") return;
            AutoTest.OnBegin.Add(BuildSpaceport);
            AutoTest.Hourly.Add(() =>
            {
                Map map = Find.AnyPlayerHomeMap;
                // Once the port has power: a visit from a spacefaring faction, which Spaceports flies in.
                if (!flown && map != null && Find.TickManager.TicksGame > GenDate.TicksPerHour * 6)
                {
                    flown = true;
                    Faction f = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => !x.IsPlayer && !x.Hidden && !x.defeated
                        && x.def.techLevel >= TechLevel.Industrial && !x.HostileTo(Faction.OfPlayer) && !x.def.pawnGroupMakers.NullOrEmpty());
                    if (f != null)
                    {
                        IncidentParms visit = StorytellerUtility.DefaultParmsNow(IncidentDefOf.VisitorGroup.category, map);
                        visit.faction = f;
                        visit.points = 150f;
                        Log.Message($"[LivingWorld:test] spaceports: visit by {f.Name} ({f.def.techLevel}): {IncidentDefOf.VisitorGroup.Worker.TryExecute(visit)}");
                    }
                }
                if (map != null && Find.TickManager.TicksGame % (GenDate.TicksPerHour * 6) < GenDate.TicksPerHour && Find.TickManager.TicksGame < GenDate.TicksPerDay * 2) Diagnose(map);
                if (map != null && map.listerThings.AllThings.Any(t => t.def.defName.StartsWith("Spaceports_Shuttle") && t.def.defName != "Spaceports_ShuttleLandingPad"))
                {
                    if (!shuttleSeen) Log.Message("[LivingWorld:test] spaceports: a shuttle landed");
                    shuttleSeen = true;
                }
            });
            AutoTest.Checks.Add(Checks);
        }

        private static void BuildSpaceport()
        {
            Map map = Find.CurrentMap;
            // An open, unroofed spot near the colony for the 16x7 spaceport.
            IntVec3 c = map.Center;
            CellFinder.TryFindRandomCellNear(map.Center, map, 40, x => Clear(map, x), out c);
            // Power first: consumers connect to transmitters that exist when they spawn.
            Spawn(map, "VanometricPowerCell", c + new IntVec3(-6, 0, 0), Rot4.North);
            Spawn(map, "VanometricPowerCell", c + new IntVec3(-6, 0, 2), Rot4.North);
            Spawn(map, "VanometricPowerCell", c + new IntVec3(-6, 0, -2), Rot4.North);
            for (int x = -7; x <= 8; x++) Spawn(map, "PowerConduit", c + new IntVec3(x, 0, 3), Rot4.North);
            Spawn(map, "Spaceports_ShuttleLandingPad", c, Rot4.North);
            Spawn(map, "Spaceports_Beacon", c + new IntVec3(6, 0, 1), Rot4.North);
            System.Type modType = AccessTools.TypeByName("Spaceports.SpaceportsMod"), settingsType = AccessTools.TypeByName("Spaceports.SpaceportsSettings");
            Mod mod = LoadedModManager.ModHandles.FirstOrDefault(m => m.GetType() == modType);
            if (mod != null && settingsType != null)
            {
                object settings = AccessTools.Method(typeof(Mod), nameof(Mod.GetSettings)).MakeGenericMethod(settingsType).Invoke(mod, null);
                Traverse t = Traverse.Create(settings);
                t.Field("hospitalityChance").SetValue(1f);
                t.Field("visitorFrequencyDays").SetValue(0.5f);
                t.Field("airspaceLockdown").SetValue(false);
            }
            // A fed colony doesn't smash its own landing pad in a tantrum.
            Thing meals = ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack);
            meals.stackCount = 60;
            GenPlace.TryPlaceThing(meals, map.mapPawns.FreeColonistsSpawned.First().Position, map, ThingPlaceMode.Near);
            Log.Message($"[LivingWorld:test] spaceports: built {built} buildings, settings {(mod != null ? "set" : "not found")}, wouldFly={SpaceportsCompat.WouldFly(map, Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => !f.IsPlayer && f.def.techLevel >= TechLevel.Industrial && !f.HostileTo(Faction.OfPlayer)))}");
        }

        private static bool Clear(Map map, IntVec3 center)
        {
            foreach (IntVec3 x in CellRect.CenteredOn(center, 9, 5))
                if (!x.InBounds(map) || x.Roofed(map) || !x.Standable(map) || x.GetEdifice(map) != null) return false;
            return true;
        }

        /// <summary>Asks Spaceports itself why shuttles can or cannot land.</summary>
        private static void Diagnose(Map map)
        {
            System.Type utils = AccessTools.TypeByName("Spaceports.Utils");
            Building pad = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.def.defName == "Spaceports_ShuttleLandingPad");
            Building beacon = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.def.defName == "Spaceports_Beacon");
            object Call(object target, string name) => target == null ? "-" : AccessTools.Method(target.GetType(), name)?.Invoke(target, null) ?? "?";
            object clear = utils == null ? "-" : AccessTools.Method(utils, "CheckIfClearForLanding")?.Invoke(null, new object[] { map, 1 });
            Log.Message($"[LivingWorld:test] spaceports diag: clearForLanding={clear} padAvailable={Call(pad, "IsAvailable")} padPowered={Call(pad, "IsPowered")} " +
                        $"padUnroofed={Call(pad, "IsUnroofed")} beaconPower={beacon?.GetComp<CompPowerTrader>()?.PowerOn} padNet={pad?.GetComp<CompPowerTrader>()?.PowerNet != null}");
        }

        private static void Spawn(Map map, string defName, IntVec3 cell, Rot4 rot)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Log.Warning($"[LivingWorld:test] spaceports: no def {defName}");
                return;
            }
            Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            t.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(t, cell, map, rot, WipeMode.Vanish);
            built++;
        }

        private static IEnumerable<string> Checks()
        {
            if (built < 3) yield return $"spaceport not built ({built} buildings)";
            if (!shuttleSeen) yield return "no shuttle landed at the spaceport";
            if (Bridge.StatVisitLords == 0) yield return "no visiting group arrived";
        }
    }
}
