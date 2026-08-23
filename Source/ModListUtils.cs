using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using JetBrains.Annotations;

namespace ModlistConfigurator;

/// <summary>
/// A single mod entry as declared in a preset's ModList.xml. Deliberately Verse-free so it (and
/// everything below) can be unit tested without a running game.
/// </summary>
public readonly struct PresetModEntry
{
    public PresetModEntry(string packageId, [CanBeNull] string name, [CanBeNull] string steamId)
    {
        PackageId = packageId;
        Name = name;
        SteamId = steamId;
    }

    public string PackageId { get; }
    [CanBeNull] public string Name { get; }
    [CanBeNull] public string SteamId { get; }

    public string DisplayName => string.IsNullOrEmpty(Name) ? PackageId : Name;
}

/// <summary>
/// Minimal shape of an installed mod's dependency, abstracted away from Verse.ModDependency so the
/// dependency-break check stays pure and testable. AlternativePackageIds mirrors
/// Verse.ModRequirement.alternativePackageIds - satisfying any one of them satisfies the dependency.
/// </summary>
public readonly struct DependencySpec
{
    public DependencySpec(string packageId, [CanBeNull] IReadOnlyList<string> alternativePackageIds,
        [CanBeNull] string displayName)
    {
        PackageId = packageId;
        AlternativePackageIds = alternativePackageIds ?? Array.Empty<string>();
        DisplayName = displayName;
    }

    public string PackageId { get; }
    public IReadOnlyList<string> AlternativePackageIds { get; }
    [CanBeNull] public string DisplayName { get; }
}

public readonly struct InstalledModDependencies
{
    public InstalledModDependencies(string packageId, [CanBeNull] string displayName,
        IReadOnlyList<DependencySpec> dependencies)
    {
        PackageId = packageId;
        DisplayName = displayName;
        Dependencies = dependencies;
    }

    public string PackageId { get; }
    [CanBeNull] public string DisplayName { get; }
    public IReadOnlyList<DependencySpec> Dependencies { get; }
}

public readonly struct BrokenDependency
{
    public BrokenDependency(string modPackageId, [CanBeNull] string modDisplayName, string missingDependencyDisplayName)
    {
        ModPackageId = modPackageId;
        ModDisplayName = modDisplayName;
        MissingDependencyDisplayName = missingDependencyDisplayName;
    }

    public string ModPackageId { get; }
    [CanBeNull] public string ModDisplayName { get; }
    public string MissingDependencyDisplayName { get; }
}

public class ModListDiff
{
    public List<PresetModEntry> ToEnable { get; } = new();
    public List<string> ToDisable { get; } = new();
    public List<PresetModEntry> Missing { get; } = new();
    public bool OrderDiffers { get; set; }
    public List<BrokenDependency> BrokenDependencies { get; } = new();

    public bool IsNoOp => ToEnable.Count == 0 && ToDisable.Count == 0 && !OrderDiffers;
}

public static class ModListUtils
{
    /// <summary>
    /// Parses a ModList.xml document root (the &lt;ModList&gt; node) into its mod entries.
    /// Uses the same DefDatabase-free raw XmlNode walking as SettingsImporter.LoadPresets, since a
    /// preset's ModList.xml is read before the DefDatabase is populated.
    /// </summary>
    public static List<PresetModEntry> ParseModList([CanBeNull] XmlNode modListRoot)
    {
        var result = new List<PresetModEntry>();
        if (modListRoot is null) return result;

        var modsNode = XmlUtils.GetChildNodeByName(modListRoot, "mods");
        if (modsNode is null) return result;

        foreach (XmlNode li in modsNode.ChildNodes)
        {
            if (li.Name != "li") continue;

            var packageId = XmlUtils.GetChildNodeByName(li, "packageId")?.InnerText;
            if (string.IsNullOrWhiteSpace(packageId)) continue;

            var name = XmlUtils.GetChildNodeByName(li, "name")?.InnerText;
            var steamId = XmlUtils.GetChildNodeByName(li, "steamId")?.InnerText;

            result.Add(new PresetModEntry(packageId, name, steamId));
        }

        return result;
    }

    private static string Lower(string packageId) => packageId.ToLowerInvariant();

