using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Components;

namespace com.osakishokai.vpm.shadowpickup
{
    /// <summary>
    /// One reusable real Pickup slot.
    ///
    /// v0.1.5 rule: a Real slot is NEVER moved out of the isolated pool until this
    /// client has actually become its ObjectSync owner. Ownership request and physical
    /// materialization are separate local states. This prevents non-owner Transform
    /// writes from fighting VRCObjectSync.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class ShadowPickupSlot : UdonSharpBehaviour
    {
        private const int ReservationNone = 0;
        private const int ReservationRequestingOwner = 1;
        private const int ReservationMaterialized = 2;
        private const float OwnershipRequestTimeout = 0.75f;
        private const float OwnershipRetryBlock = 0.50f;

        public ShadowPickupPool pool;
        public int slot;
        public VRC_Pickup pickup;
        public VRCObjectSync objectSync;
        public Rigidbody body;

        [Header("Baked payload state")]
        public Renderer[] renderers;
        public bool[] rendererDefaultEnabled;
        public Collider[] colliders;
        public bool[] colliderDefaultEnabled;
        public bool bodyDefaultKinematic;
        public bool bodyDefaultGravity;

        [Header("Isolated pool spawn")]
        public Vector3 poolPosition;
        public Quaternion poolRotation = Quaternion.identity;
        public Vector3 poolScale = Vector3.one;

        // Read by the pool every frame to decide whether this slot needs a per-frame tick (localIndex >= 0) or only the idle scan.
        // A public field read is far cheaper than a cross-behaviour call; all writes stay in this class.
        [System.NonSerialized] public int localIndex = -1;
        private byte currentVariantId = 0;

        public byte _CurrentVariantId()
        {
            return currentVariantId;
        }

        private int localHand;
        private int reservationState = ReservationNone;
        private bool committed;
        private bool localHeld;
        private bool returnRequested;
        private bool respawnIssued;
        private bool pickupBroadcastPending;
        private bool remotePreviewHidden;
        private float droppedAt = -1f;
        private float nextAction;
        private float ownershipRequestStartedAt;
        private float retryBlockedUntil;

        /// <summary>
        /// Index reserved by this client, including the short ownership-request phase.
        /// Used only to prevent this client from allocating another Real to the same
        /// logical Shadow while ownership is still in flight.
        /// </summary>
        public int _LocalReservedIndex()
        {
            return localIndex;
        }

        /// <summary>
        /// Index that already has a Real physically materialized at the Shadow.
        /// REQUESTING_OWNER intentionally returns -1 so the DMI remains visible until
        /// the Real can safely be moved by its confirmed owner.
        /// </summary>
        public int _LocalMaterializedIndex()
        {
            return reservationState == ReservationMaterialized ? localIndex : -1;
        }

        public bool _IsLocallyFree()
        {
            return localIndex < 0 && !returnRequested && reservationState == ReservationNone && Time.time >= retryBlockedUntil;
        }

        /// <summary>
        /// A slot is globally reusable only when ObjectSync has actually placed its
        /// Real back in the isolated pool. publicIndex==Empty is not enough because
        /// another client may currently own an uncommitted hover reservation.
        /// </summary>
        public bool _IsPhysicallyInPool()
        {
            return Vector3.Distance(transform.position, poolPosition) <= ShadowPickupPool.ReturnRegionRadius;
        }

        public bool IsLocallyReservedForHand(int hand)
        {
            return localIndex >= 0 && !committed && localHand == hand;
        }

        /// <summary>
        /// Starts a reservation. Do not move the Real here. SetOwner may not have
        /// completed yet; the slot stays at the isolated pool until ownership is
        /// positively confirmed by IsOwner / OnOwnershipTransferred.
        /// </summary>
        public void RequestMaterialization(int index, int hand)
        {
            if (pool == null || pickup == null || index < 0 || index >= pool.instances._InstanceCount()) return;
            if (!_IsLocallyFree()) return;

            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;

            localIndex = index;
            localHand = hand;
            reservationState = ReservationRequestingOwner;
            committed = false;
            localHeld = false;
            returnRequested = false;
            respawnIssued = false;
            pickupBroadcastPending = false;
            droppedAt = -1f;
            nextAction = 0f;
            ownershipRequestStartedAt = Time.time;
            pickup.pickupable = false;

            // Reserve the logical index locally immediately, but keep its DMI visible
            // until ownership is confirmed and the Real is actually materialized.
            pool._NotifyLocalReservationChanged();

            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(local, gameObject);

            // SetOwner can already be effective locally, or the slot may still be
            // owned by this player from an earlier use. Never assume a frame count;
            // only the actual IsOwner result is authoritative for moving the Real.
            if (Networking.IsOwner(gameObject))
                _CompleteMaterializationAfterOwnership();
        }

        private bool ReservationStillWanted()
        {
            if (pool == null || localIndex < 0 || !Utilities.IsValid(Networking.LocalPlayer)) return false;

            if (!Networking.LocalPlayer.IsUserInVR())
                return pool.IsDesktopAimingAt(localIndex);

            VRCPlayerApi.TrackingDataType type = localHand == 1
                ? VRCPlayerApi.TrackingDataType.LeftHand
                : VRCPlayerApi.TrackingDataType.RightHand;
            Vector3 hand = Networking.LocalPlayer.GetTrackingData(type).position;
            return Vector3.Distance(hand, pool.instances.PositionAt(localIndex)) <= ShadowPickupPool.ReleaseDistance;
        }

        /// <summary>
        /// The only pre-pickup path that moves a Real slot out of the pool.
        /// </summary>
        public void _CompleteMaterializationAfterOwnership()
        {
            if (reservationState != ReservationRequestingOwner || localIndex < 0) return;
            if (!Networking.IsOwner(gameObject)) return;

            // The user may have moved away while ownership was in flight. In that
            // case ownership may remain local, but the Real never leaves the pool.
            if (!ReservationStillWanted())
            {
                CancelPendingOwnershipRequest();
                return;
            }

            RestorePayloadDefaults();
            transform.localScale = pool.instances.ScaleAt(localIndex);
            transform.SetPositionAndRotation(pool.instances.PositionAt(localIndex), pool.instances.RotationAt(localIndex));

            if (body != null)
            {
                // Kinematic bodies reject velocity writes (engine warning), so zero first.
                if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }

                body.isKinematic = true;
                body.useGravity = false;
            }

            if (objectSync != null)
            {
                objectSync.SetKinematic(true);
                objectSync.SetGravity(false);
            }

            reservationState = ReservationMaterialized;
            ownershipRequestStartedAt = 0f;
            pickup.pickupable = true;

            currentVariantId = pool != null && pool.instances != null ? pool.instances.GetVariantId(localIndex) : (byte)0;
            NotifyVariantApplied(currentVariantId);

            pool._NotifyLocalReservationChanged();
        }

