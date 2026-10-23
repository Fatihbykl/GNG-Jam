using System;
using FuzzyLogicLabs.FuzzyIDE.Editor.UI;
using UnityEditor;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    public class IDEAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if ((importedAssets != null && importedAssets.Length > 0) ||
                (deletedAssets != null && deletedAssets.Length > 0) ||
                (movedAssets != null && movedAssets.Length > 0))
            {
                EditorApplication.delayCall += () =>
                {
                    if (VirtualIDEWindow.ActiveInstance != null)
                    {
                        VirtualIDEWindow.ActiveInstance.OnProjectAssetsChanged(
                            importedAssets,
                            deletedAssets,
                            movedAssets,
                            movedFromAssetPaths
                        );
                    }
                };
            }
        }
    }
}