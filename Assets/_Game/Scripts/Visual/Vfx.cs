using UnityEngine;

namespace KatKat
{
    /// <summary>
    /// Kodla kurulan parçacık efektleri: patlama (flaş, ateş topu, kıvılcım, beton parçaları, toz), yanık izi,
    /// kırılma tozu ve çöküş sonrası yere yayılan toz bulutu. Şablonlar ilk kullanımda bir kez kurulur,
    /// sonra kopyalanır. Efektler scaled zamanla oynar; ağır çekimde toz havada asılı kalır.
    /// </summary>
    public static class Vfx
    {
        private const float BlastLifetime = 6f;
        private const float ShatterLifetime = 5f;
        private const float SettleLifetime = 8f;

        private static readonly Color DustColor = new Color(0.74f, 0.69f, 0.62f, 0.55f);
        private static readonly Color FireColor = new Color(1f, 0.55f, 0.18f);
        private static readonly Color SparkColor = new Color(1f, 0.85f, 0.45f);

        private static GameContentSO _content;
        private static Transform _holder;
        private static Material _alpha, _additive, _debris;
        private static Texture2D _soft;
        private static GameObject _blast, _scorch, _shatter, _settle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _content = null;
            _holder = null;
            _alpha = _additive = _debris = null;
            _soft = null;
            _blast = _scorch = _shatter = _settle = null;
        }

        public static void Init(GameContentSO content) => _content = content;

        #region Oynatma

        /// <summary>Şarj patlaması. <paramref name="scorchParent"/> verilirse zemine yanık izi bırakır.</summary>
        public static void Blast(Vector3 pos, Transform scorchParent, float groundY)
        {
            Spawn(ref _blast, BuildBlast, pos, BlastLifetime);
            if (scorchParent == null) return;

            EnsureHolder();
            if (_scorch == null) _scorch = BuildScorch();
            var scorch = Object.Instantiate(_scorch, new Vector3(pos.x, groundY + 0.02f, pos.z), Quaternion.identity, scorchParent);
            scorch.name = "Scorch";
        }

