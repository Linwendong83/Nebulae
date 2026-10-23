using System;

namespace NebulaeWorld.Factory;

// Native build/terrain entry points temporarily borrow the local player's factory view.
// Restore the enclosing operation even when native code throws or requests are nested.
internal sealed class FactoryOperationScope : IDisposable
{
    private readonly FactoryManager manager;
    private readonly int author, planet;
    private readonly PlanetFactory eventFactory, actionFactory, noneFactory, toolFactory;
    private readonly PlanetData specifyPlanet;
    private readonly PlanetData actionPlanet, nonePlanet, toolPlanet;
    private readonly PlanetAuxData aux;
    private readonly GPUInstancingManager graphics;
    private readonly PlayerAction_Build action;
    private readonly BuildTool tool;
    private readonly PlanetPhysics physics;
    private readonly NearColliderLogic near;
    private readonly Mecha mecha;
    private readonly float buildArea;
    public FactoryOperationScope(PlayerAction_Build action = null, BuildTool tool = null)
    {
        manager = Multiplayer.Session.Factories;
        author = manager.PacketAuthor; planet = manager.TargetPlanet; eventFactory = manager.EventFactory;
        graphics = GameMain.gpuiManager; specifyPlanet = graphics?.specifyPlanet;
        this.action = action; this.tool = tool;
        if (action != null)
        {
            actionFactory = action.factory; noneFactory = action.noneTool?.factory;
            actionPlanet = action.planet; nonePlanet = action.noneTool?.planet; aux = action.planetAux;
            physics = action.planetPhysics; near = action.nearcdLogic;
        }
        if (tool != null) { toolFactory = tool.factory; toolPlanet = tool.planet; }
        mecha = GameMain.mainPlayer?.mecha;
        if (mecha != null) buildArea = mecha.buildArea;
    }
    public void Dispose()
    {
        manager.PacketAuthor = author; manager.TargetPlanet = planet; manager.EventFactory = eventFactory;
        if (graphics != null) graphics.specifyPlanet = specifyPlanet;
        if (action != null)
        {
            action.factory = actionFactory;
            action.planet = actionPlanet; action.planetAux = aux;
            if (action.noneTool != null) { action.noneTool.factory = noneFactory; action.noneTool.planet = nonePlanet; }
            action.planetPhysics = physics; action.nearcdLogic = near;
        }
        if (tool != null) { tool.factory = toolFactory; tool.planet = toolPlanet; }
        if (mecha != null) mecha.buildArea = buildArea;
    }
}
