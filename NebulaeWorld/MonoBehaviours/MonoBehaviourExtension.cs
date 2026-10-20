#region

using UnityEngine;

#endregion

namespace NebulaeWorld.MonoBehaviours;

internal static class MonoBehaviourExtension
{
    public static T AddComponentIfMissing<T>(this GameObject go) where T : Component
    {
        var component = go.GetComponent<T>();
        if (component == null)
        {
            component = go.AddComponent<T>();
        }
        return component;
    }
}
