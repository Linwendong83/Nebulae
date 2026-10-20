#region

#endregion

namespace NebulaeModel.Packets.Universe;

/// <summary>Several solar sails thrown in one frame, in a single packet.</summary>
[NebulaeModel.Networking.PacketId(3382947561029384756UL)]
internal class DysonSailBatchPacket
{
    /// <summary>Ceiling on the sails one packet may carry, above any per-frame throw count.</summary>
    public const int MaxSails = 256;

    public DysonSailBatchPacket() { }

    public DysonSailBatchPacket(int starIndex, int[] orbitId, long[] expiryTime, float[] st, float[] px, float[] py,
        float[] pz, float[] vx, float[] vy, float[] vz, float[] gs)
    {
        StarIndex = starIndex;
        OrbitId = orbitId;
        ExpiryTime = expiryTime;
        this.st = st;
        this.px = px;
        this.py = py;
        this.pz = pz;
        this.vx = vx;
        this.vy = vy;
        this.vz = vz;
        this.gs = gs;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int StarIndex { get; set; }

    [NebulaeModel.Networking.WireOrder(1)]
    public int[] OrbitId { get; set; }

    [NebulaeModel.Networking.WireOrder(2)]
    public long[] ExpiryTime { get; set; }

    [NebulaeModel.Networking.WireOrder(3)]
    public float[] st { get; set; }

    [NebulaeModel.Networking.WireOrder(4)]
    public float[] px { get; set; }

    [NebulaeModel.Networking.WireOrder(5)]
    public float[] py { get; set; }

    [NebulaeModel.Networking.WireOrder(6)]
    public float[] pz { get; set; }

    [NebulaeModel.Networking.WireOrder(7)]
    public float[] vx { get; set; }

    [NebulaeModel.Networking.WireOrder(8)]
    public float[] vy { get; set; }

    [NebulaeModel.Networking.WireOrder(9)]
    public float[] vz { get; set; }

    [NebulaeModel.Networking.WireOrder(10)]
    public float[] gs { get; set; }

    /// <summary>
    ///     Every parallel array must hold the same number of sails, or the packet describes nothing the
    ///     receiver can apply. Checked before anything is written.
    /// </summary>
    public bool IsConsistent()
    {
        return OrbitId != null && ExpiryTime != null && st != null && px != null && py != null && pz != null &&
               vx != null && vy != null && vz != null && gs != null &&
               OrbitId.Length == ExpiryTime.Length && OrbitId.Length == st.Length &&
               OrbitId.Length == px.Length && OrbitId.Length == py.Length && OrbitId.Length == pz.Length &&
               OrbitId.Length == vx.Length && OrbitId.Length == vy.Length && OrbitId.Length == vz.Length &&
               OrbitId.Length == gs.Length && OrbitId.Length <= MaxSails;
    }
}
