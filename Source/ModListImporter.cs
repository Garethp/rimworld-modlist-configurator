using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using JetBrains.Annotations;
using Verse;

namespace ModlistConfigurator;

public enum ModListApplyMode
{
    Exact,
    Merge
}

/// <summary>Everything a caller needs to show the user before committing an apply.</summary>
public class ModListApplyPreview
{
    public List<string> Candidate;
    public ModListDiff Diff;
}

/// <summary>
/// Verse-facing glue around the pure list/diff logic in ModListUtils: reads a preset's ModList.xml,
/// talks to ModsConfig/ModLister, and writes ModList.xml back out for export.
/// </summary>
public static class ModListImporter
{
    private const string FileName = "ModList.xml";

    public static bool HasModList(Preset preset)
    {
        return preset.RootDirectory != null &&
               File.Exists(Path.Combine(preset.RootDirectory.FullName, FileName));
    }

    [CanBeNull]
    public static List<PresetModEntry> ReadModList(Preset preset)
    {
        if (!HasModList(preset)) return null;

        var document = SettingsImporter.GetSettingsFromFile(Path.Combine(preset.RootDirectory.FullName, FileName));
        return document is null ? null : ModListUtils.ParseModList(document.DocumentElement);
    }

    private static string Lower(string packageId) => packageId.ToLowerInvariant();

    private static HashSet<string> GetInstalledIdsLower()
    {
        return new HashSet<string>(ModLister.AllInstalledMods.Select(mod => Lower(mod.PackageId)));
    }

    private static List<string> GetCurrentActiveLower()
    {
        return ModsConfig.ActiveModsInLoadOrder.Select(mod => Lower(mod.PackageId)).ToList();
    }

    /// <summary>
    /// Mods that must never be dropped by an apply: Core, any currently-active official expansion,
    /// this mod itself, and the preset's own owning mod - otherwise Exact mode could uninstall the very
    /// preset it's applying. Resolved from live ModMetaData rather than hardcoded, so this stays correct
    /// if this mod is ever forked/renamed.
    /// </summary>
    private static HashSet<string> GetForceKeepSetLower(Preset preset)
    {
        var forceKeep = new HashSet<string>(ModsConfig.ActiveModsInLoadOrder
            .Where(mod => mod.IsCoreMod || mod.Official)
            .Select(mod => Lower(mod.PackageId)));

        if (!string.IsNullOrEmpty(ModlistConfigurator.OwnPackageId))
        {
            forceKeep.Add(Lower(ModlistConfigurator.OwnPackageId));
        }

        var presetOwnerPackageId = GetPresetOwnerPackageId(preset);
        if (presetOwnerPackageId != null)
        {
            forceKeep.Add(Lower(presetOwnerPackageId));
        }

        return forceKeep;
    }

    [CanBeNull]
    private static string GetPresetOwnerPackageId(Preset preset)
    {
        if (preset.RootDirectory is null) return null;

        return LoadedModManager.RunningMods
            .FirstOrDefault(mod => mod.RootDir == preset.RootDirectory.FullName)
            ?.PackageId;
    }

    private static List<InstalledModDependencies> GetInstalledModDependencies()
    {
        return ModLister.AllInstalledMods.Select(mod => new InstalledModDependencies(
            mod.PackageId,
            mod.Name,
            (mod.Dependencies ?? new List<ModDependency>())
                .Select(dep => new DependencySpec(dep.packageId, dep.alternativePackageIds, dep.displayName))
                .ToList())).ToList();
    }

    /// <summary>
    /// Builds the candidate active-mod list for the given mode plus its diff against the user's current
    /// list, including any dependency breaks the candidate would introduce. Read-only: does not touch
    /// ModsConfig.
    /// </summary>
    [CanBeNull]
    public static ModListApplyPreview Preview(Preset preset, ModListApplyMode mode)
    {
        var presetEntries = ReadModList(preset);
        if (presetEntries is null) return null;

        var installedIdsLower = GetInstalledIdsLower();
        var currentActiveLower = GetCurrentActiveLower();
        var forceKeepLower = GetForceKeepSetLower(preset);

        var candidate = mode == ModListApplyMode.Exact
            ? ModListUtils.BuildExactList(presetEntries, installedIdsLower)
            : ModListUtils.BuildMergedList(presetEntries, currentActiveLower, installedIdsLower);

        candidate = ModListUtils.EnsureForceKept(candidate, currentActiveLower, forceKeepLower);

        var diff = ModListUtils.Diff(presetEntries, candidate, currentActiveLower, installedIdsLower);
        diff.BrokenDependencies.AddRange(
            ModListUtils.FindBrokenDependencies(candidate, GetInstalledModDependencies()));

        return new ModListApplyPreview { Candidate = candidate, Diff = diff };
    }

    /// <summary>
    /// Commits the given candidate list: sets it active, re-applies forced load-order constraints, and
    /// saves. Does not restart the game - callers are expected to prompt for that separately, same as
    /// the existing settings-apply flows in Settings.cs.
    /// </summary>
    public static void Commit(List<string> candidate)
    {
        ModsConfig.SetActiveToList(candidate);

        foreach (var id in candidate)
        {
            ModsConfig.EnsureModAdheresToForcedLoadOrder(id);
        }

        ModsConfig.Save();
    }

    /// <summary>
    /// Writes the currently active mod list (in load order) into the preset's own directory as
    /// ModList.xml. Falls back to the config folder if the preset directory isn't writable (e.g. some
    /// unusual local-mod permission setups) - the author can then copy the file in by hand.
    /// </summary>
    public static string ExportCurrentModList(Preset preset)
    {
        var document = new XmlDocument();
        var root = document.CreateElement("ModList");
        var mods = document.CreateElement("mods");
        root.AppendChild(mods);
        document.AppendChild(root);

        foreach (var mod in ModsConfig.ActiveModsInLoadOrder)
        {
            var li = document.CreateElement("li");

            var packageIdNode = document.CreateElement("packageId");
            packageIdNode.InnerText = mod.PackageId;
            li.AppendChild(packageIdNode);

            var nameNode = document.CreateElement("name");
            nameNode.InnerText = mod.Name;
            li.AppendChild(nameNode);

            mods.AppendChild(li);
        }

        var targetPath = preset.RootDirectory != null
            ? Path.Combine(preset.RootDirectory.FullName, FileName)
            : null;

        if (targetPath != null && TryWrite(document, targetPath)) return targetPath;

        var fallbackPath = Path.Combine(GenFilePaths.ConfigFolderPath, $"ModlistConfigurator_{preset.Name}_{FileName}");
        TryWrite(document, fallbackPath);
        return fallbackPath;
    }

    private static bool TryWrite(XmlDocument document, string path)
    {
        try
        {
            using var saveStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = XmlWriter.Create(saveStream, new XmlWriterSettings { Indent = true, IndentChars = "\t" });
            document.WriteTo(writer);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
