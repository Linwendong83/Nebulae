#region

using System;
using NebulaeModel.DataStructures;
using NebulaeModel.Packets.Players;
using NebulaeWorld.MonoBehaviours.Local;
using UnityEngine;

#endregion

namespace NebulaeWorld.MonoBehaviours.Remote;

public class RemotePlayerMovement : MonoBehaviour
{
    private const int BUFFERED_SNAPSHOT_COUNT = 3;
    private const double INTERPOLATION_TIME = 1000 / (double)LocalPlayerMovement.SEND_RATE * (BUFFERED_SNAPSHOT_COUNT - 1);

    public int localPlanetId;
    public VectorLF3 absolutePosition;

    // The pose stream (PlayerMovementDisplay) and the star-map stream (PlayerMapPosition) update at
    // different cadences and only one of them is delivered to a given peer. They therefore need
    // separate storage: writing the map packet into the pose fields lets the next interpolation
    // frame overwrite it with a stale snapshot, which is what made star-map markers jump.
    private bool hasMapPosition;
    private int mapLocalPlanetId;
    private int mapLocalStarId;
    private VectorLF3 mapAbsolutePosition;
    private long mapTimestamp;
    private long knownTimestamp;

    // To have a smooth transition between position updates, we keep a buffer of states received 
    // and once the buffer is full, we start replaying the states from the oldest to the newest state.
    // This will make sure player movement is still smooth in high latency cases and even if packets are dropped.
    private readonly Snapshot[] snapshotBuffer = new Snapshot[BUFFERED_SNAPSHOT_COUNT];
    private Transform bodyTransform;
    private RemotePlayerEffects effects;

    private GameObject playerDot;
    private GameObject playerName;
    private TextMesh playerNameText;
    private Transform playerDotTransform;
    private Transform playerNameTransform;
    private Transform minimapCamTransform;
    private Transform rootTransform;
    private bool visibilityApplied;
    public string Username { get; set; }
    public ushort PlayerID { get; set; }
    public int LocalStarId { get; set; }
    public bool IsVisibleToLocalPlayer { get; private set; }

    private RemotePlayerEffects Effects
    {
        get
        {
            if (effects == null) effects = GetComponent<RemotePlayerEffects>();
            return effects;
        }
        set => effects = value;
    }

    private bool HasPose => snapshotBuffer[0].Timestamp != 0;

    private void Awake()
    {
        rootTransform = GetComponent<Transform>();
        bodyTransform = rootTransform.Find("Model");

        localPlanetId = -1;
        absolutePosition = Vector3.zero;
    }

    /// <summary>
    ///     Seeds the model from the roster snapshot. A player in another star system only ever sends
    ///     map positions, so without this seed their body would stay at the prefab origin forever.
    /// </summary>
    internal void Initialize(PlayerData data)
    {
        if (data == null || rootTransform == null) return;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var planet = data.LocalPlanetId > 0 && GameMain.galaxy != null
            ? GameMain.galaxy.PlanetById(data.LocalPlanetId)
            : null;
        var localPosition = data.LocalPlanetPosition.ToVector3();
        var universal = new VectorLF3(data.UPosition.x, data.UPosition.y, data.UPosition.z);
        if (planet != null && IsZero(universal) && !IsZero(localPosition))
        {
            universal = planet.uPosition + (VectorLF3)(planet.runtimeRotation * localPosition);
        }
        if (planet != null && IsZero(localPosition) && !IsZero(universal))
        {
            localPosition = (Vector3)Maths.QInvRotateLF(planet.runtimeRotation, universal - planet.uPosition);
        }

        var rotation = Quaternion.Euler(data.Rotation.ToVector3());
        var bodyRotation = Quaternion.Euler(data.BodyRotation.ToVector3());
        var snapshot = new Snapshot
        {
            Timestamp = timestamp,
            LocalPlanetId = data.LocalPlanetId,
            LocalPlanetPosition = new Float3(localPosition),
            UPosition = new Double3(universal.x, universal.y, universal.z),
            VelocityL = default,
            Rotation = rotation,
            BodyRotation = bodyRotation
        };
        for (var i = 0; i < snapshotBuffer.Length; i++) snapshotBuffer[i] = snapshot;

        localPlanetId = data.LocalPlanetId;
        LocalStarId = data.LocalStarId > 0 ? data.LocalStarId
            : data.LocalPlanetId > 0 ? data.LocalPlanetId / 100 : -1;
        absolutePosition = universal;
        // The roster pose is only a fallback until the first streamed sample arrives. Do not
        // count it as a map packet: a same-planet local position is more precise than its
        // universal counterpart and the map must not outrank it by timestamp.
        hasMapPosition = false;
        mapLocalPlanetId = data.LocalPlanetId;
        mapLocalStarId = LocalStarId;
        mapAbsolutePosition = universal;
        mapTimestamp = 0;
        knownTimestamp = timestamp;

        if (GameMain.data != null)
        {
            rootTransform.SetPositionAndRotation(GetRelativePosition(snapshot), rotation);
        }
        else
        {
            rootTransform.rotation = rotation;
        }
        if (bodyTransform != null) bodyTransform.rotation = bodyRotation;
        RefreshWorldVisibility();
    }