        public void ApplyPublicMapping(int index, int claimantId)
        {
            if (pool == null) return;

            if (index == ShadowPickupPool.EmptyIndex)
            {
                // Normal return cleanup is performed by _OnPublicReturnObserved()
                // after ObjectSync actually reaches the pool. A duplicate loser can
                // be unmapped BEFORE its physical Respawn completes, so do not
                // recycle a still-returning local slot merely because the public
                // mapping has already been cleared.
                if (localIndex < 0 && pickup != null) pickup.pickupable = false;
                return;
            }

            if (localIndex >= 0 && localIndex != index)
            {
                // Someone else committed the same Real slot to another Shadow while
                // this client was only reserving/previewing it, or this client lost a race.
                if (localHeld || Networking.IsOwner(gameObject))
                    ForceReturnForConflict();
                else
                    CancelLocalMaterialization();
            }

            if (localIndex == index)
                committed = true;

            currentVariantId = pool != null && pool.instances != null ? pool.instances.GetVariantId(index) : (byte)0;
            NotifyVariantApplied(currentVariantId);

            // Remote clients never need to reconstruct interaction state; they only
            // need the real payload visible/interactable while ObjectSync supplies
            // transform/ownership. The prefab's own component state handles the rest.
            // This runs for every mapped slot on any pickup/return, so only undo the
            // hiding this slot applied itself. The payload may have turned off its own
            // Colliders (e.g. an eaten item waiting to respawn) and must keep that.
            if (remotePreviewHidden)
            {
                RestorePayloadDefaults();
                remotePreviewHidden = false;
            }
            if (pickup != null) pickup.pickupable = !returnRequested;
        }

