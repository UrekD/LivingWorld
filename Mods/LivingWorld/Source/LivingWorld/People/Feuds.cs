using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>
    /// Blood feuds: a colonist who slays a warlord (or downs one who then dies of it) makes a sworn enemy. The warlord's
    /// child (or, for a young warlord, a comrade-in-arms) takes up the war bands of that town, sworn to kill the slayer:
    /// they hunt them when they come, and come again while the feud is open. Every ending goes through Close (its return
    /// raid is called off and the town swears no new feud for a while): the avenger slain or captured by the colony, a
    /// blood price taken, the slayer dead at their people's hands, or the oath moot (slayer gone, avenger dead elsewhere).
    /// </summary>
    public static class Feuds
    {
        public static int StatSworn, StatAvenged, StatEnded;
        public const int CooldownDays = 60, ReturnDays = 25, MaxReturns = 2;
        /// <summary>Avengers on a map with their nemesis: they choose the nemesis as their target (UpdateEnemyTarget postfix).</summary>
        public static readonly Dictionary<Pawn, Pawn> Hunters = new Dictionary<Pawn, Pawn>();

        /// <summary>The colonist who killed the figure, or who downed them in the incapacitation they died of.</summary>
        public static Pawn SlayerOf(Figure f, DamageInfo? dinfo)
        {
            if (dinfo?.Instigator is Pawn p && IsColonist(p)) return p;
            return f.downedBy != null && IsColonist(f.downedBy) ? f.downedBy : null;
        }

        private static bool IsColonist(Pawn p) => p.Faction == Faction.OfPlayer && p.RaceProps.Humanlike;

        /// <summary>A warlord died at the colony's hands (Figures.Notify_Died): an avenger falls, or a feud begins.</summary>
        public static void Notify_WarlordSlain(LivingWorldComponent comp, Figure fallen, Pawn slayer, Settlement home)
        {
            if (fallen.FeudOpen)
            {
                Close(comp, fallen, LWChronicleDefOf.LW_FeudEnded, slayer);
                return;
            }
            if (!LivingWorldMod.Settings.figures || slayer == null) return;
            // The warlord's own people swear it (their town may have fallen to someone else meanwhile: then another of theirs).
            Faction f = comp.FactionById(fallen.factionId);
            if (f == null || f.IsPlayer || f.defeated || !f.def.humanlikeFaction) return;
            if (home == null || !home.Spawned || home.Faction != f) home = NearestTownWithoutFeudCooldown(comp, f, home?.Tile ?? Neighbourhood.Home?.Tile ?? PlanetTile.Invalid);
            if (home == null || !home.Spawned || home.Faction != f || RecentlyEnded(comp, home.ID)) return;
            Pawn heir = MakeHeir(fallen, f, out bool child);
            if (heir == null) return;
            Figure avenger = Figures.Register(heir, FigureRole.Warlord, home);
            if (avenger == null) return;
            avenger.nemesis = slayer;
            avenger.nemesisName = slayer.Name?.ToStringShort ?? slayer.LabelShort;
            avenger.avenges = fallen.name;
            avenger.heirOfFallen = child;
            avenger.swornTick = Find.TickManager.TicksGame;
            StatSworn++;
            ChronicleEventDef def = LWChronicleDefOf.LW_FeudSworn;
            ChronicleEntry e = comp.chronicle.Record(def, def.text.Formatted(avenger.name.Named("NAME"), (child ? "LW_Feud_Child" : "LW_Feud_Comrade").Translate().Named("REL"),
                fallen.name.Named("FALLEN"), avenger.nemesisName.Named("SLAYER"), home.Label.Named("HOME")).Resolve(), f, Faction.OfPlayer, home);
            Figures.AddDeed(avenger, e);
        }

        /// <summary>The faction's nearest town to the tile that has not just ended a feud with the colony.</summary>
        private static Settlement NearestTownWithoutFeudCooldown(LivingWorldComponent comp, Faction f, PlanetTile near)
        {
            Settlement best = null;
            float bestDist = float.MaxValue;
            foreach (Settlement s in Find.WorldObjects.Settlements)
            {
                if (s.Faction != f || RecentlyEnded(comp, s.ID)) continue;
                float d = near.Valid ? Find.WorldGrid.ApproxDistanceInTiles(near, s.Tile) : 0f;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = s;
                }
            }
            return best;
        }

        /// <summary>The town ended a feud with the colony lately (any way): its next warlord does not swear another at once.</summary>
        public static bool RecentlyEnded(LivingWorldComponent comp, int homeId) =>
            comp.feudEnded.TryGetValue(homeId, out int t) && Find.TickManager.TicksGame - t < GenDate.TicksPerDay * CooldownDays;

        /// <summary>
        /// The feud is over. def: LW_FeudEnded (avenger slain), LW_FeudBroken (captured), LW_FeudAvenged / LW_FeudSettled
        /// (chronicled by their callers: pass null), or null for a moot oath. Cooldown and return raid are handled here.
        /// </summary>
        public static void Close(LivingWorldComponent comp, Figure avenger, ChronicleEventDef def, Pawn slayer = null)
        {
            if (!avenger.FeudOpen) return;
            // The colonist they were sworn to kill can breathe again (unless they are what ended it, by dying).
            Pawn hunted = avenger.nemesis;
            if (!hunted.Dead && hunted.Faction == Faction.OfPlayer) hunted.needs?.mood?.thoughts.memories.TryGainMemory(LWThoughtDefOf.LW_FeudOver);
            avenger.nemesis = null;
            avenger.nemesisDownedTick = -1;
            if (avenger.pawn != null) Hunters.Remove(avenger.pawn);
            comp.feudEnded[avenger.homeId] = Find.TickManager.TicksGame;
            Revenge.CancelFor(comp, avenger);
            StatEnded++;
            if (slayer != null) Legends.Add(slayer, "LW_Deed_FeudEnded".Translate(avenger.name, avenger.avenges ?? "?").Resolve());
            if (def == null) return;
            WorldObject home = LivingWorldComponent.WorldObjectById(avenger.homeId);
            string slayerName = slayer?.Name?.ToStringShort ?? slayer?.LabelShort ?? Faction.OfPlayer.Name;
            ChronicleEntry e = comp.chronicle.Record(def, def.text.Formatted(avenger.name.Named("NAME"), (avenger.avenges ?? "?").Named("FALLEN"), slayerName.Named("SLAYER"),
                (home?.Label ?? avenger.homeName ?? "?").Named("HOME"), Faction.OfPlayer.Name.Named("COLONY")).Resolve(), comp.FactionById(avenger.factionId), Faction.OfPlayer, home);
            Figures.AddDeed(avenger, e);
        }

        /// <summary>The warlord's grown child (they take the warlord's name), else a comrade of the war band.</summary>
        private static Pawn MakeHeir(Figure fallen, Faction f, out bool child)
        {
            float parentAge = fallen.pawn?.ageTracker?.AgeBiologicalYearsFloat ?? 0f;
            child = parentAge >= 38f;
            float age = child ? Mathf.Clamp(parentAge - Rand.Range(18f, 28f), 18f, 60f) : Rand.Range(20f, 40f);
            PawnKindDef kind = fallen.pawn?.kindDef != null && fallen.pawn.kindDef.defaultFactionDef == f.def ? fallen.pawn.kindDef : f.RandomPawnKind();
            if (kind == null) return null;
            // No vanilla relation: the fallen pawn is not kept by the world pawn GC, and the link would vanish with it.
            var request = new PawnGenerationRequest(kind, f, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                fixedBiologicalAge: age, fixedChronologicalAge: age, fixedLastName: child ? (fallen.pawn?.Name as NameTriple)?.Last : null);
            Pawn heir = PawnGenerator.GeneratePawn(request);
            Find.WorldPawns.PassToWorld(heir);
            return heir;
        }

        /// <summary>
        /// A colonist died: an avenger sworn to kill them has their revenge if their people killed them, or downed them
        /// and they died of it (bled out); otherwise the oath is moot. Either way the feud is over.
        /// </summary>
        public static void Notify_ColonistDied(LivingWorldComponent comp, Pawn colonist, DamageInfo? dinfo, Hediff exactCulprit = null)
        {
            foreach (Figure f in comp.figures.ToArray())
            {
                if (f.nemesis != colonist || !f.FeudOpen) continue;
                Pawn killer = dinfo?.Instigator as Pawn;
                bool byBand = killer?.Faction != null && killer.Faction.loadID == f.factionId;
                // Bled out after the band downed them (no damage at all, blood loss): not a fire, a collapse or a mercy kill.
                bool ofWounds = dinfo == null && f.nemesisDownedTick >= 0 && Find.TickManager.TicksGame - f.nemesisDownedTick < GenDate.TicksPerDay * 2
                                && exactCulprit?.def == HediffDefOf.BloodLoss; // what killed them, not just a blood loss they carried
                if (!f.Alive || !byBand && !ofWounds)
                {
                    Close(comp, f, null);
                    continue;
                }
                StatAvenged++;
                ChronicleEventDef def = LWChronicleDefOf.LW_FeudAvenged;
                bool byHand = killer == f.pawn;
                ChronicleEntry e = comp.chronicle.Record(def, def.text.Formatted(f.name.Named("NAME"), (f.avenges ?? "?").Named("FALLEN"), f.nemesisName.Named("SLAYER"),
                    (byHand ? "LW_Feud_ByHand" : "LW_Feud_ByBand").Translate(f.pawn.Named("PAWN")).Named("HOW")).Resolve(), comp.FactionById(f.factionId), Faction.OfPlayer,
                    LivingWorldComponent.WorldObjectById(f.homeId));
                Figures.AddDeed(f, e);
                Close(comp, f, null);
            }
        }

        /// <summary>The avenger leads a war band onto the colony's map: they hunt the slayer there.</summary>
        public static void Notify_AvengerArrived(Figure f, Lord lord)
        {
            if (!f.FeudOpen || f.nemesis.MapHeld != lord.Map) return;
            Hunters[f.pawn] = f.nemesis;
            Messages.Message("LW_FeudArrived".Translate(f.pawn.Named("PAWN"), f.nemesisName.Named("SLAYER")), new LookTargets(f.pawn, f.nemesis), MessageTypeDefOf.ThreatBig);
        }

        /// <summary>Hunters are not saved: after a load (and daily), every avenger on a map with their nemesis hunts again.</summary>
        public static void RebuildHunters(LivingWorldComponent comp)
        {
            Hunters.Clear();
            foreach (Figure f in comp.figures)
                if (f.FeudOpen && f.Alive && f.pawn.Spawned && f.nemesis.Spawned && f.nemesis.Map == f.pawn.Map) Hunters[f.pawn] = f.nemesis;
        }

        public static bool HasOpenFeud(Faction f)
        {
            foreach (Figure a in LivingWorldComponent.Instance.figures)
                if (a.FeudOpen && a.Alive && a.factionId == f.loadID) return true;
            return false;
        }

        /// <summary>"Ann Rook (for Oxe Rook)", comma-separated, for the faction's open feuds.</summary>
        public static string OpenFeudNames(Faction f)
        {
            var names = new List<string>();
            foreach (Figure a in LivingWorldComponent.Instance.figures)
                if (a.FeudOpen && a.Alive && a.factionId == f.loadID) names.Add("LW_Feud_NameFor".Translate(a.name, a.avenges ?? "?").Resolve());
            return names.ToCommaList();
        }

        /// <summary>Silver a blood price takes: in proportion to what the colony could field (what it can afford, as others see it).</summary>
        public static int BloodPrice() => Mathf.RoundToInt(Mathf.Clamp(PlayerDiplomacy.ColonyPoints() * 0.4f, 300f, 1500f) / 50f) * 50;

        /// <summary>Warlike rulers and old grudges refuse blood money more often.</summary>
        public static float BloodPriceChance(Faction f)
        {
            FactionState fs = LivingWorldComponent.Instance.StateFor(f);
            if (fs == null) return 0.5f;
            float grudge = Diplomacy.GrudgeAgainst(fs, Faction.OfPlayer) / 200f;
            return Mathf.Clamp(0.6f - 0.3f * (fs.TraitFactor(t => t.warPropensity) - 1f) + 0.15f * fs.warWeariness - grudge, 0.1f, 0.9f);
        }

        /// <summary>The faction took the blood price: its avengers renounce their oaths. Returns their names.</summary>
        public static string Settle(LivingWorldComponent comp, Faction f, int silver)
        {
            string names = OpenFeudNames(f);
            foreach (Figure a in comp.figures.ToArray())
            {
                if (!a.FeudOpen || !a.Alive || a.factionId != f.loadID) continue;
                ChronicleEventDef def = LWChronicleDefOf.LW_FeudSettled;
                WorldObject home = LivingWorldComponent.WorldObjectById(a.homeId);
                ChronicleEntry e = comp.chronicle.Record(def, def.text.Formatted(a.name.Named("NAME"), (a.avenges ?? "?").Named("FALLEN"), a.nemesisName.Named("SLAYER"),
                    (home?.Label ?? a.homeName ?? "?").Named("HOME"), Faction.OfPlayer.Name.Named("COLONY"), silver.Named("SILVER")).Resolve(), f, Faction.OfPlayer, home, silent: true);
                Figures.AddDeed(a, e);
                Close(comp, a, null);
            }
            return names;
        }

        /// <summary>Daily: feuds whose slayer is gone lapse; an open feud brings the avenger back now and then.</summary>
        public static void DailyCheck(LivingWorldComponent comp)
        {
            int now = Find.TickManager.TicksGame;
            RebuildHunters(comp);
            foreach (Figure f in comp.figures.ToArray())
            {
                if (!f.FeudOpen) continue;
                if (!f.Alive || f.nemesis.Dead || f.nemesis.Destroyed || f.nemesis.Faction != Faction.OfPlayer)
                {
                    Close(comp, f, null); // banished, gone, or the avenger died elsewhere: the oath is moot
                    continue;
                }
                if (f.feudReturns >= MaxReturns || now - f.swornTick < GenDate.TicksPerDay * ReturnDays * (f.feudReturns + 1)) continue;
                Faction fac = comp.FactionById(f.factionId);
                if (fac == null || fac.defeated || !fac.HostileTo(Faction.OfPlayer) || LivingWorldComponent.WorldObjectById(f.homeId) is not Settlement { Spawned: true } home || home.Faction != fac) continue;
                if (Revenge.Schedule(fac, f.homeId, f.deeds.Count > 0 ? f.deeds[0] : -1, f.id)) f.feudReturns++;
            }
        }

        /// <summary>Downed: remember the colonist who did it (a figure), or the avenger's people downing their nemesis.</summary>
        public static void Notify_Downed(Pawn p, DamageInfo? dinfo)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Pawn by = dinfo?.Instigator as Pawn;
            if (Figures.Of(p) is Figure f) f.downedBy = by != null && IsColonist(by) ? by : null; // this incapacitation's, not an old one's
            if (p.Faction != Faction.OfPlayer || by?.Faction == null) return;
            foreach (Figure a in comp.figures)
                if (a.nemesis == p && a.FeudOpen && by.Faction.loadID == a.factionId) a.nemesisDownedTick = Find.TickManager.TicksGame;
        }

        /// <summary>Back on their feet: an old downing is not what they will die of.</summary>
        public static void Notify_Undowned(Pawn p)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (Figures.Of(p) is Figure f) f.downedBy = null;
            if (p.Faction != Faction.OfPlayer) return;
            foreach (Figure a in comp.figures)
                if (a.nemesis == p) a.nemesisDownedTick = -1;
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned")]
    public static class Patch_HealthTracker_MakeDowned_Feuds
    {
        public static void Postfix(Pawn ___pawn, DamageInfo? dinfo)
        {
            if (LivingWorldComponent.Instance == null) return;
            try
            {
                Feuds.Notify_Downed(___pawn, dinfo);
            }
            catch (System.Exception e)
            {
                LWLog.Error("feud downed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeUndowned")]
    public static class Patch_HealthTracker_MakeUndowned_Feuds
    {
        public static void Postfix(Pawn ___pawn)
        {
            if (LivingWorldComponent.Instance == null) return;
            try
            {
                Feuds.Notify_Undowned(___pawn);
            }
            catch (System.Exception e)
            {
                LWLog.Error("feud undowned: " + e);
            }
        }
    }

    /// <summary>An avenger hunting their nemesis keeps them as the target while they can reach them.</summary>
    [HarmonyPatch(typeof(JobGiver_AIFightEnemy), "UpdateEnemyTarget")]
    public static class Patch_AIFightEnemy_UpdateEnemyTarget_Feuds
    {
        public static void Postfix(Pawn pawn)
        {
            if (Feuds.Hunters.Count == 0 || !Feuds.Hunters.TryGetValue(pawn, out Pawn nemesis)) return;
            // Only while assaulting: a breaking war band's defend-self/exit duties must still get them off the map.
            if (pawn.mindState.duty?.def != DutyDefOf.AssaultColony) return;
            if (nemesis.Dead || nemesis.Downed || !nemesis.Spawned || nemesis.Map != pawn.Map || !pawn.CanReach(nemesis, PathEndMode.Touch, Danger.Deadly)) return;
            pawn.mindState.enemyTarget = nemesis;
            // What vanilla's internal Notify_EngagedTarget does; refreshed every time, or ShouldLoseTarget drops them every 400 ticks.
            pawn.mindState.lastEngageTargetTick = Find.TickManager.TicksGame;
        }
    }

    /// <summary>A colonist someone has sworn to kill (an open blood feud) feels it.</summary>
    public class ThoughtWorker_MarkedForVengeance : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || !p.IsColonist) return false;
            foreach (Figure f in comp.figures)
                if (f.nemesis == p && f.FeudOpen && f.Alive) return true;
            return false;
        }
    }
}
