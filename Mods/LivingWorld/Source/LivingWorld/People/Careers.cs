using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Careers of notable people: a merchant who keeps coming becomes a merchant prince (a heavier purse each visit), a
    /// warlord who keeps raiding becomes a dread warlord (more follow them: bigger war bands). Old merchants and warlords
    /// retire and hand the work on to a grown child, who starts out unknown.
    /// </summary>
    public static class Careers
    {
        public const int PrinceVisits = 5, DreadRaids = 3, DreadKills = 5;
        public const float DreadWarlordPoints = 1.2f, RetireAge = 66f;
        public static int StatRenowned, StatRetired, StatPrinceSilver;
        /// <summary>Tests: the next daily check retires every old-enough figure at once.</summary>
        public static bool TestRetireNow;

        /// <summary>The figure led their caravan or war band onto the colony's map.</summary>
        public static void Notify_Led(Figure f)
        {
            TellRenown(f);
            if (f.role != FigureRole.Merchant || !f.Renowned || f.pawn?.inventory == null) return;
            Thing coins = ThingMaker.MakeThing(ThingDefOf.Silver);
            coins.stackCount = Rand.RangeInclusive(30, 60) * 10;
            int silver = coins.stackCount;
            if (!f.pawn.inventory.innerContainer.TryAdd(coins)) return;
            StatPrinceSilver += silver;
            Messages.Message("LW_Career_PrinceSilver".Translate(f.pawn.Named("PAWN"), (f.homeName ?? "?").Named("HOME"), silver.Named("SILVER")), f.pawn, MessageTypeDefOf.PositiveEvent);
        }

        /// <summary>The rise is told once, in the chronicle.</summary>
        public static void TellRenown(Figure f)
        {
            if (f.renownTold || !f.Renowned) return;
            f.renownTold = true;
            StatRenowned++;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            ChronicleEventDef def = LWChronicleDefOf.LW_FigureRenowned;
            ChronicleEntry e = comp.chronicle.Record(def, def.text.Formatted(f.name.Named("NAME"), (f.homeName ?? "?").Named("HOME"), f.RoleLabel.Named("ROLE")).Resolve(),
                comp.FactionById(f.factionId), Faction.OfPlayer, LivingWorldComponent.WorldObjectById(f.homeId));
            Figures.AddDeed(f, e);
        }

        /// <summary>Daily: old merchants and warlords (free at home, not sworn to a feud, not leading a band) retire, now and then.</summary>
        public static void DailyCheck(LivingWorldComponent comp)
        {
            if (!LivingWorldMod.Settings.figures)
            {
                TestRetireNow = false;
                return;
            }
            foreach (Figure f in comp.figures.ToArray())
            {
                // Still registered (an earlier retirement today may have pruned it from the list).
                if (!comp.figures.Contains(f)) continue;
                if (f.role != FigureRole.Merchant && f.role != FigureRole.Warlord || !f.Available || f.FeudOpen || LeadingBand(f)) continue;
                if (f.pawn.ageTracker.AgeBiologicalYearsFloat < RetireAge || !(TestRetireNow || Rand.Chance(1f / 120f))) continue;
                Retire(comp, f);
            }
            TestRetireNow = false;
        }

        /// <summary>A war band from the warlord's town is on its way to the colony (its letter named them; they lead it in).</summary>
        private static bool LeadingBand(Figure f)
        {
            if (f.role != FigureRole.Warlord) return false;
            foreach (WorldParty p in WorldParty.All)
                if (p.kind == LWPartyDefOf.LW_WarBand && p.originId == f.homeId && p.deferredParms != null) return true;
            return false;
        }

        /// <summary>
        /// The figure steps down; a grown child of theirs (their name) takes on the caravans or the war bands, with any open
        /// order the colony took and the colonists the parent knew. All or nothing: if the heir cannot be registered (no home
        /// to hand on, notable people off), the figure carries on and the generated child is discarded.
        /// </summary>
        public static Figure Retire(LivingWorldComponent comp, Figure f)
        {
            if (!LivingWorldMod.Settings.figures || !comp.figures.Contains(f) || f.status != FigureStatus.Active) return null;
            Faction faction = comp.FactionById(f.factionId);
            if (faction == null || faction.defeated || LivingWorldComponent.WorldObjectById(f.homeId) is not Settlement home || !home.Spawned || home.Faction != faction) return null;
            float age = Mathf.Clamp(f.pawn.ageTracker.AgeBiologicalYearsFloat - Rand.Range(22f, 32f), 20f, 50f);
            PawnKindDef kind = f.pawn.kindDef != null && f.pawn.kindDef.defaultFactionDef == faction.def ? f.pawn.kindDef : faction.RandomPawnKind();
            if (kind == null) return null;
            var request = new PawnGenerationRequest(kind, faction, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                fixedBiologicalAge: age, fixedChronologicalAge: age, fixedLastName: (f.pawn.Name as NameTriple)?.Last);
            Pawn child = PawnGenerator.GeneratePawn(request);
            Find.WorldPawns.PassToWorld(child);
            // Registering may prune a full list: the parent is shielded until the heir (who keeps them afterwards) is in.
            Figures.PruneProtectId = f.id;
            Figure heir;
            try
            {
                heir = Figures.Register(child, f.role, home);
            }
            finally
            {
                Figures.PruneProtectId = -1;
            }
            if (heir == null)
            {
                Find.WorldPawns.RemoveAndDiscardPawnViaGC(child);
                return null;
            }
            // Committed: the parent retires, a real family link, the record, the order and acquaintances pass on.
            child.relations?.AddDirectRelation(PawnRelationDefOf.Parent, f.pawn);
            f.status = FigureStatus.Retired;
            StatRetired++;
            heir.successorOf = f.id;
            ChronicleEventDef def = LWChronicleDefOf.LW_FigureRetired;
            ChronicleEntry e = comp.chronicle.Record(def, def.text.Formatted(f.name.Named("NAME"), f.RoleLabel.Named("ROLE"), heir.name.Named("HEIR")).Resolve(),
                faction, null, home, silent: true);
            Figures.AddDeed(f, e);
            Figures.AddDeed(heir, e);
            heir.commissionDef = f.commissionDef;
            heir.commissionCount = f.commissionCount;
            heir.commissionPrice = f.commissionPrice;
            heir.commissionDue = f.commissionDue;
            f.ClearCommission();
            heir.friends.AddRange(f.friends);
            return heir;
        }
    }
}