    /// <summary>Preset entries, in preset order, filtered down to installed mods.</summary>
    public static List<string> BuildExactList(IReadOnlyList<PresetModEntry> presetEntries,
        ISet<string> installedIdsLower)
    {
        return presetEntries
            .Select(entry => Lower(entry.PackageId))
            .Where(installedIdsLower.Contains)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Starts from the user's current active list (in order) and inserts installed-but-inactive
    /// preset mods into it. The insertion cursor tracks position in *preset* order: it is always
    /// "immediately after the highest-preset-index mod already found in the user's active list", so a
    /// preset mod the user already has active out of the preset's intended relative order (e.g. preset
    /// order A,B,C but the user already has C active before A) doesn't produce an ambiguous or
    /// order-violating insertion point. Never removes or reorders a mod the user has that the preset
    /// doesn't mention.
    /// </summary>
    public static List<string> BuildMergedList(IReadOnlyList<PresetModEntry> presetEntries,
        IReadOnlyList<string> currentActiveLower, ISet<string> installedIdsLower)
    {
        var result = currentActiveLower.ToList();
        var presetIdsLower = presetEntries.Select(entry => Lower(entry.PackageId)).ToList();

        // Index (within presetIdsLower) of the highest preset-order mod already placed in `result`.
        var highestPlacedPresetIndex = -1;
        for (var i = 0; i < result.Count; i++)
        {
            var presetIndex = presetIdsLower.IndexOf(result[i]);
            if (presetIndex > highestPlacedPresetIndex) highestPlacedPresetIndex = presetIndex;
        }

        for (var presetIndex = 0; presetIndex < presetIdsLower.Count; presetIndex++)
        {
            var id = presetIdsLower[presetIndex];
            if (result.Contains(id)) continue;
            if (!installedIdsLower.Contains(id)) continue;

            var insertAt = highestPlacedPresetIndex < 0
                ? FindInsertionPointForNoAnchor(result, presetIdsLower, presetIndex)
                : IndexAfterHighestPlaced(result, presetIdsLower, highestPlacedPresetIndex);

            result.Insert(insertAt, id);
            highestPlacedPresetIndex = presetIndex;
        }

        return result;
    }

    private static int IndexAfterHighestPlaced(List<string> result, List<string> presetIdsLower,
        int highestPlacedPresetIndex)
    {
        var anchorId = presetIdsLower[highestPlacedPresetIndex];
        return result.IndexOf(anchorId) + 1;
    }

    private static int FindInsertionPointForNoAnchor(List<string> result, List<string> presetIdsLower,
        int presetIndex)
    {
        // No preset mod has been placed yet: insert before the first *later* preset mod that's already
        // active, if any, otherwise at the end.
        for (var laterPresetIndex = presetIndex + 1; laterPresetIndex < presetIdsLower.Count; laterPresetIndex++)
        {
            var laterId = presetIdsLower[laterPresetIndex];
            var laterPos = result.IndexOf(laterId);
            if (laterPos >= 0) return laterPos;
        }

        return result.Count;
    }

    /// <summary>
    /// Guarantees every id in forceKeepIdsLower is present in the candidate list, inserting any that are
    /// missing immediately after the nearest id that precedes it in currentActiveLower and is already
    /// present in the (evolving) result - i.e. reinserted relative to its real neighbours in the user's
    /// original list, not just relative to other force-kept ids. Falls back to the front of the list if
    /// nothing before it survived (e.g. Core, which is always first in currentActiveLower).
    /// </summary>
    public static List<string> EnsureForceKept(IReadOnlyList<string> candidateLower,
        IReadOnlyList<string> currentActiveLower, ISet<string> forceKeepIdsLower)
    {
        var result = candidateLower.ToList();
        var currentActiveList = currentActiveLower.ToList();
        var forceKeepInOrder = currentActiveList.Where(forceKeepIdsLower.Contains).ToList();

        foreach (var id in forceKeepInOrder)
        {
            if (result.Contains(id)) continue;

            var fullIndex = currentActiveList.IndexOf(id);
            var insertAt = 0;

            for (var j = fullIndex - 1; j >= 0; j--)
            {
                var precedingPos = result.IndexOf(currentActiveList[j]);
                if (precedingPos < 0) continue;

                insertAt = precedingPos + 1;
                break;
            }

            result.Insert(insertAt, id);
        }

        return result;
    }

    public static List<BrokenDependency> FindBrokenDependencies(IReadOnlyList<string> candidateLower,
        IReadOnlyList<InstalledModDependencies> installedModsWithDeps)
    {
        var candidateSet = new HashSet<string>(candidateLower);
        var broken = new List<BrokenDependency>();

        foreach (var mod in installedModsWithDeps)
        {
            if (!candidateSet.Contains(Lower(mod.PackageId))) continue;

            foreach (var dependency in mod.Dependencies)
            {
                var satisfied = candidateSet.Contains(Lower(dependency.PackageId)) ||
                                 dependency.AlternativePackageIds.Any(alt => candidateSet.Contains(Lower(alt)));

                if (satisfied) continue;

                broken.Add(new BrokenDependency(mod.PackageId, mod.DisplayName,
                    dependency.DisplayName ?? dependency.PackageId));
            }
        }

        return broken;
    }

    /// <summary>
    /// Diffs a candidate active-mod list (the output of BuildExactList/BuildMergedList) against the
    /// user's current active list, and partitions preset entries into missing (not installed).
    /// </summary>
    public static ModListDiff Diff(IReadOnlyList<PresetModEntry> presetEntries,
        IReadOnlyList<string> candidateLower, IReadOnlyList<string> currentActiveLower, ISet<string> installedIdsLower)
    {
        var diff = new ModListDiff();
        var currentSet = new HashSet<string>(currentActiveLower);
        var candidateSet = new HashSet<string>(candidateLower);

        foreach (var entry in presetEntries)
        {
            var idLower = Lower(entry.PackageId);
            if (!installedIdsLower.Contains(idLower))
            {
                diff.Missing.Add(entry);
                continue;
            }

            if (candidateSet.Contains(idLower) && !currentSet.Contains(idLower))
            {
                diff.ToEnable.Add(entry);
            }
        }

        foreach (var id in currentActiveLower.Where(id => !candidateSet.Contains(id)))
        {
            diff.ToDisable.Add(id);
        }

        var sharedInOrder = candidateLower.Where(currentSet.Contains).ToList();
        var currentSharedInOrder = currentActiveLower.Where(candidateSet.Contains).ToList();
        diff.OrderDiffers = !sharedInOrder.SequenceEqual(currentSharedInOrder);

        return diff;
    }
}
