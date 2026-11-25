using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace com.osakishokai.vpm.shadowpickup
{
    /// <summary>
    /// Draws the steady-state visual shadows for one identical-prefab group.
    ///
    /// A logical pickup can contain multiple MeshRenderers and multiple materials.
    /// The editor baker flattens those into render parts. Exact source root
    /// matrices and prefab-relative part matrices are combined at runtime.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ShadowPickupInstancing : UdonSharpBehaviour
    {
        [Header("Baked render parts")]
        public Mesh[] meshes;
        public Material[] materials;
        public int[] submeshIndices;

        [Header("Baked logical instances")]
        public int instanceCount;
        public Vector3[] instancePositions;
        public Quaternion[] instanceRotations;
        public Vector3[] instanceScales;

        [Tooltip("Exact world matrix of each logical prefab root as authored in the source Scene.")]
        public Matrix4x4[] instanceRootMatrices;

        [Header("Desktop ray selection")]
        [Tooltip("Baked world-space AABB center for each logical pickup. Used only to choose which Shadow to materialize on Desktop.")]
        public Vector3[] desktopBoundsCenters;

        [Tooltip("Baked world-space AABB extents for each logical pickup. Uses the same static MeshRenderer state as the Shadow visual.")]
        public Vector3[] desktopBoundsExtents;

        [Tooltip("Prefab-root-relative matrix for each DMI render part.")]
        public Matrix4x4[] partRelativeMatrices;

        [Header("Variant configuration")]
        [Tooltip("Variant / flavor ID for each logical instance.")]
        public byte[] variantIds;

        public byte GetVariantId(int logicalIndex)
        {
            if (variantIds == null || logicalIndex < 0 || logicalIndex >= variantIds.Length) return 0;
            return variantIds[logicalIndex];
        }

        private Matrix4x4[][] drawBatches;
        private int[] drawCounts;
        private int[] drawParts;
        private MaterialPropertyBlock[] drawBlocks;
        private int batchesPerPart;
        private int partCount;
        private int[] hiddenIndicesCache;
        private Matrix4x4 hiddenMatrix;

        public int _InstanceCount()
        {
            return instanceCount;
        }

        public Vector3 PositionAt(int index)
        {
            if (instancePositions == null || index < 0 || index >= instancePositions.Length) return Vector3.zero;
            return instancePositions[index];
        }

        // Box around every instance (desktop bounds and positions), built on first use.
        private bool nearBoundsReady;
        private bool nearBoundsValid;
        private Vector3 nearMin;
        private Vector3 nearMax;

        private void BuildNearBounds()
        {
            nearBoundsReady = true;
            int total = _InstanceCount();
            if (total <= 0 || desktopBoundsCenters == null || desktopBoundsExtents == null ||
                desktopBoundsCenters.Length != total || desktopBoundsExtents.Length != total ||
                instancePositions == null || instancePositions.Length != total) return;

            Vector3 c0 = desktopBoundsCenters[0];
            Vector3 e0 = desktopBoundsExtents[0];
            float loX = c0.x - e0.x, loY = c0.y - e0.y, loZ = c0.z - e0.z;
            float hiX = c0.x + e0.x, hiY = c0.y + e0.y, hiZ = c0.z + e0.z;
            for (int i = 0; i < total; i++)
            {
                Vector3 c = desktopBoundsCenters[i];
                Vector3 e = desktopBoundsExtents[i];
                Vector3 p = instancePositions[i];
                loX = Mathf.Min(loX, Mathf.Min(c.x - e.x, p.x));
                loY = Mathf.Min(loY, Mathf.Min(c.y - e.y, p.y));
                loZ = Mathf.Min(loZ, Mathf.Min(c.z - e.z, p.z));
                hiX = Mathf.Max(hiX, Mathf.Max(c.x + e.x, p.x));
                hiY = Mathf.Max(hiY, Mathf.Max(c.y + e.y, p.y));
                hiZ = Mathf.Max(hiZ, Mathf.Max(c.z + e.z, p.z));
            }
            nearMin = new Vector3(loX - 0.05f, loY - 0.05f, loZ - 0.05f);
            nearMax = new Vector3(hiX + 0.05f, hiY + 0.05f, hiZ + 0.05f);
            nearBoundsValid = true;
        }

        /// <summary>
        /// True when <paramref name="point"/> is within <paramref name="margin"/> metres of the box around all instances.
        /// The pool uses it to skip its per-instance searches (which run in Udon and cost O(instance count) every poll)
        /// while the player is nowhere near this pool. Conservative: returns true when the bounds are unknown.
        /// </summary>
        public bool _IsPointNear(Vector3 point, float margin)
        {
            if (!nearBoundsReady) BuildNearBounds();
            if (!nearBoundsValid) return true;
            float dx = Mathf.Max(Mathf.Max(nearMin.x - point.x, 0f), point.x - nearMax.x);
            float dy = Mathf.Max(Mathf.Max(nearMin.y - point.y, 0f), point.y - nearMax.y);
            float dz = Mathf.Max(Mathf.Max(nearMin.z - point.z, 0f), point.z - nearMax.z);
            return dx * dx + dy * dy + dz * dz <= margin * margin;
        }

        // ---- Spatial grid: keeps the per-poll searches O(cells + a few instances) instead of O(all instances) ----
        // Instances are bucketed by position into ~0.5 m cells on first use; each non-empty cell keeps a conservative box
        // (member desktop bounds + the 2 cm forgiveness margin, and positions). A search tests the cell boxes first and
        // only walks the members of the cells it can actually reach.
        private bool gridReady;
        private bool gridValid;
        private int cellCount;
        private Vector3[] cellMin;
        private Vector3[] cellMax;
        private int[] cellStart;
        private int[] cellItems;

        /// <summary>Result slots of <see cref="_RaycastBoth"/> (U# has no out parameters).</summary>
        [System.NonSerialized] public int rayRawIndex = -1;
        [System.NonSerialized] public int rayAvailableIndex = -1;

        private void BuildGrid()
        {
            gridReady = true;
            int total = _InstanceCount();
            if (total <= 0 || desktopBoundsCenters == null || desktopBoundsExtents == null ||
                desktopBoundsCenters.Length != total || desktopBoundsExtents.Length != total ||
                instancePositions == null || instancePositions.Length != total) return;

            float minX = instancePositions[0].x, minY = instancePositions[0].y, minZ = instancePositions[0].z;
            float maxX = minX, maxY = minY, maxZ = minZ;
            for (int i = 1; i < total; i++)
            {
                Vector3 p = instancePositions[i];
                minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y); minZ = Mathf.Min(minZ, p.z);
                maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y); maxZ = Mathf.Max(maxZ, p.z);
            }

            float cellSize = 0.5f;
            int nx = 1, ny = 1, nz = 1;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                nx = (int)Mathf.Floor((maxX - minX) / cellSize) + 1;
                ny = (int)Mathf.Floor((maxY - minY) / cellSize) + 1;
                nz = (int)Mathf.Floor((maxZ - minZ) / cellSize) + 1;
                if (nx * ny * nz <= 4096) break;
                cellSize *= 2f;
            }

            int gridCells = nx * ny * nz;
            int[] cellOf = new int[total];
            int[] counts = new int[gridCells];
            for (int i = 0; i < total; i++)
            {
                Vector3 p = instancePositions[i];
                int ix = Mathf.Min((int)Mathf.Floor((p.x - minX) / cellSize), nx - 1);
                int iy = Mathf.Min((int)Mathf.Floor((p.y - minY) / cellSize), ny - 1);
                int iz = Mathf.Min((int)Mathf.Floor((p.z - minZ) / cellSize), nz - 1);
                int id = ix + nx * (iy + ny * iz);
                cellOf[i] = id;
                counts[id]++;
            }

            int[] compact = new int[gridCells];
            int used = 0;
            for (int c = 0; c < gridCells; c++)
            {
                if (counts[c] > 0) { compact[c] = used; used++; }
                else compact[c] = -1;
            }

            cellCount = used;
            cellStart = new int[used + 1];
            cellMin = new Vector3[used];
            cellMax = new Vector3[used];
            cellItems = new int[total];
            int[] cursor = new int[used];
            int run = 0;
            for (int c = 0; c < gridCells; c++)
            {
                if (compact[c] < 0) continue;
                cellStart[compact[c]] = run;
                run += counts[c];
                cellMin[compact[c]] = new Vector3(1e9f, 1e9f, 1e9f);
                cellMax[compact[c]] = new Vector3(-1e9f, -1e9f, -1e9f);
            }
            cellStart[used] = run;

            for (int i = 0; i < total; i++)
            {
                int k = compact[cellOf[i]];
                cellItems[cellStart[k] + cursor[k]] = i;
                cursor[k]++;

                Vector3 c = desktopBoundsCenters[i];
                Vector3 e = desktopBoundsExtents[i] + Vector3.one * 0.02f;
                Vector3 p = instancePositions[i];
                Vector3 lo = cellMin[k];
                Vector3 hi = cellMax[k];
                cellMin[k] = new Vector3(Mathf.Min(lo.x, Mathf.Min(c.x - e.x, p.x)), Mathf.Min(lo.y, Mathf.Min(c.y - e.y, p.y)), Mathf.Min(lo.z, Mathf.Min(c.z - e.z, p.z)));
                cellMax[k] = new Vector3(Mathf.Max(hi.x, Mathf.Max(c.x + e.x, p.x)), Mathf.Max(hi.y, Mathf.Max(c.y + e.y, p.y)), Mathf.Max(hi.z, Mathf.Max(c.z + e.z, p.z)));
            }

            gridValid = true;
        }

        // Slab test of a ray against a box; returns the entry distance, or -1 when the ray misses within maxDistance.
        private float RayBoxEnter(Vector3 origin, Vector3 direction, float maxDistance, Vector3 min, Vector3 max)
        {
            float tMin = 0f;
            float tMax = maxDistance;

            if (Mathf.Abs(direction.x) < 0.00001f)
            {
                if (origin.x < min.x || origin.x > max.x) return -1f;
            }
            else
            {
                float t1 = (min.x - origin.x) / direction.x;
                float t2 = (max.x - origin.x) / direction.x;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMax < tMin) return -1f;
            }

            if (Mathf.Abs(direction.y) < 0.00001f)
            {
                if (origin.y < min.y || origin.y > max.y) return -1f;
            }
            else
            {
                float t1 = (min.y - origin.y) / direction.y;
                float t2 = (max.y - origin.y) / direction.y;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMax < tMin) return -1f;
            }

            if (Mathf.Abs(direction.z) < 0.00001f)
            {
                if (origin.z < min.z || origin.z > max.z) return -1f;
            }
            else
            {
                float t1 = (min.z - origin.z) / direction.z;
                float t2 = (max.z - origin.z) / direction.z;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMax < tMin) return -1f;
            }

            return tMin;
        }

        private bool IsExcluded(int index, int[] excluded)
        {
            if (excluded == null) return false;
            for (int e = 0; e < excluded.Length; e++)
                if (excluded[e] == index) return true;
            return false;
        }

        /// <summary>
        /// One pass that yields both <see cref="RaycastIndex"/> (rayRawIndex) and <see cref="RaycastAvailableIndex"/>
        /// (rayAvailableIndex). Same result as those two, without walking every instance twice.
        /// </summary>
        public void _RaycastBoth(Vector3 origin, Vector3 direction, float maxDistance, int[] excluded)
        {
            rayRawIndex = -1;
            rayAvailableIndex = -1;
            if (!gridReady) BuildGrid();
            if (!gridValid)
            {
                rayRawIndex = RaycastIndex(origin, direction, maxDistance);
                rayAvailableIndex = RaycastAvailableIndex(origin, direction, maxDistance, excluded);
                return;
            }

            float dirSq = direction.sqrMagnitude;
            if (maxDistance <= 0f || dirSq < 0.000001f) return;
            direction /= Mathf.Sqrt(dirSq);

            float bestRaw = maxDistance + 1f;
            float bestAvailable = maxDistance + 1f;
            for (int c = 0; c < cellCount; c++)
            {
                float cellT = RayBoxEnter(origin, direction, maxDistance, cellMin[c], cellMax[c]);
                if (cellT < 0f || cellT >= bestRaw) continue;

                int end = cellStart[c + 1];
                for (int k = cellStart[c]; k < end; k++)
                {
                    int i = cellItems[k];
                    Vector3 center = desktopBoundsCenters[i];
                    Vector3 ext = desktopBoundsExtents[i] + Vector3.one * 0.02f;
                    float t = RayBoxEnter(origin, direction, maxDistance, center - ext, center + ext);
                    if (t < 0f) continue;
                    if (t < bestRaw) { bestRaw = t; rayRawIndex = i; }
                    if (t < bestAvailable && !IsExcluded(i, excluded)) { bestAvailable = t; rayAvailableIndex = i; }
                }
            }
        }

        /// <summary>
        /// Nearest instance to <paramref name="point"/> that is not excluded and lies within <paramref name="maxDistance"/>, or -1.
        /// Only cells within reach are walked.
        /// </summary>
        public int NearestAvailableIndexWithin(Vector3 point, int[] excluded, float maxDistance)
        {
            if (!gridReady) BuildGrid();
            if (!gridValid) return NearestAvailableIndex(point, excluded);

            float limitSq = maxDistance * maxDistance + 0.000001f;
            float bestSq = limitSq;
            int best = -1;
            for (int c = 0; c < cellCount; c++)
            {
                Vector3 lo = cellMin[c];
                Vector3 hi = cellMax[c];
                float dx = Mathf.Max(Mathf.Max(lo.x - point.x, 0f), point.x - hi.x);
                float dy = Mathf.Max(Mathf.Max(lo.y - point.y, 0f), point.y - hi.y);
                float dz = Mathf.Max(Mathf.Max(lo.z - point.z, 0f), point.z - hi.z);
                if (dx * dx + dy * dy + dz * dz > limitSq) continue;

                int end = cellStart[c + 1];
                for (int k = cellStart[c]; k < end; k++)
                {
                    int i = cellItems[k];
                    float sq = (point - instancePositions[i]).sqrMagnitude;
                    if (sq < bestSq && !IsExcluded(i, excluded)) { bestSq = sq; best = i; }
                }
            }
            return best;
        }

        public Quaternion RotationAt(int index)
        {
            if (instanceRotations == null || index < 0 || index >= instanceRotations.Length) return Quaternion.identity;
            return instanceRotations[index];
        }

        public Vector3 ScaleAt(int index)
        {
            if (instanceScales == null || index < 0 || index >= instanceScales.Length) return Vector3.one;
            return instanceScales[index];
        }

        /// <summary>
        /// Finds the nearest logical instance that is not currently represented by
        /// a real pool slot. The exclusion list is tiny (8 entries), so a linear scan
        /// keeps the runtime data structure simple.
        /// </summary>
        public int NearestAvailableIndex(Vector3 point, int[] excluded)
        {
            int total = _InstanceCount();
            if (total <= 0 || instancePositions == null) return -1;

            float bestSq = float.MaxValue;
            int best = -1;
            for (int i = 0; i < total; i++)
            {
                bool skip = false;
                if (excluded != null)
                {
                    for (int e = 0; e < excluded.Length; e++)
                    {
                        if (excluded[e] == i) { skip = true; break; }
                    }
                }
                if (skip) continue;

                float sq = (point - instancePositions[i]).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = i;
                }
            }
            return best;
        }

        public int NearestIndex(Vector3 point)
        {
            return NearestAvailableIndexWithin(point, null, ShadowPickupPool.ReleaseDistance + 0.1f);
        }

        public int NearestIndexWithin(Vector3 point, float maxDistance)
        {
            return NearestAvailableIndexWithin(point, null, maxDistance);
        }

        /// <summary>
        /// Desktop selection helper. VRChat Desktop uses a camera/head ray rather
        /// than a hand-hover sphere, so ShadowPickup mirrors that interaction while
        /// the real collider does not yet exist. Bounds are baked from the same
        /// steady-state MeshRenderers that form the Shadow visual.
        /// </summary>
        public int RaycastAvailableIndex(Vector3 origin, Vector3 direction, float maxDistance, int[] excluded)
        {
            int total = _InstanceCount();
            if (total <= 0 || desktopBoundsCenters == null || desktopBoundsExtents == null ||
                desktopBoundsCenters.Length != total || desktopBoundsExtents.Length != total || maxDistance <= 0f) return -1;

            float dirSq = direction.sqrMagnitude;
            if (dirSq < 0.000001f) return -1;
            direction /= Mathf.Sqrt(dirSq);

            float bestDistance = maxDistance + 1f;
            int best = -1;

            for (int i = 0; i < total; i++)
            {
                bool skip = false;
                if (excluded != null)
                {
                    for (int e = 0; e < excluded.Length; e++)
                    {
                        if (excluded[e] == i) { skip = true; break; }
                    }
                }
                if (skip) continue;

                Vector3 center = desktopBoundsCenters[i];
                // A small forgiveness margin avoids a one-pixel / one-centimeter
                // miss feeling worse than ordinary Desktop pickup interaction.
                Vector3 ext = desktopBoundsExtents[i] + Vector3.one * 0.02f;
                Vector3 min = center - ext;
                Vector3 max = center + ext;
                float tMin = 0f;
                float tMax = maxDistance;

                if (Mathf.Abs(direction.x) < 0.00001f)
                {
                    if (origin.x < min.x || origin.x > max.x) continue;
                }
                else
                {
                    float t1 = (min.x - origin.x) / direction.x;
                    float t2 = (max.x - origin.x) / direction.x;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    tMin = Mathf.Max(tMin, t1);
                    tMax = Mathf.Min(tMax, t2);
                    if (tMax < tMin) continue;
                }

                if (Mathf.Abs(direction.y) < 0.00001f)
                {
                    if (origin.y < min.y || origin.y > max.y) continue;
                }
                else
                {
                    float t1 = (min.y - origin.y) / direction.y;
                    float t2 = (max.y - origin.y) / direction.y;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    tMin = Mathf.Max(tMin, t1);
                    tMax = Mathf.Min(tMax, t2);
                    if (tMax < tMin) continue;
                }

                if (Mathf.Abs(direction.z) < 0.00001f)
                {
                    if (origin.z < min.z || origin.z > max.z) continue;
                }
                else
                {
                    float t1 = (min.z - origin.z) / direction.z;
                    float t2 = (max.z - origin.z) / direction.z;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    tMin = Mathf.Max(tMin, t1);
                    tMax = Mathf.Min(tMax, t2);
                    if (tMax < tMin) continue;
                }

                if (tMin <= maxDistance && tMin < bestDistance)
                {
                    bestDistance = tMin;
                    best = i;
                }
            }

            return best;
        }

        public int RaycastIndex(Vector3 origin, Vector3 direction, float maxDistance)
        {
            return RaycastAvailableIndex(origin, direction, maxDistance, null);
        }

        private void Start()
        {
            BuildRuntimeBatches();
            BuildGrid();
            BuildNearBounds();
        }

        private void BuildRuntimeBatches()
        {
            partCount = meshes == null ? 0 : meshes.Length;
            if (partCount == 0 || instanceCount <= 0 || materials == null || submeshIndices == null ||
                instanceRootMatrices == null || partRelativeMatrices == null) return;
            if (materials.Length != partCount || submeshIndices.Length != partCount || partRelativeMatrices.Length != partCount) return;
            if (instanceRootMatrices.Length != instanceCount) return;

            batchesPerPart = (instanceCount + 1022) / 1023;
            int totalDrawBatches = partCount * batchesPerPart;
            drawBatches = new Matrix4x4[totalDrawBatches][];
            drawCounts = new int[totalDrawBatches];
            drawParts = new int[totalDrawBatches];
            drawBlocks = new MaterialPropertyBlock[totalDrawBatches];
            hiddenMatrix = Matrix4x4.TRS(new Vector3(0f, -99999f, 0f), Quaternion.identity, Vector3.zero);

            for (int p = 0; p < partCount; p++)
            {
                for (int b = 0; b < batchesPerPart; b++)
                {
                    int draw = p * batchesPerPart + b;
                    int count = Mathf.Min(1023, instanceCount - b * 1023);
                    drawCounts[draw] = count;
                    drawParts[draw] = p;
                    drawBlocks[draw] = new MaterialPropertyBlock();
                    drawBatches[draw] = new Matrix4x4[count];
                    float[] variantFloats = (variantIds != null && variantIds.Length == instanceCount) ? new float[count] : null;

                    for (int local = 0; local < count; local++)
                    {
                        int instance = b * 1023 + local;
                        drawBatches[draw][local] = instanceRootMatrices[instance] * partRelativeMatrices[p];
                        if (variantFloats != null)
                        {
                            variantFloats[local] = (float)variantIds[instance];
                        }
                    }

                    if (variantFloats != null)
                    {
                        drawBlocks[draw].SetFloatArray("_VariantID", variantFloats);
                    }
                }
            }

            hiddenIndicesCache = null;
        }

        private void SetMatrixHidden(int index, bool hidden)
        {
            if (drawBatches == null || index < 0 || index >= instanceCount) return;

            int b = index / 1023;
            int local = index % 1023;
            for (int p = 0; p < partCount; p++)
            {
                int draw = p * batchesPerPart + b;
                if (draw < 0 || draw >= drawBatches.Length || local >= drawBatches[draw].Length) continue;
                drawBatches[draw][local] = hidden ? hiddenMatrix : instanceRootMatrices[index] * partRelativeMatrices[p];
            }
        }

        /// <summary>
        /// The pool supplies the logical indices hidden on this client (up to 8 public + 8 local previews). All render
        /// parts belonging to those logical pickups are hidden while a real pickup
        /// represents them.
        /// </summary>
        public void SetHiddenInstances(int[] indices)
        {
            if (drawBatches == null) return;

            if (hiddenIndicesCache != null)
            {
                for (int i = 0; i < hiddenIndicesCache.Length; i++)
                    SetMatrixHidden(hiddenIndicesCache[i], false);
            }

            if (indices == null)
            {
                hiddenIndicesCache = null;
                return;
            }

            int[] copy = new int[indices.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                copy[i] = indices[i];
                SetMatrixHidden(indices[i], true);
            }
            hiddenIndicesCache = copy;
        }

        private void LateUpdate()
        {
            if (drawBatches == null || meshes == null || materials == null) return;

            for (int d = 0; d < drawBatches.Length; d++)
            {
                if (drawCounts[d] <= 0) continue;
                int p = drawParts[d];
                if (p < 0 || p >= meshes.Length || meshes[p] == null || materials[p] == null) continue;
                VRCGraphics.DrawMeshInstanced(meshes[p], submeshIndices[p], materials[p], drawBatches[d], drawCounts[d], drawBlocks[d]);
            }
        }
    }
}
