#region

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NebulaeModel.Attributes;
using NebulaeModel.Logger;
using NebulaeModel.Utils;

#endregion

namespace NebulaeModel;

internal static class Config
{
    private const string OPTION_SAVE_FILE = "nebulae.cfg";
    private const string SECTION_NAME = "Nebulae - Settings";

    public static Action OnConfigApplied;

    private static ConfigFile configFile;

    public static PluginInfo ModInfo { get; set; }
    private static string displayVersion = string.Empty;
    public static string ModVersion
    {
        get => !ReferenceEquals(ModInfo?.Instance, null) ? displayVersion : string.Empty;
        internal set => displayVersion = value ?? string.Empty;
    }
    public static MultiplayerOptions Options { get; set; }
    public static CommandLineOptions CommandLineOptions { get; set; }


    public static bool LoadOptions()
    {
        Options = new MultiplayerOptions();

        try
        {
            var path = LegacyDataMigration.ConfigFile(Paths.ConfigPath, OPTION_SAVE_FILE, "nebula.cfg", true);
            configFile = new ConfigFile(path, true);

            var properties = AccessTools.GetDeclaredProperties(typeof(MultiplayerOptions));
            var configBindMethod = typeof(ConfigFile)
                .GetMethods()
                .Where(m => m.Name == nameof(ConfigFile.Bind))
                .First(m => m.IsGenericMethod && m.GetParameters().Length == 4);

            foreach (var prop in properties)
            {
                var entry = configBindMethod.MakeGenericMethod(prop.PropertyType).Invoke(configFile,
                    new[] { SECTION_NAME, ConfigKeyOf(prop), prop.GetValue(Options), null });

                var entryType = typeof(ConfigEntry<>).MakeGenericType(prop.PropertyType);
                prop.SetValue(Options, AccessTools.Property(entryType, "Value").GetValue(entry));
            }
        }
        catch (Exception e)
        {
            Log.Error($"Could not load {OPTION_SAVE_FILE}", e);
            return false;
        }

        return true;
    }

    public static bool LoadCommandLineOptions()
    {
        try
        {
            CommandLineOptions = new CommandLineOptions();
            CommandLineOptions.ParseArgs(Environment.GetCommandLineArgs());
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Error when parsing command line: \n" + e);
            return false;
        }
    }

    public static bool SaveOptions()
    {
        try
        {
            var properties = AccessTools.GetDeclaredProperties(typeof(MultiplayerOptions));
            foreach (var prop in properties)
            {
                var key = new ConfigDefinition(SECTION_NAME, ConfigKeyOf(prop));
                configFile[key].BoxedValue = prop.GetValue(Options);
            }
            configFile.Save();
        }
        catch (Exception e)
        {
            Log.Error($"Could not save {OPTION_SAVE_FILE}", e);
            return false;
        }

        return true;
    }

    // The key has to stay stable across releases while the property name is free to be
    // renamed by the release build, so it is read from the attribute and never derived.
    private static string ConfigKeyOf(PropertyInfo prop)
    {
        var attribute = prop.GetCustomAttribute<ConfigKeyAttribute>();
        if (attribute == null)
            throw new InvalidOperationException($"Multiplayer option {prop.Name} has no config key");
        return attribute.Key;
    }
}