    private void Update()
    {
        if (!rootTransform) return;

        // The model is only a world-space body when the remote player is in the same planet/star
        // frame. A player represented by PlayerMapPosition alone must not be left at the origin.
        RefreshWorldVisibility();

        // update player dot on minimap if on same planet
        if (playerDot != null && playerName != null && IsVisibleToLocalPlayer &&
            GameMain.localPlanet != null && localPlanetId == GameMain.localPlanet.id)
        {
            var textMesh = playerNameText;

            // SetActive and TextMesh.color both cost a native round trip; only apply them
            // when the state actually changes.
            if (!playerDot.activeSelf) playerDot.SetActive(true);
            if (!playerName.activeSelf) playerName.SetActive(true);

            if (textMesh != null && minimapCamTransform != null)
            {
                var isFront = Vector3.Dot(minimapCamTransform.localPosition,
                    rootTransform.localPosition) > 0;
                var color = textMesh.color;
                var targetAlpha = isFront ? 1f : 0.2f;
                if (Mathf.Abs(color.a - targetAlpha) > 1e-4f)
                {
                    textMesh.color = new Color(color.r, color.g, color.b, targetAlpha);
                }
            }

            playerDotTransform.localPosition = rootTransform.position *
                (float)(0.5 / GameMain.localPlanet.realRadius);

            playerNameTransform.localPosition = playerDotTransform.localPosition;
            if (minimapCamTransform != null) playerNameTransform.rotation = minimapCamTransform.rotation;

            if (textMesh != null && textMesh.text != Username)
            {
                var uiSailIndicator_targetText = UIRoot.instance.uiGame.sailIndicator.targetText;

                textMesh.font = uiSailIndicator_targetText.font;
                textMesh.text = Username;
                textMesh.fontSize = 20;
            }
        }
        else if (playerDot != null && playerName != null)
        {
            if (playerDot.activeSelf) playerDot.SetActive(false);
            if (playerName.activeSelf) playerName.SetActive(false);
        }

        // Wait for the entire buffer to be full before starting to interpolate the player position
        if (snapshotBuffer[0].Timestamp == 0)
        {
            return;
        }

        // The map packet is the freshest sample for a player in another star. Running the stale
        // pose through the interpolator would overwrite absolutePosition and the map marker source.
        if (hasMapPosition && mapTimestamp >= snapshotBuffer[snapshotBuffer.Length - 1].Timestamp) return;

        var renderTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - INTERPOLATION_TIME;

        for (var i = 0; i < snapshotBuffer.Length - 1; ++i)
        {
            var t1 = snapshotBuffer[i].Timestamp;
            var t2 = snapshotBuffer[i + 1].Timestamp;

            if (renderTime <= t2 && renderTime >= t1)
            {
                var total = t2 - t1;
                var reminder = renderTime - t1;
                var ratio = total > 0 ? reminder / total : 1;

                // We interpolate to the appropriate position between our 2 known snapshot
                MoveInterpolated(snapshotBuffer[i], snapshotBuffer[i + 1], (float)ratio);
                break;
            }

            if (i == snapshotBuffer.Length - 2 && renderTime > t2)
            {
                // This will skip interpolation and will snap to the most recent position.
                MoveInterpolated(snapshotBuffer[i], snapshotBuffer[i + 1], 1);
            }
        }
    }

