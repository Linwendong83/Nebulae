using System;
using System.Collections.Generic;

namespace NebulaeWorld.Factory;

/// <summary>A view of native inventories. Laboratory component ids use a negative local handle.</summary>
internal readonly struct FactoryMachine
{
    private static RecipeExecuteData research;
    public FactoryMachine(PlanetFactory factory, int id) { Factory = factory; Id = id; }
    public PlanetFactory Factory { get; }
    public int Id { get; }
    public bool Laboratory => Id < 0;
    public bool Valid => Id != int.MinValue && Factory?.factorySystem != null && (Laboratory
        ? -Id < Factory.factorySystem.labCursor && Factory.factorySystem.labPool[-Id].id == -Id
        : Id > 0 && Id < Factory.factorySystem.assemblerCursor && Factory.factorySystem.assemblerPool[Id].id == Id);
    public AssemblerComponent View => Laboratory ? ViewOf(Factory.factorySystem.labPool[-Id], Id) : Factory.factorySystem.assemblerPool[Id];
    internal static AssemblerComponent ViewOf(in LabComponent lab, int handle)
    {
        if (lab.researchMode) research ??= new RecipeExecuteData(LabComponent.matrixIds, new int[LabComponent.matrixIds.Length], Array.Empty<int>(), Array.Empty<int>(), 60, 60, false);
        return new AssemblerComponent
        {
            id = handle,
            entityId = lab.entityId,
            recipeId = lab.researchMode ? -1 : lab.recipeId,
            recipeType = ERecipeType.Research,
            recipeExecuteData = lab.researchMode ? research : lab.recipeExecuteData,
            served = lab.researchMode ? lab.matrixServed : lab.served,
            incServed = lab.researchMode ? lab.matrixIncServed : lab.incServed,
            produced = lab.researchMode ? Array.Empty<int>() : lab.produced,
            forceAccMode = lab.forceAccMode,
            time = lab.time
        };
    }
    public void SetRecipe(int recipe)
    {
        if (Laboratory) Factory.factorySystem.labPool[-Id].SetFunction(recipe == -1, Math.Max(0, recipe), recipe == -1 ? GameMain.history.currentTech : 0, Factory.entitySignPool);
        else Factory.factorySystem.assemblerPool[Id].SetRecipe(recipe, Factory.entitySignPool);
    }
    public void SetAcceleration(bool enabled)
    {
        if (Laboratory) Factory.factorySystem.labPool[-Id].forceAccMode = enabled;
        else Factory.factorySystem.assemblerPool[Id].forceAccMode = enabled;
    }
    public IReadOnlyList<FactoryMachine> Stack()
    {
        var result = new List<FactoryMachine> { this };
        if (!Laboratory) return result;
        var seen = new HashSet<int> { View.entityId };
        foreach (var slot in new[] { 14, 15 })
        {
            var entity = View.entityId;
            for (var i = 0; i < 256; i++)
            {
                Factory.ReadObjectConn(entity, slot, out _, out var next, out _);
                if (next <= 0 || next >= Factory.entityCursor || Factory.entityPool[next].id != next || !seen.Add(next)) break;
                var id = Factory.entityPool[next].labId;
                var member = new FactoryMachine(Factory, -id);
                if (id <= 0 || !member.Valid) break;
                result.Add(member); entity = next;
            }
        }
        return result;
    }
}
