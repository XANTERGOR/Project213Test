using UnityEditor;

namespace LocalTerrainPrototype
{
    [InitializeOnLoad]
    internal static class LTDetailMaterialNotificationEditor
    {
        static LTDetailMaterialNotificationEditor()
        {
            Undo.undoRedoPerformed+=OnUndoRedo;
            AssemblyReloadEvents.beforeAssemblyReload+=OnReload;
            EditorApplication.playModeStateChanged+=OnPlayMode;
        }
        internal static void OnUndoRedo()=>LTDetailMaterialNotifications.InvalidateAll();
        static void OnReload()=>LTDetailMaterialNotifications.InvalidateAll(true);
        static void OnPlayMode(PlayModeStateChange change)=>LTDetailMaterialNotifications.InvalidateAll(true);
        internal static void OnAssetsChanged()=>LTDetailMaterialNotifications.InvalidateAll(true);
    }
    internal sealed class LTDetailMaterialNotificationImports : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported,string[] deleted,string[] moved,string[] movedFrom)
        {
            // Includes textures, shadergraph dependencies and adapter replacements.
            // Occasional broad invalidation is safer than guessing dependency lists.
            if(imported.Length+deleted.Length+moved.Length+movedFrom.Length!=0)
                LTDetailMaterialNotificationEditor.OnAssetsChanged();
        }
    }
}
