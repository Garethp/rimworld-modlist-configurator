using System.Xml;

namespace ModlistConfiguratorTest;

[TestClass]
public class ModListUtilsTests
{
    private static XmlNode BuildModListXml(params (string packageId, string? name, string? steamId)[] entries)
    {
        var document = new XmlDocument();
        var root = document.CreateElement("ModList");
        var mods = document.CreateElement("mods");
        root.AppendChild(mods);
        document.AppendChild(root);

        foreach (var (packageId, name, steamId) in entries)
        {
            var li = document.CreateElement("li");

            var packageIdNode = document.CreateElement("packageId");
            packageIdNode.InnerText = packageId;
            li.AppendChild(packageIdNode);

            if (name != null)
            {
                var nameNode = document.CreateElement("name");
                nameNode.InnerText = name;
                li.AppendChild(nameNode);
            }

            if (steamId != null)
            {
                var steamIdNode = document.CreateElement("steamId");
                steamIdNode.InnerText = steamId;
                li.AppendChild(steamIdNode);
            }

            mods.AppendChild(li);
        }

        return root;
    }

    [TestMethod]
    public void ParseModList_ReadsWellFormedEntries()
    {
        var root = BuildModListXml(
            ("ludeon.rimworld", "Core", null),
            ("brrainz.harmony", "Harmony", "2009463077"));

        var entries = ModListUtils.ParseModList(root);

        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("ludeon.rimworld", entries[0].PackageId);
        Assert.AreEqual("Core", entries[0].Name);
        Assert.IsNull(entries[0].SteamId);
        Assert.AreEqual("brrainz.harmony", entries[1].PackageId);
        Assert.AreEqual("2009463077", entries[1].SteamId);
    }

