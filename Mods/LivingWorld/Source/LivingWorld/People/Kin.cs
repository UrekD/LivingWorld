using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>
    /// Colonists have family in the world: someone who joins from a real town may leave a parent there (a world pawn
    /// with a vanilla parent relation, kept as a Figure of that town). The parent can come along with that town's
    /// visitors (a family reunion), and what befalls the town befalls them: razed, they may die (vanilla grief for the
    /// colonist) or flee to another of their people's towns.
    /// </summary>
    public static class Kin
    {
        public static int StatCreated, StatVisits;
        /// <summary>Tests: always leave a parent; always come along; die when the town falls.</summary>
        public static bool TestAlways;

        /// <summary>A colonist joined from a town: maybe a parent stays behind there.</summary>
        public static Figure CreateFor(Pawn colonist, Settlement home)
        {
            Faction f = home?.Faction;
            if (!LivingWorldMod.Settings.figures || f == null || f.IsPlayer || !colonist.RaceProps.Humanlike || colonist.relations == null || !(TestAlways || Rand.Chance(0.6f))) return null;
            // Vanilla may already have given them parents: use a living one of their people, never add a third.
            Pawn father = colonist.GetFather(), mother = colonist.GetMother();
            Pawn parent = null;
            foreach (Pawn existing in new[] { father, mother })
                if (existing != null && !existing.Dead && existing.Faction == f && Find.WorldPawns.Contains(existing) && Figures.Of(existing) == null) parent = existing;
            if (parent == null)
            {
                if (father != null && mother != null) return null;
                float age = colonist.ageTracker.AgeBiologicalYearsFloat + Rand.Range(20f, 32f);
                if (age > 90f) return null;
                PawnKindDef kind = f.def.basicMemberKind ?? f.RandomPawnKind();
                var request = new PawnGenerationRequest(kind, f, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                    fixedBiologicalAge: age, fixedChronologicalAge: age, fixedGender: father == null ? Gender.Male : Gender.Female, fixedLastName: (colonist.Name as NameTriple)?.Last);
                parent = PawnGenerator.GeneratePawn(request);
                Find.WorldPawns.PassToWorld(parent);
                colonist.relations.AddDirectRelation(PawnRelationDefOf.Parent, parent);
            }
            Figure fig = Figures.Register(parent, FigureRole.Kin, home);
            if (fig == null) return null;
            fig.visits = 1;
            fig.kinOfId = colonist.thingIDNumber;
            Figures.Befriend(fig, colonist);
            StatCreated++;
            return fig;
        }

        /// <summary>The colonist this figure is family of, if still with the colony.</summary>
        public static Pawn ColonistOf(Figure f, Map map)
        {
            if (map == null) return null;
            foreach (Pawn c in map.mapPawns.FreeColonists)
                if (c.thingIDNumber == f.kinOfId) return c;
            return null;
        }

        /// <summary>A visit from the kin's town: the parent may come along to see their child.</summary>
        public static bool Visits(Lord lord, Settlement home)
        {
            // Any of the town's kin whose child is here (several colonists may come from one town).
            Figure f = null;
            Pawn child = null;
            foreach (Figure k in LivingWorldComponent.Instance.figures)
                if (k.role == FigureRole.Kin && k.homeId == home.ID && k.Available && ColonistOf(k, lord.Map) is Pawn c)
                {
                    f = k;
                    child = c;
                    break;
                }
            if (f == null || !(TestAlways || Rand.Chance(0.35f))) return false;
            Pawn stand = null;
            foreach (Pawn p in lord.ownedPawns)
                if (p.RaceProps.Humanlike && p.Spawned && !p.mindState.wantsToTradeWithColony && (stand == null || p.ageTracker.AgeBiologicalYears > stand.ageTracker.AgeBiologicalYears)) stand = p;
            if (stand == null || !Figures.Swap(lord, stand, f.pawn, asTrader: false)) return false;
            f.visits++;
            StatVisits++;
            child.needs?.mood?.thoughts.memories.TryGainMemory(LWThoughtDefOf.LW_FamilyVisit, f.pawn);
            f.pawn.needs?.mood?.thoughts.memories.TryGainMemory(LWThoughtDefOf.LW_FamilyVisit, child);
            Find.LetterStack.ReceiveLetter("LW_KinVisitLabel".Translate(child.LabelShort), "LW_KinVisitText".Translate(f.pawn.Named("PARENT"), child.Named("CHILD"), home.Label.Named("HOME")),
                LetterDefOf.PositiveEvent, new LookTargets(f.pawn, child), f.pawn.Faction);
            return true;
        }

        /// <summary>The kin's town was razed: they die there, or flee to another town of their people.</summary>
        public static void Notify_HomeRazed(Settlement s)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            foreach (Figure f in comp.figures.ToArray())
            {
                // Only a parent living freely at home (not in a caravan, kidnapped or wanted by a quest).
                if (f.role != FigureRole.Kin || f.homeId != s.ID || !f.Available) continue;
                Pawn child = null;
                foreach (Map m in Find.Maps)
                    if (m.IsPlayerHome && ColonistOf(f, m) is Pawn c) child = c;
                if (TestAlways || Rand.Chance(0.4f))
                {
                    f.pawn.Kill(null);
                    // Vanilla only grieves deaths it sees; a parent dying far away gets the same thought here.
                    if (child?.needs?.mood != null && !child.needs.mood.thoughts.memories.Memories.Any(m => m.otherPawn == f.pawn && m.def.defName.Contains("Died"))
                        && PawnRelationDefOf.Parent.GetGenderSpecificDiedThought(f.pawn) is ThoughtDef grief)
                        child.needs.mood.thoughts.memories.TryGainMemory(grief, f.pawn);
                    if (child != null)
                        Find.LetterStack.ReceiveLetter("LW_KinDiedLabel".Translate(child.LabelShort), "LW_KinDiedText".Translate(f.pawn.Named("PARENT"), child.Named("CHILD"), s.Label.Named("HOME")),
                            LetterDefOf.NegativeEvent, child);
                    continue;
                }
                Settlement refuge = f.pawn.Faction != null ? Bridge.NearestSettlement(f.pawn.Faction, s.Tile) : null;
                if (refuge == null || refuge == s) continue;
                f.homeId = refuge.ID;
                f.homeName = refuge.Label;
                if (child != null) Messages.Message("LW_KinFled".Translate(f.pawn.Named("PARENT"), child.Named("CHILD"), s.Label.Named("HOME"), refuge.Label.Named("REFUGE")), child, MessageTypeDefOf.NeutralEvent);
            }
        }
    }
}
