using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>
    /// Mercenary contracts: visitors from a town at war ask to hire one of the colony's fighters. If the colony agrees,
    /// the colonist travels with a party of the colony to that town (held in it like a caravan member), fights for a few
    /// days, and walks back in from the map's edge: usually with pay and plunder, sometimes wounded, rarely not at all.
    /// The town remembers the help.
    /// </summary>
    public static class Mercenaries
    {
        public static int StatContracts, StatFights;
        /// <summary>Tests: force the outcome (0 = paid, 1 = wounded, 2 = killed); -1 = roll.</summary>
        public static int TestOutcome = -1;

        /// <summary>Whether the colonist can set out now (healthy, at home, not drafted or busy with a gathering).</summary>
        public static bool CanGo(Pawn p) => p != null && p.Spawned && !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState && p.GetLord() == null
            && p.Faction == Faction.OfPlayer && !p.IsQuestLodger() && !p.IsBorrowedByAnyFaction() && p.health.summaryHealth.SummaryHealthPercent > 0.9f && !p.WorkTagIsDisabled(WorkTags.Violent);

        /// <summary>The colony's best fighter who can be spared (see CanGo; never the last colonist).</summary>
        public static Pawn BestFighter(Map map)
        {
            // Someone grown must stay behind (a baby does not count).
            List<Pawn> colonists = map.mapPawns.FreeAdultColonistsSpawned.ToList();
            if (colonists.Count < 2) return null;
            return colonists.Where(CanGo)
                .OrderByDescending(p => Mathf.Max(p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0, p.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0)).FirstOrDefault();
        }

        public static int PayFor(Map map) => Mathf.RoundToInt(Mathf.Clamp(StorytellerUtility.DefaultThreatPointsNow(map) * 0.8f, 250f, 1200f) / 10f) * 10;

        /// <summary>The colony agreed: the fighter sets out with a party of the colony to the town.</summary>
        public static WorldParty Hire(Pawn fighter, Faction employer, Settlement town, int pay)
        {
            Map map = fighter.MapHeld;
            // Rechecked at acceptance: someone grown must still stay behind (the letter may have waited a day).
            bool spare = map != null && map.mapPawns.FreeAdultColonistsSpawned.Any(c => c != fighter);
            if (!CanGo(fighter) || !spare || map?.Parent is not Settlement colony || town == null || !town.Spawned || employer == null)
            {
                Messages.Message("LW_Merc_CannotGo".Translate(fighter.Named("PAWN")), fighter, MessageTypeDefOf.RejectInput, historical: false);
                return null;
            }
            WorldParty p = WorldParty.Launch(LWPartyDefOf.LW_Envoy, Faction.OfPlayer, colony, town);
            p.targetFactionId = employer.loadID;
            p.mission = (byte)EnvoyMission.PlayerMercenary;
            p.pay = pay;
            p.strength = 30f;
            if (!p.Board(fighter))
            {
                p.Disband();
                return null;
            }
            StatContracts++;
            Messages.Message("LW_Merc_SetOut".Translate(fighter.Named("PAWN"), town.Label.Named("TOWN"), p.TicksToArrive().ToStringTicksToPeriod().Named("ETA")), new LookTargets(p), MessageTypeDefOf.NeutralEvent);
            return p;
        }

        /// <summary>Arrived: the fighting, and what comes of it.</summary>
        public static void Fight(LivingWorldComponent comp, WorldParty p, Faction employer)
        {
            Pawn fighter = p.Passengers.FirstOrDefault();
            if (fighter == null) return;
            // Days on the road: the employer may have turned on the colony or lost the town. No fight; they come home unpaid.
            if (employer == null || employer.HostileTo(Faction.OfPlayer) || (p.Destination as Settlement)?.Faction != employer)
            {
                Messages.Message("LW_Merc_TurnedBack".Translate(fighter.Named("PAWN"), (p.Destination?.Label ?? "?").Named("TOWN")), new LookTargets(p), MessageTypeDefOf.NeutralEvent);
                return;
            }
            StatFights++;
            int roll = TestOutcome >= 0 ? TestOutcome : Rand.Value < 0.7f ? 0 : Rand.Value < 0.83f ? 1 : 2;
            CompSettlementLife town = (p.Destination as Settlement)?.GetComponent<CompSettlementLife>();
            if (town != null)
            {
                town.garrison += 40f; // they held the walls with the colony's help
                town.fame = Mathf.Clamp(town.fame + 0.2f, -1f, 1f);
            }
            employer?.TryAffectGoodwillWith(Faction.OfPlayer, 8, canSendMessage: false, reason: LWHistoryDefOf.LW_FoughtOurEnemies);
            string key;
            if (roll == 2)
            {
                fighter.Kill(null);
                key = "LW_Merc_Killed";
                p.silver += p.pay / 2; // they send half the pay home with the body
            }
            else if (roll == 1)
            {
                Wound(fighter);
                key = "LW_Merc_Wounded";
                p.silver += p.pay;
            }
            else
            {
                key = "LW_Merc_Paid";
                p.silver += p.pay + Mathf.RoundToInt(Rand.Range(100f, 400f) / 10f) * 10;
            }
            Legends.Add(fighter, (roll == 2 ? "LW_Deed_MercFell" : "LW_Deed_Merc").Translate(p.Destination?.Label ?? "?").Resolve());
            Find.LetterStack.ReceiveLetter(("LW_Merc_Label").Translate(fighter.LabelShort), key.Translate(fighter.Named("PAWN"), (p.Destination?.Label ?? "?").Named("TOWN"),
                Mathf.RoundToInt(p.silver).Named("SILVER")), roll == 0 ? LetterDefOf.PositiveEvent : LetterDefOf.NegativeEvent, new LookTargets(p), employer);
        }

        /// <summary>A few wounds of battle on limbs and torso (never the head), dressed in the field: the held pawn is not
        /// ticked on the road, so untended wounds would only start bleeding when they walk back in.</summary>
        internal static void Wound(Pawn p)
        {
            HediffDef shot = DefDatabase<HediffDef>.GetNamedSilentFail("Gunshot") ?? HediffDefOf.Cut;
            int wounds = Rand.RangeInclusive(2, 3);
            for (int i = 0; i < wounds; i++)
            {
                // Parts as they are now, and never a wound that takes the part off (no fresh stump to bleed on the road).
                List<BodyPartRecord> parts = p.health.hediffSet.GetNotMissingParts(BodyPartHeight.Undefined, BodyPartDepth.Outside)
                    .Where(b => b.coverageAbs > 0f && !b.IsInGroup(BodyPartGroupDefOf.FullHead) && !b.IsInGroup(BodyPartGroupDefOf.UpperHead)
                                && p.health.hediffSet.GetPartHealth(b) > 4f).ToList();
                if (parts.Count == 0) break;
                BodyPartRecord part = parts.RandomElement();
                Hediff h = HediffMaker.MakeHediff(Rand.Bool ? shot : HediffDefOf.Cut, p, part);
                h.Severity = Mathf.Min(Rand.Range(3f, 7f), p.health.hediffSet.GetPartHealth(part) - 1f);
                p.health.AddHediff(h);
            }
            // Dressed in the field: every untended wound they carry home.
            foreach (Hediff h in p.health.hediffSet.hediffs.ToList())
                if (h.TendableNow()) h.Tended(Rand.Range(0.3f, 0.6f), 1f);
        }
    }
}
