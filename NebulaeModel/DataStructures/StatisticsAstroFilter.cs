namespace NebulaeModel.DataStructures;

internal static class StatisticsAstroFilter
{
    public static int Resolve(int filter, int localPlanetId, int localStarAstroId) =>
        filter != 0 ? filter : localPlanetId > 0 ? localPlanetId : localStarAstroId > 0 ? localStarAstroId : -1;
}
