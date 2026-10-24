using System;
using System.Collections.Generic;
using UnityEngine;

namespace KatKat
{
    public enum EndpointKind
    {
        Item,
        Anchor
    }

    /// <summary>İp ucunun kat içindeki kalıcı referansı (sıfırlamadan sonra yeniden kurmak için).</summary>
    [Serializable]
    public struct EndpointRef
    {
        public EndpointKind Kind;
        public int Index;
        public Vector3 LocalOffset;
    }

    [Serializable]
    public struct RopePlanEntry
    {
        public int TypeIndex;
        public EndpointRef A;
        public EndpointRef B;
    }

    /// <summary>
    /// Planlama aşamasında kurulan iplerin listesi. Hızlı sıfırlama, katı aynı seed ile yeniden
    /// üretip bu listeyi tekrar uygular; Rigidbody durumunu kaydetmeye gerek kalmaz.
    /// </summary>
    [Serializable]
    public class RopePlan
    {
        public List<RopePlanEntry> Entries = new List<RopePlanEntry>();

        public RopePlan Clone() => new RopePlan { Entries = new List<RopePlanEntry>(Entries) };
    }
}
