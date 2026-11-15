using Basis.Scripts.BasisSdk;
using Suzuryg.FaceEmo.Domain;
using Suzuryg.FaceEmo.UseCase;
using Suzuryg.FaceEmo.Components;
using Suzuryg.FaceEmo.Components.Settings;
using Suzuryg.FaceEmo.Detail;
using Suzuryg.FaceEmo.Detail.AV3;
using Suzuryg.FaceEmo.Detail.Drawing;
using Suzuryg.FaceEmo.Detail.View;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using Suzuryg.FaceEmo.Detail.Localization;
using UniRx;
using System.Linq;

namespace Suzuryg.FaceEmo.AppMain
{
    [CustomEditor(typeof(FaceEmoLauncherComponent))]
    public class FaceEmoLauncher : ScriptlessEditor
    {
        GameObject _rootObject;
        InspectorView _inspectorView;
        CompositeDisposable _disposables = new CompositeDisposable();

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            Initialize();
            _inspectorView?.OnGUI();
        }

        private void Initialize()
        {
            if (_rootObject == null)
            {
                _disposables?.Dispose();
                _disposables = new CompositeDisposable();

                _rootObject = (target as FaceEmoLauncherComponent).gameObject;
                var installer = new FaceEmoInstaller(_rootObject);

                // Inspector view
                _inspectorView = installer.Container.Resolve<InspectorView>().AddTo(_disposables);
                _inspectorView.OnLaunchButtonClicked.Synchronize().Subscribe(_ => Launch(target as FaceEmoLauncherComponent)).AddTo(_disposables);
                _inspectorView.OnLocaleChanged.Synchronize().Subscribe(ChangeLocale).AddTo(_disposables);
                _inspectorView.OnMenuUpdated.Synchronize().Subscribe(x => UpdateMenu(x.menu, x.isModified)).AddTo(_disposables);

                // Disposables
                installer.Container.Resolve<InspectorThumbnailDrawer>().AddTo(_disposables);

                // Initialize
                var menuRepository = installer.Container.Resolve<IMenuRepository>();
                var updateMenuSubject = installer.Container.Resolve<UpdateMenuSubject>();
                updateMenuSubject.OnNext(menuRepository.Load(null), isModified: false);
            }
        }

        private static bool CanLaunch() => true;

        public static void Launch(FaceEmoLauncherComponent launcher)
        {
            var loc = LocalizationSetting.GetTable(LocalizationSetting.GetLocale());
            try
            {
                if (!CanLaunch()) { return; }

                var rootObject = launcher.gameObject;

                MainWindow mainWindow;
                var existingWindows = Resources.FindObjectsOfTypeAll<MainWindow>();
                if (existingWindows.Any())
                {
                    mainWindow = existingWindows.First();
                }
                else
                {
                    mainWindow = CreateInstance<MainWindow>();
                }

                mainWindow.Initialize(rootObject.GetFullPath());
                mainWindow.Show();
                mainWindow.Focus();
            }
            catch (Exception ex)
            {
                ReadableErrorWindow.Open(DomainConstants.SystemName,
                    loc.Common_Message_FailedToLaunch + "\n" + loc.Common_Message_SeeConsole, ex.ToString());
                Debug.LogError(loc.Common_Message_FailedToLaunch + "\n" + ex?.ToString());
            }
        }

        private void UpdateMenu(IMenu menu, bool isModified)
        {
            var existingWindows = Resources.FindObjectsOfTypeAll<MainWindow>();
            if (existingWindows.Any())
            {
                existingWindows.First().UpdateMenu(menu, isModified);
            }
        }

        private void ChangeLocale(Locale locale)
        {
            // Change all instance's locale
            foreach (var window in Resources.FindObjectsOfTypeAll<MainWindow>())
            {
                window.ChangeLocale(locale);
            }
        }

        [MenuItem("FaceEmo/New Menu", false, 0)]
        public static GameObject CreateCommand()
        {
            var gameObject = GetLauncherObject();

            // Create GameObject which has unique name in acitive scene.
            var baseName = DomainConstants.SystemName;
            var objectName = baseName;
            var cnt = 1;
            while (GameObject.Find(objectName))
            {
                objectName = $"{baseName}_{cnt}";
                cnt++;
            }
            gameObject.name = objectName;

            return gameObject;
        }

