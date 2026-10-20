using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

public static class BuildChatBundle
{
    private const string BundleName = "nebulaebundle";
    private const string AssetsRoot = "Assets/Resources/TextMeshPro";
    private const string SettingsPath = "Assets/Resources/TextMeshPro/TMP Settings.asset";
    private const string UnusedStaticFont = AssetsRoot + "/Fonts/Pretendard-Static.asset";

    // Batch entry point: Unity.exe -batchmode -quit -projectPath <project>
    // -executeMethod BuildChatBundle.Build
    public static void Build()
    {
        // Build files explicitly: folder labels and stale importer labels must not
        // pull the unused static Korean font back into the bundle.
        var assets = AssetDatabase.GetAllAssetPaths()
            .Where(p => p.StartsWith(AssetsRoot + "/", StringComparison.Ordinal)
                        && !AssetDatabase.IsValidFolder(p) && p != UnusedStaticFont
                        && new[] { ".asset", ".shader", ".png", ".ttf", ".txt" }.Contains(Path.GetExtension(p)))
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();

        if (Array.IndexOf(assets, SettingsPath) < 0)
        {
            throw new InvalidOperationException("TMP Settings is missing from the nebulaebundle build inputs.");
        }
        if (AssetDatabase.GetDependencies(assets, true).Contains(UnusedStaticFont))
            throw new InvalidOperationException("A chat resource now depends on the excluded static Korean font.");

        var outputDirectory = "Assets/StreamingAssets/AssetBundles";
        Directory.CreateDirectory(outputDirectory);
        var builds = new[] { new AssetBundleBuild { assetBundleName = BundleName, assetNames = assets } };
        var manifest = BuildPipeline.BuildAssetBundles(outputDirectory, builds, BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            throw new InvalidOperationException("Unity failed to build the nebulaebundle asset bundle.");
        }

        var bundle = AssetBundle.LoadFromFile(Path.Combine(outputDirectory, BundleName));
        if (bundle == null)
        {
            throw new InvalidOperationException("The rebuilt nebulaebundle could not be loaded.");
        }

        try
        {
            var settings = bundle.LoadAsset<TMP_Settings>(SettingsPath);
            if (settings == null)
            {
                throw new InvalidOperationException("TMP Settings is missing from nebulaebundle.");
            }
        }
        finally
        {
            bundle.Unload(false);
        }

        Debug.Log("Nebulae chat bundle built and TMP Settings verified.");
    }
}
