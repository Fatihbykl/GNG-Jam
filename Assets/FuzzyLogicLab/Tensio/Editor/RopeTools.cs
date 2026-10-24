using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Tensio;

namespace Tensio.Editor
{
    public class RopeTools
    {
        [MenuItem("Tools/Tensio/Make Colliders Rope-Ready")]
        public static void MakeCollidersReady()
        {
            GameObject[] selected = Selection.gameObjects;
            if (selected.Length == 0)
            {
                Debug.LogWarning("No objects selected.");
                return;
            }

            int convertedCount = 0;
            List<string> logs = new List<string>();

            foreach (var go in selected)
            {
                bool processed = false;

                // Box
                if (go.GetComponent<BoxCollider>())
                {
                    if (!go.GetComponent<RopeBoxProxy>())
                    {
                        Undo.AddComponent<RopeBoxProxy>(go);
                        processed = true;
                    }
                }

                // Sphere
                if (go.GetComponent<SphereCollider>())
                {
                    if (!go.GetComponent<RopeSphereProxy>())
                    {
                        Undo.AddComponent<RopeSphereProxy>(go);
                        processed = true;
                    }
                }

                // Capsule
                if (go.GetComponent<CapsuleCollider>())
                {
                    if (!go.GetComponent<RopeCapsuleProxy>())
                    {
                        Undo.AddComponent<RopeCapsuleProxy>(go);
                        processed = true;
                    }
                }

                // Plane (MeshCollider)
                if (go.GetComponent<MeshCollider>())
                {
                    string meshName = go.GetComponent<MeshFilter>()?.sharedMesh?.name ?? "";
                    bool isPlaneMesh = meshName.Contains("Plane") || meshName.Contains("Quad") || meshName.Contains("Floor");
                    bool isPlaneObj = go.name.Contains("Plane") || go.name.Contains("Floor") || go.name.Contains("Quad");

                    if (isPlaneMesh || isPlaneObj)
                    {
                        if (!go.GetComponent<RopePlaneProxy>())
                        {
                            Undo.AddComponent<RopePlaneProxy>(go);
                            processed = true;
                        }
                    }
                }

                if (processed)
                {
                    convertedCount++;
                    logs.Add(go.name);
                }
            }

            if (convertedCount > 0)
            {
                Debug.Log($"<color=green>Converted {convertedCount} objects to Rope-Ready Colliders:</color>\n" + string.Join(", ", logs));
            }
            else
            {
                Debug.Log("No suitable colliders found to convert in selection (or they already have proxies).");
            }
        }

        public static Material FindDefaultRopeMaterial()
        {
            string[] guids = AssetDatabase.FindAssets("RopeMaterial t:Material");
            if (guids != null && guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<Material>(path);
            }
            return null;
        }

        public static Material FindDefaultChainMaterial()
        {
            string[] guids = AssetDatabase.FindAssets("ChainLinkMaterial t:Material");
            if (guids != null && guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<Material>(path);
            }
            return null;
        }

        public static Mesh FindDefaultChainMesh()
        {
            string[] guids = AssetDatabase.FindAssets("ChainLink t:Mesh");
            if (guids != null && guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<Mesh>(path);
            }
            return null;
        }

        [MenuItem("GameObject/Tensio/Elastic Rope", false, 10)]
        [MenuItem("GameObject/3D Object/Tensio/Elastic Rope", false, 10)]
        public static void CreateElasticRope(MenuCommand menuCommand)
        {
            GameObject go = new GameObject("Tensio Elastic Rope");
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);

            if (menuCommand.context == null && SceneView.lastActiveSceneView != null)
            {
                go.transform.position = SceneView.lastActiveSceneView.pivot;
            }

            var controller = go.AddComponent<RopeController>();
            var renderer = go.AddComponent<RopeRenderer>();

            Material defMat = FindDefaultRopeMaterial();
            if (defMat != null) renderer.Material = defMat;

            Undo.RegisterCreatedObjectUndo(go, "Create Tensio Elastic Rope");
            Selection.activeGameObject = go;
            RopeControllerEditor.FrameRope(controller);
        }

        [MenuItem("GameObject/Tensio/Chain Rope", false, 11)]
        [MenuItem("GameObject/3D Object/Tensio/Chain Rope", false, 11)]
        public static void CreateChainRope(MenuCommand menuCommand)
        {
            GameObject go = new GameObject("Tensio Chain Rope");
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);

            if (menuCommand.context == null && SceneView.lastActiveSceneView != null)
            {
                go.transform.position = SceneView.lastActiveSceneView.pivot;
            }

            var controller = go.AddComponent<RopeController>();
            controller.InitSettings.Type = RopeController.RopeInitSettings.RopeType.Chain;
            var chainRenderer = go.AddComponent<RopeChainRenderer>();

            Material defMat = FindDefaultChainMaterial();
            if (defMat != null) chainRenderer.LinkMaterial = defMat;
            Mesh defMesh = FindDefaultChainMesh();
            if (defMesh != null) chainRenderer.LinkMesh = defMesh;

            Undo.RegisterCreatedObjectUndo(go, "Create Tensio Chain Rope");
            Selection.activeGameObject = go;
            RopeControllerEditor.FrameRope(controller);
        }
    }
}