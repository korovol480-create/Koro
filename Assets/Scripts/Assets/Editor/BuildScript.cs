using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildScript
{
    public static void Build()
    {
        var g = BuildTargetGroup.Android;
        var nbt = NamedBuildTarget.Android;

        PlayerSettings.companyName = "SandboxStudio";
        PlayerSettings.productName = "QuestPhysicsSandbox";
        PlayerSettings.SetApplicationIdentifier(nbt, "com.sandboxstudio.questphysics");
        PlayerSettings.SetScriptingBackend(nbt, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        EditorUserBuildSettings.buildAppBundle = false;

        SetupOpenXR(g);
        string scenePath = CreateScene();

        string outDir = "build/Android";
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-customBuildPath") outDir = args[i + 1];
        string apk = outDir.EndsWith(".apk") ? outDir : Path.Combine(outDir, "QuestPhysicsSandbox.apk");
        Directory.CreateDirectory(Path.GetDirectoryName(apk));

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = apk,
            target = BuildTarget.Android,
            options = BuildOptions.None
        });
        Debug.Log("Build result: " + report.summary.result);
        if (report.summary.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    static string CreateScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Game").AddComponent<SandboxGameLogic>();
        const string path = "Assets/Scenes/Main.unity";
        EditorSceneManager.SaveScene(scene, path);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(path, true) };
        return path;
    }

    static void SetupOpenXR(BuildTargetGroup g)
    {
        Directory.CreateDirectory("Assets/XR");
        const string key = "com.unity.xr.management.loader_settings";
        XRGeneralSettingsPerBuildTarget perBuild;
        EditorBuildSettings.TryGetConfigObject(key, out perBuild);
        if (perBuild == null)
        {
            perBuild = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(perBuild, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
            EditorBuildSettings.AddConfigObject(key, perBuild, true);
        }
        if (!perBuild.HasSettingsForBuildTarget(g)) perBuild.CreateDefaultSettingsForBuildTarget(g);
        if (!perBuild.HasManagerSettingsForBuildTarget(g)) perBuild.CreateDefaultManagerSettingsForBuildTarget(g);
        perBuild.SettingsForBuildTarget(g).InitManagerOnStart = true;
        var mgr = perBuild.ManagerSettingsForBuildTarget(g);
        XRPackageMetadataStore.AssignLoader(mgr, "UnityEngine.XR.OpenXR.OpenXRLoader", g);
        EditorUtility.SetDirty(perBuild);

        var touch = Feature(g, "com.unity.openxr.feature.input.oculustouch");
        var quest = Feature(g, "com.unity.openxr.feature.quest");
        if (quest != null) EnableQuestTargets(quest);
        AssetDatabase.SaveAssets();
    }

    static OpenXRFeature Feature(BuildTargetGroup g, string id)
    {
        var f = FeatureHelpers.GetFeatureWithIdForBuildTarget(g, id);
        if (f == null) { Debug.LogWarning("OpenXR feature not found: " + id); return null; }
        f.enabled = true;
        EditorUtility.SetDirty(f);
        return f;
    }

    // best effort: tick all Quest target devices via reflection
    static void EnableQuestTargets(object feature)
    {
        try
        {
            var fl = feature.GetType().GetField("targetDevices", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var list = fl != null ? fl.GetValue(feature) as IEnumerable : null;
            if (list == null) return;
            foreach (var item in list)
            {
                var ef = item.GetType().GetField("enabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ef != null) ef.SetValue(item, true);
            }
            EditorUtility.SetDirty((UnityEngine.Object)feature);
        }
        catch (Exception e) { Debug.LogWarning("Quest target setup skipped: " + e.Message); }
    }
}
