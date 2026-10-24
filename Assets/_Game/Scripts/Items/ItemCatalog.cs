using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    public enum ItemType
    {
        Fridge,
        Bookshelf,
        Crate,
        Lamp
    }

    /// <summary>
    /// Prototip eşyaları primitiflerden kurar. Gerçek modeller (Kenney/Quaternius) gelince
    /// burası prefab örnekleyen bir yapıya döner.
    /// </summary>
    public static class ItemCatalog
    {
        public static readonly ItemType[] BigTypes = { ItemType.Fridge, ItemType.Bookshelf };
        public static readonly ItemType[] SmallTypes = { ItemType.Crate, ItemType.Lamp };

        public static float FootprintRadius(ItemType type)
        {
            switch (type)
            {
                case ItemType.Fridge: return 0.65f;
                case ItemType.Bookshelf: return 0.8f;
                case ItemType.Crate: return 0.45f;
                default: return 0.3f;
            }
        }

        /// <param name="floorPos">Eşyanın oturacağı zemin noktası.</param>
        /// <param name="extraBodies">Eşyaya bağlı ek Rigidbody'ler (ör. kitaplar) buraya eklenir.</param>
        public static Item Build(ItemType type, Transform parent, Vector3 floorPos, float yaw, List<Rigidbody> extraBodies)
        {
            switch (type)
            {
                case ItemType.Fridge:
                {
                    var item = CreateItem("Buzdolabı", type, ItemSize.Big, parent, floorPos, yaw,
                        new Vector3(0.8f, 1.8f, 0.7f), new Color(0.92f, 0.95f, 0.98f), 90f);
                    Detail(item.transform, new Vector3(0.3f, 0.2f, 0.52f), new Vector3(0.05f, 0.3f, 0.05f), Color.gray);
                    return item;
                }
                case ItemType.Bookshelf:
                {
                    var item = CreateItem("Kitaplık", type, ItemSize.Big, parent, floorPos, yaw,
                        new Vector3(1.2f, 1.9f, 0.4f), new Color(0.55f, 0.35f, 0.2f), 40f);
                    AddBooks(item, extraBodies);
                    return item;
                }
                case ItemType.Crate:
                    return CreateItem("Koli", type, ItemSize.Small, parent, floorPos, yaw,
                        new Vector3(0.5f, 0.5f, 0.5f), new Color(0.85f, 0.65f, 0.35f), 8f);
                default:
                    return CreateItem("Lamba", type, ItemSize.Small, parent, floorPos, yaw,
                        new Vector3(0.3f, 0.6f, 0.3f), new Color(0.3f, 0.75f, 0.85f), 4f);
            }
        }

        private static Item CreateItem(string name, ItemType type, ItemSize size, Transform parent, Vector3 floorPos,
            float yaw, Vector3 dims, Color color, float mass)
        {
            var go = GameMaterials.CreateBox(name, parent, floorPos + Vector3.up * (dims.y * 0.5f + 0.01f), dims, color);
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            GameMaterials.AddBody(go, mass, continuous: true);
            var item = go.AddComponent<Item>();
            item.DisplayName = name;
            item.Type = type;
            item.Size = size;
            return item;
        }

        private static void Detail(Transform item, Vector3 localPos, Vector3 worldSize, Color color)
        {
            var go = GameMaterials.CreateBox("Detail", item, item.TransformPoint(localPos), Vector3.one, color, false);
            go.transform.rotation = item.rotation;
            var s = item.lossyScale;
            go.transform.localScale = new Vector3(worldSize.x / s.x, worldSize.y / s.y, worldSize.z / s.z);
        }

        private static void AddBooks(Item shelf, List<Rigidbody> extraBodies)
        {
            var shelfRb = shelf.GetComponent<Rigidbody>();
            float top = shelf.transform.position.y + shelf.transform.lossyScale.y * 0.5f;
            Color[] colors = { new Color(0.8f, 0.2f, 0.25f), new Color(0.2f, 0.45f, 0.8f), new Color(0.3f, 0.7f, 0.35f) };

            for (int i = 0; i < 3; i++)
            {
                Vector3 local = new Vector3((i - 1) * 0.32f, 0f, 0f);
                Vector3 pos = shelf.transform.position + shelf.transform.rotation * local;
                pos.y = top + 0.16f;
                var book = GameMaterials.CreateBox("Kitap", shelf.transform.parent, pos, new Vector3(0.22f, 0.3f, 0.28f),
                    colors[i]);
                book.transform.rotation = shelf.transform.rotation;
                var rb = GameMaterials.AddBody(book, 1.5f, continuous: true);
                var joint = book.AddComponent<FixedJoint>();
                joint.connectedBody = shelfRb;
                joint.breakForce = 120f;
                joint.breakTorque = 60f;
                extraBodies.Add(rb);
            }
        }
    }
}
