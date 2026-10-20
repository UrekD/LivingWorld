using Verse;

namespace LivingWorld
{
    public enum ContextKind : byte { Raid, Trade, Visit }

    /// <summary>
    /// Where a group on the player's map came from and what became of it: tied to the group's Lord (by loadID) when
    /// the lord is made, finished when the lord is removed (survivors travel home as a party, losses hit the home
    /// settlement, the chronicle records it).
    /// </summary>
    public class IncidentContext : IExposable
    {
        public int id;
        public ContextKind kind;
        public int originId = -1;
        public string originName;
        public int factionId = -1;
        public int lordId = -1;
        /// <summary>Party that brought the group (-1: arrived the vanilla way).</summary>
        public int partyId = -1;
        /// <summary>Strength the home settlement already paid when the party set out (0 when it did not travel).</summary>
        public float strengthPaid;
        public float points;
        public int pawnsExited;
        public float exitedPower;
        public float exitedSilver;
        public int createdTicks;
        public string mapLabel;
        public int goodwillAtStart;
        public int silverAtStart;
        /// <summary>What is going on at home (VisitorStories.HomeStory), how often they told it, whether old friends met.</summary>
        public byte story;
        public int newsTold;
        /// <summary>This group already warned the colony of a blood feud (People/VisitorStories.WarnOfFeud).</summary>
        public bool warnedOfFeud;
        public bool reunited;
        /// <summary>The group has made (or passed on) its plea to the colony (VisitorPleas).</summary>
        public bool pleaAsked;

        public IncidentContext CloneForLord(int newLordId)
        {
            var c = (IncidentContext)MemberwiseClone();
            c.id = LivingWorldComponent.Instance.NextId();
            c.lordId = newLordId;
            return c;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref originId, "originId", -1);
            Scribe_Values.Look(ref originName, "originName");
            Scribe_Values.Look(ref factionId, "factionId", -1);
            Scribe_Values.Look(ref lordId, "lordId", -1);
            Scribe_Values.Look(ref partyId, "partyId", -1);
            Scribe_Values.Look(ref strengthPaid, "strengthPaid");
            Scribe_Values.Look(ref points, "points");
            Scribe_Values.Look(ref pawnsExited, "pawnsExited");
            Scribe_Values.Look(ref exitedPower, "exitedPower");
            Scribe_Values.Look(ref exitedSilver, "exitedSilver");
            Scribe_Values.Look(ref createdTicks, "created");
            Scribe_Values.Look(ref mapLabel, "mapLabel");
            Scribe_Values.Look(ref goodwillAtStart, "goodwillAtStart");
            Scribe_Values.Look(ref silverAtStart, "silverAtStart");
            Scribe_Values.Look(ref story, "story");
            Scribe_Values.Look(ref newsTold, "newsTold");
            Scribe_Values.Look(ref warnedOfFeud, "warnedOfFeud");
            Scribe_Values.Look(ref reunited, "reunited");
            Scribe_Values.Look(ref pleaAsked, "pleaAsked");
        }
    }
}
