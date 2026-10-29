using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// The ruler (vanilla faction leader) as an actor: each new ruler gets a temperament (two RulerTraitDefs, which
    /// multiply into FactionState.TraitFactor) and a goal that biases diplomacy, war and expansion. Goals end in
    /// success or are given up, and a contested succession can split a big faction.
    /// </summary>
    public static class Rulers
    {
        /// <summary>A goal not reached in this time is given up (and a new one chosen).</summary>
        public const int GoalYears = 2;
        /// <summary>Settlements taken from the target that count as a conquest.</summary>
        public const int ConquestTowns = 2;

        /// <summary>
        /// The ruling house when a ruler is chosen (Faction.TryGenerateNewLeader, before vanilla's letter names them) or first
        /// seen: usually an heir of the house follows and bears its name; sometimes a rival house seizes the rule (then
        /// pendingNewHouse is set for the day's chronicle). A pawn the player knows, or one in a quest, keeps their name:
        /// their own house takes over instead.
        /// </summary>
        public static void Succeed(FactionState fs, Pawn leader, bool succession, bool knownBefore = false)
        {
            if (succession && fs.houseLeaderId == leader.thingIDNumber) return; // the same ruler chosen again: no new generation
            fs.houseLeaderId = leader.thingIDNumber;
            NameTriple nt = leader.Name as NameTriple;
            string own = nt != null && nt.IsValid ? nt.Last : null;
            if (fs.house == null || !succession)
            {
                TakeOwn(fs, own);
                return;
            }
            if (own == fs.house)
            {
                fs.houseGenerations++;
                return;
            }
            if (own != null && !knownBefore && Renameable(leader) && Rand.Chance(0.65f))
            {
                // A nickname that was the old surname follows the new one; a distinct nickname stays.
                string nick = nt.Nick == nt.Last ? fs.house : nt.Nick == nt.First ? null : nt.Nick;
                leader.Name = new NameTriple(nt.First, nick, fs.house);
                fs.houseGenerations++;
                return;
            }
            if (own == null) return; // a single-named ruler: the house is not theirs, but nobody else holds it either
            fs.pendingNewHouse = true;
            fs.pendingOldHouse = fs.house;
            fs.pendingOldGenerations = fs.houseGenerations;
            TakeOwn(fs, own);
        }

        private static void TakeOwn(FactionState fs, string own)
        {
            fs.house = own;
            fs.houseGenerations = own == null ? 0 : 1;
        }

        /// <summary>A world pawn nobody at the colony knows: not on a map, in no quest, with no family among the colony's people or known figures.</summary>
        private static bool Renameable(Pawn p)
        {
            if (p.Spawned || !Find.WorldPawns.Contains(p) || Figures.Of(p) != null || Find.QuestManager.IsReservedByAnyQuest(p)
                || PawnUtility.EverBeenColonistOrTameAnimal(p) || PawnUtility.EverBeenQuestLodger(p)) return false;
            if (p.relations != null)
            {
                // Partners too (FamilyByBlood leaves out a colonist's spouse or lover).
                foreach (DirectPawnRelation r in p.relations.DirectRelations)
                    if (r.otherPawn != null && (r.otherPawn.IsColonist || r.otherPawn.IsPrisonerOfColony || r.otherPawn.Spawned || Figures.Of(r.otherPawn) != null)) return false;
                foreach (Pawn kin in p.relations.FamilyByBlood)
                    // Not everSeenByPlayer: vanilla sets it on every faction leader, and heirs are often kin of former ones.
                    if (kin != null && (kin.IsColonist || kin.IsPrisonerOfColony || kin.Spawned || Figures.Of(kin) != null)) return false;
            }
            return true;
        }

        /// <summary>", of House Vane (3rd of the line)"; the founder is "founder of House Vane".</summary>
        public static string HouseLabel(FactionState fs) => fs.house == null ? ""
            : ", " + (fs.houseGenerations >= 2 ? "LW_Ruler_House".Translate(fs.house, Find.ActiveLanguageWorker.OrdinalNumber(fs.houseGenerations)) : "LW_Ruler_HouseFounder".Translate(fs.house)).Resolve();

        /// <summary>Faction leader changed (or seen for the first time): roll the newcomer's temperament and goal.</summary>
        public static void Notify_Leader(LivingWorldComponent comp, FactionState fs, Pawn leader, bool succession, bool newHouse = false)
        {
            if (leader == null || fs.rulerThingId == leader.thingIDNumber) return;
            fs.rulerThingId = leader.thingIDNumber;
            fs.rulerFriends.Clear(); // a new ruler knows nobody at the colony
            RollTraits(fs, leader);
            fs.goal = RulerGoalKind.Hold; // the successor's choice is not dampened by the predecessor's goal
            ChooseGoal(comp, fs);
            // First seen (old save, new faction): only a vow against the colony is worth telling.
            if (!succession && !InvolvesPlayer(fs)) return;
            Faction f = fs.faction;
            ChronicleEventDef def = LWChronicleDefOf.LW_RulerVow;
            comp.chronicle.Record(def, def.text.Formatted(leader.Name.ToStringFull.Named("NAME"), f.LeaderTitle.Named("TITLE"), f.Name.Named("FACTION"),
                TraitsLabel(fs).Named("TRAITS"), GoalLabel(comp, fs).Named("GOAL")).Resolve(), f, comp.FactionById(fs.goalTargetId), silent: !InvolvesPlayer(fs));
            if (succession) TrySuccessionWar(comp, fs, newHouse);
        }

        /// <summary>A faction that has just come into being (rebels, newcomers): its ruler's temperament and goal now.</summary>
        public static void InitNewFaction(LivingWorldComponent comp, Faction f)
        {
            FactionState fs = comp.StateFor(f);
            if (fs == null || f.leader == null) return;
            fs.lastLeaderThingId = f.leader.thingIDNumber;
            fs.lastLeaderName = f.leader.Name?.ToStringFull;
            Succeed(fs, f.leader, succession: false);
            Notify_Leader(comp, fs, f.leader, succession: false);
        }

        private static bool InvolvesPlayer(FactionState fs) => fs.goalTargetId == Faction.OfPlayer.loadID;

        /// <summary>
        /// The seed of a ruler's temperament: the faction (as in FactionState.RollTraits), the accession tick and the ruler's name. Not the
        /// thingIDNumber: that counter is shared with every thing the game makes (world generation, the landing, gear, relatives), so the
        /// same ruler got other traits, hence another goal, in another run of the same seed.
        /// </summary>
        internal static int TraitSeed(FactionState fs, Pawn leader)
        {
            int faction = fs.factionId * 7919 + (fs.faction?.randomKey ?? 0);
            int name = GenText.StableStringHash(leader.Name?.ToStringFull ?? "");
            return Gen.HashCombineInt(Gen.HashCombineInt(faction, SimClock.Now), name ^ 0x4C575254);
        }

        private static void RollTraits(FactionState fs, Pawn leader)
        {
            fs.rulerTraits.Clear();
            Rand.PushState(TraitSeed(fs, leader));
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    var candidates = new List<RulerTraitDef>();
                    foreach (RulerTraitDef t in DefDatabase<RulerTraitDef>.AllDefsListForReading)
                    {
                        if (fs.rulerTraits.Contains(t)) continue;
                        bool clash = false;
                        foreach (RulerTraitDef have in fs.rulerTraits)
                            if (have.conflictsWith.Contains(t) || t.conflictsWith.Contains(have)) clash = true;
                        if (!clash) candidates.Add(t);
                    }
                    if (candidates.TryRandomElementByWeight(t => t.commonality, out RulerTraitDef pick)) fs.rulerTraits.Add(pick);
                }
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static float GoalWeight(FactionState fs, System.Func<RulerTraitDef, float> pick)
        {
            float w = 1f;
            foreach (RulerTraitDef t in fs.rulerTraits) w *= pick(t);
            return w;
        }

        /// <summary>What the ruler sets out to do, from temperament and situation.</summary>
        public static void ChooseGoal(LivingWorldComponent comp, FactionState fs)
        {
            RulerGoalKind previous = fs.goal;
            fs.goalSince = SimClock.Now;
            fs.goalProgress = 0;
            fs.goalTargetId = -1;
            Faction f = fs.faction;
            // Candidates: the faction with the worst grudge (vengeance), a weaker hostile neighbour (conquest).
            Faction grudgeTarget = null;
            float worst = 15f;
            foreach (Grievance g in fs.grievances)
                if (g.amount > worst && comp.FactionById(g.otherFactionId) is Faction o && !o.defeated && (o.IsPlayer || comp.StateFor(o)?.settlements.Count > 0))
                {
                    worst = g.amount;
                    grudgeTarget = o;
                }
            Faction prey = null;
            float bestRatio = 1.2f;
            foreach (FactionState o in comp.factionStates)
            {
                if (o == fs || o.faction == null || o.faction.defeated || o.settlements.Count == 0 || !f.HostileTo(o.faction)) continue;
                float ratio = (fs.strength + 1f) / (o.strength + 1f);
                if (ratio > bestRatio)
                {
                    bestRatio = ratio;
                    prey = o.faction;
                }
            }
            var options = new List<(RulerGoalKind kind, Faction target, float w)>
            {
                (RulerGoalKind.Hold, null, 0.6f),
                (RulerGoalKind.Prosper, null, 1f * GoalWeight(fs, t => t.prosperWeight) * (f.def.permanentEnemy ? 0.2f : 1f)),
                // Expanding needs room to found towns (Expansion's cap).
                (RulerGoalKind.Expand, null, 0.8f * GoalWeight(fs, t => t.expandWeight) * (fs.settlements.Count >= Expansion.Cap(comp, fs) - 0.5f ? 0.15f : 1f)),
            };
            if (grudgeTarget != null) options.Add((RulerGoalKind.Avenge, grudgeTarget, 1.2f * GoalWeight(fs, t => t.avengeWeight) * Mathf.Min(3f, worst / 30f)));
            if (prey != null) options.Add((RulerGoalKind.Conquer, prey, 1f * GoalWeight(fs, t => t.conquerWeight) * Mathf.Min(2f, bestRatio - 0.5f)));
            // A ruler who just finished (or gave up) a goal rarely takes up the same one again at once.
            if (!options.TryRandomElementByWeight(o => o.kind == previous && previous != RulerGoalKind.Hold ? o.w * 0.3f : o.w, out var pick)) pick = options[0];
            fs.goal = pick.kind;
            fs.goalTargetId = pick.target?.loadID ?? -1;
        }

        /// <summary>Daily: goals are reached, become moot, or are given up after a while.</summary>
        public static void DailyCheck(LivingWorldComponent comp, FactionState fs)
        {
            if (fs.faction?.leader == null) return;
            if (fs.goal == RulerGoalKind.Hold)
            {
                // A ruler content to hold looks around again every half year (new grudges, weaker neighbours).
                if (SimClock.Now - fs.goalSince > GenDate.TicksPerYear / 2) NewGoal(comp, fs);
                return;
            }
            Faction target = comp.FactionById(fs.goalTargetId);
            bool targeted = fs.goal == RulerGoalKind.Conquer || fs.goal == RulerGoalKind.Avenge;
            if (targeted && (target == null || target.defeated))
            {
                Finish(comp, fs, achieved: target != null && fs.goalProgress > 0);
                return;
            }
            if (fs.goal == RulerGoalKind.Conquer && fs.goalProgress >= ConquestTowns)
            {
                Finish(comp, fs, achieved: true);
                return;
            }
            // Vengeance on the colony: now and then the ruler sends warriors (through the storyteller's queue, capped).
            if (fs.goal == RulerGoalKind.Avenge && target != null && target.IsPlayer && !SimClock.InHistory && fs.faction.HostileTo(Faction.OfPlayer)
                && Neighbourhood.Home is Map home && Rand.Chance(1f / 30f) && Bridge.NearestSettlement(fs.faction, home.Tile) is Settlement from)
            {
                if (Revenge.Schedule(fs.faction, from.ID, -1)) fs.goalProgress++;
            }
                        if (SimClock.Now - fs.goalSince > GenDate.TicksPerYear * GoalYears) Finish(comp, fs, achieved: fs.goal == RulerGoalKind.Prosper && fs.treasury > 2000f && AverageProsperity(fs) > 0.55f
                                                                                                      || fs.goal == RulerGoalKind.Expand && fs.goalProgress > 0
                                                                                                      || fs.goal == RulerGoalKind.Avenge && fs.goalProgress >= ConquestTowns);
        }

        private static float AverageProsperity(FactionState fs)
        {
            float sum = 0f;
            foreach (CompSettlementLife s in fs.settlements) sum += s.prosperity;
            return fs.settlements.Count == 0 ? 0f : sum / fs.settlements.Count;
        }

        private static void Finish(LivingWorldComponent comp, FactionState fs, bool achieved)
        {
            Faction f = fs.faction;
            Pawn leader = f.leader;
            Faction target = comp.FactionById(fs.goalTargetId);
            ChronicleEventDef def = achieved ? LWChronicleDefOf.LW_RulerGoalDone : LWChronicleDefOf.LW_RulerGoalAbandoned;
            comp.chronicle.Record(def, def.text.Formatted(leader.Name.ToStringFull.Named("NAME"), f.LeaderTitle.Named("TITLE"), f.Name.Named("FACTION"),
                GoalLabel(comp, fs).Named("GOAL")).Resolve(), f, target, silent: !InvolvesPlayer(fs));
            if (achieved) fs.warWeariness = Mathf.Clamp01(fs.warWeariness - 0.15f);
            NewGoal(comp, fs);
        }

        /// <summary>A sitting ruler picks a new goal; a real one (not "hold") is told in the chronicle.</summary>
        private static void NewGoal(LivingWorldComponent comp, FactionState fs)
        {
            ChooseGoal(comp, fs);
            if (fs.goal == RulerGoalKind.Hold) return;
            Faction f = fs.faction;
            ChronicleEventDef def = LWChronicleDefOf.LW_RulerNewGoal;
            comp.chronicle.Record(def, def.text.Formatted(f.leader.Name.ToStringFull.Named("NAME"), f.LeaderTitle.Named("TITLE"), f.Name.Named("FACTION"),
                GoalLabel(comp, fs).Named("GOAL")).Resolve(), f, comp.FactionById(fs.goalTargetId), silent: !InvolvesPlayer(fs) && !NearColony(fs));
        }

        /// <summary>The faction has a town within the colony's neighbourhood: its rulers' aims matter to the player.</summary>
        private static bool NearColony(FactionState fs)
        {
            if (SimClock.InHistory || Neighbourhood.Home is not Map home) return false;
            float r = Neighbourhood.Radius(home.Tile, Neighbours.Range);
            foreach (CompSettlementLife s in fs.settlements)
                if (s.Active && Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Settlement.Tile) <= r) return true;
            return false;
        }

        /// <summary>A settlement changed hands: progress for a conqueror sworn against the loser.</summary>
        public static void Notify_Captured(LivingWorldComponent comp, Faction conqueror, Faction loser)
        {
            FactionState fs = comp?.StateFor(conqueror);
            if (fs != null && loser != null && fs.GoalAgainst(loser)) fs.goalProgress++;
        }

        /// <summary>Settlers founded a town: progress for an expanding ruler.</summary>
        public static void Notify_Founded(LivingWorldComponent comp, Faction f)
        {
            FactionState fs = comp?.StateFor(f);
            if (fs != null && fs.goal == RulerGoalKind.Expand) fs.goalProgress++;
        }

        /// <summary>A big, restless faction may not accept the new ruler: a rival claimant takes a town and splits off.</summary>
        private static void TrySuccessionWar(LivingWorldComponent comp, FactionState fs, bool newHouse)
        {
            if (!LivingWorldMod.Settings.allowConquest || fs.settlements.Count < 6 || !Rebellion.CanSplit(fs.faction.def) || !Rebellion.CanRise(comp)) return;
            CompSettlementLife restless = null;
            float unrest = 0f;
            Settlement capital = SettlementOps.CapitalOf(fs.faction);
            foreach (CompSettlementLife s in fs.settlements)
            {
                if (!s.Active || s.Settlement == capital || SettlementOps.Protected(s.Settlement) || !SettlementOps.CanFall(s.Settlement)) continue;
                unrest += s.unrest;
                if (restless == null || s.unrest > restless.unrest) restless = s;
            }
            unrest /= fs.settlements.Count;
            // A new house on the throne finds fewer towns willing to accept it.
            if (restless == null || !Rand.Chance(Mathf.Clamp01((unrest - 0.15f) * 1.5f * (newHouse ? 2f : 1f)))) return;
            if (newHouse && fs.pendingOldHouse != null)
                Rebellion.Secede(comp, restless, fs.faction, "LW_WarCause_Usurper".Translate(fs.pendingOldHouse, fs.house).Resolve(), fs.pendingOldHouse, fs.pendingOldGenerations);
            else Rebellion.Secede(comp, restless, fs.faction, "LW_WarCause_Succession".Translate().Resolve());
        }

        public static string TraitsLabel(FactionState fs)
        {
            var labels = new List<string>();
            foreach (RulerTraitDef t in fs.rulerTraits) labels.Add(t.label);
            return labels.Count == 0 ? "?" : string.Join(", ", labels);
        }

        public static string GoalLabel(LivingWorldComponent comp, FactionState fs)
        {
            Faction target = comp.FactionById(fs.goalTargetId);
            string name = target == null ? "?" : target.IsPlayer ? (string)"LW_Ruler_TheColony".Translate(target.Name) : target.Name;
            return ("LW_RulerGoal_" + fs.goal).Translate(name).Resolve();
        }

        /// <summary>"Queen: Name, of House X — ambitious, cruel; wants to conquer Y" (null without a leader); a ruler not yet rolled has only title and name.</summary>
        public static string Line(LivingWorldComponent comp, FactionState fs)
        {
            Faction f = fs.faction;
            if (f?.leader == null) return null;
            return fs.rulerTraits.Count > 0
                ? "LW_Ruler_Line".Translate(f.LeaderTitle.CapitalizeFirst().Named("TITLE"), (f.leader.Name.ToStringFull + HouseLabel(fs)).Named("NAME"), TraitsLabel(fs).Named("TRAITS"),
                    GoalLabel(comp, fs).Named("GOAL")).Resolve()
                : $"{f.LeaderTitle.CapitalizeFirst()}: {f.leader.Name.ToStringFull}";
        }
    }

    /// <summary>The ruling house is decided when vanilla picks a new leader, so its letter already names the heir rightly.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Faction), nameof(Faction.TryGenerateNewLeader))]
    public static class Patch_Faction_TryGenerateNewLeader_Houses
    {
        /// <summary>The faction's world pawns the player had seen before vanilla marks its new leader as seen (a reused pawn is known).</summary>
        public static void Prefix(Faction __instance, out HashSet<Pawn> __state)
        {
            __state = null;
            if (LivingWorldComponent.Instance?.ExistingStateFor(__instance) is not { house: not null }) return;
            __state = new HashSet<Pawn>();
            foreach (Pawn p in Find.WorldPawns.AllPawnsAlive)
                if (p.Faction == __instance && p.relations != null && p.relations.everSeenByPlayer) __state.Add(p);
        }

        public static void Postfix(Faction __instance, bool __result, HashSet<Pawn> __state)
        {
            if (!__result || __state == null || __instance.leader == null || LivingWorldComponent.Instance?.ExistingStateFor(__instance) is not FactionState fs || fs.house == null) return;
            try
            {
                Rulers.Succeed(fs, __instance.leader, succession: true, knownBefore: __state.Contains(__instance.leader));
            }
            catch (System.Exception e)
            {
                LWLog.Error("ruling house: " + e);
            }
        }
    }
}
