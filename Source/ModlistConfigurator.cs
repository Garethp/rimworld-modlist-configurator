using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace ModlistConfigurator;

public class ModlistConfigurator : Mod
{
    public static DirectoryInfo SettingsDir;

    // Resolved at runtime rather than hardcoded, so a fork/rename of this mod doesn't silently disable
    // the "never deactivate this mod" safety rail in ModListImporter.
    public static string OwnPackageId;

    public ModlistConfigurator(ModContentPack content) : base(content)
    {
        SettingsDir = Content.ModMetaData.RootDir.GetDirectories().FirstOrDefault(dir => dir.Name == "Settings");
        OwnPackageId = Content.ModMetaData.PackageId;
        GetSettings<Settings>().AutomaticSettingsImport();
    }

    public override void DoSettingsWindowContents(Rect canvas)
    {
        base.DoSettingsWindowContents(canvas);
        GetSettings<Settings>().DoWindowContents(canvas);
    }

    public override string SettingsCategory()
    {
        return "Auto Mod Config";
    }
}