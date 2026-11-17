using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
using VRC.SDK3.UdonNetworkCalling;

namespace com.osakishokai.vpm.shadowpickup
{
    /// <summary>
    /// Lightweight network coordination for one identical-prefab ShadowPickup group.
    ///
    /// IMPORTANT: ShadowPickup intentionally uses NO synced variables.
    /// The only world-visible custom state is slot -> logicalIndex, replicated with
    /// parameterized NetworkCallable events. Everything else is local control state
    /// or ordinary VRC_Pickup/VRCObjectSync state.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ShadowPickupPool : UdonSharpBehaviour
    {
        // Product decisions: intentionally code constants, not inspector settings.
        // Idle real pickups per group (= how many of one kind can be held at once, all players together). Kept small:
        // each idle one (VRC_Pickup + ObjectSync, parked kinematic) costs ~32 us per frame on Quest.
        public const int Capacity = 4;
        public const int MaxLogicalInstances = 1023;
        public const int EmptyIndex = 1023; // 10-bit sentinel; valid logical indices are 0..1022.
        public const float ActivationDistance = 0.15f;
        public const float ReleaseDistance = 0.25f;
        public const float ReturnDelay = 5.0f;
        public const float DesktopProximity = 2.0f;
        public const float DesktopRayDistance = 2.0f;
        public const float ReturnRegionRadius = 5.0f;
        public const float ReturnExitDistance = 10.0f;

        private const float DesktopPollInterval = 0.05f;
        private const float VrPollInterval = 0.20f;
        // If an assignment event beats ObjectSync to a remote client, the real slot
        // can still look like it is sitting in the pool for a moment. Never release
        // immediately just because it is inside the return region; wait for either
        // an observed round trip or this conservative grace period.
        private const float ReturnObservationGrace = 5.0f;
        private const float LateJoinSnapshotDelay = 2.0f;
        private const float LateJoinInteractionGrace = 3.0f;

        // NetPickup packs: slot[3] + logicalIndex[10] + claimantPlayerId[10] = 23 bits.
        private const int SlotBits = 3;
        private const int IndexBits = 10;
        private const int IndexMask = (1 << IndexBits) - 1;
        private const int PlayerBits = 10;
        private const int PlayerMask = (1 << PlayerBits) - 1;

        public ShadowPickupInstancing instances;
        [HideInInspector] public ShadowPickupSlot[] slots;

        // Local mirror of the ONLY public ShadowPickup state.
        // Never make these synced variables.
        private int[] publicIndices;
        private int[] claimantIds;
        private bool[] leftPoolRegion;
        private float[] mappingReceivedAt;
        private int[] unavailableIndices; // public assignments + this client's local reservations (including ownership requests).
        private int[] hiddenIndices;      // public assignments + locally materialized real slots only.

        private float nextRequest;
        private Vector3 left;
        private Vector3 right;
        private int desktopAimIndex = -1;     // available target used for creating a new reservation
        private int desktopRawAimIndex = -1;  // ignores exclusions; keeps an existing reservation alive
        private bool runtimeInitialized;
        private bool needsInitialSnapshot;
        private float interactionReadyAt;

        private void Start()
        {
            InitializeRuntimeState();
        }

        private void InitializeRuntimeState()
        {
            if (runtimeInitialized) return;
            if (slots == null || slots.Length != Capacity) return;

            publicIndices = new int[Capacity];
            claimantIds = new int[Capacity];
            leftPoolRegion = new bool[Capacity];
            mappingReceivedAt = new float[Capacity];
            unavailableIndices = new int[Capacity * 2];
            hiddenIndices = new int[Capacity * 2];

            for (int i = 0; i < Capacity; i++)
            {
                publicIndices[i] = EmptyIndex;
                claimantIds[i] = -1;
                leftPoolRegion[i] = false;
                mappingReceivedAt[i] = 0f;
            }

            needsInitialSnapshot = !Networking.IsMaster;
            interactionReadyAt = Time.time + (needsInitialSnapshot ? LateJoinInteractionGrace : 0f);
            runtimeInitialized = true;
            RefreshVisibility();
        }

        private bool Ready()
        {
            if (readyCached) return true;
            if (!runtimeInitialized) InitializeRuntimeState();
            // The instance count and the slot array never change at runtime, so once ready it stays ready (this used to be a
            // cross-behaviour call every frame in every pool).
            readyCached = runtimeInitialized && instances != null && instances._InstanceCount() > 0 && slots != null && slots.Length == Capacity;
            return readyCached;
        }

        // Idle slots (no local reservation) only refresh a remote preview's visibility, and the return detection is time-based:
        // both are fine at this cadence. A slot that holds a reservation (localIndex >= 0) is still ticked every frame.
        // On Quest the pools' Update cost ~3.6 ms per frame for 10 groups while nobody was reaching for anything, when
        // every pool read both hands and asked all its slots for localIndex (cross-behaviour reads) every frame.
        // Now an idle pool only compares times: the slots are asked at the idle scan (which also refreshes anyLocal),
        // the hands are read only while a slot holds a reservation or when the poll is due, and the scan slows down while
        // the head is away from this group's instances.
        private const float IdleScanInterval = 0.05f;
        private const float FarScanInterval = 0.25f;
        private const float NearScanDistance = 4.0f;
        private bool readyCached;
        private float nextIdleScan;
        private bool vrKnown;
        private bool isVrCached;
        private bool anyLocal;   // some slot held a local reservation at the last scan, or one was just requested

        private void Update()
        {
            if (!Ready()) return;

            float now = Time.time;
            bool scanDue = now >= nextIdleScan;
            bool pollDue = now >= nextRequest;
            if (!scanDue && !pollDue && !anyLocal) return;

            VRCPlayerApi player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player)) return;