        public void CancelUncommittedReservationIfMatches(int logicalIndex)
        {
            if (logicalIndex < 0 || localIndex != logicalIndex) return;
            if (committed || localHeld || (pickup != null && pickup.IsHeld)) return;

            CancelLocalMaterialization();
        }

        public void _HandleDuplicateLoss()
        {
            // DMI mapping is already cleared deterministically on every client.
            // Only the actual local ObjectSync owner is able/required to send the
            // losing physical duplicate home.
            if (Networking.IsOwner(gameObject)) ForceReturnForConflict();
            else if (!localHeld) CancelLocalMaterialization();
        }

        public void _OnPublicReturnObserved()
        {
            ClearLocalState();
            pool.PrepareFreeSlot(slot);
        }

        public void _TickLocal()
        {
            if (pool == null || pickup == null || !Utilities.IsValid(Networking.LocalPlayer)) return;

            // An uncommitted hover reservation is a private interaction preview.
            // ObjectSync still moves the Real on every client, but non-owners must not
            // render/collide with that preview until NetPickup commits the mapping.
            UpdateRemoteReservationPresentation();

            if (localIndex < 0) return;

            // Ownership handoff and materialization are deliberately separate.
            // While waiting, the Real remains at the pool and must NOT run ordinary
            // distance-from-Real release logic (the pool is ~10 km away by design).
            if (reservationState == ReservationRequestingOwner)
            {
                if (!ReservationStillWanted())
                {
                    CancelPendingOwnershipRequest();
                    return;
                }

                if (Networking.IsOwner(gameObject))
                {
                    _CompleteMaterializationAfterOwnership();
                    return;
                }

                // A current owner may reject this request because the Real is already
                // reserved on another client. Do not wedge this hand/ray forever.
                // After a short timeout, release the local request and briefly avoid
                // reselecting the same physical slot while ObjectSync catches up.
                if (Time.time - ownershipRequestStartedAt >= OwnershipRequestTimeout)
                    CancelPendingOwnershipRequest();
                return;
            }

            // Duplicate self-healing can clear the public mapping before the losing
            // physical slot has completed its Respawn. Since there is intentionally
            // no release event, the local owner finishes that recycle by observing
            // its own slot back at the isolated pool position.
            if (returnRequested && pool.PublicIndexAt(slot) == ShadowPickupPool.EmptyIndex && Networking.IsOwner(gameObject))
            {
                if (Vector3.Distance(transform.position, poolPosition) <= ShadowPickupPool.ReturnRegionRadius)
                {
                    ClearLocalState();
                    pool.PrepareFreeSlot(slot);
                    pool._NotifyLocalReservationChanged();
                }
                return;
            }

            if (pickupBroadcastPending)
            {
                _ConfirmPickupOwnership();
                if (pickupBroadcastPending) return;
            }

            if (pickup.IsHeld) return;

            if (committed)
            {
                // Only the physical ObjectSync owner performs the five-second
                // automatic return. Everyone else simply observes ObjectSync.
                if (!Networking.IsOwner(gameObject) || returnRequested || droppedAt < 0f) return;
                if (Time.time - droppedAt >= ShadowPickupPool.ReturnDelay) BeginReturn();
                return;
            }

            if (localHeld || returnRequested || Time.time < nextAction) return;

            bool isVr = Networking.LocalPlayer.IsUserInVR();
            bool away;
            if (!isVr)
            {
                // Pool.IsDesktopAimingAt uses the RAW ray, not the available-target
                // ray. The latter excludes this reservation and caused v0.1.3 to
                // cancel itself every poll (pool <-> Shadow position flicker).
                away = !pool.IsDesktopAimingAt(localIndex);
            }
            else
            {
                away = pool.HandDistance(transform.position) > ShadowPickupPool.ReleaseDistance;
            }

            if (away)
            {
                nextAction = Time.time + 0.5f;
                CancelLocalMaterialization();
            }
        }

