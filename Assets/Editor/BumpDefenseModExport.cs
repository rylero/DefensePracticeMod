using System;
using System.IO;
using BumpDefenseMod;
using GameSystems.Management;
using GameSystems.Modding.Authoring.GameSystems.Modding.Editor;
using RobotFramework;
using UnityEditor;
using UnityEngine;

public static class BumpDefenseModExport
{
    private const string ModFolder = "Assets/Prefabs/Games/Rebuilt/Robots/Mods/BumpDefense";
    private const string SourcePrefabPath = "Assets/Prefabs/Games/Rebuilt/Robots/CitrusCircuits/CitrusCircuits.prefab";
    private const string SourceMetadataPath = "Assets/Prefabs/Games/Rebuilt/Robots/CitrusCircuits/CitrusCircuits.asset";
    private const string VariantPath = ModFolder + "/BumpDefense.prefab";
    private const string MetadataPath = ModFolder + "/BumpDefense.asset";
    private const string PackPath = ModFolder + "/BumpDefensePack.asset";
    private const string BundlePrefix = "rebuilt_bump_defense";

    public static string Reimport()
    {
        AssetDatabase.ImportAsset(ModFolder, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        return "Reimported " + ModFolder;
    }

    [MenuItem("Tools/Modding/Generate Bump Defense Prefabs")]
    public static void GeneratePrefabsFromMenu()
    {
        var result = GeneratePrefabs();
        Debug.Log(result);
        EditorUtility.DisplayDialog("Bump Defense", result, "OK");
    }

    [MenuItem("Tools/Modding/Export Bump Defense For Steam")]
    public static void ExportFromMenu()
    {
        var result = Export();
        Debug.Log(result);
        EditorUtility.DisplayDialog("Bump Defense", result, "OK");
    }

    public static string GeneratePrefabs()
    {
        EnsureVariantPrefab();
        PointMetadataAtVariant();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return "Wrote 1678 bump-defense variants Easy/Medium/Hard/Brutal with serialized defense parameters.";
    }

    public static string Export()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
        {
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
            {
                throw new InvalidOperationException("Could not switch the active build target to Windows 64-bit.");
            }
        }

        EnsureVariantPrefab();
        PointMetadataAtVariant();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var pack = AssetDatabase.LoadAssetAtPath<BaseModpackSO>(PackPath);
        if (pack == null)
        {
            throw new InvalidOperationException("BumpDefensePack.asset was not found.");
        }

        if (!ModBuildUtility.TryBuildModpack(pack, true, out var buildPath, buildAllPlatforms: false))
        {
            throw new InvalidOperationException("Modpack build failed. See the Unity console.");
        }

        ClearBundleNamesOutsideModFolder();
        AssetDatabase.SaveAssets();

        var platformFolder = Path.Combine(buildPath, "standalonewindows64");
        if (!Directory.Exists(platformFolder))
        {
            throw new InvalidOperationException("Windows build folder was not produced at " + platformFolder);
        }

        var dest = SteamModsFolder();
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);
        CopyDirectory(platformFolder, dest);

