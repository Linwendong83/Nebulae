#region

using System.IO;
using System.Reflection;
using NebulaeModel.Logger;
using UnityEngine;

#endregion

namespace NebulaeWorld;

internal static class AssetLoader
{
    private static AssetBundle assetBundle;
    private static AssetBundle nameTagAssetBundle;

    public static AssetBundle AssetBundle
    {
        get
        {
            if (assetBundle != null)
            {
                return assetBundle;
            }
            var pluginfolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (pluginfolder == null)
            {
                Log.Warn("Plugin folder is null, unable to load chat");
                return null;
            }

            var fullAssetPath = Path.Combine(pluginfolder, "nebulaebundle");
            assetBundle = AssetBundle.LoadFromFile(fullAssetPath);

            return assetBundle;
        }
    }

    public static AssetBundle NameTagAssetBundle
    {
        get
        {
            if (nameTagAssetBundle != null)
            {
                return nameTagAssetBundle;
            }

            var pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (pluginFolder == null)
            {
                return null;
            }

            nameTagAssetBundle = AssetBundle.LoadFromFile(Path.Combine(pluginFolder, "nebulaenametag"));
            return nameTagAssetBundle;
        }
    }
}
