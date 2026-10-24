using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Eşyayı tanımından kurar. Prefab varsa onu örnekler ve eksik oyun bileşenlerini tamamlar;
    /// yoksa tanımdaki yedek kutuyu oluşturur.
    /// </summary>
    public static class ItemCatalog
    {
        private static readonly Color[] LoosePartColors =
        {
            new Color(0.8f, 0.2f, 0.25f), new Color(0.2f, 0.45f, 0.8f), new Color(0.3f, 0.7f, 0.35f)
        };

        /// <param name="floorPos">Eşyanın oturacağı zemin noktası.</param>
        /// <param name="extraBodies">Eşyaya bağlı gevşek parçaların Rigidbody'leri buraya eklenir.</param>
        public static Item Build(ItemDefinitionSO def, Transform parent, Vector3 floorPos, float yaw, List<Rigidbody> extraBodies)
        {
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            GameObject go;
            var looseParts = new List<Transform>();

            if (def.Prefab != null)
            {
                // Kendi Rigidbody'si olan alt objeler gevşek parçadır; kökün sınırlarına ve collider'ına katılmazlar.
                go = PrefabPlacement.PlaceOnFloor(def.Prefab, def.DisplayName, parent, floorPos, rotation, out _);
                foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb.gameObject != go) looseParts.Add(rb.transform);
                }

                if (looseParts.Count > 0)
                {
                    // Gevşek parçalar hariç yeniden ölç ve kökü tekrar zemine oturt.
                    var b = PrefabPlacement.MeasureLocal(go.transform, looseParts);
                    var pivotOffset = new Vector3(b.center.x, b.min.y, b.center.z);
                    go.transform.position = floorPos + Vector3.up * 0.01f - rotation * pivotOffset;
                    PrefabPlacement.EnsureCollider(go, b, looseParts);
                }
                else
                {
                    PrefabPlacement.EnsureCollider(go, PrefabPlacement.MeasureLocal(go.transform), null);
                }
            }
            else
            {
                var size = def.FallbackSize;
                go = GameMaterials.CreateBox(def.DisplayName, parent, floorPos + Vector3.up * (size.y * 0.5f + 0.01f), size,
                    def.FallbackColor);
                go.transform.rotation = rotation;
            }

            var body = PrefabPlacement.EnsureBody(go, def.Mass, continuous: true);
            var item = PrefabPlacement.GetOrAdd<Item>(go);
            item.Definition = def;
            item.DisplayName = def.DisplayName;
            item.Size = def.Size;
            item.CanBeTied = def.CanBeTied;

            if (def.Prefab != null)
            {
                foreach (var part in looseParts) AttachLoosePart(part, body, parent, def, extraBodies);
            }
            else
            {
                AddFallbackLooseParts(go, body, parent, def, extraBodies);
            }

            item.RefreshRenderers(); // gevşek parçalar ayrıldıktan sonra
            return item;
        }

        private static void AttachLoosePart(Transform part, Rigidbody owner, Transform parent, ItemDefinitionSO def,
            List<Rigidbody> extraBodies)
        {
            part.SetParent(parent, true);
            var rb = PrefabPlacement.EnsureBody(part.gameObject, part.GetComponent<Rigidbody>().mass, continuous: true);
            PrefabPlacement.EnsureCollider(part.gameObject, PrefabPlacement.MeasureLocal(part), null);

            var joint = part.gameObject.AddComponent<FixedJoint>();
            joint.connectedBody = owner;
            joint.breakForce = def.LoosePartBreakForce;
            joint.breakTorque = def.LoosePartBreakTorque;
            extraBodies.Add(rb);
        }

        /// <summary>Prefab yokken kutunun üstüne sıralanan küçük kutular (ör. kitaplıktaki kitaplar).</summary>
        private static void AddFallbackLooseParts(GameObject owner, Rigidbody ownerBody, Transform parent,
            ItemDefinitionSO def, List<Rigidbody> extraBodies)
        {
            if (def.FallbackLooseParts <= 0) return;

            var t = owner.transform;
            float top = t.position.y + def.FallbackSize.y * 0.5f;
            float spacing = def.FallbackSize.x / def.FallbackLooseParts;
            var partSize = new Vector3(Mathf.Min(0.22f, spacing * 0.8f), 0.3f, Mathf.Min(0.28f, def.FallbackSize.z * 0.9f));

            for (int i = 0; i < def.FallbackLooseParts; i++)
            {
                float x = (i - (def.FallbackLooseParts - 1) * 0.5f) * spacing;
                Vector3 pos = t.position + t.rotation * new Vector3(x, 0f, 0f);
                pos.y = top + partSize.y * 0.5f + 0.01f;
                var part = GameMaterials.CreateBox("Parça", parent, pos, partSize, LoosePartColors[i % LoosePartColors.Length]);
                part.transform.rotation = t.rotation;
                var rb = PrefabPlacement.EnsureBody(part, def.LoosePartMass, continuous: true);
                var joint = part.AddComponent<FixedJoint>();
                joint.connectedBody = ownerBody;
                joint.breakForce = def.LoosePartBreakForce;
                joint.breakTorque = def.LoosePartBreakTorque;
                extraBodies.Add(rb);
            }
        }
    }
}