        [MenuItem("FaceEmo/Restore Menu", false, 1)]
        public static void RestoreCommand()
        {
            var selectedPath = EditorUtility.OpenFilePanelWithFilters(title: null, directory: FaceEmoBackupper.BackupDir, filters: new[] { "FaceEmoProject" , "asset" });
            if (string.IsNullOrEmpty(selectedPath)) { return; }

            // OpenFilePanel path is in OS format, so convert it to Unity format
            var unityPath = PathConverter.ToUnityPath(selectedPath);

            try
            {
                Restore(unityPath);
            }
            catch (Exception ex)
            {
                var loc = LocalizationSetting.GetTable(LocalizationSetting.GetLocale());
                ReadableErrorWindow.Open(DomainConstants.SystemName,
                    loc.Common_Message_FailedToOpenProject + "\n" + loc.Common_Message_SeeConsole, ex.ToString());
                Debug.LogError(loc.Common_Message_FailedToOpenProject + "\n" + ex?.ToString());
            }
        }

        public static bool CanRestore(RestorationCheckpoint restorationCheckpoint)
        {
            return
                restorationCheckpoint != null &&
                restorationCheckpoint.LatestBackup != null &&
                !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(restorationCheckpoint.LatestBackup));
        }

        public static GameObject Restore(RestorationCheckpoint restorationCheckpoint)
        {
            var launcherObject = Restore(AssetDatabase.GetAssetPath(restorationCheckpoint.LatestBackup));

            restorationCheckpoint.gameObject.SetActive(false);
            Selection.activeGameObject = launcherObject;

            // Close MainWindow to avoid users editing the old menu.
            var mainWindow = EditorWindow.GetWindow<MainWindow>();
            if (mainWindow != null) { mainWindow.Close(); }

            var newCheckpoint = launcherObject.GetComponent<RestorationCheckpoint>();
            if (newCheckpoint == null) { launcherObject.AddComponent<RestorationCheckpoint>(); }
            EditorUtility.CopySerialized(restorationCheckpoint, newCheckpoint);

            return launcherObject;
        }

        private static GameObject Restore(string path)
        {
            var gameObject = GetLauncherObject();
            var name = System.IO.Path.GetFileName(path).Replace(".asset", string.Empty);
            gameObject.name = name;

            var installer = new FaceEmoInstaller(gameObject);
            var backupper = installer.Container.Resolve<IBackupper>();
            try
            {
                backupper.Import(path);
                return gameObject;
            }
            catch (Exception)
            {
                if (gameObject != null) { DestroyImmediate(gameObject); }
                throw;
            }
        }

        private static GameObject GetLauncherObject(MenuCommand menuCommand = null)
        {
            var gameObject = new GameObject();
            gameObject.AddComponent<FaceEmoLauncherComponent>();
            if (menuCommand != null) { GameObjectUtility.SetParentAndAlign(gameObject, menuCommand.context as GameObject); }
            Undo.RegisterCreatedObjectUndo(gameObject, $"Create {DomainConstants.SystemName} Object");
            Selection.activeObject = gameObject;
            UnityEditorInternal.InternalEditorUtility.SetIsInspectorExpanded(gameObject.GetComponent<FaceEmoLauncherComponent>(), true);
            return gameObject;
        }

        [InitializeOnLoadMethod]
        private static void AddHierarchyItemOnGUI()
        {
#if UNITY_6000_2_OR_NEWER
            EditorApplication.hierarchyWindowItemByEntityIdOnGUI += HierarchyWindowItemByEntityIdOnGUI;
#else
            EditorApplication.hierarchyWindowItemOnGUI += HierarchyWindowItemOnGUI;
#endif
        }

#if UNITY_6000_2_OR_NEWER
        private static void HierarchyWindowItemByEntityIdOnGUI(EntityId entityId, Rect selectionRect)
        {
            HierarchyWindowItemOnGUI(EditorUtility.EntityIdToObject(entityId) as GameObject, selectionRect);
        }
#endif

#if !UNITY_6000_2_OR_NEWER
        private static void HierarchyWindowItemOnGUI(int instanceID, Rect selectionRect)
        {
            HierarchyWindowItemOnGUI(EditorUtility.InstanceIDToObject(instanceID) as GameObject, selectionRect);
        }