        return FinishSteamInstall();
    }

    public static string FinishSteamInstall()
    {
        var pack = AssetDatabase.LoadAssetAtPath<BaseModpackSO>(PackPath);
        if (pack == null)
        {
            throw new InvalidOperationException("BumpDefensePack.asset was not found.");
        }

        var projectRoot = Path.GetDirectoryName(Application.dataPath);
        var dllSource = Path.Combine(projectRoot ?? string.Empty, "Library", "ScriptAssemblies", "BumpDefense.dll");
        if (!File.Exists(dllSource))
        {
            throw new InvalidOperationException("BumpDefense.dll was not compiled.");
        }

        var modId = ModBuildUtility.GetModId(pack);
        if (string.IsNullOrEmpty(modId))
        {
            throw new InvalidOperationException("Modpack id could not be resolved. GameOption/Rebuilt may have failed to load.");
        }

        var dests = new[]
        {
            SteamModsFolder(),
            Path.Combine(projectRoot ?? string.Empty, "BuiltMods", modId, "standalonewindows64")
        };

        var manifest = new GameSystems.Modding.ModpackManifest
        {
            id = modId + "_assets",
            name = pack.ModpackName,
            version = pack.ModpackVersion,
            author = pack.AuthorName,
            description = pack.Description,
            gameYear = pack.GameOption != null ? pack.GameOption.GameYear : 2026,
            gameId = pack.GameOption != null ? pack.GameOption.name : "Rebuilt",
            gameName = pack.GameOption != null ? pack.GameOption.name : "Rebuilt",
            gameType = pack.GameOption != null ? pack.GameOption.GameType.ToString() : "FRC",
            modDll = "BumpDefense.dll",
            buildHost = Application.platform.ToString(),
            modpack = new GameSystems.Modding.ModpackEntry
            {
                metadataGuid = pack.GetGuid(),
                metadataName = pack.ModpackName
            }
        };
        var json = JsonUtility.ToJson(manifest, true);

        foreach (var dest in dests)
        {
            Directory.CreateDirectory(dest);
            File.Copy(dllSource, Path.Combine(dest, "BumpDefense.dll"), true);
            File.WriteAllText(Path.Combine(dest, "modpack.json"), json);
        }

        return $"Installed Bump Defense to {SteamModsFolder()} (id {modId}, guid {pack.GetGuid()}). Restart Steam MoSim, enable the Bump Defense modpack, then pick it as splitscreen player 2.";
    }

    public static string StageWindowsForWorkshop()
    {
        var pack = AssetDatabase.LoadAssetAtPath<BaseModpackSO>(PackPath);
        if (pack == null)
        {
            throw new InvalidOperationException("BumpDefensePack.asset was not found.");
        }

        FinishSteamInstall();
        if (!ModpackWorkshopStaging.PrepareStagingFolder(pack, ModBuildUtility.GetBuildPath(pack), out var stagingFolder))
        {
            throw new InvalidOperationException("Staging failed.");
        }

        var problems = ModpackWorkshopStaging.Validate(pack, stagingFolder);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", problems));
        }

        return "Windows build staged. In Steam Workshop Uploader add a change note and click Upload Staged Build to Steam.";
    }

    private readonly struct DefenseVariantSpec
    {
        public readonly string FileName;
        public readonly string DisplayName;
        public readonly DefenseLevel Level;
        public readonly ShotContest Shot;
        public readonly float DriveSpeed;

        public DefenseVariantSpec(string fileName, string displayName, DefenseLevel level, ShotContest shot, float driveSpeed)
        {
            FileName = fileName;
            DisplayName = displayName;
            Level = level;
            Shot = shot;
            DriveSpeed = driveSpeed;
        }

        public string PrefabPath => ModFolder + "/" + FileName + ".prefab";
    }

    private static DefenseVariantSpec[] VariantSpecs()
    {
        return new[]
        {
            new DefenseVariantSpec("BumpDefense", "Medium", DefenseLevel.Medium, ShotContest.Normal, 1f),
            new DefenseVariantSpec("BumpDefense_Easy", "Easy", DefenseLevel.Easy, ShotContest.Normal, 0.7f),
            new DefenseVariantSpec("BumpDefense_Hard", "Hard", DefenseLevel.Hard, ShotContest.Aggressive, 1.05f),
            new DefenseVariantSpec("BumpDefense_Brutal", "Brutal", DefenseLevel.Brutal, ShotContest.Aggressive, 1.2f)
        };
    }

    private static void EnsureVariantPrefab()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefabPath);
        if (source == null)
        {
            throw new InvalidOperationException("Citrus Circuits 1678 prefab was not found.");
        }

        foreach (var spec in VariantSpecs())
        {
            WriteVariantPrefab(source, spec);
        }
    }

    private static void WriteVariantPrefab(GameObject source, DefenseVariantSpec spec)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
        instance.name = spec.FileName;

        var autopilot = instance.GetComponent<HubBumpDefenseAutopilot>();
        if (autopilot == null)
        {
            autopilot = instance.AddComponent<HubBumpDefenseAutopilot>();
        }

        autopilot.UsePracticeMenuSettings = false;
        autopilot.Level = spec.Level;
        autopilot.Shot = spec.Shot;
        autopilot.DriveSpeed = spec.DriveSpeed;
        autopilot.StayOffBump = true;
        autopilot.RequireRealPin = true;

        PrefabUtility.SaveAsPrefabAsset(instance, spec.PrefabPath);
        UnityEngine.Object.DestroyImmediate(instance);
    }

    private static void PointMetadataAtVariant()
    {
        var metadata = AssetDatabase.LoadAssetAtPath<RobotMetadataSO>(MetadataPath);
        var citrus = AssetDatabase.LoadAssetAtPath<RobotMetadataSO>(SourceMetadataPath);
        var specs = VariantSpecs();
        var mainPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(specs[0].PrefabPath);
        if (metadata == null || citrus == null || mainPrefab == null)
        {
            throw new InvalidOperationException("Bump Defense metadata or 1678 variant prefab is missing.");
        }

        var citrusSo = new SerializedObject(citrus);
        var citrusMain = citrusSo.FindProperty("mainRobot");
        var citrusImage = citrusSo.FindProperty("robotImage");

        var so = new SerializedObject(metadata);
        so.FindProperty("teamNumber").intValue = 9000;
        so.FindProperty("teamName").stringValue = "Bump Defense";
        if (citrusImage != null)
        {
            so.FindProperty("robotImage").objectReferenceValue = citrusImage.objectReferenceValue;
        }

        CopyVariantVisuals(so.FindProperty("mainRobot"), citrusMain, mainPrefab, specs[0].DisplayName);

        var alternates = so.FindProperty("alternateRobots");
        alternates.arraySize = specs.Length - 1;
        for (var i = 1; i < specs.Length; i++)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(specs[i].PrefabPath);
            CopyVariantVisuals(alternates.GetArrayElementAtIndex(i - 1), citrusMain, prefab, specs[i].DisplayName);
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(metadata);

        var pack = AssetDatabase.LoadAssetAtPath<BaseModpackSO>(PackPath);
        if (pack == null) return;
        var packSo = new SerializedObject(pack);
        packSo.FindProperty("description").stringValue =
            "Splitscreen P2 bump-defense AI on 1678 Citrus Circuits (goes under the trench). Variants Easy/Medium/Hard/Brutal expose difficulty, shot contest, stay-off-bump, and real-pin settings.";
        if (citrusImage != null)
        {
            packSo.FindProperty("thumbnail").objectReferenceValue = citrusImage.objectReferenceValue;
        }

        packSo.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(pack);
    }

    private static void CopyVariantVisuals(SerializedProperty dest, SerializedProperty citrusMain, GameObject prefab, string displayName)
    {
        dest.FindPropertyRelative("displayName").stringValue = displayName;
        dest.FindPropertyRelative("robotPrefab").objectReferenceValue = prefab;
        dest.FindPropertyRelative("mainMenuPrefab").objectReferenceValue =
            citrusMain.FindPropertyRelative("mainMenuPrefab").objectReferenceValue;
        dest.FindPropertyRelative("spawnSelectionPrefab").objectReferenceValue =
            citrusMain.FindPropertyRelative("spawnSelectionPrefab").objectReferenceValue;
        dest.FindPropertyRelative("preloadGamePiece").objectReferenceValue =
            citrusMain.FindPropertyRelative("preloadGamePiece").objectReferenceValue;

        var destCards = dest.FindPropertyRelative("tutorialCards");
        var sourceCards = citrusMain.FindPropertyRelative("tutorialCards");
        destCards.arraySize = sourceCards.arraySize;
        for (var i = 0; i < sourceCards.arraySize; i++)
        {
            destCards.GetArrayElementAtIndex(i).objectReferenceValue =
                sourceCards.GetArrayElementAtIndex(i).objectReferenceValue;
        }
    }

    private static void ClearBundleNamesOutsideModFolder()
    {
        foreach (var path in AssetDatabase.GetAllAssetPaths())
        {
            if (path.StartsWith(ModFolder, StringComparison.OrdinalIgnoreCase)) continue;
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) continue;
            if (string.IsNullOrEmpty(importer.assetBundleName)) continue;
            if (!importer.assetBundleName.StartsWith(BundlePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            importer.assetBundleName = string.Empty;
        }
    }

    private static string SteamModsFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "CascadeStudios", "MoSimulator", "Mods", "BumpDefense");
    }

    private static void CopyDirectory(string source, string dest)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, dest), true);
        }
    }
}
