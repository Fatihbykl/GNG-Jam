using System;
using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Tüm ayar değerleri tek yerde; jam sırasında Inspector'dan canlı ayarlanır.
    /// </summary>
    [Serializable]
    public class GameConfig
    {
        [Header("Kat geometrisi")]
        public int gridX = 3;
        public int gridZ = 2;
        public float cellSize = 3f;
        public float floorHeight = 3f;
        public float slabThickness = 0.3f;
        public float columnSize = 0.4f;
        public float wallThickness = 0.2f;
        [Range(0f, 1f)] public float openEdgeChance = 0.3f;

        [Header("Kütle ve eklemler")]
        public float slabMass = 150f;
        public float columnMass = 50f;
        public float wallMass = 30f;
        public float jointBreakForce = 6000f;
        public float jointBreakTorque = 4000f;

        [Header("Taşıyıcı kolonlar")]
        [Tooltip("İlk katta şarjsız (patlamayan) kolonların oranı.")]
        [Range(0f, 0.6f)] public float bearingRatio = 0.4f;
        public float bearingRatioDecreasePerFloor = 0.03f;
        public float minBearingRatio = 0.15f;
        [Tooltip("Taşıyıcı kolonun zemine bağlantısı bu kadar güçlü.")]
        public float bearingBaseJointScale = 4f;
        [Tooltip("Tavanın taşıyıcı kolona bağlantısı zayıf: çevre çökünce tavan kolondan kopar, kolon ayakta kalır.")]
        public float bearingTopJointScale = 0.3f;

        [Header("Kesici şarj")]
        [Tooltip("Hız değişimi (m/s), kütleden bağımsız. Şarj kolonu keser; çöküşü yerçekimi ve sıra belirler.")]
        public float explosionKick = 1.2f;
        public float explosionRadius = 2.5f;
        public float explosionUpward = 0.2f;
        public float ropeBlastPower = 40f;

        [Header("Çöküş itkisi (eşyaları kaydırır, planlamada tahmin edilir)")]
        [Tooltip("Patlayan sıranın dibindeki küçük eşyaya uygulanan yatay ivme (m/s²).")]
        public float pushAccelSmall = 15f;
        [Tooltip("Büyük eşyaya uygulanan yatay ivme (m/s²). İp yükü ≈ kütle × bu ivme: ince ip ağır eşyada kopmalı.")]
        public float pushAccelBig = 18f;
        [Tooltip("Her katta ivmeye eklenen miktar (m/s²).")]
        public float pushAccelPerFloor = 0.4f;
        [Tooltip("Her şarjın itki dalgasının süresi (s, ağır çekimde yavaşlar).")]
        public float pushDuration = 0.4f;
        [Tooltip("Dalga, patlayan kolonlardan bu uzaklıkta (m) sıfıra iner.")]
        public float waveRadius = 4.5f;
        [Tooltip("Çöküş okunun gücü bunun altındaysa eşyalar merkezden dışa (her yöne) saçılır.")]
        [Range(0f, 1f)] public float pushRadialBelow = 0.08f;
        [Tooltip("Çöküş okunun gücü bunun üstündeyse eşyalar tamamen ok yönünde kayar.")]
        [Range(0f, 1f)] public float pushDirectionalAbove = 0.4f;
        [Tooltip("Eşyaların zemindeki sürtünmesi; tahmin bu değeri kullanır.")]
        [Range(0f, 1f)] public float itemFriction = 0.4f;

        [Header("Gaz tüpü (ikinci patlama)")]
        [Tooltip("Bağsız gaz tüpüne itki ulaşınca fitil yanar; bu kadar saniye sonra patlar. Bu sürede ip atılırsa söner.")]
        public float gasFuse = 0.7f;
        [Tooltip("Gaz patlamasının itki dalgası yarıçapı (m).")]
        public float gasBlastRadius = 3.5f;
        [Tooltip("Gaz patlamasının itkisi, bir şarj dalgasına göre kaç kat.")]
        public float gasBlastStrength = 1.6f;

        [Header("Duvar düşüşü (patlama sırasına bağlı)")]
        [Tooltip("Duvar, dışa bakan yönü çöküş okuyla bu kadar (cos) aynı yöndeyse dışa düşer; değilse içe.")]
        [Range(-1f, 1f)] public float wallOutwardDot = 0.3f;
        [Tooltip("Duvarın tepesine verilen devrilme hızı (m/s).")]
        public float wallFallKick = 2.2f;

        [Header("Parçalanma (Cell Fracture modelleri)")]
        [Tooltip("Kırılabilir duvar/tavan bu bağıl hızın (m/s) üstündeki çarpmada parçalanır.")]
        public float shatterImpactSpeed = 4f;
        [Tooltip("Şarj bu yarıçaptaki kırılabilir parçaları anında parçalar.")]
        public float shatterBlastRadius = 1.2f;
        public float shardMinMass = 0.5f;
        [Tooltip("Kolondan kopan tuğla bu bağıl hızın (m/s) üstündeki çarpmada kırık versiyonuna bölünür.")]
        public float brickBreakSpeed = 5f;
        [Tooltip("Şarja bu kadar yakın tuğlalar patlamada anında bölünür.")]
        public float brickBreakBlastRadius = 0.45f;
        [Tooltip("Bir katta en fazla bu kadar tuğla bölünür (fizik yükü).")]
        public int maxBrokenBricks = 60;
        [Tooltip("Bölünen tuğla parçasının en küçük kütlesi.")]
        public float brickCellMinMass = 0.1f;

        [Header("İp")]
        public float ropeSlack = 1.05f;
        public float maxRopeLength = 9f;
        public float ropeBreakWarmup = 0.4f;
        public int ropeSubSteps = 16;

        [Header("Bütçe, can, bölge")]
        public float baseBudget = 24f;
        public float budgetDecreasePerFloor = 1f;
        public float minBudget = 10f;
        public float penaltyBudgetCost = 0.5f;
        public int startLives = 3;
        public int slowMoCharges = 2;
        public float safeZoneMargin = 1.5f;
        [Tooltip("Kat cezası bu sınırı aşarsa bir can daha gider.")]
        public int penaltyLimit = 15;
        public float penaltyLimitDecreasePerFloor = 0.4f;
        public int minPenaltyLimit = 8;

        [Header("Ağır çekim")]
        public float slowMoScale = 0.15f;
        public float slowMoDuration = 1.5f;
        [Range(0f, 1f)] public float slowMoAutoForceRatio = 0.8f;
        public float slowMoRopeAttachRange = 6f;
        public float reinforceMultiplier = 1.5f;
        public float reinforceCost = 1.5f;

        [Header("Durma tespiti")]
        public float settleSpeed = 0.25f;
        public float settleHoldTime = 1.2f;
        public float settleTimeout = 10f;
        public float killDepth = 15f;

        public float FloorWidth => gridX * cellSize;
        public float FloorDepth => gridZ * cellSize;
    }
}
