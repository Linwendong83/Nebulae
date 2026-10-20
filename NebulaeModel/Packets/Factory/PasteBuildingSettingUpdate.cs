namespace NebulaeModel.Packets.Factory;

[NebulaeModel.Networking.PacketId(14198338500845348366UL)]
internal class PasteBuildingSettingUpdate
{
    public PasteBuildingSettingUpdate() { }

    public PasteBuildingSettingUpdate(int objectId, BuildingParameters clipboard, int planetId)
    {
        ObjectId = objectId;
        Type = clipboard.type;
        ItemId = clipboard.itemId;
        ModelIndex = clipboard.modelIndex;
        Yaw = clipboard.yaw;
        RecipeType = clipboard.recipeType;
        RecipeId = clipboard.recipeId;
        FilterId = clipboard.filterId;
        Mode0 = clipboard.mode0;
        Mode1 = clipboard.mode1;
        Mode2 = clipboard.mode2;
        Mode3 = clipboard.mode3;
        Parameters = clipboard.parameters;
        InserterItemIds = clipboard.inserterItemIds;
        InserterLengths = clipboard.inserterLengths;
        InserterFilters = clipboard.inserterFilters;
        PlanetId = planetId;
    }

    [NebulaeModel.Networking.WireOrder(0)]
    public int ObjectId { get; set; }
    [NebulaeModel.Networking.WireOrder(1)]
    public BuildingType Type { get; set; }
    [NebulaeModel.Networking.WireOrder(2)]
    public int ItemId { get; set; }
    [NebulaeModel.Networking.WireOrder(3)]
    public int ModelIndex { get; set; }
    [NebulaeModel.Networking.WireOrder(4)]
    public float Yaw { get; set; }
    [NebulaeModel.Networking.WireOrder(5)]
    public ERecipeType RecipeType { get; set; }
    [NebulaeModel.Networking.WireOrder(6)]
    public int RecipeId { get; set; }
    [NebulaeModel.Networking.WireOrder(7)]
    public int FilterId { get; set; }
    [NebulaeModel.Networking.WireOrder(8)]
    public int Mode0 { get; set; }
    [NebulaeModel.Networking.WireOrder(9)]
    public int Mode1 { get; set; }
    [NebulaeModel.Networking.WireOrder(10)]
    public int Mode2 { get; set; }
    [NebulaeModel.Networking.WireOrder(11)]
    public int Mode3 { get; set; }
    [NebulaeModel.Networking.WireOrder(12)]
    public int[] Parameters { get; set; }
    [NebulaeModel.Networking.WireOrder(13)]
    public int[] InserterItemIds { get; set; }
    [NebulaeModel.Networking.WireOrder(14)]
    public int[] InserterLengths { get; set; }
    [NebulaeModel.Networking.WireOrder(15)]
    public int[] InserterFilters { get; set; }
    [NebulaeModel.Networking.WireOrder(16)]
    public int PlanetId { get; set; }

    public BuildingParameters GetBuildingSettings()
    {
        var result = new BuildingParameters
        {
            type = Type,
            itemId = ItemId,
            modelIndex = ModelIndex,
            yaw = Yaw,
            recipeType = RecipeType,
            recipeId = RecipeId,
            filterId = FilterId,
            mode0 = Mode0,
            mode1 = Mode1,
            mode2 = Mode2,
            mode3 = Mode3,
            parameters = Parameters,
            inserterItemIds = InserterItemIds,
            inserterLengths = InserterLengths,
            inserterFilters = InserterFilters
        };
        return result;
    }
}
