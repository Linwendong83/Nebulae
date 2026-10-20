using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildNebulaeAssetBundle
{
    private const string BundleName = "nebulaenametag";
    private const string NameTagShaderPath = "Assets/Resources/ui/shaders/playerNameTag.shader";

    // Batch entry point: Unity.exe -batchmode -quit -projectPath <project>
    // -executeMethod BuildNebulaeAssetBundle.Build
    public static void Build()
    {
        if (AssetDatabase.LoadAssetAtPath<Shader>(NameTagShaderPath) == null)
        {
            throw new InvalidOperationException("The player name tag shader is missing.");
        }

        var outputDirectory = "Assets/StreamingAssets/AssetBundles";
        Directory.CreateDirectory(outputDirectory);
        // Build only this bundle so importer labels cannot rebuild the chat bundle.
        var builds = new[] { new AssetBundleBuild { assetBundleName = BundleName, assetNames = new[] { NameTagShaderPath } } };
        var manifest = BuildPipeline.BuildAssetBundles(outputDirectory, builds, BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            throw new InvalidOperationException("Unity failed to build the Nebulae asset bundles.");
        }

        var bundle = AssetBundle.LoadFromFile(Path.Combine(outputDirectory, BundleName));
        if (bundle == null)
        {
            throw new InvalidOperationException("The rebuilt nebulaenametag could not be loaded.");
        }

        try
        {
            var shader = bundle.LoadAsset<Shader>(NameTagShaderPath);
            if (shader == null || shader.name != "Nebulae/PlayerNameTag")
            {
                throw new InvalidOperationException("The player name tag shader is missing from nebulaenametag.");
            }
        }
        finally
        {
            bundle.Unload(false);
        }

        Debug.Log("Nebulae asset bundle built and player name tag shader verified.");
    }
}
