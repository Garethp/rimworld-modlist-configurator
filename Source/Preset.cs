using System.Collections.Generic;
using System.IO;
using System.Xml;
using Verse;

namespace ModlistConfigurator;

public readonly struct Preset
{
    public Preset(string name, string label, string version, DirectoryInfo settingsDir, DirectoryInfo rootDir)
    {
        Name = name;
        Label = label;
        Version = version;
        SettingsDirectory = settingsDir;
        RootDirectory = rootDir;
    }

    public string Name { get; }
    public string Label { get; }
    public string Version { get; }

    // Null when the preset mod ships no Settings/ folder (e.g. a mod-list-only preset).
    public DirectoryInfo SettingsDirectory { get; }
    public DirectoryInfo RootDirectory { get; }
}

public class LoadedPreset: IExposable
{
    public LoadedPreset()
    {
    }

    public LoadedPreset(string name, string version)
    {
        Name = name;
        Version = version;
        Configs = new();
    }

    public string Name;
    public string Version;
    public List<StoredConfig> Configs = new();

    // Null for any LoadedPreset saved before mod-list bundling shipped. Callers must treat null as
    // "no mod-list data recorded" and never dereference it directly.
    public List<StoredModEntry> ModList;

    public void AddLoadedConfig(string modId, XmlNode config)
    {
        Configs.Add(new StoredConfig(modId, config));
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref Name, "Name");
        Scribe_Values.Look(ref Version, "Version");
        Scribe_Collections.Look(ref Configs, "Configs", LookMode.Deep);
        Scribe_Collections.Look(ref ModList, "ModList", LookMode.Deep);
    }
}

public class StoredConfig: IExposable
{
    public StoredConfig()
    {
    }

    public StoredConfig(string modId, XmlNode settings)
    {
        ModId = modId;
        settingsString = settings.OuterXml;
    }

    private string settingsString;

    public string ModId;

    public XmlNode Settings
    {
        get
        {
            if (settingsString == null) return null;
            
            var document = new XmlDocument();
            document.LoadXml(settingsString);
            return document.ChildNodes[0];
        }
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref ModId, "ModId");
        Scribe_Values.Look(ref settingsString, "Settings");
    }
}

// Scribable mirror of the Verse-free PresetModEntry (ModListUtils.cs) - IExposable requires Verse,
// so the pure list/diff logic works against PresetModEntry and this type only exists for persistence.
public class StoredModEntry : IExposable
{
    public StoredModEntry()
    {
    }

    public StoredModEntry(PresetModEntry entry)
    {
        PackageId = entry.PackageId;
        Name = entry.Name;
        SteamId = entry.SteamId;
    }

    public string PackageId;
    public string Name;
    public string SteamId;

    public PresetModEntry ToPresetModEntry() => new(PackageId, Name, SteamId);

    public void ExposeData()
    {
        Scribe_Values.Look(ref PackageId, "PackageId");
        Scribe_Values.Look(ref Name, "Name");
        Scribe_Values.Look(ref SteamId, "SteamId");
    }
}