        /// <summary>Cell Fracture parçası kırılınca kopan toz ve ufak parçalar.</summary>
        public static void Shatter(Bounds bounds)
        {
            var go = Spawn(ref _shatter, BuildShatter, bounds.center, ShatterLifetime);
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>())
            {
                var shape = ps.shape;
                shape.scale = Vector3.Max(bounds.size, Vector3.one * 0.3f);
            }
        }

        /// <summary>Kat çöktükten sonra zeminde yayılan alçak toz bulutu.</summary>
        public static void SettleDust(Vector3 center, Vector2 footprint)
        {
            var go = Spawn(ref _settle, BuildSettle, center, SettleLifetime);
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>())
            {
                var shape = ps.shape;
                shape.scale = new Vector3(footprint.x, 0.4f, footprint.y);
            }
        }

        private static GameObject Spawn(ref GameObject template, System.Func<GameObject> build, Vector3 pos, float lifetime)
        {
            EnsureHolder();
            if (template == null) template = build();
            var go = Object.Instantiate(template, pos, Quaternion.identity);
            go.name = template.name;
            Object.Destroy(go, lifetime); // scaled zaman: ağır çekimde efekt de uzar
            return go;
        }

        #endregion

        #region Şablonlar

        private static GameObject BuildBlast()
        {
            var root = NewTemplate("BlastFx");

            var flash = Make(root, "Flash", _additive);
            Main(flash, 0.12f, 0f, 3.5f, new Color(1f, 0.8f, 0.55f));
            Burst(flash, 1);
            Fade(flash, 0.6f, 1f);

            var fire = Make(root, "Fire", _additive);
            Main(fire, new Vector2(0.25f, 0.45f), new Vector2(1.5f, 4f), new Vector2(0.8f, 1.6f), FireColor);
            Burst(fire, 10);
            Sphere(fire, 0.3f);
            Gradient(fire, FireColor, new Color(0.7f, 0.15f, 0.05f));
            Grow(fire, 1.8f);

            var sparks = Make(root, "Sparks", _additive);
            Main(sparks, new Vector2(0.4f, 0.9f), new Vector2(5f, 11f), new Vector2(0.04f, 0.08f), SparkColor, gravity: 1.2f);
            Burst(sparks, 30);
            Sphere(sparks, 0.2f);
            Fade(sparks, 0f, 1f);
            var sr = sparks.GetComponent<ParticleSystemRenderer>();
            sr.renderMode = ParticleSystemRenderMode.Stretch;
            sr.velocityScale = 0.06f;
            sr.lengthScale = 1f;

            Debris(root, "Debris", 14, new Vector2(3f, 7f));
            Dust(root, "Smoke", 16, new Vector2(2.5f, 4.5f), new Vector2(0.6f, 2.2f), new Vector2(1f, 2.2f), 0.4f);
            return root;
        }

        private static GameObject BuildShatter()
        {
            var root = NewTemplate("ShatterFx");
            var dust = Dust(root, "Dust", 9, new Vector2(1.8f, 3.2f), new Vector2(0.3f, 1.2f), new Vector2(0.7f, 1.5f), 0.5f);
            Box(dust);
            var bits = Debris(root, "Bits", 8, new Vector2(1f, 3f));
            Box(bits);
            return root;
        }

        private static GameObject BuildSettle()
        {
            var root = NewTemplate("SettleFx");
            var dust = Dust(root, "GroundDust", 30, new Vector2(4f, 6.5f), new Vector2(0.3f, 1.2f), new Vector2(2f, 4f), 0.5f,
                alpha: 0.35f);
            Box(dust);
            var main = dust.main;
            main.gravityModifier = 0f;
            return root;
        }

        private static GameObject BuildScorch()
        {
            var root = NewTemplate("ScorchFx");
            var ps = Make(root, "Scorch", _alpha);
            Main(ps, 100000f, 0f, 1.7f, new Color(0.05f, 0.04f, 0.03f, 0.6f));
            Burst(ps, 1);
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
            r.sortingFudge = 10f; // tozun arkasında kalsın
            var main = ps.main;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            return root;
        }

        private static ParticleSystem Dust(GameObject root, string name, int count, Vector2 life, Vector2 speed, Vector2 size,
            float radius, float alpha = -1f)
        {
            var ps = Make(root, name, _alpha);
            var color = DustColor;
            if (alpha >= 0f) color.a = alpha;
            Main(ps, life, speed, size, color, gravity: -0.03f);
            var main = ps.main;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            Burst(ps, count);
            Sphere(ps, radius);
            Fade(ps, 0.15f, 1f);
            Grow(ps, 2.2f);
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.drag = 1.5f;
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-0.6f, 0.6f);
            return ps;
        }

        private static ParticleSystem Debris(GameObject root, string name, int count, Vector2 speed)
        {
            var ps = Make(root, name, _debris);
            Main(ps, new Vector2(2.5f, 3.5f), speed, new Vector2(0.06f, 0.18f), Color.white, gravity: 1.5f);
            var main = ps.main;
            main.startRotation3D = true;
            main.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            Burst(ps, count);
            Sphere(ps, 0.25f);

            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.separateAxes = true;
            rot.x = new ParticleSystem.MinMaxCurve(-6f, 6f);
            rot.y = new ParticleSystem.MinMaxCurve(-6f, 6f);
            rot.z = new ParticleSystem.MinMaxCurve(-6f, 6f);

            var col = ps.collision;
            col.enabled = true;
            col.type = ParticleSystemCollisionType.World;
            col.bounce = 0.25f;
            col.dampen = 0.4f;
            col.lifetimeLoss = 0f;
            col.quality = ParticleSystemCollisionQuality.Medium;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0.8f, 1f, 1f, 0f));

            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            r.alignment = ParticleSystemRenderSpace.Local;
            return ps;
        }

        #endregion

        #region Yardımcılar

        private static void EnsureHolder()
        {
            if (_holder != null) return;
            var go = new GameObject("VfxTemplates");
            go.SetActive(false); // şablonlar oynamasın
            _holder = go.transform;
            BuildMaterials();
        }

        private static GameObject NewTemplate(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_holder, false);
            return go;
        }

        private static ParticleSystem Make(GameObject root, string name, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = false;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            var emission = ps.emission;
            emission.rateOverTime = 0f;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        private static void Main(ParticleSystem ps, float life, float speed, float size, Color color, float gravity = 0f)
        {
            Main(ps, new Vector2(life, life), new Vector2(speed, speed), new Vector2(size, size), color, gravity);
        }

        private static void Main(ParticleSystem ps, Vector2 life, Vector2 speed, Vector2 size, Color color, float gravity = 0f)
        {
            var main = ps.main;
            main.duration = Mathf.Max(0.1f, life.y);
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed.x, speed.y);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startColor = color;
            main.gravityModifier = gravity;
            main.maxParticles = 200;
        }

        private static void Burst(ParticleSystem ps, int count)
        {
            var emission = ps.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
        }

        private static void Sphere(ParticleSystem ps, float radius)
        {
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = radius;
        }

        private static void Box(ParticleSystem ps)
        {
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = Vector3.one;
        }

        /// <summary>Hızlı belirip yavaş sönen alfa eğrisi.</summary>
        private static void Fade(ParticleSystem ps, float fadeInEnd, float startAlpha)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(fadeInEnd > 0f ? 0f : startAlpha, 0f),
                    new GradientAlphaKey(startAlpha, Mathf.Max(0.01f, fadeInEnd)),
                    new GradientAlphaKey(0f, 1f)
                });
            col.color = g;
        }

        private static void Gradient(ParticleSystem ps, Color from, Color to)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(to / Mathf.Max(0.01f, from.maxColorComponent), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
        }

        private static void Grow(ParticleSystem ps, float endScale)
        {
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f / endScale, 1f, 1f));
        }

        private static void BuildMaterials()
        {
            _soft = SoftCircle(64);

            var template = _content != null ? _content.ParticleMaterial : null;
            if (template == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                template = shader != null ? new Material(shader) : new Material(Shader.Find("Sprites/Default"));
            }

            _alpha = new Material(template) { name = "Vfx_Alpha" };
            _alpha.SetTexture("_BaseMap", _soft);

            // Toplamalı karışım + HDR renk: bloom eşiğini aşar, ateş ve kıvılcım parlar
            _additive = new Material(template) { name = "Vfx_Additive" };
            _additive.SetTexture("_BaseMap", _soft);
            _additive.SetFloat("_Blend", 2f);
            _additive.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _additive.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            _additive.SetColor("_BaseColor", new Color(3f, 3f, 3f, 1f));

            // Beton parçaları: binayla aynı ışıklı materyal
            _debris = GameMaterials.Get(GameMaterials.Concrete);
        }

        private static Texture2D SoftCircle(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Vfx_Soft", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half;
                float a = Mathf.Clamp01(1f - d);
                a = a * a * (3f - 2f * a); // yumuşak kenar
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        #endregion
    }
}