#endif

        private static void HierarchyWindowItemOnGUI(GameObject gameObject, Rect selectionRect)
        {
            if (EditorPrefs.GetBool(DetailConstants.KeyHideHierarchyIcon, DetailConstants.DefaultHideHierarchyIcon))
            {
                return;
            }

            if (gameObject == null)
            {
                return;
            }

            var avatar = gameObject.GetComponent<BasisAvatar>();
            if (avatar == null)
            {
                return;
            }

            var loc = LocalizationSetting.GetTable(LocalizationSetting.GetLocale());

            const float buttonWidth = 30;
            float xOffsetForOtherTools = EditorPrefs.GetFloat(DetailConstants.KeyHierarchyIconOffset, DetailConstants.DefaultHierarchyIconOffset);
            selectionRect.xMin += selectionRect.width - buttonWidth - xOffsetForOtherTools;
            selectionRect.xMax -= xOffsetForOtherTools;
            selectionRect.yMin += 1;
            selectionRect.yMax -= 1;
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath("b8710db71cc992745987c4e92d53fcd1")); // logo
            if (GUI.Button(selectionRect, new GUIContent(string.Empty, loc?.Common_Tooltip_LaunchFromHierarchy)))
            {
                if (!CanLaunch()) { return; }

                if (TryLaunchFromExisting(avatar)) { return; }

                if (TryLaunchFromCheckpoint(avatar, loc)) { return; }

                LaunchFromNew(avatar, loc);
            }
            GUI.DrawTexture(selectionRect, icon, ScaleMode.ScaleToFit, alphaBlend: true);
        }

        private static bool TryLaunchFromExisting(BasisAvatar avatar)
        {
            foreach (var launcher in FindObjectsByType<FaceEmoLauncherComponent>(FindObjectsSortMode.None)?.OrderBy(x => x.name))
            {
                if (ReferenceEquals(launcher?.AV3Setting?.TargetAvatar, avatar))
                {
                    Launch(launcher);
                    return true;
                }
            }
            return false;
        }

        private static bool TryLaunchFromCheckpoint(BasisAvatar avatar, LocalizationTable loc)
        {
            foreach (var checkpoint in FindObjectsByType<RestorationCheckpoint>(FindObjectsSortMode.None)?.OrderBy(x => x.name))
            {
                if (!ReferenceEquals(checkpoint.TargetAvatar, avatar) || !CanRestore(checkpoint)) { continue; }

                if (OptoutableDialog.Show(DomainConstants.SystemName, loc.Launcher_Message_Restore, "OK", isRiskyAction: false))
                {
                    try
                    {
                        var launcherObject = Restore(checkpoint);
                        var launcher = launcherObject.GetComponent<FaceEmoLauncherComponent>();
                        Launch(launcher);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        ReadableErrorWindow.Open(DomainConstants.SystemName, loc.Launcher_Message_RestoreError,
                            ex.ToString());
                        Debug.LogError(loc.Launcher_Message_RestoreError + ex);
                        return false;
                    }
                }
            }
            return false;
        }

        private static void LaunchFromNew(BasisAvatar avatar, LocalizationTable loc)
        {
            var launcherObject = CreateCommand();
            var installer = new FaceEmoInstaller(launcherObject);

            var launcher = launcherObject.GetComponent<FaceEmoLauncherComponent>();
            launcher.AV3Setting.TargetAvatar = avatar;

            try
            {
                ImportPatternsAndOptions(launcherObject, installer, avatar);
            }
            catch (Exception ex)
            {
                ReadableErrorWindow.Open(DomainConstants.SystemName, loc.Launcher_Message_ImportError, ex.ToString());
                Debug.LogError(loc.Launcher_Message_ImportError + ex);

                DestroyImmediate(launcher);
                new FaceEmoInstaller(launcherObject);
                launcher = launcherObject.GetComponent<FaceEmoLauncherComponent>();
                launcher.AV3Setting.TargetAvatar = avatar;
            }
            Launch(launcher);
        }

        // The VRChat FX importers were removed in the Basis port: a new project starts with an empty menu.
        private static void ImportPatternsAndOptions(GameObject rootObject, FaceEmoInstaller installer, BasisAvatar avatar)
        {
            var menuRepository = installer.Container.Resolve<IMenuRepository>();
            var menu = menuRepository.Load(string.Empty);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            menuRepository.Save(string.Empty, menu, "Create project");
        }

        private const string HideHierarchyIconPath = "FaceEmo/Hide Hierarchy Icon";
        private const int HideHierarchyIconOrder = 100;

        [MenuItem(HideHierarchyIconPath, false, HideHierarchyIconOrder)]
        private static void HideHierarchyIcon()
        {
            EditorPrefs.SetBool(DetailConstants.KeyHideHierarchyIcon, !EditorPrefs.GetBool(DetailConstants.KeyHideHierarchyIcon, DetailConstants.DefaultHideHierarchyIcon));
        }

        [MenuItem(HideHierarchyIconPath, true, HideHierarchyIconOrder)]
        private static bool HideHierarchyIconValidate()
        {
            UnityEditor.Menu.SetChecked(HideHierarchyIconPath, EditorPrefs.GetBool(DetailConstants.KeyHideHierarchyIcon, DetailConstants.DefaultHideHierarchyIcon));
            return true;
        }
    }
}