    [TestMethod]
    public void ParseModList_SkipsEntriesMissingPackageId()
    {
        var document = new XmlDocument();
        var root = document.CreateElement("ModList");
        var mods = document.CreateElement("mods");
        var li = document.CreateElement("li");
        var nameOnly = document.CreateElement("name");
        nameOnly.InnerText = "NoPackageId";
        li.AppendChild(nameOnly);
        mods.AppendChild(li);
        root.AppendChild(mods);
        document.AppendChild(root);

        var entries = ModListUtils.ParseModList(root);

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public void ParseModList_MissingModsNode_ReturnsEmpty()
    {
        var document = new XmlDocument();
        var root = document.CreateElement("ModList");
        document.AppendChild(root);

        Assert.AreEqual(0, ModListUtils.ParseModList(root).Count);
    }

    [TestMethod]
    public void ParseModList_NullRoot_ReturnsEmpty()
    {
        Assert.AreEqual(0, ModListUtils.ParseModList(null).Count);
    }

    [TestMethod]
    public void BuildExactList_PreservesPresetOrder_FiltersToInstalled()
    {
        var entries = ModListUtils.ParseModList(BuildModListXml(
            ("Ludeon.RimWorld", null, null),
            ("brrainz.harmony", null, null),
            ("not.installed", null, null)));

        var installed = new HashSet<string> { "ludeon.rimworld", "brrainz.harmony" };

        var result = ModListUtils.BuildExactList(entries, installed);

        CollectionAssert.AreEqual(new List<string> { "ludeon.rimworld", "brrainz.harmony" }, result);
    }

    [TestMethod]
    public void BuildMergedList_KeepsUserExtras_AppendsMissingPresetModsInPresetOrder()
    {
        var entries = ModListUtils.ParseModList(BuildModListXml(
            ("a", null, null),
            ("b", null, null),
            ("c", null, null)));

        var currentActive = new List<string> { "a", "userextra" };
        var installed = new HashSet<string> { "a", "b", "c", "userextra" };

        var result = ModListUtils.BuildMergedList(entries, currentActive, installed);

        CollectionAssert.AreEqual(new List<string> { "a", "b", "c", "userextra" }, result);
    }

    [TestMethod]
    public void BuildMergedList_PresetModAlreadyActiveOutOfPresetOrder_InsertsRemainingAfterHighestPresetIndexSeen()
    {
        // Preset order is a, b, c but the user already has c active before a - the highest-preset-index
        // mod already placed is "c" (index 2), so "b" must land immediately after "c", not before "a".
        var entries = ModListUtils.ParseModList(BuildModListXml(
            ("a", null, null),
            ("b", null, null),
            ("c", null, null)));

        var currentActive = new List<string> { "c", "a" };
        var installed = new HashSet<string> { "a", "b", "c" };

        var result = ModListUtils.BuildMergedList(entries, currentActive, installed);

        CollectionAssert.AreEqual(new List<string> { "c", "b", "a" }, result);
    }

    [TestMethod]
    public void BuildMergedList_NeverRemovesOrReordersExistingUserMods()
    {
        var entries = ModListUtils.ParseModList(BuildModListXml(("a", null, null)));

        var currentActive = new List<string> { "z", "y", "a", "x" };
        var installed = new HashSet<string> { "a", "x", "y", "z" };

        var result = ModListUtils.BuildMergedList(entries, currentActive, installed);

        CollectionAssert.AreEqual(currentActive, result);
    }

    [TestMethod]
    public void Diff_PartitionsMissingMods_NotInstalled()
    {
        var entries = ModListUtils.ParseModList(BuildModListXml(
            ("a", "A Mod", null),
            ("notinstalled", "Missing Mod", null)));

        var installed = new HashSet<string> { "a" };
        var currentActive = new List<string> { "a" };
        var candidate = ModListUtils.BuildExactList(entries, installed);

        var diff = ModListUtils.Diff(entries, candidate, currentActive, installed);

        Assert.AreEqual(1, diff.Missing.Count);
        Assert.AreEqual("notinstalled", diff.Missing[0].PackageId);
        Assert.AreEqual(0, diff.ToEnable.Count);
        Assert.AreEqual(0, diff.ToDisable.Count);
    }

    [TestMethod]
    public void Diff_ApplyingTwice_IsIdempotent()
    {
        var entries = ModListUtils.ParseModList(BuildModListXml(("a", null, null), ("b", null, null)));
        var installed = new HashSet<string> { "a", "b" };

        var firstCandidate = ModListUtils.BuildExactList(entries, installed);
        var firstDiff = ModListUtils.Diff(entries, firstCandidate, new List<string>(), installed);
        Assert.IsFalse(firstDiff.IsNoOp);

        // Second apply starts from the result of the first - re-diffing against itself is a no-op.
        var secondCandidate = ModListUtils.BuildExactList(entries, installed);
        var secondDiff = ModListUtils.Diff(entries, secondCandidate, firstCandidate, installed);

        Assert.IsTrue(secondDiff.IsNoOp);
    }

    [TestMethod]
    public void EnsureForceKept_InsertsMissingForceKeptModsPreservingCurrentOrder()
    {
        var candidate = new List<string> { "somemod" };
        var currentActive = new List<string> { "core", "somemod", "expansion" };
        var forceKeep = new HashSet<string> { "core", "expansion" };

        var result = ModListUtils.EnsureForceKept(candidate, currentActive, forceKeep);

        CollectionAssert.AreEqual(new List<string> { "core", "somemod", "expansion" }, result);
    }

    [TestMethod]
    public void FindBrokenDependencies_FlagsModWhoseDependencyWouldBeDropped()
    {
        var installedMods = new List<InstalledModDependencies>
        {
            new("dependent.mod", "Dependent Mod",
                new List<DependencySpec> { new("framework.mod", null, "Framework") })
        };

        var candidateWithoutFramework = new List<string> { "dependent.mod" };
        var broken = ModListUtils.FindBrokenDependencies(candidateWithoutFramework, installedMods);

        Assert.AreEqual(1, broken.Count);
        Assert.AreEqual("dependent.mod", broken[0].ModPackageId);
        Assert.AreEqual("Framework", broken[0].MissingDependencyDisplayName);

        var candidateWithFramework = new List<string> { "dependent.mod", "framework.mod" };
        Assert.AreEqual(0, ModListUtils.FindBrokenDependencies(candidateWithFramework, installedMods).Count);
    }

    [TestMethod]
    public void FindBrokenDependencies_SatisfiedByAlternativePackageId()
    {
        var installedMods = new List<InstalledModDependencies>
        {
            new("dependent.mod", "Dependent Mod",
                new List<DependencySpec>
                    { new("framework.mod", new List<string> { "framework.mod.alt" }, "Framework") })
        };

        var candidate = new List<string> { "dependent.mod", "framework.mod.alt" };

        Assert.AreEqual(0, ModListUtils.FindBrokenDependencies(candidate, installedMods).Count);
    }
}
