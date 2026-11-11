#region

using System.Collections.Generic;
using System.IO;
using NebulaeModel.Networking;
using UnityEngine;

#endregion

namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(9292858076544527323UL)]
internal class CreatePrebuildsRequest
{
    public CreatePrebuildsRequest() { }

    public CreatePrebuildsRequest(int planetId, IList<BuildPreview> buildPreviews, int playerId, string buildToolType,
        int prebuildId)
    {
        AuthorId = playerId;
        PlanetId = planetId;
        BuildToolType = buildToolType;
        using (var writer = new BinaryUtils.Writer())
        {
            writer.BinaryWriter.Write(buildPreviews.Count);
            // A blueprint request carries one preview per pasted building, and every preview names
            // its neighbours through <see cref="BuildPreview.output"/> and <see cref="BuildPreview.input"/>.
            // Resolving those with List.IndexOf inside the loop compared the whole list once per
            // preview, so a large paste spent quadratic time here before a single byte was sent.
            // Index the list once; the wire format is unchanged.
            var indices = BuildPreviewIndices(buildPreviews);
            for (var i = 0; i < buildPreviews.Count; i++)
            {
                SerializeBuildPreview(buildPreviews[i], indices, writer.BinaryWriter);
            }
            BuildPreviewData = writer.CloseAndGetBytes();
        }
        PrebuildId = prebuildId;
    }

