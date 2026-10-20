using UnityEngine;

namespace NebulaePatcher.MonoBehaviours;

public sealed class RespawnWreckageMaterialOwner : MonoBehaviour
{
    private Material[] materials;

    public void Initialize(Material[] ownedMaterials) => materials = ownedMaterials;

    private void OnDestroy()
    {
        if (materials == null) return;
        foreach (var material in materials)
            if (material != null) Destroy(material);
        materials = null;
    }
}
