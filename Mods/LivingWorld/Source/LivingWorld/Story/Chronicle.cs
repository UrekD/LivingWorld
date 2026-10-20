using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// One recorded world event. Text is resolved when recorded (names snapshotted), so entries survive the factions,
    /// settlements and people they mention. Actor ids are kept for filtering and jumping.
    /// </summary>
    public class ChronicleEntry : IExposable
    {
        public int id;
        public int ticksAbs;
        /// <summary>Stored by name so entries survive the removal of their def.</summary>
        public string defName;
        public string text;
        /// <summary>The entry that caused this one (-1 = none): wars link their battles, famines their refugees.</summary>
        public int causeId = -1;
        /// <summary>Faction load ids (-1 = none).</summary>
        public int factionA = -1, factionB = -1;
        /// <summary>World object id of the settlement or party concerned (-1 = none).</summary>
        public int worldObjectId = -1;
        public PlanetTile tile = PlanetTile.Invalid;
        public bool involvesPlayer;
        /// <summary>Figure ids mentioned (empty for most entries).</summary>
        public List<int> figures;

        [Unsaved] private ChronicleEventDef defCache;

        public ChronicleEventDef def
        {
            get => defCache ??= DefDatabase<ChronicleEventDef>.GetNamedSilentFail(defName) ?? LWChronicleDefOf.LW_Unknown;
            set
            {
                defCache = value;
                defName = value.defName;
            }
        }

        public string DateString
        {
            get
            {
                Vector2 ll = tile.Valid ? Find.WorldGrid.LongLatOf(tile) : Vector2.zero;
                return GenDate.DateFullStringAt(ticksAbs, ll);
            }
        }

        public bool Concerns(Faction f) => f != null && (factionA == f.loadID || factionB == f.loadID);

        public GlobalTargetInfo Target
        {
            get
            {
                if (worldObjectId >= 0)
                {
                    WorldObject wo = LivingWorldComponent.WorldObjectById(worldObjectId);
                    if (wo != null && wo.Spawned) return wo;
                }
                return tile.Valid ? new GlobalTargetInfo(tile) : GlobalTargetInfo.Invalid;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref ticksAbs, "t");
            Scribe_Values.Look(ref defName, "def");
            Scribe_Values.Look(ref causeId, "cause", -1);
            Scribe_Values.Look(ref text, "text");
            Scribe_Values.Look(ref factionA, "fa", -1);
            Scribe_Values.Look(ref factionB, "fb", -1);
            Scribe_Values.Look(ref worldObjectId, "wo", -1);
            Scribe_Values.Look(ref tile, "tile", PlanetTile.Invalid);
            Scribe_Values.Look(ref involvesPlayer, "player");
            Scribe_Collections.Look(ref figures, "figures", LookMode.Value);
        }
    }

    /// <summary>
    /// The world's history: every event, newest last. Minor entries are capped; notable and major ones are kept.
    /// Decides how each event reaches the player (own letter, digest, or only the chronicle tab).
    /// </summary>
    public class ChronicleLog : IExposable
    {
        public List<ChronicleEntry> entries = new List<ChronicleEntry>();
        private List<int> pendingDigest = new List<int>();
        private int lastDigestTick = -1;
        private int minorCount;

        public int Count => entries.Count;

        public ChronicleEntry Record(ChronicleEventDef def, string text, Faction a = null, Faction b = null, WorldObject wo = null,
            PlanetTile? tile = null, bool involvesPlayer = false, bool silent = false, int causeId = -1)
        {
            var e = new ChronicleEntry
            {
                id = LivingWorldComponent.Instance.NextId(),
                ticksAbs = SimClock.TicksAbs,
                def = def,
                text = text,
                factionA = a?.loadID ?? -1,
                factionB = b?.loadID ?? -1,
                worldObjectId = wo?.ID ?? -1,
                tile = tile ?? wo?.Tile ?? PlanetTile.Invalid,
                involvesPlayer = involvesPlayer || (a != null && a.IsPlayer) || (b != null && b.IsPlayer),
                causeId = causeId,
            };
            entries.Add(e);
            if (def.importance == ChronicleImportance.Minor && ++minorCount > Tuning.T.maxMinorEntries) PruneMinor();
            if (!silent && !SimClock.InHistory) Announce(e);
            if (Testing.AutoTest.Active && (!SimClock.InHistory || def.importance >= ChronicleImportance.Notable)) Log.Message($"[LivingWorld:chronicle] {def.defName}: {text}");
            return e;
        }

        private void Announce(ChronicleEntry e)
        {
            LetterPolicy policy = LivingWorldMod.Settings.letterPolicy;
            if (policy == LetterPolicy.Off) return;
            bool ownLetter = policy == LetterPolicy.All
                ? e.def.importance >= ChronicleImportance.Notable
                : e.involvesPlayer && e.def.letterWhenPlayerInvolved;
            if (ownLetter && e.def.letterDef != null)
            {
                Find.LetterStack.ReceiveLetter(e.def.LabelCap, e.text, e.def.letterDef, new LookTargets(e.Target));
                return;
            }
            if (e.def.importance >= ChronicleImportance.Notable) pendingDigest.Add(e.id);
        }

        /// <summary>Called daily: sends the world news digest every digestDays.</summary>
        public void DigestTick()
        {
            int now = Find.TickManager.TicksGame;
            if (lastDigestTick < 0) lastDigestTick = now;
            if (now - lastDigestTick < LivingWorldMod.Settings.digestDays * GenDate.TicksPerDay) return;
            lastDigestTick = now;
            if (pendingDigest.Count == 0 || LivingWorldMod.Settings.letterPolicy != LetterPolicy.Digest) return;
            var sb = new StringBuilder("LW_DigestIntro".Translate().Resolve());
            int shown = 0;
            ChronicleEntry first = null;
            // Major news first, then the rest in order.
            for (int pass = 0; pass < 2; pass++)
                foreach (int id in pendingDigest)
                {
                    ChronicleEntry e = Get(id);
                    if (e == null || (pass == 0) != (e.def.importance == ChronicleImportance.Major)) continue;
                    if (shown++ >= Tuning.T.digestMaxLines) continue;
                    first ??= e;
                    sb.Append("\n\n• ").Append(e.text);
                }
            if (shown > Tuning.T.digestMaxLines) sb.Append("\n\n").Append("LW_DigestMore".Translate(shown - Tuning.T.digestMaxLines).Resolve());
            pendingDigest.Clear();
            if (first != null)
                Find.LetterStack.ReceiveLetter("LW_DigestLabel".Translate(), sb.ToString(), LetterDefOf.NeutralEvent, new LookTargets(first.Target));
        }

        public ChronicleEntry Get(int id)
        {
            // Entries are appended with increasing ids: binary search.
            int lo = 0, hi = entries.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int v = entries[mid].id;
                if (v == id) return entries[mid];
                if (v < id) lo = mid + 1;
                else hi = mid - 1;
            }
            return null;
        }

        private void PruneMinor()
        {
            // Drop the oldest quarter of minor entries in one pass.
            int toDrop = Tuning.T.maxMinorEntries / 4;
            entries.RemoveAll(e => e.def.importance == ChronicleImportance.Minor && toDrop-- > 0);
            minorCount = 0;
            foreach (ChronicleEntry e in entries)
                if (e.def.importance == ChronicleImportance.Minor) minorCount++;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            Scribe_Collections.Look(ref pendingDigest, "pendingDigest", LookMode.Value);
            Scribe_Values.Look(ref lastDigestTick, "lastDigestTick", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                entries ??= new List<ChronicleEntry>();
                entries.RemoveAll(e => e == null || e.defName == null);
                pendingDigest ??= new List<int>();
                minorCount = 0;
                foreach (ChronicleEntry e in entries)
                    if (e.def.importance == ChronicleImportance.Minor) minorCount++;
            }
        }
    }
}
