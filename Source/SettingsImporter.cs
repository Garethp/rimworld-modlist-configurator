using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using JetBrains.Annotations;
using Verse;

namespace ModlistConfigurator;

public class SettingsImporter
{
    private static Dictionary<string, Preset> Presets = new();

    public List<Preset> GetPresets()
    {
        return Presets.Values.ToList();
    }

    public SettingsImporter()
    {
        LoadPresets();
    }

    private static string GetSettingsFilename(string modIdentifier, string modHandleName) => Path.Combine(
        GenFilePaths.ConfigFolderPath,
        GenText.SanitizeFilename(string.Format("Mod_{0}_{1}.xml", (object)modIdentifier, (object)modHandleName)));

    /**
     * God, this feels so dirty. I'm sorry.
     *
     * What we're doing here is grabbing all the mods that are enabled and manually scanning their Defs for our own
     * PresetDefs. We then load the values from PresetDefs by grabbing them straight out of the XMLNode instead of
     * using the DefDatabase. This is because the DefDatabase isn't actually loaded and populated until **after** the
     * constructors for mods have been called and we need to modify the settings before that happens.
     *
     * Is there a better way to do this? Probably. But I couldn't see one, considering the order that Rimworld loads
     * Mods.
     */
    public void LoadPresets()
    {
        if (Presets.Count > 0) return;

        var stopWatch = new Stopwatch();
        stopWatch.Start();

        var mods = LoadedModManager.RunningMods;
        List<Preset> presets = new();

        var defClassName = new ModlistPresetDef().GetType().FullName;
        
        // Grab all that are enabled
        foreach (var modContentPack in mods)
        {
            // Grab all Defs that have a valid `<Defs>` node with a `<ModlistConfigurator.ModlistPresetDef>` node under it
            // @TODO: Maybe the `<ModlistConfigurator.ModlistPresetDef>` won't be the first node? We could check all children if we want...
            var presetDefs = modContentPack.LoadDefs()
                .Where(loadableAsset => loadableAsset.xmlDoc?.FirstChild?.FirstChild is not null)
                .Select(loadableAsset => loadableAsset.xmlDoc.FirstChild.FirstChild)
                .Where(defNode => defNode.Name == defClassName)
                .ToList();
            
            foreach (var presetDef in presetDefs)
            {
                // For each `<ModlistConfigurator.ModlistPresetDef>` node, grab the defName, presetLabel and version
                var name = XmlUtils.GetChildNodeByName(presetDef, "defName")?.InnerText;
                var label = XmlUtils.GetChildNodeByName(presetDef, "presetLabel")?.InnerText;
                var version = XmlUtils.GetChildNodeByName(presetDef, "version")?.InnerText;
                var subDir = XmlUtils.GetChildNodeByName(presetDef, "subDir")?.InnerText;

                if (name is null || label is null || version is null) continue;

                var fullPath = subDir == null ? modContentPack.ModMetaData.RootDir : new DirectoryInfo(Path.Combine(modContentPack.RootDir, subDir));

                // Get the directory where the preset settings are stored
                var presetLocation = fullPath.GetDirectories()
                    .FirstOrDefault(dir => dir.Name == "Settings");

                if (presetLocation is null)
                {
                    if (fullPath.Name == "Settings" && fullPath.Exists)
                    {
                        presetLocation = fullPath;
                    }
                    else
                    {
                        Log.Error($"No settings directory found for preset {name}");
                        continue;
                    }
                }

                presets.Add(new Preset(name, label, version, presetLocation));
            }
        }

        foreach (var presetDef in presets)
        {
            if (Presets.ContainsKey(presetDef.Label)) continue;

            Presets.Add(presetDef.Name, presetDef);
        }
        
        stopWatch.Stop();
        
        Log.Message($"This took {stopWatch.Elapsed.TotalMilliseconds} ms ({stopWatch.Elapsed.Seconds} seconds) to filter all defs");
    }

    /**
     * This is our "safe" way of loading updated settings. How is it safe?
     *
     * First, we generate a diff between the last version of a preset and the new version
     * Then, we generate a diff between the current user settings and last version of the preset
     *
     * Given that, we know exactly which settings were changed in the update and which settings the user has changed
     * since the previous version.
     *
     * We can then call `NodesAreCompatible` which will check if our update will override any settings that the user has
     * changed manually. If it doesn't override settings which the user has changed, we can safely apply the update.
     *
     * If it does, we should probably let the user know that there's a conflict and let them decide what to do.
     * @TODO: Let the user know there's a conflict and let them decide what to do.
     */
    [CanBeNull]
    public LoadedPreset AutoUpdate(string presetName, LoadedPreset previousSettings)
    {
        if (!Presets.ContainsKey(presetName)) return null;
        var preset = Presets[presetName];

        var loadedPreset = new LoadedPreset(preset.Name, preset.Version);

        foreach (var file in preset.SettingsDirectory.GetFiles())
        {
            var match = Regex.Match(file.Name, @"^Mod_(.*)_(.*).xml$");
            if (!match.Success) continue;

            var filePath = file.FullName;
            var modId = match.Groups[1].Value;
            var modName = match.Groups[2].Value;

            var currentPreset = previousSettings.Configs.First(config => config.ModId == modId)?.Settings;

            var currentUserSettings = GetSettingsFromFile(GetSettingsFilename(modId, modName));
            var newPresetSettings = GetSettingsFromFile(filePath)!.DocumentElement;

            var userDiff = XmlUtils.GenerateDiff(currentPreset, currentUserSettings!.DocumentElement);
            var updatesToApply = XmlUtils.GenerateDiff(currentPreset, newPresetSettings);

            if (updatesToApply is null) continue;
            if (userDiff is not null && !XmlUtils.NodesAreCompatible(userDiff, updatesToApply))
            {
                // @TODO: Store this and let the user know there's a conflict
                continue;
            }

            var newSettings = XmlUtils.MergeNodes(updatesToApply, currentUserSettings!.DocumentElement);

            currentUserSettings.ReplaceChild(currentUserSettings.ImportNode(newSettings, true),
                currentUserSettings.DocumentElement);

            loadedPreset.AddLoadedConfig(modId, currentUserSettings.DocumentElement);

            var saveStream =
                (Stream)new FileStream(GetSettingsFilename(modId, modName), FileMode.Create,
                    FileAccess.Write, FileShare.None);

            var writer = XmlWriter.Create(saveStream, new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "\t"
            });

            currentUserSettings.WriteTo(writer);
            writer.Close();
            saveStream.Close();
        }

