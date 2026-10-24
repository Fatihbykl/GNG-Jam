using UnityEngine;
using UnityEditor;

namespace Tensio.Editor
{
    [CustomEditor(typeof(RopeRenderer))]
    [CanEditMultipleObjects]
    public class RopeRendererEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            RopeRenderer renderer = (RopeRenderer)target;
            SerializedProperty matProp = serializedObject.FindProperty("ropeMaterial");

            if (matProp != null && matProp.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox("⚠️ No Material assigned! Please assign a Material below for the rope to be rendered.", MessageType.Error);
                Material defMat = RopeTools.FindDefaultRopeMaterial();
                if (defMat != null)
                {
                    if (GUILayout.Button("Assign Default Rope Material", GUILayout.Height(24)))
                    {
                        matProp.objectReferenceValue = defMat;
                        serializedObject.ApplyModifiedProperties();
                        renderer.Material = defMat;
                        EditorUtility.SetDirty(renderer);
                    }
                }
                EditorGUILayout.Space(4);
            }

            DrawDefaultInspector();

            serializedObject.ApplyModifiedProperties();
        }
    }
}