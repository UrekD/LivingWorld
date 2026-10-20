using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Plan "ages": factions learn and the world renews. A tribal raider gets a weapon of the next age; a big faction
    /// advances (chronicled) and its traditional town breaks away as a faction of the old kind; a new people arrives.
    /// </summary>
    public static class AgesChecks
    {
        private static readonly List<string> problems = new List<string>();

        public static void Register()
        {
            if (AutoTest.Plan != "ages") return;
            AutoTest.OnBegin.Add(Run);
            AutoTest.Checks.Add(() => problems);
        }

        private static void Run()
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            comp.RebuildIndexes(seedMissing: true);
            Map map = Neighbourhood.Home;
            // Raider gear: a neolithic pawn with a neolithic weapon, upgraded to medieval.
            FactionDef tribal = DefDatabase<FactionDef>.AllDefsListForReading.FirstOrDefault(d => d.humanlikeFaction && d.techLevel == TechLevel.Neolithic && !d.hidden && !d.isPlayer && !d.pawnGroupMakers.NullOrEmpty());
            Faction tribe = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f.def == tribal);
            if (tribe != null)
            {
                Pawn p = PawnGenerator.GeneratePawn(tribe.RandomPawnKind(), tribe);
                // A neolithic club to upgrade (melee always has a medieval step; ranged may not).
                if (p.equipment != null && DefDatabase<ThingDef>.GetNamedSilentFail("MeleeWeapon_Club") is ThingDef club)
                {
                    if (p.equipment.Primary != null) p.equipment.DestroyEquipment(p.equipment.Primary);
                    p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(club, GenStuff.DefaultStuffFor(club)));
                }
                string before = p.equipment?.Primary?.def.defName ?? "none";
                bool up = RaidGear.Upgrade(p, TechLevel.Medieval);
                Log.Message($"[LivingWorld:test] ages: {p.LabelShort} weapon {before} -> {p.equipment?.Primary?.def.defName ?? "none"} ({up})");
                if (p.equipment?.Primary != null && p.equipment.Primary.def.techLevel < TechLevel.Medieval && before != "none") problems.Add("raid gear: weapon not upgraded");
            }
            // Advance + old ways.
            FactionState big = comp.factionStates.Where(fs => fs.faction != null && !fs.faction.def.permanentEnemy && Rebellion.CanSplit(fs.faction.def) && fs.settlements.Count >= 5)
                .OrderByDescending(fs => fs.settlements.Count).FirstOrDefault();
            if (big == null) Log.Message("[LivingWorld:test] ages: no faction big enough to split (skipped)");
            else
            {
                int factions = comp.factionStates.Count, chronicle = comp.chronicle.Count;
                TechLevel was = KnowHow.Effective(big.faction);
                comp.lastRebellionTick = int.MinValue / 2;
                KnowHow.TestForceSplit = true;
                KnowHow.TestAdvance(comp, big);
                KnowHow.ProcessSplits(comp);
                KnowHow.TestForceSplit = false;
                comp.RebuildIndexes(seedMissing: false);
                FactionState split = comp.factionStates.Count > factions ? comp.factionStates[comp.factionStates.Count - 1] : null;
                Log.Message($"[LivingWorld:test] ages: {big.faction.Name} {was} -> {KnowHow.Effective(big.faction)}; split: {split?.faction?.Name} ({(split == null ? "-" : KnowHow.Effective(split.faction).ToString())}), chronicle +{comp.chronicle.Count - chronicle}");
                if (KnowHow.Effective(big.faction) <= was) problems.Add("know-how: no advance");
                if (split == null) problems.Add("old ways: no faction split off");
                else if (KnowHow.Effective(split.faction) != big.faction.def.techLevel) problems.Add("old ways: the split did not keep the old age");
            }
            // A deposed house leads a breakaway: its line goes on in the new faction.
            FactionState realm = comp.factionStates.Where(fs => fs.faction != null && !fs.faction.defeated && !fs.faction.def.permanentEnemy && Rebellion.CanSplit(fs.faction.def) && fs.settlements.Count >= 3)
                .OrderByDescending(fs => fs.settlements.Count).FirstOrDefault();
            CompSettlementLife claim = realm?.settlements.FirstOrDefault(x => x.Active && x.Settlement != SettlementOps.CapitalOf(realm.faction) && !SettlementOps.Protected(x.Settlement) && SettlementOps.CanFall(x.Settlement));
            if (claim == null) Log.Message("[LivingWorld:test] ages: no town for a claimant house (skipped)");
            else
            {
                int count = comp.factionStates.Count;
                Rebellion.Secede(comp, claim, realm.faction, "test", "Testhouse", 3);
                comp.RebuildIndexes(seedMissing: false);
                FactionState exiles = comp.factionStates.Count > count ? comp.factionStates[comp.factionStates.Count - 1] : null;
                Log.Message($"[LivingWorld:test] ages: claimant faction {exiles?.faction?.Name} led by {exiles?.faction?.leader?.Name?.ToStringFull}{(exiles == null ? "" : Rulers.HouseLabel(exiles))}");
                if (exiles == null || exiles.house != "Testhouse" || exiles.houseGenerations != 4 || (exiles.faction.leader?.Name as NameTriple)?.Last != "Testhouse")
                    problems.Add("houses: the deposed house does not lead the breakaway");
            }
            // Renewal: a new people arrives.
            int arrivals = Renewal.StatArrivals;
            Faction newcomers = tribal == null ? null : Renewal.Arrive(comp, tribal);
            int towns = newcomers == null ? 0 : comp.StateFor(newcomers)?.settlements.Count ?? 0;
            Log.Message($"[LivingWorld:test] ages: new people {newcomers?.Name} with {towns} towns");
            if (newcomers == null || towns == 0) problems.Add("renewal: no new people settled");
            else
            {
                // Strangers, not enemies: hostile only to permanent enemies.
                int hostile = Find.FactionManager.AllFactionsListForReading.Count(o => o != newcomers && !o.def.hidden && !o.def.permanentEnemy && newcomers.HostileTo(o));
                Log.Message($"[LivingWorld:test] ages: newcomers hostile to {hostile} factions (colony: {newcomers.HostileTo(Faction.OfPlayer)})");
                if (hostile > 0) problems.Add($"renewal: newcomers arrived hostile to {hostile} factions");
            }
            // An industrial gun "upgraded to ultra": the most advanced age that has guns (vanilla has no ultra guns).
            Faction outlanders = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f.def.techLevel == TechLevel.Industrial && FactionState.IsSimulated(f));
            ThingDef gun = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_BoltActionRifle");
            if (outlanders != null && gun != null)
            {
                Pawn q = PawnGenerator.GeneratePawn(outlanders.RandomPawnKind(), outlanders);
                if (q.equipment?.Primary != null) q.equipment.DestroyEquipment(q.equipment.Primary);
                q.equipment?.AddEquipment((ThingWithComps)ThingMaker.MakeThing(gun));
                bool up = RaidGear.Upgrade(q, TechLevel.Ultra);
                Log.Message($"[LivingWorld:test] ages: rifle upgraded to ultra -> {q.equipment?.Primary?.def.defName} ({q.equipment?.Primary?.def.techLevel})");
                if (!up) problems.Add("raid gear: no upgrade for an ultra faction's gun");
            }
        }
    }
}