    private void OnEnable()
    {
        var minimapControl = UIRoot.instance?.uiGame?.planetGlobe?.minimapControl;
        var uiSailIndicator_targetText = UIRoot.instance?.uiGame?.sailIndicator?.targetText;
        if (minimapControl?.playerDot == null || uiSailIndicator_targetText == null)
        {
            return;
        }
        var parent = minimapControl.playerDot.transform.parent;
        playerDot = Instantiate(minimapControl.playerDot.gameObject, parent, false);
        // TextMesh cannot share a GameObject with MeshFilter. Destroy is deferred until the
        // end of the frame, so cloning the dot and removing its filter still conflicts here.
        playerName = new GameObject("playerName(Clone)", typeof(TextMesh))
        {
            layer = minimapControl.playerDot.gameObject.layer
        };
        playerName.transform.SetParent(parent, false);
        playerName.transform.localScale = minimapControl.playerDot.transform.localScale;

        // Cache everything the per-frame minimap update touches. The objects above live
        // exactly as long as this component is enabled, so the references stay valid.
        playerNameText = playerName.GetComponent<TextMesh>();
        playerDotTransform = playerDot.transform;
        playerNameTransform = playerName.transform;
        minimapCamTransform = minimapControl.cam.transform;
        playerDotTransform.localScale = 0.02f * Vector3.one;

        // targetText is a uGUI Text, so use its font material.
        var meshRenderer = playerName.GetComponent<MeshRenderer>();
        if (meshRenderer != null && uiSailIndicator_targetText.font != null)
        {
            meshRenderer.sharedMaterial = uiSailIndicator_targetText.font.material;
        }

        playerName.SetActive(true);
    }

    private void OnDisable()
    {
        if (playerDot != null)
        {
            Destroy(playerDot);
        }
        if (playerName != null)
        {
            Destroy(playerName);
        }
    }

    internal void UpdatePosition(PlayerMovementDisplay movement)
    {
        if (!rootTransform)
        {
            return;
        }

        for (var i = 0; i < snapshotBuffer.Length - 1; ++i)
        {
            snapshotBuffer[i] = snapshotBuffer[i + 1];
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        snapshotBuffer[snapshotBuffer.Length - 1] = new Snapshot
        {
            Timestamp = timestamp,
            LocalPlanetId = movement.LocalPlanetId,
            LocalPlanetPosition = movement.LocalPlanetPosition,
            UPosition = movement.UPosition,
            VelocityL = movement.VelocityL,
            Rotation = Quaternion.Euler(movement.Rotation.ToVector3()),
            BodyRotation = Quaternion.Euler(movement.BodyRotation.ToVector3())
        };
        ApplyKnownLocation(movement.LocalPlanetId, null, timestamp);
    }

    /// <summary>
    ///     Place a player who is in another star system, where only their map position is known.
    /// </summary>
    internal void UpdateMapPosition(PlayerMapPosition position)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        mapLocalPlanetId = position.LocalPlanetId;
        mapLocalStarId = position.LocalStarId;
        if (mapLocalStarId <= 0 && mapLocalPlanetId > 0) mapLocalStarId = mapLocalPlanetId / 100;
        mapAbsolutePosition = new VectorLF3(position.UPosition.x, position.UPosition.y, position.UPosition.z);
        if (IsZero(mapAbsolutePosition) && mapLocalPlanetId > 0 && GameMain.galaxy != null)
        {
            var planet = GameMain.galaxy.PlanetById(mapLocalPlanetId);
            if (planet != null) mapAbsolutePosition = planet.uPosition;
        }
        hasMapPosition = true;
        mapTimestamp = timestamp;
        absolutePosition = mapAbsolutePosition;
        ApplyKnownLocation(mapLocalPlanetId, mapLocalStarId, timestamp);
        RefreshWorldVisibility();
    }

    private void ApplyKnownLocation(int planetId, int? starId, long timestamp)
    {
        if (timestamp < knownTimestamp) return;
        knownTimestamp = timestamp;
        localPlanetId = planetId;
        if (starId.HasValue) LocalStarId = starId.Value;
        else if (planetId > 0) LocalStarId = planetId / 100;
    }

    private void MoveInterpolated(Snapshot previous, Snapshot current, float ratio)
    {
        var previousRelativePosition = GetRelativePosition(previous);
        var currentRelativePosition = GetRelativePosition(current);
        var previousAbsolutePosition = GetAbsolutePosition(previous);
        var currentAbsolutePosition = GetAbsolutePosition(current);

        ApplyKnownLocation(current.LocalPlanetId, null, current.Timestamp);

        rootTransform.SetPositionAndRotation(Vector3.Lerp(previousRelativePosition, currentRelativePosition, ratio),
            Quaternion.Slerp(previous.Rotation, current.Rotation, ratio));
        if (bodyTransform != null) bodyTransform.rotation = Quaternion.Slerp(previous.BodyRotation, current.BodyRotation, ratio);

        absolutePosition = Vector3.Lerp(previousAbsolutePosition, currentAbsolutePosition, ratio);
    }

