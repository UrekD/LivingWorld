using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>What kind of party travels the world (war band, merchants, …): presentation and behaviour worker.</summary>
    public class PartyKindDef : Def
    {
        public Type workerClass = typeof(PartyWorker);
        /// <summary>Icon texture (white glyph, tinted with the faction colour).</summary>
        public string iconPath;
        /// <summary>Ticks to cross a plain tile (vanilla caravans: 3300).</summary>
        public int ticksPerTile = 3300;
        public float expandingIconPriority = 60f;
        /// <summary>Shown as a threat (red label) when heading for the player.</summary>
        public bool hostile;
        [MustTranslate] public string labelOutbound;
        [MustTranslate] public string labelReturning;

        [Unsaved] private PartyWorker workerInt;
        [Unsaved] private Texture2D iconTex;

        public PartyWorker Worker
        {
            get
            {
                if (workerInt == null)
                {
                    workerInt = (PartyWorker)Activator.CreateInstance(workerClass);
                    workerInt.def = this;
                }
                return workerInt;
            }
        }

        /// <summary>Main thread only (texture loading).</summary>
        public Texture2D Icon => iconTex ??= ContentFinder<Texture2D>.Get(iconPath, reportFailure: true) ?? BaseContent.BadTex;
    }

    /// <summary>Behaviour of a party kind; stateless (state lives on the <see cref="WorldParty"/>).</summary>
    public class PartyWorker
    {
        public PartyKindDef def;

        /// <summary>Reached its destination on the outbound leg. Return true when the party is done (destroyed).</summary>
        public virtual bool ArrivedAtDestination(WorldParty p) => true;

        /// <summary>Back at its home settlement.</summary>
        public virtual void ArrivedHome(WorldParty p)
        {
            if (p.Origin != null) ArrivedHomeInstant(p.Origin, p.strength, p.silver);
        }

        /// <summary>Effects of coming home (also used when a party skips the journey because the map is full).</summary>
        public virtual void ArrivedHomeInstant(RimWorld.Planet.Settlement home, float strength, float silver) { }

        /// <summary>Destination vanished or became unreachable on the way.</summary>
        public virtual void DestinationLost(WorldParty p) => p.TurnBack();

        public virtual string InspectExtra(WorldParty p) => null;
    }

    [DefOf]
    public static class LWPartyDefOf
    {
        public static PartyKindDef LW_WarBand;
        public static PartyKindDef LW_Merchants;
        public static PartyKindDef LW_Visitors;
        public static PartyKindDef LW_Envoy;
        public static PartyKindDef LW_Settlers;
        public static WorldObjectDef LW_Party;

        static LWPartyDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(LWPartyDefOf));
    }
}
