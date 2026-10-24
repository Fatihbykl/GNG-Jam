using UnityEngine;
using UnityEditor;

namespace Tensio.Editor
{
    [CustomEditor(typeof(RopeChainRenderer))]
    [CanEditMultipleObjects]
    public class RopeChainRendererEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            RopeChainRenderer renderer = (RopeChainRenderer)target;
            SerializedProperty meshProp = serializedObject.FindProperty("linkMesh");
            SerializedProperty matProp = serializedObject.FindProperty("linkMaterial");

            if (meshProp != null && matProp != null && (meshProp.objectReferenceValue == null || matProp.objectReferenceValue == null))
            {
                EditorGUILayout.HelpBox("⚠️ Missing Link Mesh or Link Material! Please assign them below for the chain to be rendered.", MessageType.Error);
                Material defMat = RopeTools.FindDefaultChainMaterial();
                Mesh defMesh = RopeTools.FindDefaultChainMesh();
                if (defMat != null && defMesh != null)
                {
                    if (GUILayout.Button("Assign Default Chain Assets", GUILayout.Height(24)))
                    {
                        if (meshProp.objectReferenceValue == null) meshProp.objectReferenceValue = defMesh;
                        if (matProp.objectReferenceValue == null) matProp.objectReferenceValue = defMat;
                        serializedObject.ApplyModifiedProperties();
                        renderer.LinkMesh = meshProp.objectReferenceValue as Mesh;
                        renderer.LinkMaterial = matProp.objectReferenceValue as Material;
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