            if (scanDue)
            {
                Vector3 headPosition = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
                nextIdleScan = now + (instances._IsPointNear(headPosition, NearScanDistance) ? IdleScanInterval : FarScanInterval);
                DetectReturnedSlots();
            }

            if (scanDue || anyLocal)
            {
                bool stillLocal = false;
                for (int i = 0; i < Capacity; i++)
                {
                    ShadowPickupSlot slot = slots[i];
                    if (slot == null) continue;
                    if (scanDue || slot.localIndex >= 0) slot._TickLocal();
                    if (slot.localIndex >= 0) stillLocal = true;
                }
                anyLocal = stillLocal;
            }

            // A late joiner gets a short quiet window so the Master's compact
            // snapshot can arrive before this client starts locally materializing
            // slots based on an all-empty mirror.
            if (now < interactionReadyAt) return;

            // Whether the user is in VR never changes during a session.
            if (!vrKnown) { isVrCached = player.IsUserInVR(); vrKnown = true; }
            bool isVr = isVrCached;
            if (isVr && (anyLocal || pollDue))
            {
                // Slots read the hand positions every frame while they hold a reservation.
                left = player.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand).position;
                right = player.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand).position;
            }

            if (!pollDue) return;
            nextRequest = now + (isVr ? VrPollInterval : DesktopPollInterval);
            VRCPlayerApi.TrackingData head = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            if (!isVr)
            {
                // Desktop: the hand positions are the head position, needed only where a poll uses them.
                left = head.position;
                right = head.position;
            }

            if (isVr)
            {
                desktopAimIndex = -1;
                desktopRawAimIndex = -1;
                // TryHand searches every instance in Udon; skip it while the hand is far from this pool.
                if (player.GetPickupInHand(VRC_Pickup.PickupHand.Left) == null && instances._IsPointNear(left, ActivationDistance)) TryHand(left, 1);
                if (player.GetPickupInHand(VRC_Pickup.PickupHand.Right) == null && instances._IsPointNear(right, ActivationDistance)) TryHand(right, 2);
            }
            else
            {
                if (instances._IsPointNear(head.position, DesktopRayDistance))
                {
                    Vector3 viewDirection = head.rotation * Vector3.forward;
                    instances._RaycastBoth(head.position, viewDirection, DesktopRayDistance, unavailableIndices);
                    desktopRawAimIndex = instances.rayRawIndex;
                    desktopAimIndex = instances.rayAvailableIndex;
                    if (player.GetPickupInHand(VRC_Pickup.PickupHand.Right) == null) TryDesktop(desktopAimIndex);
                }
                else
                {
                    // Out of reach of every instance: exactly what the searches would return.
                    desktopRawAimIndex = -1;
                    desktopAimIndex = -1;
                }
            }
        }

        public int PublicIndexAt(int slotIndex)
        {
            if (!runtimeInitialized || publicIndices == null || slotIndex < 0 || slotIndex >= Capacity) return EmptyIndex;
            return publicIndices[slotIndex];
        }

        public float HandDistance(Vector3 point)
        {
            return Mathf.Min(Vector3.Distance(left, point), Vector3.Distance(right, point));
        }

        public bool IsDesktopAimingAt(int index)
        {
            // IMPORTANT: use the raw ray here, not the "available" ray. Once a local
            // reservation exists its logical index is intentionally excluded from
            // desktopAimIndex. Using desktopAimIndex for release detection made a
            // locally materialized slot cancel itself on the next poll, producing the
            // X=pool <-> X=shadow flicker even in single-player Play Mode.
            return index >= 0 && desktopRawAimIndex == index;
        }

        private bool IsPublicIndexUsed(int index)
        {
            if (publicIndices == null) return false;
            for (int i = 0; i < Capacity; i++)
                if (publicIndices[i] == index) return true;
            return false;
        }

        private bool HasLocalReservationForHand(int hand)
        {
            for (int i = 0; i < Capacity; i++)
                if (slots[i] != null && slots[i].IsLocallyReservedForHand(hand)) return true;
            return false;
        }

        private int FindFreeLocalSlot()
        {
            for (int i = 0; i < Capacity; i++)
            {
                if (publicIndices[i] != EmptyIndex) continue;
                if (slots[i] == null || !slots[i]._IsLocallyFree()) continue;
                // Empty public mapping does NOT imply a free Real. Another client can
                // own an uncommitted hover reservation that intentionally has no custom
                // network event. ObjectSync position is the cross-client reservation bit.
                if (!slots[i]._IsPhysicallyInPool()) continue;
                return i;
            }
            return -1;
        }

        private void TryDesktop(int index)
        {
            if (index < 0 || index >= instances._InstanceCount()) return;
            if (HasLocalReservationForHand(2) || IsPublicIndexUsed(index)) return;
            int free = FindFreeLocalSlot();
            if (free < 0) return;
            slots[free].RequestMaterialization(index, 2);
            anyLocal = true;
            RefreshVisibility();
        }

        private void TryHand(Vector3 point, int hand)
        {
            if (HasLocalReservationForHand(hand)) return;
            int index = instances.NearestAvailableIndexWithin(point, unavailableIndices, ActivationDistance);
            if (index < 0 || Vector3.Distance(point, instances.PositionAt(index)) > ActivationDistance) return;
            int free = FindFreeLocalSlot();
            if (free < 0) return;
            slots[free].RequestMaterialization(index, hand);
            anyLocal = true;
            RefreshVisibility();
        }

        /// <summary>
        /// Called by a locally-held real slot after VRCObjectSync ownership has
        /// actually transferred to this client. This is the only ordinary custom
        /// state broadcast: one packed int, once when a new Shadow becomes real.
        /// </summary>
        public void BroadcastPickup(int slotIndex, int logicalIndex)
        {
            if (!Ready() || slotIndex < 0 || slotIndex >= Capacity || logicalIndex < 0 || logicalIndex >= instances._InstanceCount()) return;
            if (logicalIndex >= MaxLogicalInstances) return;
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local) || local.playerId < 0 || local.playerId > PlayerMask) return;
            if (slots[slotIndex] == null || !Networking.IsOwner(slots[slotIndex].gameObject)) return;

            int packed = PackPickup(slotIndex, logicalIndex, local.playerId);
            this.SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetPickup), packed);
        }

        private int PackPickup(int slotIndex, int logicalIndex, int playerId)
        {
            return (slotIndex & 7) |
                   ((logicalIndex & IndexMask) << SlotBits) |
                   ((playerId & PlayerMask) << (SlotBits + IndexBits));
        }

        [NetworkCallable(maxEventsPerSecond: 40)]
        public void NetPickup(int packed)
        {
            if (!Ready()) return;

            int slotIndex = packed & 7;
            int logicalIndex = (packed >> SlotBits) & IndexMask;
            int claimant = (packed >> (SlotBits + IndexBits)) & PlayerMask;
            if (slotIndex < 0 || slotIndex >= Capacity || logicalIndex < 0 || logicalIndex >= instances._InstanceCount() || logicalIndex >= MaxLogicalInstances) return;

            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (!Utilities.IsValid(caller) || caller.playerId != claimant) return;

            // Basic plausibility guard. Pickup happens at/near the Shadow position;
            // a 5m allowance is intentionally generous for avatars and motion.
            if (Vector3.Distance(caller.GetPosition(), instances.PositionAt(logicalIndex)) > 5f) return;

            // A normal Pickup event arriving before a late-join snapshot is newer
            // than that snapshot for this client. Mark bootstrap complete so a stale
            // snapshot arriving later cannot roll this mapping back.
            needsInitialSnapshot = false;
            interactionReadyAt = Time.time;

            int existingIndex = publicIndices[slotIndex];
            int existingClaimant = claimantIds[slotIndex];

            if (existingIndex == EmptyIndex)
            {
                SetPublicMapping(slotIndex, logicalIndex, claimant);
            }
            else if (existingIndex == logicalIndex)
            {
                // Same physical slot / same Shadow (e.g. a dropped item is picked by
                // another player). The public mapping is unchanged. Keep the original
                // tie-break priority; ownership itself is VRCObjectSync's concern.
            }
            else
            {
                // Simultaneous local materialization can make two clients claim the
                // same Real slot for different Shadows. Network-event order is not
                // globally ordered across senders, so choose a deterministic winner:
                // lower claimant playerId, then lower logical index.
                bool newWins = claimant < existingClaimant ||
                               (claimant == existingClaimant && logicalIndex < existingIndex);
                if (newWins) SetPublicMapping(slotIndex, logicalIndex, claimant);
            }

            ResolveDuplicateLogicalIndices();
            CancelLocalUncommittedReservationsClaimedByPublicMappings();
            ApplyPublicStateToSlots();
            RefreshVisibility();
        }

        private void SetPublicMapping(int slotIndex, int logicalIndex, int claimant)
        {
            publicIndices[slotIndex] = logicalIndex;
            claimantIds[slotIndex] = claimant;
            leftPoolRegion[slotIndex] = false;
            mappingReceivedAt[slotIndex] = Time.time;

            if (slots[slotIndex] != null)
            {
                float distance = Vector3.Distance(slots[slotIndex].transform.position, slots[slotIndex].poolPosition);
                if (distance > ReturnExitDistance) leftPoolRegion[slotIndex] = true;
            }
        }

        private void ResolveDuplicateLogicalIndices()
        {
            // If one Shadow was independently taken through two Real slots, the
            // lower slot number wins everywhere. Losers are locally unmapped and
            // the loser owner self-returns through ordinary ObjectSync.Respawn().
            for (int winner = 0; winner < Capacity; winner++)
            {
                int index = publicIndices[winner];
                if (index == EmptyIndex) continue;

                for (int loser = winner + 1; loser < Capacity; loser++)
                {
                    if (publicIndices[loser] != index) continue;
                    publicIndices[loser] = EmptyIndex;
                    claimantIds[loser] = -1;
                    leftPoolRegion[loser] = false;
                    mappingReceivedAt[loser] = 0f;
                    if (slots[loser] != null) slots[loser]._HandleDuplicateLoss();
                }
            }
        }

        private void DetectReturnedSlots()
        {
            if (publicIndices == null) return;
            bool changed = false;

            for (int i = 0; i < Capacity; i++)
            {
                if (publicIndices[i] == EmptyIndex || slots[i] == null) continue;

                float distance = Vector3.Distance(slots[i].transform.position, slots[i].poolPosition);
                if (!leftPoolRegion[i] && distance > ReturnExitDistance)
                {
                    leftPoolRegion[i] = true;
                    // Once every client has physically observed this committed slot
                    // outside the isolated pool, treat the mapping as an incumbent.
                    // Priority 0 prevents a much later stale local preview claim from
                    // replacing an established real pickup on the same slot.
                    claimantIds[i] = 0;
                }

                bool observedRoundTrip = leftPoolRegion[i] && distance <= ReturnRegionRadius;
                bool gracefulFallback = !leftPoolRegion[i] && distance <= ReturnRegionRadius &&
                                        Time.time - mappingReceivedAt[i] >= ReturnObservationGrace &&
                                        (slots[i].pickup == null || !slots[i].pickup.IsHeld);

                if (!observedRoundTrip && !gracefulFallback) continue;

                publicIndices[i] = EmptyIndex;
                claimantIds[i] = -1;
                leftPoolRegion[i] = false;
                mappingReceivedAt[i] = 0f;
                slots[i]._OnPublicReturnObserved();
                changed = true;
            }

            if (changed)
            {
                ApplyPublicStateToSlots();
                RefreshVisibility();
            }
        }

        private void CancelLocalUncommittedReservationsClaimedByPublicMappings()
        {
            // A committed/public Real wins immediately over any other local hover
            // reservation for the same logical Shadow. Without this, Client A can
            // keep its private preview sitting at Shadow A after Client B has actually
            // picked A up through another Real slot, making A appear to remain behind.
            //
            // Do not touch the physical slot that owns the public mapping itself, and
            // never cancel a locally-held/committed Real here. True pickup-vs-pickup
            // duplicates remain the responsibility of ResolveDuplicateLogicalIndices().
            for (int publicSlot = 0; publicSlot < Capacity; publicSlot++)
            {
                int logicalIndex = publicIndices[publicSlot];
                if (logicalIndex == EmptyIndex) continue;

                for (int localSlot = 0; localSlot < Capacity; localSlot++)
                {
                    if (localSlot == publicSlot || slots[localSlot] == null) continue;
                    slots[localSlot].CancelUncommittedReservationIfMatches(logicalIndex);
                }
            }
        }

        private void ApplyPublicStateToSlots()
        {
            for (int i = 0; i < Capacity; i++)
                if (slots[i] != null) slots[i].ApplyPublicMapping(publicIndices[i], claimantIds[i]);
        }

        public void _NotifyLocalReservationChanged()
        {
            if (!Ready()) return;
            anyLocal = true;   // re-counted at the next scan
            RefreshVisibility();
        }

        private void RefreshVisibility()
        {
            if (!runtimeInitialized || unavailableIndices == null || hiddenIndices == null || instances == null) return;
            for (int i = 0; i < unavailableIndices.Length; i++) unavailableIndices[i] = -1;
            for (int i = 0; i < hiddenIndices.Length; i++) hiddenIndices[i] = -1;

            int unavailableWrite = 0;
            int hiddenWrite = 0;

            // Public mappings are both unavailable for new reservations and hidden.
            for (int i = 0; i < Capacity; i++)
            {
                int index = publicIndices[i];
                if (index == EmptyIndex) continue;

                if (unavailableWrite < unavailableIndices.Length)
                    unavailableIndices[unavailableWrite++] = index;
                if (hiddenWrite < hiddenIndices.Length)
                    hiddenIndices[hiddenWrite++] = index;
            }

            // A local ownership request must reserve its logical index immediately so
            // the other hand / next desktop poll cannot request a second real slot for
            // the same Shadow. However, do NOT hide the DMI until ownership has actually
            // been confirmed and the Real has been moved out of the pool.
            for (int i = 0; i < Capacity; i++)
            {
                if (slots[i] == null) continue;

                int reserved = slots[i]._LocalReservedIndex();
                if (reserved >= 0)
                {
                    bool duplicate = false;
                    for (int j = 0; j < unavailableWrite; j++)
                        if (unavailableIndices[j] == reserved) { duplicate = true; break; }
                    if (!duplicate && unavailableWrite < unavailableIndices.Length)
                        unavailableIndices[unavailableWrite++] = reserved;
                }

                int materialized = slots[i]._LocalMaterializedIndex();
                if (materialized >= 0)
                {
                    bool duplicate = false;
                    for (int j = 0; j < hiddenWrite; j++)
                        if (hiddenIndices[j] == materialized) { duplicate = true; break; }
                    if (!duplicate && hiddenWrite < hiddenIndices.Length)
                        hiddenIndices[hiddenWrite++] = materialized;
                }
            }

            instances.SetHiddenInstances(hiddenIndices);
        }

        /// <summary>
        /// Local recycle only. No custom network state is sent. The slot's actual
        /// position is already in the isolated pool via ObjectSync (or was only a
        /// local uncommitted materialization). Toggling inactive -> active gives the
        /// prefab its ordinary OnEnable-style reset hook before reuse.
        /// </summary>
        public void PrepareFreeSlot(int i)
        {
            if (slots == null || i < 0 || i >= Capacity || slots[i] == null) return;
            ShadowPickupSlot s = slots[i];
            GameObject go = s.gameObject;

            Renderer[] renderers = s.renderers;
            bool[] rendererDefaults = s.rendererDefaultEnabled;
            Collider[] colliders = s.colliders;
            bool[] colliderDefaults = s.colliderDefaultEnabled;
            Rigidbody body = s.body;
            VRC_Pickup pickup = s.pickup;
            VRC.SDK3.Components.VRCObjectSync objectSync = s.objectSync;
            Vector3 poolPosition = s.poolPosition;
            Quaternion poolRotation = s.poolRotation;
            Vector3 poolScale = s.poolScale;

            if (pickup != null && pickup.IsHeld) pickup.Drop();
            go.SetActive(false);
            go.transform.SetPositionAndRotation(poolPosition, poolRotation);
            go.transform.localScale = poolScale;

            if (renderers != null && rendererDefaults != null)
                for (int r = 0; r < renderers.Length && r < rendererDefaults.Length; r++)
                    if (renderers[r] != null) renderers[r].enabled = rendererDefaults[r];

            if (colliders != null && colliderDefaults != null)
                for (int c = 0; c < colliders.Length && c < colliderDefaults.Length; c++)
                    if (colliders[c] != null) colliders[c].enabled = colliderDefaults[c];

            if (body != null)
            {
                if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }

                body.isKinematic = true;
                body.useGravity = false;
            }

            go.SetActive(true);
            if (pickup != null) pickup.pickupable = false;
            if (objectSync != null && Networking.IsOwner(go))
            {
                objectSync.SetKinematic(true);
                objectSync.SetGravity(false);
            }
            s._MarkPreparedByPool();
        }

        // ------------------------- Late join snapshot -------------------------
        // 8 x 10-bit slot values = 80 bits. Keep each 30-bit group inside a signed
        // int without crossing sign-bit arithmetic: slots 0-2, 3-5, 6-7.

        private int PackSnapshot3(int a, int b, int c)
        {
            return (a & IndexMask) | ((b & IndexMask) << 10) | ((c & IndexMask) << 20);
        }

        private int PackSnapshot2(int a, int b)
        {
            return (a & IndexMask) | ((b & IndexMask) << 10);
        }

        private bool HasActivePublicMapping()
        {
            if (publicIndices == null) return false;
            for (int i = 0; i < Capacity; i++) if (publicIndices[i] != EmptyIndex) return true;
            return false;
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (!Ready() || !Networking.IsMaster || player == null || player.isLocal) return;
            SendCustomEventDelayedSeconds(nameof(_SendLateJoinSnapshot), LateJoinSnapshotDelay);
        }

        // KNOWN BUG (kaizen #251): Capacity is 4 but this snapshot reads/writes publicIndices[3..7]
        // (out of range -> the master's pool halts when a late joiner arrives while an item is held).
        // This file is frozen (shipped in OsakiConveni 1.0.0) and no product places this pool today.
        // To use it, fix in a new type: loop i < Capacity, 3 slots per int (see OkashiNoIe OkieSnackPool).
        public void _SendLateJoinSnapshot()
        {
            if (!Ready() || !Networking.IsMaster || !HasActivePublicMapping()) return;
            int a = PackSnapshot3(publicIndices[0], publicIndices[1], publicIndices[2]);
            int b = PackSnapshot3(publicIndices[3], publicIndices[4], publicIndices[5]);
            int c = PackSnapshot2(publicIndices[6], publicIndices[7]);
            this.SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetSnapshot), a, b, c);
        }


        [NetworkCallable(maxEventsPerSecond: 10)]
        public void NetSnapshot(int a, int b, int c)
        {
            if (!Ready() || !needsInitialSnapshot) return;
            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (!Utilities.IsValid(caller) || !caller.isMaster) return;

            publicIndices[0] = a & IndexMask;
            publicIndices[1] = (a >> 10) & IndexMask;
            publicIndices[2] = (a >> 20) & IndexMask;
            publicIndices[3] = b & IndexMask;
            publicIndices[4] = (b >> 10) & IndexMask;
            publicIndices[5] = (b >> 20) & IndexMask;
            publicIndices[6] = c & IndexMask;
            publicIndices[7] = (c >> 10) & IndexMask;

            for (int i = 0; i < Capacity; i++)
            {
                if (publicIndices[i] >= instances._InstanceCount()) publicIndices[i] = EmptyIndex;
                // Snapshot state is already-established incumbent state. Give it
                // highest same-slot conflict priority so a stale local reservation
                // cannot replace it while this late joiner is catching up.
                claimantIds[i] = publicIndices[i] == EmptyIndex ? -1 : 0;
                leftPoolRegion[i] = false;
                mappingReceivedAt[i] = Time.time;
            }

            needsInitialSnapshot = false;
            interactionReadyAt = Time.time;
            ResolveDuplicateLogicalIndices();
            CancelLocalUncommittedReservationsClaimedByPublicMappings();
            ApplyPublicStateToSlots();
            RefreshVisibility();
        }
    }
}