        return loadedPreset;
    }

    /**
     * While `AutoUpdate` is our "safe" way of loading updated settings, this is our sledgehammer. Screw whatever
     * the user settings were, just write our own settings in.
     */
    [CanBeNull]
    public LoadedPreset OverwriteSettings(string presetName)
    {
        if (!Presets.ContainsKey(presetName)) return null;
        var preset = Presets[presetName];

        var loadedPreset = new LoadedPreset(preset.Name, preset.Version);

        foreach (var file in preset.SettingsDirectory.GetFiles())
        {
            var match = Regex.Match(file.Name, @"^Mod_(.*)_(.*).xml$");
            if (!match.Success) continue;

            var filePath = file.FullName;
            var modId = match.Groups[1].Value;
            var modName = match.Groups[2].Value;

            using var input = new StreamReader(filePath);
            using var reader = new XmlTextReader(input);

            var xmlDocument = new XmlDocument();
            xmlDocument.Load(reader);
            var settings = xmlDocument.DocumentElement;

            loadedPreset.AddLoadedConfig(modId, settings);

            var saveStream =
                (Stream)new FileStream(GetSettingsFilename(modId, modName), FileMode.Create,
                    FileAccess.Write, FileShare.None);

            var writer = XmlWriter.Create(saveStream, new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "\t"
            });

            xmlDocument.WriteTo(writer);
            writer.Close();
            saveStream.Close();
        }

        return loadedPreset;
    }

    public void MergeSettings(string presetName)
    {
        if (!Presets.ContainsKey(presetName)) return;
        var preset = Presets[presetName];

        foreach (var file in preset.SettingsDirectory.GetFiles())
        {
            var match = Regex.Match(file.Name, @"^Mod_(.*)_(.*).xml$");
            if (!match.Success) continue;

            var filePath = file.FullName;
            var modId = match.Groups[1].Value;
            var modName = match.Groups[2].Value;

            var settingToImport = GetSettingsFromFile(filePath)!.DocumentElement;
            var currentDocument = GetSettingsFromFile(GetSettingsFilename(modId, modName));

            var newSettings = XmlUtils.MergeNodes(settingToImport, currentDocument!.DocumentElement);

            currentDocument.ReplaceChild(currentDocument.ImportNode(newSettings, true),
                currentDocument.DocumentElement);

            var saveStream =
                (Stream)new FileStream(GetSettingsFilename(modId, modName), FileMode.Create,
                    FileAccess.Write, FileShare.None);

            var writer = XmlWriter.Create(saveStream, new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "\t"
            });

            currentDocument.WriteTo(writer);
            writer.Close();
            saveStream.Close();
        }
    }

    public bool ShouldImport(string importFilePath, string modId, string modName)
    {
        var importSettings = GetSettingsFromFile(importFilePath)!.DocumentElement;
        var currentSettings = GetSettingsFromFile(GetSettingsFilename(modId, modName))!.DocumentElement;

        if (importSettings is null) return false;
        if (currentSettings is null) return true;

        return !XmlUtils.NodesAreEqual(importSettings, currentSettings);
    }

    [CanBeNull]
    public static XmlDocument GetSettingsFromFile(string fileLocation)
    {
        if (!File.Exists(fileLocation)) return null;

        using var reader = new StreamReader(fileLocation);
        using var xmlReader = new XmlTextReader(reader);
        var document = new XmlDocument();
        document.Load(xmlReader);
        xmlReader.Close();
        reader.Close();

        return document;
    }

    public List<string> ModsToImport(string presetName)
    {
        if (!Presets.ContainsKey(presetName)) return new();

        List<string> modsToImport = new();

        foreach (var file in Presets[presetName].SettingsDirectory.GetFiles())
        {
            var match = Regex.Match(file.Name, @"^Mod_(.*)_(.*).xml$");
            if (!match.Success) continue;

            var filePath = file.FullName;
            var modId = match.Groups[1].Value;
            var modName = match.Groups[2].Value;

            if (!ShouldImport(filePath, modId, modName)) continue;

            modsToImport.Add(modName);
        }

        return modsToImport;
    }
}