    private static Vector3 GetRelativePosition(Snapshot snapshot)
    {
        if (GameMain.data == null)
        {
            return Vector3.zero;
        }

        // If we are on a local planet and the remote player is on the same local planet, we use the LocalPlanetPosition that is more precise.
        if (GameMain.localPlanet != null && GameMain.localPlanet.id == snapshot.LocalPlanetId)
        {
            return snapshot.LocalPlanetPosition.ToVector3();
        }

        // If the remote player is in space, we need to calculate the remote player relative position from our local player position.
        var uPosition = new VectorLF3(snapshot.UPosition.x, snapshot.UPosition.y, snapshot.UPosition.z);
        return Maths.QInvRotateLF(GameMain.data.relativeRot, uPosition - GameMain.data.relativePos);
    }

    private static Vector3 GetAbsolutePosition(Snapshot snapshot)
    {
        return new VectorLF3(snapshot.UPosition.x, snapshot.UPosition.y, snapshot.UPosition.z);
    }

    internal Snapshot GetLastPosition()
    {
        return snapshotBuffer[snapshotBuffer.Length - 1];
    }

    /// <summary>
    ///     Resolves the best position known for the star map. The pose and map streams are compared by
    ///     their receive time so a stale snapshot cannot pull a distant marker back to an old planet.
    /// </summary>
    internal bool TryGetStarmapPose(out VectorLF3 position, out int planetId, out Vector3 forward)
    {
        position = default;
        planetId = 0;
        forward = bodyTransform != null ? bodyTransform.forward : Vector3.forward;
        var last = snapshotBuffer[snapshotBuffer.Length - 1];
        var hasPose = last.Timestamp != 0;

        if (hasMapPosition && (!hasPose || mapTimestamp >= last.Timestamp))
        {
            position = mapAbsolutePosition;
            planetId = mapLocalPlanetId;
        }
        else if (hasPose)
        {
            planetId = last.LocalPlanetId;
            if (planetId > 0 && GameMain.galaxy != null)
            {
                var planet = GameMain.galaxy.PlanetById(planetId);
                position = planet != null
                    ? planet.uPosition + (VectorLF3)Maths.QRotate(planet.runtimeRotation, last.LocalPlanetPosition.ToVector3())
                    : new VectorLF3(last.UPosition.x, last.UPosition.y, last.UPosition.z);
            }
            else
            {
                position = new VectorLF3(last.UPosition.x, last.UPosition.y, last.UPosition.z);
            }
        }
        else
        {
            return false;
        }

        if (IsZero(position)) return false;
        if (double.IsNaN(position.x) || double.IsNaN(position.y) || double.IsNaN(position.z) ||
            double.IsInfinity(position.x) || double.IsInfinity(position.y) || double.IsInfinity(position.z)) return false;
        if (forward.sqrMagnitude < 1e-8f) forward = Vector3.forward;
        return true;
    }

    private void RefreshWorldVisibility()
    {
        var visible = ComputeWorldVisibility();
        if (visibilityApplied && visible == IsVisibleToLocalPlayer) return;
        visibilityApplied = true;
        IsVisibleToLocalPlayer = visible;
        if (bodyTransform != null && bodyTransform.gameObject.activeSelf != visible)
            bodyTransform.gameObject.SetActive(visible);
        var currentEffects = Effects;
        if (currentEffects != null) currentEffects.SetWorldVisible(visible);
    }

    private bool HasKnownPosition
    {
        get
        {
            if (hasMapPosition && (!IsZero(mapAbsolutePosition) || mapLocalPlanetId > 0)) return true;
            if (!HasPose) return false;
            var last = snapshotBuffer[snapshotBuffer.Length - 1];
            return !IsZero(last.UPosition) || last.LocalPlanetId > 0;
        }
    }

    private bool ComputeWorldVisibility()
    {
        if (!HasPose || !HasKnownPosition || GameMain.data == null || GameMain.mainPlayer == null) return false;
        // A body is only meaningful inside the receiver's current star frame. Players in other
        // stars are represented by PlayerMapPosition alone and would otherwise sit at the local
        // world origin, which is the local player while flying in space.
        var localStarId = GameMain.localStar?.id ?? GameMain.localPlanet?.star?.id ?? -1;
        return localStarId > 0 ? LocalStarId == localStarId : LocalStarId <= 0;
    }

    private static bool IsZero(VectorLF3 value) => value.sqrMagnitude < 1e-8;
    private static bool IsZero(Vector3 value) => value.sqrMagnitude < 1e-8f;
    private static bool IsZero(Double3 value) => value.x * value.x + value.y * value.y + value.z * value.z < 1e-8;

    internal struct Snapshot
    {
        public long Timestamp { get; set; }
        public int LocalPlanetId { get; set; }
        public Float3 LocalPlanetPosition { get; set; }
        public Double3 UPosition { get; set; }
        public Float3 VelocityL { get; set; }
        public Quaternion Rotation { get; set; }
        public Quaternion BodyRotation { get; set; }
    }
}