    /// <summary>Wraps an already encoded preview payload (the host relaying a client request).</summary>
    /// <remarks>
    /// The bytes describe exactly the previews the host is about to create, so forwarding them keeps
    /// the blueprint off the encoder a second time. Nothing may mutate this array afterwards; the
    /// receiver only ever reads it.
    /// </remarks>
    public static CreatePrebuildsRequest FromEncodedPayload(int planetId, byte[] buildPreviewData,
        int playerId, string buildToolType, int prebuildId)
    {
        var request = new CreatePrebuildsRequest();
        request.PlanetId = planetId;
        request.BuildPreviewData = buildPreviewData;
        request.AuthorId = playerId;
        request.BuildToolType = buildToolType;
        request.PrebuildId = prebuildId;
        return request;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int PlanetId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public byte[] BuildPreviewData { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int AuthorId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public string BuildToolType { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public int PrebuildId { get; set; }

    public bool TryGetBuildPreviews(out List<BuildPreview> buildPreviews)
    {
        buildPreviews = new();

        try
        {
            using var compressed = new BinaryUtils.Reader(BuildPreviewData);
            // LZ4 frames written by BinaryUtils do not declare their decoded size:
            // the decoder's Length is -1 and it cannot seek. Validate remaining
            // bytes against a bounded decoded buffer, never against that sentinel.
            using var payload = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = compressed.BinaryReader.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (payload.Length > PacketCompression.MaxDecompressedMessageBytes - read) return false;
                payload.Write(buffer, 0, read);
            }
            payload.Position = 0;
            using var reader = new BinaryReader(payload);
            var previewCount = reader.ReadInt32();
            if (previewCount <= 0 || previewCount > 100000 || previewCount > payload.Length / 128) return false;
            for (var i = 0; i < previewCount; i++)
            {
                buildPreviews.Add(new BuildPreview());
            }
            for (var i = 0; i < previewCount; i++)
            {
                DeserializeBuildPreview(buildPreviews[i], buildPreviews, reader);
            }
            if (payload.Position != payload.Length) throw new InvalidDataException("Trailing build preview data");
            return true;
        }
        catch (System.Exception e)
        {
            Logger.Log.Warn("DeserializeBuildPreview parse error\n".Translate() + e);
            return false;
        }
    }

    public static void DeserializeBuildPreview(BuildPreview buildPreview, IReadOnlyList<BuildPreview> list, BinaryReader br)
    {
        var outputRef = br.ReadInt32();
        buildPreview.output = outputRef == -1 ? null : list[outputRef];
        var inputRef = br.ReadInt32();
        buildPreview.input = inputRef == -1 ? null : list[inputRef];
        buildPreview.objId = br.ReadInt32();
        var num = br.ReadInt32();
        if (num < 0 || num > 4096 || (long)num * sizeof(int) > br.BaseStream.Length - br.BaseStream.Position)
            throw new InvalidDataException("Invalid nearby power-object count");
        buildPreview.nearestPowerObjId = num == 0 ? null : new int[num];
        for (var i = 0; i < num; i++)
        {
            if (buildPreview.nearestPowerObjId != null)
            {
                buildPreview.nearestPowerObjId[i] = br.ReadInt32();
            }
        }
        buildPreview.coverObjId = br.ReadInt32();
        buildPreview.willRemoveCover = br.ReadBoolean();
        buildPreview.genNearColliderArea2 = br.ReadSingle();
        buildPreview.outputObjId = br.ReadInt32();
        buildPreview.inputObjId = br.ReadInt32();
        buildPreview.outputToSlot = br.ReadInt32();
        buildPreview.inputFromSlot = br.ReadInt32();
        buildPreview.outputFromSlot = br.ReadInt32();
        buildPreview.inputToSlot = br.ReadInt32();
        buildPreview.outputOffset = br.ReadInt32();
        buildPreview.inputOffset = br.ReadInt32();
        buildPreview.needModel = br.ReadBoolean();
        buildPreview.previewIndex = br.ReadInt32();
        buildPreview.bpgpuiModelId = br.ReadInt32();
        buildPreview.bpgpuiModelInstIndex = br.ReadInt32();
        buildPreview.recipeId = br.ReadInt32();
        buildPreview.filterId = br.ReadInt32();
        buildPreview.isConnNode = br.ReadBoolean();

        var modelIndex = br.ReadInt32();
        if (modelIndex >= 0 && modelIndex < LDB.models.modelArray.Length)
        {
            var modelProto = LDB.models.modelArray[modelIndex];
            if (modelProto != null)
            {
                buildPreview.desc = modelProto.prefabDesc;
            }
        }
        if (buildPreview.desc == null)
        {
            throw new System.Exception("DeserializeBuildPreview: can't find modelIndx " + modelIndex);
        }
        var itemId = br.ReadInt32();
        buildPreview.item = LDB.items.Select(itemId);
        if (buildPreview.item == null)
        {
            throw new System.Exception("DeserializeBuildPreview: can't find itemId " + itemId);
        }

        buildPreview.paramCount = br.ReadInt32();
        if (buildPreview.paramCount < 0 || buildPreview.paramCount > 4096 ||
            (long)buildPreview.paramCount * sizeof(int) > br.BaseStream.Length - br.BaseStream.Position)
            throw new InvalidDataException("Invalid build parameter count");
        buildPreview.parameters = new int[buildPreview.paramCount];
        for (var i = 0; i < buildPreview.paramCount; i++)
        {
            buildPreview.parameters[i] = br.ReadInt32();
        }
        buildPreview.lpos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        buildPreview.lpos2 = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        buildPreview.lrot = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        buildPreview.lrot2 = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        buildPreview.tilt = br.ReadSingle();
        buildPreview.condition = (EBuildCondition)br.ReadInt32();
    }

    public static void SerializeBuildPreview(BuildPreview buildPreview, IList<BuildPreview> list, BinaryWriter bw) =>
        SerializeBuildPreview(buildPreview, BuildPreviewIndices(list), bw);

    /// <summary>Maps a preview to its position the way <c>IList.IndexOf</c> would, in one pass.</summary>
    private static Dictionary<BuildPreview, int> BuildPreviewIndices(IList<BuildPreview> list)
    {
        // BuildPreview does not override Equals, so identity is the reference. The comparer is still
        // explicit: a game update that adds value equality must not silently reindex the links.
        var indices = new Dictionary<BuildPreview, int>(list.Count, ReferenceComparer.Instance);
        for (var i = 0; i < list.Count; i++)
            // First occurrence wins, matching IndexOf for the (unlikely) duplicated reference.
            if (list[i] != null && !indices.ContainsKey(list[i])) indices.Add(list[i], i);
        return indices;
    }

    private static int IndexOf(Dictionary<BuildPreview, int> indices, BuildPreview preview) =>
        preview != null && indices.TryGetValue(preview, out var index) ? index : -1;

    private sealed class ReferenceComparer : IEqualityComparer<BuildPreview>
    {
        internal static readonly ReferenceComparer Instance = new();
        public bool Equals(BuildPreview x, BuildPreview y) => ReferenceEquals(x, y);
        public int GetHashCode(BuildPreview obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    public static void SerializeBuildPreview(BuildPreview buildPreview, Dictionary<BuildPreview, int> indices,
        BinaryWriter bw)
    {
        bw.Write(IndexOf(indices, buildPreview.output));
        bw.Write(IndexOf(indices, buildPreview.input));
        bw.Write(buildPreview.objId);
        var num = buildPreview.nearestPowerObjId?.Length ?? 0;
        bw.Write(num);
        for (var i = 0; i < num; i++)
        {
            bw.Write(buildPreview.nearestPowerObjId![i]);
        }
        bw.Write(buildPreview.coverObjId);
        bw.Write(buildPreview.willRemoveCover);
        bw.Write(buildPreview.genNearColliderArea2);
        bw.Write(buildPreview.outputObjId);
        bw.Write(buildPreview.inputObjId);
        bw.Write(buildPreview.outputToSlot);
        bw.Write(buildPreview.inputFromSlot);
        bw.Write(buildPreview.outputFromSlot);
        bw.Write(buildPreview.inputToSlot);
        bw.Write(buildPreview.outputOffset);
        bw.Write(buildPreview.inputOffset);
        bw.Write(buildPreview.needModel);
        bw.Write(buildPreview.previewIndex);
        bw.Write(buildPreview.bpgpuiModelId);
        bw.Write(buildPreview.bpgpuiModelInstIndex);
        bw.Write(buildPreview.recipeId);
        bw.Write(buildPreview.filterId);
        bw.Write(buildPreview.isConnNode);

        //Export the index of PrefabDesc and fetch in LDB.models on the receiving end
        bw.Write(buildPreview.desc.modelIndex);
        //Export the index of ItemProto and featch by LDB.items on the receiving end
        bw.Write(buildPreview.item.ID);

        bw.Write(buildPreview.paramCount);
        for (var i = 0; i < buildPreview.paramCount; i++)
        {
            bw.Write(buildPreview.parameters[i]);
        }
        bw.Write(buildPreview.lpos.x);
        bw.Write(buildPreview.lpos.y);
        bw.Write(buildPreview.lpos.z);
        bw.Write(buildPreview.lpos2.x);
        bw.Write(buildPreview.lpos2.y);
        bw.Write(buildPreview.lpos2.z);
        bw.Write(buildPreview.lrot.x);
        bw.Write(buildPreview.lrot.y);
        bw.Write(buildPreview.lrot.z);
        bw.Write(buildPreview.lrot.w);
        bw.Write(buildPreview.lrot2.x);
        bw.Write(buildPreview.lrot2.y);
        bw.Write(buildPreview.lrot2.z);
        bw.Write(buildPreview.lrot2.w);
        bw.Write(buildPreview.tilt);
        bw.Write((int)buildPreview.condition);
    }
}
