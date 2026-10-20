using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>A prisoner of the colony with a return address: where they came from and what became of them.</summary>
    public class PrisonerRecord : IExposable
    {
        public Pawn pawn;
        public int factionId = -1;
        public int homeId = -1;
        public string homeName;
        public string name;
        public int capturedTick;
        public bool offered;
        public bool diedCaptive;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn", saveDestroyedThings: true);
            Scribe_Values.Look(ref factionId, "faction", -1);
            Scribe_Values.Look(ref homeId, "home", -1);
            Scribe_Values.Look(ref homeName, "homeName");
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref capturedTick, "captured");
            Scribe_Values.Look(ref offered, "offered");
            Scribe_Values.Look(ref diedCaptive, "diedCaptive");
        }
    }

    /// <summary>What a town last heard about its people held by the colony (visitors bring it up).</summary>
    public enum CaptiveNews : byte { None, Released, Ransomed, Executed }

    /// <summary>
    /// Prisoners and the reckoning: a captured raider or visitor belongs to a real town. Their faction offers a ransom
    /// (silver from its treasury) after a few days; releasing them is remembered kindly at home, a prisoner who dies in the
    /// colony's hands (execution, neglect) brings a grudge and a bad name there. Visitors from that town talk about it.
    /// </summary>
    public static class Prisoners
    {
        public const int OfferAfterDays = 3;
        public static int StatOffers;
        /// <summary>Tests: offer at once instead of after a few days.</summary>
        public static bool TestOfferNow;

        public static PrisonerRecord RecordOf(Pawn p)
        {
            foreach (PrisonerRecord r in LivingWorldComponent.Instance.prisoners)
                if (r.pawn == p) return r;
            return null;
        }

        /// <summary>A pawn of a simulated faction became the colony's prisoner.</summary>
        public static void Notify_Captured(Pawn p)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || SimClock.InHistory || p?.Faction == null || !FactionState.IsSimulated(p.Faction) || !p.RaceProps.Humanlike || RecordOf(p) != null) return;
            Settlement home = HomeOf(comp, p);
            if (home == null) return;
            comp.prisoners.Add(new PrisonerRecord
            {
                pawn = p, factionId = p.Faction.loadID, homeId = home.ID, homeName = home.Label, name = p.Name?.ToStringShort ?? p.LabelShort,
                capturedTick = Find.TickManager.TicksGame,
            });
            Messages.Message("LW_PrisonerFrom".Translate(p.Named("PAWN"), home.Label.Named("HOME"), p.Faction.Name.Named("FACTION")), p, MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>The town the pawn came with (the group's context), else the nearest town of their faction.</summary>
        private static Settlement HomeOf(LivingWorldComponent comp, Pawn p)
        {
            if (Figures.Of(p) is Figure f && LivingWorldComponent.WorldObjectById(f.homeId) is Settlement fh && fh.Faction == p.Faction) return fh;
            Lord lord = p.GetLord();
            if (lord != null)
                foreach (IncidentContext ctx in comp.contexts)
                    if (ctx.lordId == lord.loadID && LivingWorldComponent.WorldObjectById(ctx.originId) is Settlement s) return s;
            return p.MapHeld != null ? Bridge.NearestSettlement(p.Faction, p.MapHeld.Tile) : null;
        }

        /// <summary>The pawn is about to die: remember whether it happened in captivity.</summary>
        public static void Notify_Dying(Pawn p)
        {
            if (p.IsPrisonerOfColony && RecordOf(p) is PrisonerRecord r) r.diedCaptive = true;
        }

        public static void DailyCheck(LivingWorldComponent comp)
        {
            int now = Find.TickManager.TicksGame;
            for (int i = comp.prisoners.Count - 1; i >= 0; i--)
            {
                PrisonerRecord r = comp.prisoners[i];
                Faction f = comp.FactionById(r.factionId);
                Pawn p = r.pawn;
                if (p == null || p.Discarded || f == null)
                {
                    comp.prisoners.RemoveAt(i);
                    continue;
                }
                if (p.Dead)
                {
                    if (r.diedCaptive) DiedInCaptivity(comp, r, f);
                    comp.prisoners.RemoveAt(i);
                    continue;
                }
                if (p.IsPrisonerOfColony)
                {
                    if (!r.offered && !f.defeated && p.SpawnedOrAnyParentSpawned && p.MapHeld?.IsPlayerHome == true
                        && (TestOfferNow || now - r.capturedTick >= GenDate.TicksPerDay * OfferAfterDays))
                        OfferRansom(comp, r, f);
                    continue;
                }
                // No longer a prisoner and alive: joined the colony (their roots take over), or set free.
                if (p.Faction == f) Released(comp, r, f);
                comp.prisoners.RemoveAt(i);
            }
        }

        /// <summary>Silver their people would pay: worth and rank of the captive, what the treasury can spare.</summary>
        public static int RansomFor(LivingWorldComponent comp, PrisonerRecord r, Faction f)
        {
            float worth = r.pawn.MarketValue * 0.6f;
            if (Figures.Of(r.pawn) != null) worth *= 2.5f; // a known merchant or warlord
            if (f.leader == r.pawn) worth *= 5f;
            float treasury = comp.StateFor(f)?.treasury ?? 0f;
            return Mathf.RoundToInt(Mathf.Clamp(Mathf.Min(worth, treasury * 0.3f), 0f, 5000f) / 10f) * 10;
        }

        private static void OfferRansom(LivingWorldComponent comp, PrisonerRecord r, Faction f)
        {
            r.offered = true;
            int amount = RansomFor(comp, r, f);
            if (amount < 50) return; // they cannot (or will not) pay for this one
            StatOffers++;
            var letter = (ChoiceLetter_Ransom)LetterMaker.MakeLetter(LWLetterDefOf.LW_RansomOffer);
            letter.Label = "LW_RansomLabel".Translate(r.name);
            letter.Text = "LW_RansomText".Translate(f.NameColored.Named("FACTION"), r.homeName.Named("HOME"), r.pawn.Named("PAWN"), amount.Named("AMOUNT"));
            letter.lookTargets = r.pawn;
            letter.relatedFaction = f;
            letter.prisoner = r.pawn;
            letter.amount = amount;
            letter.StartTimeout(GenDate.TicksPerDay * 3);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>Ransom accepted: silver to the colony, the prisoner goes home (to their town's garrison).</summary>
        public static void AcceptRansom(Pawn prisoner, int amount)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            PrisonerRecord r = RecordOf(prisoner);
            Map map = prisoner.MapHeld;
            if (r == null || map == null || !prisoner.IsPrisonerOfColony) return;
            Faction f = comp.FactionById(r.factionId);
            FactionState fs = comp.StateFor(f);
            if (fs != null) fs.treasury = Mathf.Max(0f, fs.treasury - amount);
            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = amount;
            GenPlace.TryPlaceThing(silver, DropCellFinder.TradeDropSpot(map), map, ThingPlaceMode.Near);
            prisoner.guest.SetGuestStatus(null);
            if (prisoner.Spawned) prisoner.DeSpawn();
            Find.WorldPawns.PassToWorld(prisoner);
            if (LivingWorldComponent.WorldObjectById(r.homeId)?.GetComponent<CompSettlementLife>() is CompSettlementLife home) home.garrison += 10f;
            f?.TryAffectGoodwillWith(Faction.OfPlayer, 5, canSendMessage: false);
            SetNews(r, CaptiveNews.Ransomed);
            Record(comp, LWChronicleDefOf.LW_PrisonerRansomed, r, f, amount);
            comp.prisoners.Remove(r);
        }

        private static void DiedInCaptivity(LivingWorldComponent comp, PrisonerRecord r, Faction f)
        {
            FactionState fs = comp.StateFor(f);
            Diplomacy.AddGrudge(fs, Faction.OfPlayer, 25f, "LW_Grudge_PrisonerDied".Translate(r.name).Resolve());
            if (LivingWorldComponent.WorldObjectById(r.homeId)?.GetComponent<CompSettlementLife>() is CompSettlementLife home)
                home.fame = Mathf.Clamp(home.fame - 0.3f, -1f, 1f);
            SetNews(r, CaptiveNews.Executed);
            Record(comp, LWChronicleDefOf.LW_PrisonerDied, r, f, 0);
        }

        private static void Released(LivingWorldComponent comp, PrisonerRecord r, Faction f)
        {
            // They may come back one day (Figures: a freed captive of their town).
            if (LivingWorldComponent.WorldObjectById(r.homeId) is Settlement town) Figures.RegisterFreed(r.pawn, town, r.pawn.MapHeld);
            if (LivingWorldComponent.WorldObjectById(r.homeId)?.GetComponent<CompSettlementLife>() is CompSettlementLife home)
                home.fame = Mathf.Clamp(home.fame + 0.2f, -1f, 1f);
            Diplomacy.AddGrudge(comp.StateFor(f), Faction.OfPlayer, -10f, "LW_Grudge_PrisonerReleased".Translate(r.name).Resolve());
            SetNews(r, CaptiveNews.Released);
            Record(comp, LWChronicleDefOf.LW_PrisonerReleased, r, f, 0);
        }

        private static void SetNews(PrisonerRecord r, CaptiveNews news)
        {
            if (LivingWorldComponent.WorldObjectById(r.homeId)?.GetComponent<CompSettlementLife>() is not CompSettlementLife home) return;
            home.captiveNews = news;
            home.captiveNewsTick = Find.TickManager.TicksGame;
        }

        private static void Record(LivingWorldComponent comp, ChronicleEventDef def, PrisonerRecord r, Faction f, int silver) =>
            comp.chronicle.Record(def, def.text.Formatted(r.name.Named("NAME"), r.homeName.Named("HOME"), (f?.Name ?? "?").Named("FACTION"),
                Faction.OfPlayer.Name.Named("COLONY"), silver.Named("SILVER")).Resolve(), Faction.OfPlayer, f, LivingWorldComponent.WorldObjectById(r.homeId));
    }

    /// <summary>Pay the ransom (the prisoner goes home) or keep them.</summary>
    public class ChoiceLetter_Ransom : ChoiceLetter
    {
        public Pawn prisoner;
        public int amount;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                if (ArchivedOnly)
                {
                    yield return Option_Close;
                    yield break;
                }
                var accept = new DiaOption("LW_RansomAccept".Translate(amount))
                {
                    action = () =>
                    {
                        Prisoners.AcceptRansom(prisoner, amount);
                        Find.LetterStack.RemoveLetter(this);
                    },
                    resolveTree = true,
                };
                if (prisoner == null || prisoner.Dead || !prisoner.IsPrisonerOfColony) accept.Disable("LW_RansomGone".Translate());
                yield return accept;
                yield return Option_Reject;
                yield return Option_Postpone;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref prisoner, "prisoner");
            Scribe_Values.Look(ref amount, "amount");
        }
    }

    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.SetGuestStatus))]
    public static class Patch_GuestTracker_SetGuestStatus_Prisoners
    {
        public static void Postfix(Pawn_GuestTracker __instance, Faction newHost, GuestStatus guestStatus, Pawn ___pawn)
        {
            if (newHost != null && newHost.IsPlayer && guestStatus == GuestStatus.Prisoner && LivingWorldComponent.Instance != null) Prisoners.Notify_Captured(___pawn);
        }
    }
}
