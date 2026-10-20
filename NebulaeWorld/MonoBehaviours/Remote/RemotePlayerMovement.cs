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

    // To have a smooth transition between position updates, we keep a buffer of states received 
    // and once the buffer is full, we start replaying the states from the oldest to the newest state.
    // This will make sure player movement is still smooth in high latency cases and even if there are dropped packets.
    private readonly Snapshot[] snapshotBuffer = new Snapshot[BUFFERED_SNAPSHOT_COUNT];
    private Transform bodyTransform;

    private GameObject playerDot;
    private GameObject playerName;
    private TextMesh playerNameText;
    private Transform playerDotTransform;
    private Transform playerNameTransform;
    private Transform minimapCamTransform;
    private Transform rootTransform;
    public string Username { get; set; }
    public ushort PlayerID { get; set; }
    public int LocalStarId { get; set; }

    private void Awake()
    {
        rootTransform = GetComponent<Transform>();
        bodyTransform = rootTransform.Find("Model");

        localPlanetId = -1;
        absolutePosition = Vector3.zero;
    }

    private void Update()
    {
        // Wait for the entire buffer to be full before starting to interpolate the player position
        if (snapshotBuffer[0].Timestamp == 0)
        {
            return;
        }

        // update player dot on minimap if on same planet
        if (playerDot != null && playerName != null && localPlanetId == GameMain.mainPlayer.planetId)
        {
            var textMesh = playerNameText;

            // SetActive and TextMesh.color both cost a native round trip; only apply them
            // when the state actually changes.
            if (!playerDot.activeSelf) playerDot.SetActive(true);
            if (!playerName.activeSelf) playerName.SetActive(true);

            if (textMesh != null)
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

            playerDotTransform.localPosition = rootTransform.position * (float)(0.5 / GameMain.localPlanet.realRadius);

            playerNameTransform.localPosition = playerDotTransform.localPosition;
            playerNameTransform.rotation = minimapCamTransform.rotation;

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

        snapshotBuffer[snapshotBuffer.Length - 1] = new Snapshot
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LocalPlanetId = movement.LocalPlanetId,
            LocalPlanetPosition = movement.LocalPlanetPosition,
            UPosition = movement.UPosition,
            VelocityL = movement.VelocityL,
            Rotation = Quaternion.Euler(movement.Rotation.ToVector3()),
            BodyRotation = Quaternion.Euler(movement.BodyRotation.ToVector3())
        };
    }

    /// <summary>
    ///     Place a player who is in another star system, where only their map position is known.
    /// </summary>
    internal void UpdateMapPosition(PlayerMapPosition position)
    {
        localPlanetId = position.LocalPlanetId;
        if (position.LocalPlanetId > 0)
        {
            LocalStarId = position.LocalPlanetId / 100;
        }
        absolutePosition = new VectorLF3(position.UPosition.x, position.UPosition.y, position.UPosition.z);
    }

    private void MoveInterpolated(Snapshot previous, Snapshot current, float ratio)
    {
        var previousRelativePosition = GetRelativePosition(previous);
        var currentRelativePosition = GetRelativePosition(current);
        var previousAbsolutePosition = GetAbsolutePosition(previous);
        var currentAbsolutePosition = GetAbsolutePosition(current);

        localPlanetId = current.LocalPlanetId;
        if (current.LocalPlanetId > 0) LocalStarId = current.LocalPlanetId / 100;

        rootTransform.SetPositionAndRotation(Vector3.Lerp(previousRelativePosition, currentRelativePosition, ratio),
            Quaternion.Slerp(previous.Rotation, current.Rotation, ratio));
        bodyTransform.rotation = Quaternion.Slerp(previous.BodyRotation, current.BodyRotation, ratio);

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