        private void UpdateRemoteReservationPresentation()
        {
            if (pool == null) return;

            // A committed slot is public. ApplyPublicMapping() restores normal payload
            // state, but also recover here if ObjectSync/network event ordering differs.
            if (pool.PublicIndexAt(slot) != ShadowPickupPool.EmptyIndex)
            {
                if (remotePreviewHidden)
                {
                    RestorePayloadDefaults();
                    remotePreviewHidden = false;
                }
                return;
            }

            // The reservation owner must see and interact with its own Real.
            if (Networking.IsOwner(gameObject))
            {
                if (remotePreviewHidden && localIndex >= 0)
                {
                    RestorePayloadDefaults();
                    remotePreviewHidden = false;
                }
                return;
            }

            bool outsidePool = Vector3.Distance(transform.position, poolPosition) > ShadowPickupPool.ReturnExitDistance;
            if (outsidePool)
            {
                if (!remotePreviewHidden)
                {
                    HidePayloadForRemoteReservation();
                    remotePreviewHidden = true;
                }
            }
            else if (remotePreviewHidden)
            {
                // Returned uncommitted preview: prepare the local presentation for the
                // next owner without making this remote pooled object interactable.
                RestorePayloadDefaults();
                pickup.pickupable = false;
                remotePreviewHidden = false;
            }
        }

