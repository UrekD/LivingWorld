using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// The territory tint overlay: each claimed tile tinted in its faction's colour, with a stronger band along every edge where
    /// the neighbouring tile belongs to someone else (or no one). What a source's land looks like (fill, edge, band width, and
    /// the group that decides where bands appear) comes from the active <see cref="ITerritoryPaint"/>: the faction colour picture of
    /// <see cref="BordersPaint"/> for the borders mode, one colour per band of a town's value in the heat modes. Vertex colours on the game's shared transparent material.
    /// The surface is cut into fixed regions of <see cref="TilesPerRegion"/> tile ids, each with its own submeshes (more than
    /// one only if a region would pass the 40000 vertex limit); the meshes are created once and reused. The layer keeps what
    /// each tile looks like (group, fill, edge and band width: the owning object does not matter for drawing) for the picture it was last
    /// told about. A new picture is diffed against that in <see cref="Notify_Published"/>: the regions of the tiles that look
    /// different, and of their neighbours (the edge bands depend on them), are marked, and only those are rebuilt when the layer
    /// is next drawn. The layer is marked dirty only when something visible changed, not on every revision. Everything is
    /// rebuilt when the layer is first drawn, after <see cref="Invalidate"/> (the mode changed, the picture was dropped) and
    /// when more than half of the regions are marked. Allocates nothing from Unity until it is first drawn. Like the
    /// pollution layer, the vertices sit a little above the terrain (no z-fighting), the edge bands a little above the fill.
    /// Factionless sources are not drawn.
    /// </summary>
    public class WorldDrawLayer_LWBorders : WorldDrawLayer
    {
        private const float CornerEpsilonSq = 1e-6f;
        internal const float FillLift = 0.025f, EdgeLift = 0.03f;
        private const int MaxVerticesPerSubMesh = 40000;

        /// <summary>
        /// Tile ids per region. A tile takes at most its corners plus four vertices for each side (30 for a hexagon), so a full
        /// region can need two submeshes (61,440 vertices at worst); SubMeshWithRoom reserves a whole tile before emitting it and
        /// keeps every submesh under MaxVerticesPerSubMesh. Fewer ids mean cheaper rebuilds, more mean fewer meshes to draw.
        /// </summary>
        public const int TilesPerRegion = 2048;

        public override bool Visible =>
            base.Visible && planetLayer.IsRootSurface && Territory.Ready && WorldOverlay.TintMode;

        public override bool VisibleWhenLayerNotSelected => false;

        public override bool VisibleInBackground => false;

        /// <summary>Wall-clock cost of the last regeneration (full or not) and how many there were (autotest metrics).</summary>
        public double LastRegenMs;
        public int Regens;
        /// <summary>The last full regeneration and the last incremental one, in ms, and how many of each there were.</summary>
        public double LastFullRegenMs, LastIncrementalRegenMs;
        public int FullRegens, IncrementalRegens;
        /// <summary>Whether the last regeneration rebuilt everything, how many regions it rebuilt, and the cost of the last diff in <see cref="Notify_Published"/>.</summary>
        public bool LastRegenFull;
        public int LastRegionsRebuilt;
        public double LastSyncMs;
        /// <summary>Vertices and triangles in all regions as of the last regeneration.</summary>
        public int LastVerts, LastTris;
        /// <summary>Edge bands drawn along a front (a border between two factions at war), as of the last regeneration: the sum over the regions. Each tile edge on a front has a band on each side.</summary>
        public int LastWarEdges;
        public int SubMeshCount => subMeshes.Count;

        /// <summary>For the autotest, counted from the submeshes themselves rather than the running totals: geometry that would
        /// not be drawn (built but never finalized) or totals that disagree with what is there; null when all is well.</summary>
        public string GeometryProblem()
        {
            int verts = 0, unfinalized = 0;
            foreach (LayerSubMesh sm in subMeshes)
            {
                verts += sm.verts.Count;
                if (sm.verts.Count > 0 && !sm.finalized) unfinalized++;
            }
            if (unfinalized > 0) return $"{unfinalized} submeshes hold geometry that was never finalized (not drawn)";
            return verts != LastVerts ? $"the submeshes hold {verts} vertices, the running total says {LastVerts}" : null;
        }
        public int RegionCount => regionCount;

        /// <summary>For the metrics: how many regions are marked for rebuilding (more than half of them means a full redraw), and whether the next regeneration rebuilds everything.</summary>
        internal int DirtyRegions => dirtyRegions;

        internal bool NeedsFull => needFull || dirtyRegions * 2 > regionCount;

        /// <summary>Submeshes that are drawn (hold geometry), the draw calls the overlay costs.</summary>
        public int DrawnSubMeshCount
        {
            get
            {
                int n = 0;
                foreach (LayerSubMesh sm in subMeshes)
                    if (sm.finalized) n++;
                return n;
            }
        }

        // Created with the first regeneration.
        private int regionCount;
        private int[] known; // per tile: visual id (see VisualId) as of the last picture the layer was told about, 0 for not drawn
        private readonly List<int> knownTiles = new List<int>(); // the tiles whose known id is not 0, so the next diff can find those that stop being drawn
        private List<LayerSubMesh>[] regionMeshes;
        private int[] regionVerts, regionTris, regionWarEdges;
        private bool[] regionDirty;
        private int dirtyRegions;
        private bool needFull = true;
        private int syncedRevision = -1;
        // The distinct (group, fill, edge, band width) looks of the picture, numbered from 1 (0: not drawn); a full rebuild starts the table over.
        private readonly Dictionary<(int group, int fill, int edge, int width), int> visualIds = new Dictionary<(int, int, int, int), int>();
        private readonly List<int> groupById = new List<int>();
        private readonly List<Color32> fillById = new List<Color32>(), edgeById = new List<Color32>();
        private readonly List<float> edgeWidthById = new List<float>();
        private int[] sourceVisual = new int[0];
        // The region being rebuilt.
        private int buildRegion, buildUsed;
        private LayerSubMesh buildMesh;

        /// <summary>Everything is redrawn when the layer is next drawn.</summary>
        public void Invalidate()
        {
            needFull = true;
            SetDirty();
        }

        /// <summary>
        /// A new picture was published: marks the regions that look different from the previous one (and with them the layer, at the
        /// caller's hands) and returns whether anything visible changed. Before the first full draw there is nothing to compare with.
        /// </summary>
        internal bool Notify_Published(TerritorySnapshot snap)
        {
            if (needFull || known == null) return true;
            int[] owner = snap.LiveOwners;
            if (snap.surface != planetLayer || owner == null)
            {
                needFull = true;
                return true;
            }
            var sw = Stopwatch.StartNew();
            PrepareVisuals(snap.sources);
            bool changed = false;
            for (int i = 0; i < knownTiles.Count; i++)
            {
                int t = knownTiles[i];
                int o = owner[t];
                if (o != 0 && sourceVisual[o - 1] != 0) continue; // still drawn: compared below
                known[t] = 0;
                MarkAround(t);
                changed = true;
            }
            knownTiles.Clear();
            List<int> claimed = snap.ClaimedList;
            for (int c = 0; c < claimed.Count; c++)
            {
                int t = claimed[c];
                int id = sourceVisual[owner[t] - 1];
                if (id == 0) continue;
                knownTiles.Add(t);
                if (known[t] == id) continue;
                known[t] = id;
                MarkAround(t);
                changed = true;
            }
            syncedRevision = snap.revision;
            if (dirtyRegions * 2 > regionCount) needFull = true;
            LastSyncMs = sw.Elapsed.TotalMilliseconds;
            return changed || needFull;
        }

        public override IEnumerable Regenerate()
        {
            var sw = Stopwatch.StartNew();
            dirty = false; // not base.Regenerate(): it would clear every region's submeshes, and only the marked ones are rebuilt here
            TerritorySnapshot snap = Territory.Current;
            if (snap != null && snap.surface != planetLayer) snap = null;
            EnsureState();
            bool full = needFull || snap == null || snap.revision != syncedRevision || dirtyRegions * 2 > regionCount;
            int rebuilt = 0;
            if (full)
            {
                ResetKnown(snap);
                for (int r = 0; r < regionCount; r++) RebuildRegion(r);
                System.Array.Clear(regionDirty, 0, regionDirty.Length);
                rebuilt = regionCount;
            }
            else
            {
                for (int r = 0; r < regionCount; r++)
                {
                    if (!regionDirty[r]) continue;
                    regionDirty[r] = false;
                    RebuildRegion(r);
                    rebuilt++;
                }
            }
            dirtyRegions = 0;
            LastRegenFull = full;
            LastRegionsRebuilt = rebuilt;
            LastRegenMs = sw.Elapsed.TotalMilliseconds;
            if (full)
            {
                LastFullRegenMs = LastRegenMs;
                FullRegens++;
            }
            else
            {
                LastIncrementalRegenMs = LastRegenMs;
                IncrementalRegens++;
            }
            Regens++;
            yield break;
        }

        private void EnsureState()
        {
            if (known != null) return;
            int tiles = planetLayer.TilesCount;
            regionCount = (tiles + TilesPerRegion - 1) / TilesPerRegion;
            known = new int[tiles];
            regionMeshes = new List<LayerSubMesh>[regionCount];
            regionVerts = new int[regionCount];
            regionTris = new int[regionCount];
            regionWarEdges = new int[regionCount];
            regionDirty = new bool[regionCount];
            ResetVisuals();
        }

        private void ResetVisuals()
        {
            visualIds.Clear();
            groupById.Clear();
            fillById.Clear();
            edgeById.Clear();
            edgeWidthById.Clear();
            groupById.Add(TerritorySource.NoFaction);
            fillById.Add(default);
            edgeById.Add(default);
            edgeWidthById.Add(0f);
        }

        /// <summary>Forgets what was known and learns the picture from scratch (null: nothing is drawn).</summary>
        private void ResetKnown(TerritorySnapshot snap)
        {
            System.Array.Clear(known, 0, known.Length);
            knownTiles.Clear();
            ResetVisuals();
            needFull = false;
            syncedRevision = -1;
            int[] owner = snap?.LiveOwners;
            if (owner == null) return;
            PrepareVisuals(snap.sources);
            List<int> claimed = snap.ClaimedList;
            for (int c = 0; c < claimed.Count; c++)
            {
                int t = claimed[c];
                int id = sourceVisual[owner[t] - 1];
                if (id == 0) continue;
                known[t] = id;
                knownTiles.Add(t);
            }
            syncedRevision = snap.revision;
        }

        /// <summary>The visual id of each source: 0 for one that is not drawn (no faction, or the paint leaves it out).</summary>
        private void PrepareVisuals(TerritorySource[] sources)
        {
            if (sourceVisual.Length < sources.Length) sourceVisual = new int[sources.Length];
            ITerritoryPaint paint = WorldOverlay.Paint;
            for (int i = 0; i < sources.Length; i++)
            {
                sourceVisual[i] = 0;
                if (sources[i].faction == null) continue;
                if (paint.Paint(i, in sources[i], out int group, out Color32 fill, out Color32 edge, out float edgeWidth)) sourceVisual[i] = VisualId(group, fill, edge, edgeWidth);
            }
        }

        /// <summary>
        /// What a tile looks like: its group, its fill and edge colours as drawn (8 bits a channel including alpha, so heat modes that only
        /// change the alpha are told apart) and its band width in hundredths. Edge bands also depend on the neighbours' groups, which the
        /// marking of the neighbours' regions covers.
        /// </summary>
        private int VisualId(int group, Color32 fill, Color32 edge, float edgeWidth)
        {
            var key = (group, Pack(fill), Pack(edge), Mathf.RoundToInt(edgeWidth * 100f));
            if (visualIds.TryGetValue(key, out int id)) return id;
            id = groupById.Count;
            visualIds[key] = id;
            groupById.Add(group);
            fillById.Add(fill);
            edgeById.Add(edge);
            edgeWidthById.Add(edgeWidth);
            return id;
        }

        private static int Pack(Color32 c) => c.r | c.g << 8 | c.b << 16 | c.a << 24;

        /// <summary>For the autotest: how the tile looks as of the last picture the layer was told about (false: not drawn, or no picture yet).</summary>
        internal bool TryGetVisual(int tile, out int group, out Color32 fill, out Color32 edge, out float edgeWidth)
        {
            int id = known != null && (uint)tile < (uint)known.Length ? known[tile] : 0;
            if (id == 0)
            {
                group = TerritorySource.NoFaction;
                fill = edge = default;
                edgeWidth = 0f;
                return false;
            }
            group = groupById[id];
            fill = fillById[id];
            edge = edgeById[id];
            edgeWidth = edgeWidthById[id];
            return true;
        }

        /// <summary>
        /// For the autotest: while set, each rebuild records where in its submesh every tile's geometry starts and how many vertices it has
        /// (the fan, then four per edge band), so the colours and band width actually written can be read back. Set it before the regeneration,
        /// and call <see cref="DropSpans"/> when done with them.
        /// </summary>
        internal bool RecordSpans;
        private LayerSubMesh[] spanMesh;
        private int[] spanFirst, spanCount;

        /// <summary>Turns recording off and frees the span tables (planet-sized) and their references to the submeshes.</summary>
        internal void DropSpans()
        {
            RecordSpans = false;
            spanMesh = null;
            spanFirst = null;
            spanCount = null;
        }

        /// <summary>For the autotest, with <see cref="RecordSpans"/>: the tile's geometry as built (false: it was not drawn in the last recording).</summary>
        internal bool TryGetSpan(int tile, out LayerSubMesh mesh, out int first, out int count)
        {
            mesh = spanMesh != null && (uint)tile < (uint)spanMesh.Length ? spanMesh[tile] : null;
            first = mesh == null ? 0 : spanFirst[tile];
            count = mesh == null ? 0 : spanCount[tile];
            return mesh != null;
        }

        private void MarkAround(int tile)
        {
            var nbOffsets = planetLayer.UnsafeTileIDToNeighbors_offsets;
            var nbValues = planetLayer.UnsafeTileIDToNeighbors_values;
            MarkRegion(tile / TilesPerRegion);
            int end = tile + 1 < nbOffsets.Length ? nbOffsets[tile + 1] : nbValues.Length;
            for (int k = nbOffsets[tile]; k < end; k++) MarkRegion(nbValues[k].tileId / TilesPerRegion);
        }

        private void MarkRegion(int region)
        {
            if (regionDirty[region]) return;
            regionDirty[region] = true;
            dirtyRegions++;
        }

        /// <summary>The region's geometry from the known tile looks: its submeshes are cleared and refilled, extra ones left empty and undrawn.</summary>
        private void RebuildRegion(int region)
        {
            List<LayerSubMesh> meshes = regionMeshes[region];
            if (meshes != null)
                foreach (LayerSubMesh sm in meshes) sm.Clear(MeshParts.All);
            buildRegion = region;
            buildUsed = 0;
            buildMesh = null;
            ITerritoryPaint paint = WorldOverlay.Paint;
            bool warPaint = paint == WarPaint.Instance;
            int warEdges = 0;
            var verts = planetLayer.UnsafeVerts;
            var vertOffsets = planetLayer.UnsafeTileIDToVerts_offsets;
            var nbOffsets = planetLayer.UnsafeTileIDToNeighbors_offsets;
            var nbValues = planetLayer.UnsafeTileIDToNeighbors_values;
            int from = region * TilesPerRegion, to = Mathf.Min(from + TilesPerRegion, known.Length);
            if (RecordSpans && spanMesh != null) System.Array.Clear(spanMesh, from, to - from);
            for (int t = from; t < to; t++)
            {
                int id = known[t];
                if (id == 0) continue;
                int start = vertOffsets[t];
                int end = t + 1 < vertOffsets.Length ? vertOffsets[t + 1] : verts.Length;
                int n = end - start;
                if (n < 3) continue;
                int nbStart = nbOffsets[t];
                int nbEnd = t + 1 < nbOffsets.Length ? nbOffsets[t + 1] : nbValues.Length;
                LayerSubMesh sm = SubMeshWithRoom(n + 4 * (nbEnd - nbStart)); // the corners and at most one band per side
                Color32 fill = fillById[id], edge = edgeById[id];
                float edgeWidth = edgeWidthById[id];
                int group = groupById[id];
                int first = sm.verts.Count;
                if (RecordSpans)
                {
                    spanMesh ??= new LayerSubMesh[known.Length];
                    spanFirst ??= new int[known.Length];
                    spanCount ??= new int[known.Length];
                    spanMesh[t] = sm;
                    spanFirst[t] = first;
                }
                Vector3 centre = Vector3.zero;
                for (int j = 0; j < n; j++)
                {
                    Vector3 v = verts[start + j];
                    centre += v;
                    sm.verts.Add(Lift(v, FillLift));
                    sm.colors.Add(fill);
                }
                centre /= n;
                for (int k = 0; k < n - 2; k++)
                {
                    sm.tris.Add(first + k + 2);
                    sm.tris.Add(first + k + 1);
                    sm.tris.Add(first);
                }
                // The edge band: along each side whose neighbour is not in this group (this faction's land, in the borders mode).
                for (int k = nbStart; k < nbEnd; k++)
                {
                    int nb = nbValues[k].tileId;
                    int nbGroup = groupById[known[nb]];
                    if (nbGroup == group) continue;
                    float width;
                    bool front = false;
                    Color32 band = warPaint ? WarPaint.Instance.EdgeBetween(group, edge, edgeWidth, nbGroup, out width, out front) : paint.EdgeBetween(group, edge, edgeWidth, nbGroup, out width);
                    int nbVertStart = vertOffsets[nb];
                    int nbVertEnd = nb + 1 < vertOffsets.Length ? vertOffsets[nb + 1] : verts.Length;
                    for (int j = 0; j < n; j++)
                    {
                        Vector3 a = verts[start + j], b = verts[start + (j + 1) % n];
                        if (!HasCorner(verts, nbVertStart, nbVertEnd, a) || !HasCorner(verts, nbVertStart, nbVertEnd, b)) continue;
                        int q = sm.verts.Count;
                        sm.verts.Add(Lift(a, EdgeLift));
                        sm.verts.Add(Lift(b, EdgeLift));
                        sm.verts.Add(Lift(b + (centre - b) * width, EdgeLift));
                        sm.verts.Add(Lift(a + (centre - a) * width, EdgeLift));
                        for (int c = 0; c < 4; c++) sm.colors.Add(band);
                        sm.tris.Add(q + 2);
                        sm.tris.Add(q + 1);
                        sm.tris.Add(q);
                        sm.tris.Add(q + 3);
                        sm.tris.Add(q + 2);
                        sm.tris.Add(q);
                        if (front) warEdges++;
                        break;
                    }
                }
                if (RecordSpans) spanCount[t] = sm.verts.Count - first;
            }
            int vertCount = 0, triCount = 0;
            meshes = regionMeshes[region]; // SubMeshWithRoom creates the list on a region's first use
            if (meshes != null)
            {
                for (int i = 0; i < meshes.Count; i++)
                {
                    LayerSubMesh sm = meshes[i];
                    if (i < buildUsed)
                    {
                        sm.FinalizeMesh(MeshParts.All);
                        vertCount += sm.verts.Count;
                        triCount += sm.tris.Count / 3;
                    }
                    else sm.mesh.Clear(); // a smaller picture keeps no old geometry
                }
            }
            LastVerts += vertCount - regionVerts[region];
            LastTris += triCount - regionTris[region];
            LastWarEdges += warEdges - regionWarEdges[region];
            regionVerts[region] = vertCount;
            regionTris[region] = triCount;
            regionWarEdges[region] = warEdges;
            buildMesh = null;
        }

        /// <summary>The region's current submesh if it has room for that many more vertices, else its next one (created only when the region has used up the ones it has).</summary>
        private LayerSubMesh SubMeshWithRoom(int vertices)
        {
            if (buildMesh != null && buildMesh.verts.Count + vertices <= MaxVerticesPerSubMesh) return buildMesh;
            List<LayerSubMesh> meshes = regionMeshes[buildRegion] ??= new List<LayerSubMesh>(1);
            if (buildUsed == meshes.Count)
            {
                var mesh = new Mesh();
                if (UnityData.isEditor) mesh.name = "WorldLayerSubMesh_" + GetType().Name + "_" + Find.World.info.seedString;
                var created = new LayerSubMesh(mesh, WorldMaterials.VertexColorTransparent);
                meshes.Add(created);
                subMeshes.Add(created); // the base class draws and disposes of what is in its list
            }
            buildMesh = meshes[buildUsed++];
            return buildMesh;
        }

        private static Vector3 Lift(Vector3 v, float height) => v + v.normalized * height;

        /// <summary>Is the point one of the tile's corners (neighbouring tiles carry their own copies of the shared ones)?</summary>
        private static bool HasCorner(Unity.Collections.NativeArray<Vector3> verts, int start, int end, Vector3 p)
        {
            for (int i = start; i < end; i++)
                if ((verts[i] - p).sqrMagnitude < CornerEpsilonSq) return true;
            return false;
        }
    }
}