        private void HidePayloadForRemoteReservation()
        {
            if (pickup != null) pickup.pickupable = false;
            if (renderers != null)
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i] != null) renderers[i].enabled = false;
            if (colliders != null)
                for (int i = 0; i < colliders.Length; i++)
                    if (colliders[i] != null) colliders[i].enabled = false;
        }

        private void RestorePayloadDefaults()
        {
            if (renderers != null && rendererDefaultEnabled != null)
                for (int i = 0; i < renderers.Length && i < rendererDefaultEnabled.Length; i++)
                    if (renderers[i] != null) renderers[i].enabled = rendererDefaultEnabled[i];

            if (colliders != null && colliderDefaultEnabled != null)
                for (int i = 0; i < colliders.Length && i < colliderDefaultEnabled.Length; i++)
                    if (colliders[i] != null) colliders[i].enabled = colliderDefaultEnabled[i];
        }

        private void NotifyVariantApplied(byte variantId)
        {
            // Update MaterialPropertyBlock on renderers for _VariantID
            MaterialPropertyBlock mpb = new MaterialPropertyBlock();
            mpb.SetFloat("_VariantID", (float)variantId);
            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null)
                    {
                        renderers[i].SetPropertyBlock(mpb);
                    }
                }
            }

            // Notify custom product scripts on this Real GameObject if they implement _ApplyShadowVariant
            UdonSharpBehaviour[] behaviours = GetComponentsInChildren<UdonSharpBehaviour>(true);
            if (behaviours != null)
            {
                for (int i = 0; i < behaviours.Length; i++)
                {
                    if (behaviours[i] != null && behaviours[i] != this)
                    {
                        behaviours[i].SetProgramVariable("shadowVariantId", (int)variantId);
                        behaviours[i].SendCustomEvent("_ApplyShadowVariant");
                    }
                }
            }
        }

        private void HidePayloadForReturn()
        {
            if (pickup != null) pickup.pickupable = false;
            if (renderers != null)
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i] != null) renderers[i].enabled = false;
            if (colliders != null)
                for (int i = 0; i < colliders.Length; i++)
                    if (colliders[i] != null) colliders[i].enabled = false;
        }

        private void CancelPendingOwnershipRequest()
        {
            if (reservationState != ReservationRequestingOwner || committed || localHeld) return;
            // The Real was never moved, so do NOT call PrepareFreeSlot here. Ownership
            // may have changed, but the physical slot is already at its pool spawn.
            // Briefly block this physical slot locally so a denied ownership request
            // can fall through to another pool slot on the next polls.
            retryBlockedUntil = Time.time + OwnershipRetryBlock;
            ClearLocalState();
            pool._NotifyLocalReservationChanged();
        }

        private void CancelLocalMaterialization()
        {
            if (committed || localHeld) return;

            if (reservationState == ReservationRequestingOwner)
            {
                CancelPendingOwnershipRequest();
                return;
            }

            // A materialized preview is only created after ownership confirmation,
            // so the normal path is safe to put back into the isolated pool locally.
            // If ownership was unexpectedly stolen meanwhile, never fight ObjectSync
            // with a non-owner Transform write; simply clear local control state.
            bool ownsSlot = Networking.IsOwner(gameObject);
            ClearLocalState();
            if (ownsSlot)
                pool.PrepareFreeSlot(slot);
            pool._NotifyLocalReservationChanged();
        }

        private void ClearLocalState()
        {
            localIndex = -1;
            localHand = 0;
            reservationState = ReservationNone;
            committed = false;
            localHeld = false;
            returnRequested = false;
            respawnIssued = false;
            pickupBroadcastPending = false;
            droppedAt = -1f;
            nextAction = 0f;
            ownershipRequestStartedAt = 0f;
            if (pickup != null) pickup.pickupable = false;
        }

        private void BeginReturn()
        {
            if (returnRequested || localIndex < 0 || !Networking.IsOwner(gameObject)) return;
            returnRequested = true;
            localHeld = false;
            if (pickup != null && pickup.IsHeld) pickup.Drop();
            HidePayloadForReturn();
            TryIssueRespawn();
        }

        private void ForceReturnForConflict()
        {
            if (!Networking.IsOwner(gameObject))
            {
                if (!localHeld) CancelLocalMaterialization();
                return;
            }
            if (localIndex < 0)
            {
                // A remote mapping can exist even if this client never locally
                // materialized it. Recover the public index only for return bookkeeping.
                int publicIndex = pool.PublicIndexAt(slot);
                if (publicIndex != ShadowPickupPool.EmptyIndex) localIndex = publicIndex;
            }
            if (localIndex < 0) return;
            BeginReturn();
        }

        private void TryIssueRespawn()
        {
            if (respawnIssued || localIndex < 0 || !Networking.IsOwner(gameObject)) return;

            if (body != null && !body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            if (objectSync != null)
            {
                objectSync.SetKinematic(true);
                objectSync.SetGravity(false);
            }

            if (objectSync != null)
                objectSync.Respawn();
            else
            {
                transform.SetPositionAndRotation(poolPosition, poolRotation);
                transform.localScale = poolScale;
            }
            respawnIssued = true;
        }

        public void _MarkPreparedByPool()
        {
            remotePreviewHidden = false;
            ClearLocalState();
        }

        public override void OnPickup()
        {
            if (pool == null || pickup == null || returnRequested)
            {
                if (pickup != null) pickup.Drop();
                return;
            }

            if (localIndex < 0)
            {
                int publicIndex = pool.PublicIndexAt(slot);
                if (publicIndex == ShadowPickupPool.EmptyIndex)
                {
                    pickup.Drop();
                    return;
                }
                localIndex = publicIndex;
                committed = true;
            }

            localHeld = true;
            droppedAt = -1f;

            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(local, gameObject);

            pickupBroadcastPending = pool.PublicIndexAt(slot) != localIndex;
            _ConfirmPickupOwnership();
        }

        public void _ConfirmPickupOwnership()
        {
            if (!pickupBroadcastPending && (!localHeld || !Networking.IsOwner(gameObject))) return;
            if (!localHeld || pickup == null || !pickup.IsHeld)
            {
                pickupBroadcastPending = false;
                return;
            }

            if (!Networking.IsOwner(gameObject))
            {
                SendCustomEventDelayedFrames(nameof(_ConfirmPickupOwnership), 1);
                return;
            }

            // Restore the Prefab's own Rigidbody settings; the Real stayed kinematic
            // without gravity only while pooled.
            if (objectSync != null)
            {
                objectSync.SetKinematic(bodyDefaultKinematic);
                objectSync.SetGravity(bodyDefaultGravity);
            }
            else if (body != null)
            {
                body.isKinematic = bodyDefaultKinematic;
                body.useGravity = bodyDefaultGravity;
            }

            committed = true;
            if (pickupBroadcastPending)
            {
                pickupBroadcastPending = false;
                pool.BroadcastPickup(slot, localIndex);
            }
        }

        public override void OnDrop()
        {
            localHeld = false;
            if (committed && Networking.IsOwner(gameObject)) droppedAt = Time.time;
        }

        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            if (player != null && player.isLocal && reservationState == ReservationRequestingOwner)
            {
                _CompleteMaterializationAfterOwnership();
                return;
            }

            // If ownership lands on this client because the previous owner left,
            // the slot is not held and no local pickup state exists. Give normal
            // pickup transfer a moment to settle, then send an orphaned item home.
            if (player != null && player.isLocal && pool != null &&
                pool.PublicIndexAt(slot) != ShadowPickupPool.EmptyIndex &&
                (pickup == null || !pickup.IsHeld) && localIndex < 0)
            {
                SendCustomEventDelayedSeconds(nameof(_CheckOrphanAfterOwnershipTransfer), 1f);
                return;
            }

            // The previous owner may also have left after materializing a Shadow but
            // before picking it up. Nothing was published (public mapping is Empty), yet
            // the Real still sits at the Shadow, outside the pool, so no client would
            // ever treat it as free again. That private preview is not handed over:
            // the new owner sends it home so the Shadow simply becomes a Shadow again.
            if (player != null && player.isLocal && pool != null &&
                _RuleShouldRecoverUnclaimed(true, reservationState, localIndex, pool.PublicIndexAt(slot),
                    pickup != null && pickup.IsHeld, _IsPhysicallyInPool(), returnRequested))
            {
                SendCustomEventDelayedSeconds(nameof(_CheckUnclaimedAfterOwnershipTransfer), 1f);
            }
        }

        /// <summary>
        /// Pure rule (no side effects): may this client take over an ownerless, materialized-but-
        /// never-picked-up Real and send it back to the pool? Only when this client is the owner,
        /// has no local state of its own for the slot, nothing is published for it, nobody holds
        /// it, and it is physically away from the pool.
        /// </summary>
        public bool _RuleShouldRecoverUnclaimed(bool isOwner, int reservation, int localIdx, int publicIdx,
            bool held, bool inPool, bool returning)
        {
            return isOwner &&
                   reservation == ReservationNone &&
                   localIdx < 0 &&
                   publicIdx == ShadowPickupPool.EmptyIndex &&
                   !held &&
                   !inPool &&
                   !returning;
        }

        public void _CheckUnclaimedAfterOwnershipTransfer()
        {
            if (pool == null) return;
            if (!_RuleShouldRecoverUnclaimed(Networking.IsOwner(gameObject), reservationState, localIndex,
                    pool.PublicIndexAt(slot), pickup != null && pickup.IsHeld, _IsPhysicallyInPool(), returnRequested))
                return;

            // BeginReturn needs a logical index only as bookkeeping for the return; use the
            // Shadow this Real is standing on.
            int nearest = pool.instances.NearestIndex(transform.position);
            localIndex = nearest >= 0 ? nearest : 0;
            committed = false;
            BeginReturn();
        }

        public void _CheckOrphanAfterOwnershipTransfer()
        {
            if (pool == null || !Networking.IsOwner(gameObject) ||
                pool.PublicIndexAt(slot) == ShadowPickupPool.EmptyIndex ||
                (pickup != null && pickup.IsHeld) || localIndex >= 0) return;

            localIndex = pool.PublicIndexAt(slot);
            committed = true;
            BeginReturn();
        }

        public override bool OnOwnershipRequest(VRCPlayerApi requester, VRCPlayerApi newOwner)
        {
            // Only the CURRENT owner may veto. The requesting client can receive this
            // callback too; its local ReservationRequestingOwner state must not make it
            // reject its own request.
            if (!Networking.IsOwner(gameObject)) return true;

            // An uncommitted hover/proximity reservation is an ownership lock. Stealing
            // it would make another user's Real slide from Shadow A to Shadow B. Once
            // committed/picked up, use normal VRC Pickup ownership semantics instead.
            if (!committed && localIndex >= 0 &&
                (reservationState == ReservationRequestingOwner || reservationState == ReservationMaterialized))
                return false;

            return true;
        }
    }
}
