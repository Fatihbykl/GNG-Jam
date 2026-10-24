using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System.Collections.Generic;
using Unity.Mathematics;
using Tensio;
using UnityEngine.UIElements;
#if TENSIO_SPLINES_SUPPORT
using UnityEngine.Splines;
#endif

namespace Tensio.Editor
{
    [CustomEditor(typeof(RopeController))]
    [CanEditMultipleObjects]
    public class RopeControllerEditor : UnityEditor.Editor
    {
        #region Serialized Properties
        private SerializedProperty _initMode;
#if TENSIO_SPLINES_SUPPORT
        private SerializedProperty _splineReference;
        private SerializedProperty _pinnedSplineKnots;
#endif
        private SerializedProperty _initSettings;
        private SerializedProperty _physicsSettings;
        private SerializedProperty _aeroSettings;
        private SerializedProperty _pinConstraints;
        private SerializedProperty _useBezierCurve;
        private SerializedProperty _controlPoints;
        #endregion

        #region Editor State
        private enum SelectionType { None, Anchor, TangentIn, TangentOut }
        
        // Overlay State
        private static Rect _overlayRect = new Rect(20, 20, 240, 0);
        private static Rect _toolbarRect = new Rect(10, 30, 200, 0);
        private static bool _overlayCollapsed = false;
        private static bool _toolbarCollapsed = false;
        private static bool _showAllTangents = false;
        private static bool _boxSelectionMode = false;
        private static bool _magnetMode = false;
        
        // Pin Picking State
        private static bool _isPickingTarget = false;
        private static int _pickingIndex = -1;
        
        private static int _currentTab = 0;
        private static readonly string[] _tabNames = { "General", "Physics", "Visuals", "Pins", "Extras" };
        private static bool _showPhysicsSim = true;
        private static bool _showPhysicsMat = true;
        private static bool _showPhysicsAero = true;
        private static bool _showPhysicsBreak = true;
        private static bool _lockStartToTransform = true;
        
        private ReorderableList _pinList;
        
        // Visuals
        private GUIStyle _cardStyle;
        private GUIStyle _headerStyle;

#if TENSIO_SPLINES_SUPPORT
        private int _lastSplineSignature = 0;
#endif
        #endregion

        private void OnEnable()
        {
            // Reset states for Fast Enter Play Mode
            _isPickingTarget = false;
            _pickingIndex = -1;

            _initMode = serializedObject.FindProperty("InitMode");
#if TENSIO_SPLINES_SUPPORT
            _splineReference = serializedObject.FindProperty("SplineReference");
            _pinnedSplineKnots = serializedObject.FindProperty("PinnedSplineKnots");
#endif
            _initSettings = serializedObject.FindProperty("_initSettings");
            _physicsSettings = serializedObject.FindProperty("_physicsSettings");
            _aeroSettings = serializedObject.FindProperty("_aeroSettings");
            _pinConstraints = serializedObject.FindProperty("_pinConstraints");
            _useBezierCurve = serializedObject.FindProperty("UseBezierCurve");
            _controlPoints = serializedObject.FindProperty("ControlPoints");
            
            InitPinList();

            if (!Application.isPlaying)
            {
                RopeController controller = (RopeController)target;
                if (controller != null)
                {
                    if (controller.InitMode == RopeController.RopeInitMode.Bezier && (controller.ControlPoints == null || controller.ControlPoints.Count == 0))
                    {
                        Undo.RecordObject(controller, "Auto Initialize Rope");
                        controller.InitializeBezierCurve();
                        EditorUtility.SetDirty(controller);
                    }
#if TENSIO_SPLINES_SUPPORT
                    else if (controller.InitMode == RopeController.RopeInitMode.Spline)
                    {
                        controller.RefreshParticlesFromSpline();
                        _lastSplineSignature = GetSplineSignature(controller.SplineReference);
                        EditorUtility.SetDirty(controller);
                        SceneView.RepaintAll();
                    }
#endif
                }
            }
        }

        private void OnDisable()
        {
            Tools.hidden = false;
        }

        private void InitPinList()
        {
            _pinList = new ReorderableList(serializedObject, _pinConstraints, true, true, true, true);
            
            _pinList.drawHeaderCallback = (Rect rect) => {
                EditorGUI.LabelField(rect, "Connected Anchors");
            };

            _pinList.elementHeight = EditorGUIUtility.singleLineHeight * 2 + 6;

            _pinList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) => {
                SerializedProperty element = _pinConstraints.GetArrayElementAtIndex(index);
                SerializedProperty targetProp = element.FindPropertyRelative("Target");
                SerializedProperty idxProp = element.FindPropertyRelative("ParticleIndex");
                SerializedProperty offsetProp = element.FindPropertyRelative("LocalOffset");
                SerializedProperty kinematicProp = element.FindPropertyRelative("UseKinematicCoupling");

                rect.y += 2;
                float lineHeight = EditorGUIUtility.singleLineHeight;
                
                // Row 1: Index | Target | Kinematic
                Rect rIndex = new Rect(rect.x, rect.y, 50, lineHeight);
                Rect rTarget = new Rect(rect.x + 55, rect.y, rect.width - 130, lineHeight);
                Rect rKinematic = new Rect(rect.x + rect.width - 70, rect.y, 70, lineHeight);

                EditorGUIUtility.labelWidth = 20;
                EditorGUI.PropertyField(rIndex, idxProp, new GUIContent("#", "Particle Index"));
                
                EditorGUIUtility.labelWidth = 30;
                EditorGUI.PropertyField(rTarget, targetProp, new GUIContent("To", "Target Transform"));
                
                EditorGUIUtility.labelWidth = 50;
                EditorGUI.PropertyField(rKinematic, kinematicProp, new GUIContent("Kin", "Use Kinematic Coupling"));

                // Row 2: Offset
                Rect rOffset = new Rect(rect.x, rect.y + lineHeight + 2, rect.width, lineHeight);
                EditorGUIUtility.labelWidth = 60;
                EditorGUI.PropertyField(rOffset, offsetProp, new GUIContent("Offset", "Local Offset from Target"));
                
                // Validation
                if (targetProp.objectReferenceValue == null)
                {
                    Rect warnRect = new Rect(rect.x + rect.width - 20, rect.y, 20, 20);
                    GUI.Label(warnRect, new GUIContent(EditorGUIUtility.IconContent("console.warnicon.sml").image, "Target is missing!"));
                }
            };

            _pinList.onAddCallback = (ReorderableList l) => {
                var index = l.serializedProperty.arraySize;
                l.serializedProperty.arraySize++;
                l.index = index;
                var element = l.serializedProperty.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("ParticleIndex").intValue = 0;
                element.FindPropertyRelative("Target").objectReferenceValue = null;
                element.FindPropertyRelative("LocalOffset").vector3Value = Vector3.zero;
                element.FindPropertyRelative("UseKinematicCoupling").boolValue = false;
            };
        }

        public override void OnInspectorGUI()
        {
            RopeController controller = (RopeController)target;
            serializedObject.Update();
            InitStyles();

#if TENSIO_SPLINES_SUPPORT
            if (!Application.isPlaying && controller.InitMode == RopeController.RopeInitMode.Spline && controller.SplineReference != null)
            {
                int currentSplineSig = GetSplineSignature(controller.SplineReference);
                if (currentSplineSig != _lastSplineSignature)
                {
                    _lastSplineSignature = currentSplineSig;
                    controller.RefreshParticlesFromSpline();
                    serializedObject.Update();
                    EditorUtility.SetDirty(controller);
                    SceneView.RepaintAll();
                }
            }
#endif

            DrawBrandedHeader(controller);

            if (!Application.isPlaying && controller.ControlPoints != null && controller.ControlPoints.Count > 0)
            {
                float offsetDist = Vector3.Distance(controller.transform.position, controller.ControlPoints[0].Position);
                if (offsetDist > 0.05f)
                {
                    EditorGUILayout.Space(2);
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.LabelField("⚠️ Rope Offset from GameObject", EditorStyles.boldLabel);
                        EditorGUILayout.LabelField($"The rope start pin is {offsetDist:F2}m away from this GameObject's position.", EditorStyles.wordWrappedMiniLabel);
                        
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("Snap Rope to GameObject", EditorStyles.miniButtonLeft))
                            {
                                MoveRopeToTransform(controller);
                            }
                            if (GUILayout.Button("Move GameObject to Rope", EditorStyles.miniButtonMid))
                            {
                                MoveTransformToRope(controller);
                            }
                            if (GUILayout.Button("Focus Pins", EditorStyles.miniButtonRight))
                            {
                                FrameRope(controller);
                            }
                        }
                    }
                    EditorGUILayout.Space(2);
                }
            }
            
            EditorGUILayout.Space(5);

            // TABS
            _currentTab = GUILayout.Toolbar(_currentTab, _tabNames, GUILayout.Height(25));
            EditorGUILayout.Space(5);

            EditorGUILayout.BeginVertical(_cardStyle);

            if (_currentTab == 0) // General
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(_initMode);
#if TENSIO_SPLINES_SUPPORT
                if (_initMode.enumValueIndex == (int)RopeController.RopeInitMode.Spline)
                {
                    EditorGUILayout.PropertyField(_splineReference);
                    EditorGUILayout.PropertyField(_pinnedSplineKnots);
                }
#endif
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties();
#if TENSIO_SPLINES_SUPPORT
                    if (controller.InitMode == RopeController.RopeInitMode.Spline) controller.RefreshParticlesFromSpline();
                    else 
#endif
                        controller.RefreshParticlesFromBezier();
                    EditorUtility.SetDirty(controller);
                    SceneView.RepaintAll();
                }
                
                EditorGUI.BeginChangeCheck();
                SerializedProperty typeProp = _initSettings.FindPropertyRelative("Type");
                EditorGUILayout.PropertyField(typeProp);
                if (EditorGUI.EndChangeCheck())
                {
                    UpdateRenderer(controller, (RopeController.RopeInitSettings.RopeType)typeProp.enumValueIndex);
                }

                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(_initSettings.FindPropertyRelative("ParticleCount"));
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties(); 
                    foreach (var t in targets)
                    {
                        var c = t as RopeController;
                        if (c == null) continue;
#if TENSIO_SPLINES_SUPPORT
                        if (c.InitMode == RopeController.RopeInitMode.Spline)
                        {
                            c.RefreshParticlesFromSpline();
                        }
                        else
#endif
                        {
                            c.RefreshParticlesFromBezier();
                        }
                        EditorUtility.SetDirty(c);
                    }
                    SceneView.RepaintAll();
                }

                SerializedProperty lengthProp = _initSettings.FindPropertyRelative("Length");
                SerializedProperty autoLenProp = _initSettings.FindPropertyRelative("AutoCalculateLength");

                EditorGUILayout.Space(2);
                EditorGUILayout.BeginHorizontal();

                EditorGUI.BeginChangeCheck();
                float currentLen = lengthProp.floatValue;
                float newLen = EditorGUILayout.FloatField(new GUIContent("Length", "Total resting length of the rope in meters."), currentLen);
                if (EditorGUI.EndChangeCheck())
                {
                    newLen = Mathf.Max(0.1f, newLen);
                    Undo.RecordObject(controller, "Change Rope Length");
                    lengthProp.floatValue = newLen;
                    
                    float curveLen = controller.GetCalculatedCurveLength();
                    if (Mathf.Abs(newLen - curveLen) > 0.01f && autoLenProp.boolValue)
                    {
                        autoLenProp.boolValue = false;
                    }
                    serializedObject.ApplyModifiedProperties();
                    controller.UpdateLengthFromEditor(newLen);
                    EditorUtility.SetDirty(controller);
                    SceneView.RepaintAll();
                }

                float activeCurveLen = controller.GetCalculatedCurveLength();
                bool isSynced = Mathf.Abs(lengthProp.floatValue - activeCurveLen) < 0.02f;

                // Fit Button: syncs Length to the curve geometry
                GUI.enabled = !isSynced || !autoLenProp.boolValue;
                if (GUILayout.Button(new GUIContent("Fit", "Reset Length to match the curve geometry perfectly"), EditorStyles.miniButton, GUILayout.Width(36)))
                {
                    Undo.RecordObject(controller, "Fit Length to Curve");
                    autoLenProp.boolValue = true;
                    lengthProp.floatValue = activeCurveLen;
                    serializedObject.ApplyModifiedProperties();
#if TENSIO_SPLINES_SUPPORT
                    if (controller.InitMode == RopeController.RopeInitMode.Spline)
                        controller.RefreshParticlesFromSpline();
                    else
#endif
                        controller.RefreshParticlesFromBezier();
                    EditorUtility.SetDirty(controller);
                    SceneView.RepaintAll();
                }
                GUI.enabled = true;

                // Scale Button: scales the curve geometry to match this Length
                if (!isSynced && activeCurveLen > 0.001f)
                {
                    if (GUILayout.Button(new GUIContent("Scale", "Scale the curve geometry to match this Length"), EditorStyles.miniButton, GUILayout.Width(44)))
                    {
                        Undo.RecordObject(controller, "Scale Curve to Length");
#if TENSIO_SPLINES_SUPPORT
                        if (controller.InitMode == RopeController.RopeInitMode.Spline && controller.SplineReference != null)
                        {
                            Undo.RecordObject(controller.SplineReference, "Scale Spline Knots");
                        }
#endif
                        float targetLen = lengthProp.floatValue;
                        controller.ScaleCurveToLength(targetLen);
                        serializedObject.Update();
                        autoLenProp.boolValue = true;
                        serializedObject.ApplyModifiedProperties();
                        EditorUtility.SetDirty(controller);
#if TENSIO_SPLINES_SUPPORT
                        if (controller.SplineReference != null)
                        {
                            EditorUtility.SetDirty(controller.SplineReference);
                        }
#endif
                        SceneView.RepaintAll();
                    }
                }
                EditorGUILayout.EndHorizontal();

                // Auto-sync toggle
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(autoLenProp, new GUIContent("Auto-Fit to Curve", "Automatically keeps rope length synchronized with the curve length."));
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties();
                    if (autoLenProp.boolValue)
                    {
#if TENSIO_SPLINES_SUPPORT
                        if (controller.InitMode == RopeController.RopeInitMode.Spline)
                            controller.RefreshParticlesFromSpline();
                        else
#endif
                            controller.RefreshParticlesFromBezier();
                        EditorUtility.SetDirty(controller);
                        SceneView.RepaintAll();
                    }
                }

                // Help box if slack or tension exists
                if (!autoLenProp.boolValue && activeCurveLen > 0.01f && !isSynced)
                {
                    float diff = lengthProp.floatValue - activeCurveLen;
                    if (diff > 0.02f)
                    {
                        EditorGUILayout.HelpBox($"Curve: {activeCurveLen:F2}m | Slack: +{diff:F2}m (rope will hang loosely).", MessageType.None);
                    }
                    else if (diff < -0.02f)
                    {
                        EditorGUILayout.HelpBox($"Curve: {activeCurveLen:F2}m | Tension: {diff:F2}m (rope will pull tight).", MessageType.None);
                    }
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Quick Actions", EditorStyles.boldLabel);
                EditorGUILayout.BeginHorizontal();
                
                if (GUILayout.Button("Relax Shape", EditorStyles.miniButton))
                {
#if TENSIO_SPLINES_SUPPORT
                     if (controller.InitMode == RopeController.RopeInitMode.Spline)
                         controller.RegeneratePinsFromSpline();
                     else
#endif
                         controller.RegeneratePinsFromControlPoints();
                     SimulateGravity(controller);
                }
                
                if (GUILayout.Button("Reset Shape", EditorStyles.miniButton))
                {
                    Undo.RecordObject(controller, "Reset Shape");
                    if (controller.InitMode == RopeController.RopeInitMode.Bezier)
                    {
                        controller.ControlPoints.Clear();
                        controller.InitializeBezierCurve();
                        controller.RegeneratePinsFromControlPoints();
                    }
#if TENSIO_SPLINES_SUPPORT
                    else if (controller.InitMode == RopeController.RopeInitMode.Spline)
                    {
                        controller.RefreshParticlesFromSpline();
                    }
#endif
                }

                if (GUILayout.Button("Bake Mesh", EditorStyles.miniButton))
                {
                    BakeToStaticMesh(controller);
                    GUIUtility.ExitGUI();
                }

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("Focus Pins", EditorGUIUtility.IconContent("d_ViewToolZoom").image ?? EditorGUIUtility.IconContent("Search Icon").image, "Focus Scene View camera on rope and pins"), EditorStyles.miniButtonLeft))
                {
                    FrameRope(controller);
                }
                if (GUILayout.Button(new GUIContent(" Snap to GameObject", EditorGUIUtility.ObjectContent(null, typeof(Transform)).image, "Move rope to GameObject position"), EditorStyles.miniButtonRight))
                {
                    MoveRopeToTransform(controller);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(_initSettings.FindPropertyRelative("IsClosedLoop"), new GUIContent("Loop", "Connects ends to form a loop."));
                
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties();
                    foreach (var t in targets)
                    {
                        var c = t as RopeController;
                        if (c == null) continue;
#if TENSIO_SPLINES_SUPPORT
                        if (c.InitMode == RopeController.RopeInitMode.Spline)
                            c.RefreshParticlesFromSpline();
                        else
#endif
                            c.RefreshParticlesFromBezier();
                        EditorUtility.SetDirty(c);
                    }
                    SceneView.RepaintAll();
                }
            }
            else if (_currentTab == 1) // Physics
            {
                SerializedProperty isStatic = _physicsSettings.FindPropertyRelative("IsStatic");
                EditorGUILayout.PropertyField(isStatic);

                if (!isStatic.boolValue)
                {
                    EditorGUILayout.Space(4);
                    if (DrawSectionHeader("Simulation Quality", ref _showPhysicsSim))
                    {
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("SubSteps"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("Gravity"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("LinearDamping"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("BendingStiffness"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("MaxVelocity"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("TeleportThreshold"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("MassScale"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("Interpolate"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("WarmupSteps"));
                    }

                    if (DrawSectionHeader("Material & Collision", ref _showPhysicsMat))
                    {
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("ParticleMass"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("Compliance"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("Friction"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("ParticleRadius"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("CollisionMask"));
                        
                        // Collision Info Box
                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                        EditorGUILayout.LabelField("⚠️ Performance Warning", EditorStyles.boldLabel);
                        
                        GUIStyle wrapMini = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                        EditorGUILayout.LabelField("This mask enables expensive collision against standard Mesh Colliders.", wrapMini);
                        EditorGUILayout.Space(2);
                        EditorGUILayout.LabelField("For optimal performance, use Proxy Colliders:", EditorStyles.boldLabel);
                        EditorGUILayout.Space(2);
                        EditorGUILayout.LabelField("Select objects from the scene that has Unity's colliders (Sphere, Box, Capsule, Plane) and go to Tools > Tensio > Make Colliders Rope-Ready", wrapMini);
                        EditorGUILayout.EndVertical();
                        
                        EditorGUILayout.Space(2);
                        var twoWayProp = _physicsSettings.FindPropertyRelative("EnableTwoWayCoupling");
                        EditorGUILayout.PropertyField(twoWayProp);
                        if (twoWayProp.boolValue)
                        {
                            EditorGUI.indentLevel++;
                            EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("TwoWayCouplingForce"));
                            EditorGUI.indentLevel--;
                        }

                        EditorGUILayout.Space(4);
                        EditorGUILayout.LabelField("Inter-Rope Collision", EditorStyles.boldLabel);
                        SerializedProperty interRopeProp = _physicsSettings.FindPropertyRelative("SupportInterRopeCollision");
                        
                        EditorGUI.BeginChangeCheck();
                        EditorGUILayout.PropertyField(interRopeProp, new GUIContent("Enable Interaction"));
                        if (EditorGUI.EndChangeCheck())
                        {
                            if (interRopeProp.boolValue)
                            {
#if UNITY_2023_1_OR_NEWER
                                var manager = Object.FindAnyObjectByType<RopeInteractionManager>();
#else
                                var manager = FindObjectOfType<RopeInteractionManager>();
#endif
                                if (manager == null)
                                {
                                    GameObject go = new GameObject("Rope Interaction Manager");
                                    go.AddComponent<RopeInteractionManager>();
                                    Undo.RegisterCreatedObjectUndo(go, "Create Rope Interaction Manager");
                                    Debug.Log("Tensio: Auto-created RopeInteractionManager in the scene to handle inter-rope collisions.");
                                }
                            }
                        }
                        
                        if (interRopeProp.boolValue)
                        {
                            EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("InterRopeStiffness"));
                        }
                    }

                    if (DrawSectionHeader("Aerodynamics", ref _showPhysicsAero))
                    {
                        EditorGUILayout.PropertyField(_aeroSettings.FindPropertyRelative("WindDirection"));
                        EditorGUILayout.PropertyField(_aeroSettings.FindPropertyRelative("WindSpeed"));
                        EditorGUILayout.PropertyField(_aeroSettings.FindPropertyRelative("TurbulenceStrength"));
                        EditorGUILayout.PropertyField(_aeroSettings.FindPropertyRelative("AirDrag"));
                    }

                    if (DrawSectionHeader("Breaking & Cutting", ref _showPhysicsBreak))
                    {
                        SerializedProperty breakOnStretch = _physicsSettings.FindPropertyRelative("BreakOnStretch");
                        EditorGUILayout.PropertyField(breakOnStretch);
                        if (breakOnStretch.boolValue)
                        {
                            EditorGUI.indentLevel++;
                            EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("MaxStretchRatio"));
                            EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("BreakWarmup"));
                            EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("BreakIndexOverride"));
                            EditorGUI.indentLevel--;
                        }
                        
                        EditorGUILayout.Space(4);
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("MinCutLength"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("UnpinnedDebrisMode"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("DebrisLifetime"));
                        EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("DebrisFadeTime"));
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("Static Mode: Physics skipped.", MessageType.Info);
                }
            }
            else if (_currentTab == 2) // Visuals
            {
                bool hasChain = controller.GetComponent<RopeChainRenderer>() != null;
                bool hasElastic = controller.GetComponent<RopeRenderer>() != null;
                
                if (hasChain) EditorGUILayout.LabelField("Active Renderer: Chain", EditorStyles.miniLabel);
                else if (hasElastic) EditorGUILayout.LabelField("Active Renderer: Elastic", EditorStyles.miniLabel);
                
                EditorGUILayout.Space(4);
                SerializedProperty vis = _physicsSettings.FindPropertyRelative("VisualizeStretch");
                EditorGUILayout.PropertyField(vis, new GUIContent("Visualize Strain"));
                if (vis.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("StretchColor"));
                    EditorGUILayout.PropertyField(_physicsSettings.FindPropertyRelative("MaxThinningScale"));
                    EditorGUI.indentLevel--;
                }
            }
            else if (_currentTab == 3) // Pins
            {
                EditorGUILayout.HelpBox("Pins are now managed via the Scene View handles. Select a Control Point to pin it.", MessageType.Info);
                EditorGUILayout.Space(4);
                
                GUI.enabled = false;
                _pinList.DoLayoutList();
                GUI.enabled = true;
            }
            else if (_currentTab == 4) // Extras
            {
                DrawFeatureToggle(controller, typeof(RopeAudio), "Procedural Audio", "Adds wind, tension, and impact sounds.");
                DrawFeatureToggle(controller, typeof(RopeWinch), "Winch System", "Allows extending/retracting rope at runtime.");
            }

            EditorGUILayout.EndVertical();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawFeatureToggle(RopeController c, System.Type componentType, string title, string description)
        {
            Component comp = c.GetComponent(componentType);
            bool isActive = comp != null;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel, GUILayout.Width(120));
            if (isActive)
            {
                GUI.backgroundColor = new Color(0.5f, 1f, 0.5f);
                if (GUILayout.Button("Active (Remove)", EditorStyles.miniButton))
                {
                    Undo.DestroyObjectImmediate(comp);
                }
                GUI.backgroundColor = Color.white;
            }
            else
            {
                if (GUILayout.Button("Add Feature", EditorStyles.miniButton))
                {
                    Undo.AddComponent(c.gameObject, componentType);
                }
            }
            EditorGUILayout.EndHorizontal();
            if (!isActive) EditorGUILayout.HelpBox(description, MessageType.None);
            EditorGUILayout.Space(2);
        }

        // --- QUICK ACTIONS ---
        private void DrawQuickActions(RopeController c)
        {
            EditorGUILayout.BeginHorizontal();
            
            if (GUILayout.Button("Pin Start to Selection", EditorStyles.miniButtonLeft, GUILayout.Height(24)))
            {
                if (Selection.activeGameObject != null)
                {
                    Undo.RecordObject(c, "Pin Start");
                    c.PinParticle(0, Selection.activeTransform, Selection.activeTransform.position);
                    EditorUtility.SetDirty(c);
                }
                else Debug.LogWarning("Select a GameObject to pin to.");
            }
            
            if (GUILayout.Button("Pin End to Selection", EditorStyles.miniButtonRight, GUILayout.Height(24)))
            {
                if (Selection.activeGameObject != null)
                {
                    Undo.RecordObject(c, "Pin End");
                    c.PinParticle(c.ParticleCount - 1, Selection.activeTransform, Selection.activeTransform.position);
                    EditorUtility.SetDirty(c);
                }
                else Debug.LogWarning("Select a GameObject to pin to.");
            }
            EditorGUILayout.EndHorizontal();
        }

        // --- SCENE GUI & TOOLBAR ---
        private bool _showHelpPopup = false;

        private void DrawSceneToolbar(RopeController controller)
        {
            Handles.BeginGUI();
            
            // Initialize or Constrain
            if (_toolbarRect.width < 10) _toolbarRect = new Rect(10, 30, 200, 0);
            
            // Use specific ID for window
            _toolbarRect = GUILayout.Window(2025, _toolbarRect, (id) => DrawToolbarWindow(id, controller), "");
            
            // Prevent input pass-through
            if (_toolbarRect.Contains(Event.current.mousePosition))
            {
                 HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
                 if (Event.current.type == EventType.ScrollWheel) Event.current.Use();
            }

            Handles.EndGUI();
        }

        private void DrawToolbarWindow(int id, RopeController controller)
        {
            // --- Custom Header ---
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
             GUILayout.Label("Tensio Tools", EditorStyles.boldLabel);
             GUILayout.FlexibleSpace();
             
             // Docking
             if (GUILayout.Button(EditorGUIUtility.IconContent("pane options"), EditorStyles.toolbarButton, GUILayout.Width(22)))
             {
                 GenericMenu menu = new GenericMenu();
                 menu.AddItem(new GUIContent("Snap Top Left"), false, () => SnapWindow(ref _toolbarRect, TextAnchor.UpperLeft));
                 menu.AddItem(new GUIContent("Snap Top Right"), false, () => SnapWindow(ref _toolbarRect, TextAnchor.UpperRight));
                 menu.AddItem(new GUIContent("Snap Bottom Left"), false, () => SnapWindow(ref _toolbarRect, TextAnchor.LowerLeft));
                 menu.AddItem(new GUIContent("Snap Bottom Right"), false, () => SnapWindow(ref _toolbarRect, TextAnchor.LowerRight));
                 menu.ShowAsContext();
             }

             // Collapse
             if (GUILayout.Button(_toolbarCollapsed ? "▼" : "▲", EditorStyles.toolbarButton, GUILayout.Width(22)))
             {
                 _toolbarCollapsed = !_toolbarCollapsed;
                 _toolbarRect.height = 0; 
             }
            GUILayout.EndHorizontal();

            if (_toolbarCollapsed) { GUI.DragWindow(); return; }

            // --- 1. Simulation ---
            GUILayout.Label("Simulation", EditorStyles.boldLabel);
            GUILayout.BeginHorizontal();
             if (GUILayout.Button(new GUIContent(" Relax", EditorGUIUtility.IconContent("PlayButton").image, "Run physics to settle shape"), GUILayout.Height(24))) 
             {
#if TENSIO_SPLINES_SUPPORT
                 if (controller.InitMode == RopeController.RopeInitMode.Spline) controller.RegeneratePinsFromSpline();
                 else
#endif
                     controller.RegeneratePinsFromControlPoints();
                 SimulateGravity(controller);
             }
             if (GUILayout.Button(new GUIContent(" Reset", EditorGUIUtility.IconContent("Refresh").image, "Reset to straight line at GameObject position"), GUILayout.Height(24)))
             {
                Undo.RecordObject(controller, "Reset Shape");
                if (controller.InitMode == RopeController.RopeInitMode.Bezier)
                {
                    controller.ControlPoints.Clear();
                    controller.InitializeBezierCurve();
                }
#if TENSIO_SPLINES_SUPPORT
                else
                {
                    controller.RefreshParticlesFromSpline();
                }
#endif
                EditorUtility.SetDirty(controller);
             }
             if (GUILayout.Button(new GUIContent(" Frame", EditorGUIUtility.IconContent("d_ViewToolZoom").image ?? EditorGUIUtility.IconContent("Search Icon").image, "Focus camera on rope and pins"), GUILayout.Height(24)))
             {
                 FrameRope(controller);
             }
            GUILayout.EndHorizontal();

            // --- 2. Shape Tools ---
            GUILayout.Space(5);
            GUILayout.Label("Shape Modifiers", EditorStyles.boldLabel);
            
            GUILayout.BeginHorizontal();
             if (GUILayout.Button("Smooth", EditorStyles.miniButtonLeft)) AutoSmoothSelected(controller);
             if (GUILayout.Button("Straight", EditorStyles.miniButtonMid)) StraightenSelection(controller);
             if (GUILayout.Button("Align", EditorStyles.miniButtonRight)) AlignControlPoints(controller);
            GUILayout.EndHorizontal();
            
            if (GUILayout.Button("Flatten Y Axis", EditorStyles.miniButton)) FlattenSelection(controller, 1);
            if (GUILayout.Button("Bake to Static Mesh", EditorStyles.miniButton)) 
            {
                BakeToStaticMesh(controller);
                GUIUtility.ExitGUI();
            }

            // --- 3. View Settings ---
            GUILayout.Space(5);
            GUILayout.Label("Editor Settings", EditorStyles.boldLabel);
            
            GUILayout.BeginHorizontal();
             _showAllTangents = GUILayout.Toggle(_showAllTangents, "Tangents", "Button");
             _boxSelectionMode = GUILayout.Toggle(_boxSelectionMode, "Box Select", "Button");
            GUILayout.EndHorizontal();
            
            bool newMagnet = GUILayout.Toggle(_magnetMode, "Magnet Snapping", "Button");
            if (newMagnet != _magnetMode) _magnetMode = newMagnet;

            // --- Help ---
            GUILayout.Space(5);
            if (GUILayout.Button("Toggle Help", EditorStyles.miniButton)) _showHelpPopup = !_showHelpPopup;
            
            if (_showHelpPopup)
            {
                EditorGUILayout.HelpBox(
                    "Controls:\n" +
                    "• Click: Select Point\n" +
                    "• Ctrl+Click: Add Point\n" +
                    "• Shift+Drag: Snap to Surface\n" +
                    "• Right-Click: Context Menu", MessageType.Info);
            }

            GUI.DragWindow();
        }

        private void DrawPinHandles(RopeController controller)
        {
            Vector3[] points = GetParticlePositions(controller);
            if (points == null) return;
            
            Handles.color = new Color(1f, 1f, 1f, 0.5f);
            Handles.DrawAAPolyLine(2f, points);
            
            if (controller.InitSettings.IsClosedLoop && points.Length > 1)
            {
                Handles.DrawDottedLine(points[points.Length - 1], points[0], 2f);
            }

            List<int> existingPins = new List<int>();
            SerializedProperty pins = _pinConstraints;
            for (int i = 0; i < pins.arraySize; i++)
            {
                SerializedProperty p = pins.GetArrayElementAtIndex(i);
                int idx = p.FindPropertyRelative("ParticleIndex").intValue;
                existingPins.Add(idx);
                
                Transform t = p.FindPropertyRelative("Target").objectReferenceValue as Transform;
                Vector3 offset = p.FindPropertyRelative("LocalOffset").vector3Value;
                
                Vector3 particlePos = points[Mathf.Clamp(idx, 0, points.Length - 1)];
                Vector3 targetPos = (t != null) ? t.TransformPoint(offset) : particlePos;

                if (t != null)
                {
                    Handles.color = Color.green;
                    Handles.DrawDottedLine(particlePos, targetPos, 4f);
                }
                else
                {
                    Handles.color = Color.red;
                }

                if (Handles.Button(targetPos, Quaternion.identity, 0.2f, 0.2f, Handles.CubeHandleCap))
                {
                    Undo.RecordObject(controller, "Remove Pin");
                    pins.DeleteArrayElementAtIndex(i);
                    serializedObject.ApplyModifiedProperties();
                    SimulateGravity(controller);
                    return;
                }
            }

            for (int i = 0; i < points.Length; i++)
            {
                if (existingPins.Contains(i)) continue;

                if (Vector3.Distance(SceneView.currentDrawingSceneView.camera.transform.position, points[i]) < 100f)
                {
                    int id = GUIUtility.GetControlID(FocusType.Passive);
                    Event evt = Event.current;

                    if (GUIUtility.hotControl == id && evt.GetTypeForControl(id) == EventType.MouseUp)
                    {
                        Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                        if (Physics.Raycast(ray, out RaycastHit hit))
                        {
                            Undo.RecordObject(controller, "Add Pin");
                            controller.PinParticle(i, hit.transform, hit.point);
                            SimulateGravity(controller); 
                            EditorUtility.SetDirty(controller);
                        }
                        GUIUtility.hotControl = 0;
                        evt.Use();
                        continue;
                    }

                    Handles.color = (GUIUtility.hotControl == id) ? Color.green : new Color(1f, 1f, 1f, 0.5f);
                    Handles.FreeMoveHandle(id, points[i], 0.1f, Vector3.zero, Handles.SphereHandleCap);

                    if (Event.current.alt || Vector3.Distance(SceneView.currentDrawingSceneView.camera.transform.position, points[i]) < 10f)
                    {
                        Handles.Label(points[i], i.ToString());
                    }

                    if (GUIUtility.hotControl == id)
                    {
                        Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                        if (Physics.Raycast(ray, out RaycastHit hit))
                        {
                            Handles.color = Color.green;
                            Handles.DrawWireDisc(hit.point + hit.normal * 0.01f, hit.normal, 0.2f);
                            Handles.DrawLine(points[i], hit.point);
                            Handles.Label(hit.point, $"Pin to {hit.collider.name}");
                        }
                        else
                        {
                            Handles.color = Color.red;
                            Vector3 cursor = ray.origin + ray.direction * Vector3.Distance(ray.origin, points[i]);
                            Handles.DrawLine(points[i], cursor);
                        }
                    }
                }
            }
        }
        private HashSet<int> _selectedIndices = new HashSet<int>();
        private SelectionType _selectionType = SelectionType.None;
        private bool _isBoxSelecting;
        private Vector2 _boxSelectStart;

        private void OnSceneGUI()
        {
            RopeController controller = (RopeController)target;
            InitStyles();
            
            // Hide default Move tool to prevent conflict with custom handles
            Tools.hidden = Tools.current == Tool.Move;

            // Picking Logic (Highest Priority)
            if (_isPickingTarget && _pickingIndex != -1)
            {
                HandlePinPicking(controller);
            }

            DrawSceneToolbar(controller);

            // Unified Editing Logic
#if TENSIO_SPLINES_SUPPORT
            if (controller.InitMode == RopeController.RopeInitMode.Spline)
            {
                DrawSplinePointSelection(controller);
            }
            else
#endif
            {
                HandleBoxSelection(controller);
                HandleInput(controller);
                DrawGhostInsertion(controller);
                DrawPointSelection(controller);
            }
            DrawVolumePreview(controller);
            
            // Allow deselecting by clicking empty space
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && !Event.current.alt && !_isBoxSelecting) 
            {
                 // handled by Handles.Button return values. 
            }
        }

        private void HandlePinPicking(RopeController controller)
        {
             // Change cursor
             if (SceneView.lastActiveSceneView != null) 
                EditorGUIUtility.AddCursorRect(SceneView.lastActiveSceneView.position, MouseCursor.ArrowPlus);
             
             Event e = Event.current;
             int id = GUIUtility.GetControlID(FocusType.Passive);
             
             // Prevent other interactions
             if (e.type == EventType.Layout || e.type == EventType.Repaint)
             {
                 HandleUtility.AddDefaultControl(id);
             }

             if (e.type == EventType.MouseDown && e.button == 0)
             {
                  Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                  if (Physics.Raycast(ray, out RaycastHit hit))
                  {
                       Undo.RecordObject(controller, "Pick Pin Target");
                       var pt = controller.ControlPoints[_pickingIndex];
                       pt.PinTarget = hit.transform;
                       pt.PinOffset = hit.transform.InverseTransformPoint(hit.point);
                       pt.IsPinned = true;
                       
                       // Snap Control Point to hit position
                       pt.Position = hit.point;
                       
                       controller.RegeneratePinsFromControlPoints();
                       controller.RefreshParticlesFromBezier();
                       
                       _isPickingTarget = false;
                       _pickingIndex = -1;
                       GUIUtility.hotControl = 0;
                       e.Use();
                  }
             }
             else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
             {
                  _isPickingTarget = false;
                  _pickingIndex = -1;
                  e.Use();
             }
             
             // Visual Feedback
             Handles.BeginGUI();
             Vector2 mouse = e.mousePosition;
             Rect labelRect = new Rect(mouse.x + 15, mouse.y, 150, 20);
             GUI.Label(labelRect, "Click Object to Pin", EditorStyles.whiteLabel);
             Handles.EndGUI();
        }
        
        private void HandleBoxSelection(RopeController controller)
        {
            if (!_boxSelectionMode) return;

            Event e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);

            // Force consume input to prevent other tools from interfering
            if (e.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(id);
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                _isBoxSelecting = true;
                _boxSelectStart = e.mousePosition;
                GUIUtility.hotControl = id;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _isBoxSelecting)
            {
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _isBoxSelecting)
            {
                _isBoxSelecting = false;
                GUIUtility.hotControl = 0;
                SelectPointsInBox(controller, _boxSelectStart, e.mousePosition);
                _boxSelectionMode = false;
                e.Use();
            }
            else if (e.type == EventType.Repaint && _isBoxSelecting)
            {
                Vector2 min = Vector2.Min(_boxSelectStart, e.mousePosition);
                Vector2 max = Vector2.Max(_boxSelectStart, e.mousePosition);
                Rect r = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                
                Handles.BeginGUI();
                GUI.color = new Color(0f, 1f, 1f, 0.2f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = Color.white;
                Handles.EndGUI();
            }
        }
        
        private void SelectPointsInBox(RopeController controller, Vector2 start, Vector2 end)
        {
            Vector2 min = Vector2.Min(start, end);
            Vector2 max = Vector2.Max(start, end);
            Rect r = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            
            // If small drag, treat as deselect all (click on empty space)
            if (r.width < 5 && r.height < 5)
            {
                _selectedIndices.Clear();
                Repaint();
                return;
            }
            
            _selectedIndices.Clear();
            for(int i=0; i<controller.ControlPoints.Count; i++)
            {
                Vector2 screenPos = HandleUtility.WorldToGUIPoint(controller.ControlPoints[i].Position);
                if (r.Contains(screenPos))
                {
                    _selectedIndices.Add(i);
                }
            }
            Repaint();
        }

        private void DrawGhostInsertion(RopeController controller)
        {
            if (!Event.current.control) return;
            
            // Force repaint to follow mouse when holding Ctrl
            if (Event.current.type == EventType.MouseMove) SceneView.RepaintAll();

            Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
            
            // Replicate the logic from HandleInput but for visualization
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                float t;
                int segIndex = GetClosestSegment(controller, ray, out t);
                
                if (segIndex != -1)
                {
                    var pPrev = controller.ControlPoints[segIndex];
                    var pNext = controller.ControlPoints[segIndex + 1];
                    Vector3 splitPos = EvaluateCubicBezierLocal(pPrev, pNext, t);
                    
                    Handles.color = new Color(0.5f, 1f, 0.5f, 0.6f); 
                    Handles.SphereHandleCap(0, splitPos, Quaternion.identity, HandleUtility.GetHandleSize(splitPos) * 0.15f, EventType.Repaint);
                    Handles.DrawDottedLine(splitPos, pPrev.Position, 4f);
                    Handles.DrawDottedLine(splitPos, pNext.Position, 4f);
                }
                else
                {
                    // End point addition
                    Handles.color = new Color(0.5f, 1f, 0.5f, 0.6f);
                    Vector3 pos = hit.point + hit.normal * controller.Settings.ParticleRadius;
                    Handles.SphereHandleCap(0, pos, Quaternion.identity, HandleUtility.GetHandleSize(pos) * 0.15f, EventType.Repaint);
                    
                    if (controller.ControlPoints.Count > 0)
                    {
                        Handles.DrawDottedLine(pos, controller.ControlPoints[controller.ControlPoints.Count - 1].Position, 4f);
                    }
                }
            }
        }
        
#if TENSIO_SPLINES_SUPPORT
        private void DrawSplinePointSelection(RopeController controller)
        {
            if (controller.SplineReference == null || controller.SplineReference.Spline == null) return;
            var spline = controller.SplineReference.Spline;
            if (spline.Count == 0) return;

            int currentSig = GetSplineSignature(controller.SplineReference);
            if (currentSig != _lastSplineSignature)
            {
                _lastSplineSignature = currentSig;
                controller.RefreshParticlesFromSpline();
                EditorUtility.SetDirty(controller);
            }

            // Draw Curve Preview
            if (controller.EditorPreviewPositions != null)
            {
                Handles.color = Color.yellow;
                Handles.DrawAAPolyLine(4f, controller.EditorPreviewPositions);
                
                if (controller.InitSettings.IsClosedLoop && controller.EditorPreviewPositions.Length > 0)
                {
                    Handles.color = new Color(1f, 1f, 0f, 0.5f);
                    Handles.DrawDottedLine(controller.EditorPreviewPositions[controller.EditorPreviewPositions.Length - 1], controller.EditorPreviewPositions[0], 4f);
                }
            }

            Matrix4x4 localToWorld = controller.SplineReference.transform.localToWorldMatrix;

            // Draw Handles for each Knot
            for (int i = 0; i < spline.Count; i++)
            {
                Vector3 worldPos = localToWorld.MultiplyPoint3x4(spline[i].Position);
                float size = HandleUtility.GetHandleSize(worldPos) * 0.15f;
                
                bool isPinned = controller.PinnedSplineKnots.Contains(i);
                
                // Color Logic
                Color handleColor = Color.white;
                if (isPinned) handleColor = Color.red;
                else if (i == 0) handleColor = Color.green;
                else if (i == spline.Count - 1) handleColor = Color.red * 0.5f + Color.white * 0.5f;
                
                Handles.color = handleColor;

                // Shape Logic: Cube for Pinned, Sphere for Free
                if (isPinned)
                {
                    if (Handles.Button(worldPos, Quaternion.identity, size, size, Handles.CubeHandleCap))
                    {
                        Undo.RecordObject(controller, "Toggle Spline Pin");
                        controller.PinnedSplineKnots.Remove(i);
                        controller.RefreshParticlesFromSpline();
                        EditorUtility.SetDirty(controller);
                    }
                }
                else
                {
                    if (Handles.Button(worldPos, Quaternion.identity, size, size, Handles.SphereHandleCap))
                    {
                        Undo.RecordObject(controller, "Toggle Spline Pin");
                        controller.PinnedSplineKnots.Add(i);
                        controller.RefreshParticlesFromSpline();
                        EditorUtility.SetDirty(controller);
                    }
                }
                
                Handles.Label(worldPos, isPinned ? $"Pin {i}" : $"Knot {i}");
            }
        }

        private int GetSplineSignature(SplineContainer container)
        {
            if (container == null || container.Spline == null) return 0;
            unchecked
            {
                int hash = 17;
                var tr = container.transform;
                hash = hash * 31 + tr.position.GetHashCode();
                hash = hash * 31 + tr.rotation.GetHashCode();
                hash = hash * 31 + tr.lossyScale.GetHashCode();
                var spline = container.Spline;
                int count = spline.Count;
                hash = hash * 31 + count;
                for (int i = 0; i < count; i++)
                {
                    var knot = spline[i];
                    hash = hash * 31 + knot.Position.GetHashCode();
                    hash = hash * 31 + knot.TangentIn.GetHashCode();
                    hash = hash * 31 + knot.TangentOut.GetHashCode();
                }
                return hash;
            }
        }
#endif

        private void DrawPointSelection(RopeController controller)
        {
            if (controller.ControlPoints == null) return;
            
            // Draw Curve
            if (controller.EditorPreviewPositions != null)
            {
                Handles.color = Color.yellow;
                Handles.DrawAAPolyLine(4f, controller.EditorPreviewPositions);
                
                if (controller.InitSettings.IsClosedLoop && controller.EditorPreviewPositions.Length > 0)
                {
                    Handles.color = new Color(1f, 1f, 0f, 0.5f);
                    Handles.DrawDottedLine(controller.EditorPreviewPositions[controller.EditorPreviewPositions.Length - 1], controller.EditorPreviewPositions[0], 4f);
                }
            }

            // 1. Draw Handles & Detect Selection
            for (int i = 0; i < controller.ControlPoints.Count; i++)
            {
                var point = controller.ControlPoints[i];
                float size = HandleUtility.GetHandleSize(point.Position) * 0.15f;
                
                bool isPinned = point.IsPinned;
                
                // Color Logic
                Color handleColor = Color.white;
                if (isPinned) handleColor = Color.red;
                else if (i == 0) handleColor = Color.green;
                else if (i == controller.ControlPoints.Count - 1) handleColor = Color.red * 0.5f + Color.white * 0.5f;
                
                Handles.color = handleColor;

                // Shape Logic: Cube for Pinned, Sphere for Free
                if (isPinned)
                {
                    if (Handles.Button(point.Position, Quaternion.identity, size, size, Handles.CubeHandleCap))
                    {
                        HandleClickSelection(i);
                    }
                }
                else
                {
                    if (Handles.Button(point.Position, Quaternion.identity, size, size, Handles.SphereHandleCap))
                    {
                        HandleClickSelection(i);
                    }
                }
                
                if (_selectedIndices.Contains(i))
                {
                    Handles.color = Color.yellow;
                    Handles.DrawWireDisc(point.Position, SceneView.currentDrawingSceneView.camera.transform.forward, size * 1.5f);
                }

                Handles.Label(point.Position, isPinned ? $"Pin {i}" : $"CP {i}");

                // ... (Context Menu Logic remains similar but simplified if needed) ...
                if (Event.current.type == EventType.MouseDown && Event.current.button == 1)
                {
                    float clickDist = HandleUtility.DistanceToCircle(point.Position, size);
                    if (clickDist < 5f)
                    {
                        GenericMenu menu = new GenericMenu();
                        int idx = i;
                        
                        menu.AddItem(new GUIContent("Delete Point"), false, () => DeletePoint(controller, idx));
                        menu.AddItem(new GUIContent("Reset Tangents"), false, () => ResetTangents(controller, idx));
                        menu.AddSeparator("");
                        menu.AddItem(new GUIContent(point.IsPinned ? "Unpin Point" : "Pin Point"), false, () => {
                            Undo.RecordObject(controller, "Toggle Pin");
                            point.IsPinned = !point.IsPinned;
                            if (point.IsPinned && point.PinTarget == null) point.PinTarget = controller.transform; // Default
                            controller.RegeneratePinsFromControlPoints();
                        });

                        if (idx > 0)
                            menu.AddItem(new GUIContent("Subdivide Previous Segment"), false, () => SubdivideSegment(controller, idx - 1));
                        
                        menu.ShowAsContext();
                        Event.current.Use();
                    }
                }

                // --- TANGENTS ---
                bool isSelected = _selectedIndices.Contains(i);
                if (isSelected || _showAllTangents)
                {
                    Handles.color = new Color(0.7f, 0.7f, 0.7f, 0.5f);
                    Handles.DrawLine(point.Position, point.Position + point.TangentIn);
                    Handles.DrawLine(point.Position, point.Position + point.TangentOut);

                    if (isSelected && _selectedIndices.Count == 1)
                    {
                        // Tangent In
                        Vector3 posIn = point.Position + point.TangentIn;
                        float tanSize = HandleUtility.GetHandleSize(posIn) * 0.1f;
                        Handles.color = (_selectionType == SelectionType.TangentIn) ? Color.yellow : Color.cyan;
                        if (Handles.Button(posIn, Quaternion.identity, tanSize, tanSize, Handles.SphereHandleCap))
                        {
                            _selectionType = SelectionType.TangentIn;
                            Repaint();
                        }
                        
                        // Tangent Out
                        Vector3 posOut = point.Position + point.TangentOut;
                        float tanSizeOut = HandleUtility.GetHandleSize(posOut) * 0.1f;
                        Handles.color = (_selectionType == SelectionType.TangentOut) ? Color.yellow : Color.cyan;
                        if (Handles.Button(posOut, Quaternion.identity, tanSizeOut, tanSizeOut, Handles.SphereHandleCap))
                        {
                            _selectionType = SelectionType.TangentOut;
                            Repaint();
                        }
                    }
                    else
                    {
                        Handles.color = Color.cyan;
                        Handles.SphereHandleCap(0, point.Position + point.TangentIn, Quaternion.identity, HandleUtility.GetHandleSize(point.Position)*0.05f, EventType.Repaint);
                        Handles.SphereHandleCap(0, point.Position + point.TangentOut, Quaternion.identity, HandleUtility.GetHandleSize(point.Position)*0.05f, EventType.Repaint);
                    }
                }
            }

            // 2. Draw Gizmo for Active Selection
            if (_selectedIndices.Count > 0)
            {
                // Calculate Center of Selection
                Vector3 center = Vector3.zero;
                int count = 0;
                foreach(int idx in _selectedIndices)
                {
                    if(idx >= 0 && idx < controller.ControlPoints.Count)
                    {
                        center += controller.ControlPoints[idx].Position;
                        count++;
                    }
                }
                if(count > 0) center /= count;

                EditorGUI.BeginChangeCheck();
                Vector3 newCenter = center;

                if (_selectionType == SelectionType.Anchor)
                {
                    newCenter = Handles.PositionHandle(center, Tools.handleRotation);
                }
                else if (_selectionType == SelectionType.TangentIn && _selectedIndices.Count == 1)
                {
                     int idx = -1; foreach(int i in _selectedIndices) idx = i;
                     var pt = controller.ControlPoints[idx];
                     Vector3 tPos = pt.Position + pt.TangentIn;
                     Vector3 newTPos = Handles.PositionHandle(tPos, Tools.handleRotation);
                     if (EditorGUI.EndChangeCheck())
                     {
                         Undo.RecordObject(controller, "Move Tangent");
                         pt.TangentIn = newTPos - pt.Position;
                         EnforceTangentModes(pt, true);
                         controller.RefreshParticlesFromBezier();
                         EditorUtility.SetDirty(controller);
                     }
                     return;
                }
                else if (_selectionType == SelectionType.TangentOut && _selectedIndices.Count == 1)
                {
                     int idx = -1; foreach(int i in _selectedIndices) idx = i;
                     var pt = controller.ControlPoints[idx];
                     Vector3 tPos = pt.Position + pt.TangentOut;
                     Vector3 newTPos = Handles.PositionHandle(tPos, Tools.handleRotation);
                     if (EditorGUI.EndChangeCheck())
                     {
                         Undo.RecordObject(controller, "Move Tangent");
                         pt.TangentOut = newTPos - pt.Position;
                         EnforceTangentModes(pt, false);
                         controller.RefreshParticlesFromBezier();
                         EditorUtility.SetDirty(controller);
                     }
                     return;
                }

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(controller, "Move Bezier Handles");
                    
                    Vector3 delta = newCenter - center;
                    
                    // Magnet Mode Snapping
                    if (_magnetMode && _selectionType == SelectionType.Anchor)
                    {
                        Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
                        if (Physics.Raycast(ray, out RaycastHit hit))
                        {
                            Vector3 snapPos = hit.point + hit.normal * controller.Settings.ParticleRadius;
                            delta = snapPos - center;
                            
                            Handles.color = Color.green;
                            Handles.DrawWireDisc(hit.point, hit.normal, 0.2f);
                            Handles.DrawLine(hit.point, snapPos);
                        }
                    }
                    
                    if (_selectionType == SelectionType.Anchor)
                    {
                        bool startMoved = false;
                        foreach(int idx in _selectedIndices)
                        {
                            if(idx < 0 || idx >= controller.ControlPoints.Count) continue;
                            
                            controller.ControlPoints[idx].Position += delta;
                            
                            // Update Offset if Pinned
                            if (controller.ControlPoints[idx].IsPinned && controller.ControlPoints[idx].PinTarget != null)
                            {
                                controller.ControlPoints[idx].PinOffset = controller.ControlPoints[idx].PinTarget.InverseTransformPoint(controller.ControlPoints[idx].Position);
                            }
                            
                            if (idx == 0 && _lockStartToTransform) startMoved = true;
                        }
                        
                        if (startMoved)
                        {
                            Undo.RecordObject(controller.transform, "Move Rope Transform");
                            controller.transform.position += delta;
                            
                            for (int k = 1; k < controller.ControlPoints.Count; k++)
                            {
                                if (!_selectedIndices.Contains(k))
                                {
                                    controller.ControlPoints[k].Position += delta;
                                }
                            }
                        }
                    }
                    
                    controller.RefreshParticlesFromBezier();
                    EditorUtility.SetDirty(controller);
                }
            }

            DrawSelectedPointOverlay(controller);
        }

        private void HandleClickSelection(int i)
        {
            if (Event.current.shift)
            {
                if (_selectedIndices.Contains(i)) _selectedIndices.Remove(i);
                else _selectedIndices.Add(i);
            }
            else if (!_selectedIndices.Contains(i))
            {
                _selectedIndices.Clear();
                _selectedIndices.Add(i);
            }
            
            _selectionType = SelectionType.Anchor;
            Repaint();
        }

        private void DrawSelectedPointOverlay(RopeController controller)
        {
            if (_selectedIndices.Count == 0) return;
            
            // For overlay, just pick the first one if multiple are selected, or show "Multiple Selected"
            int index = -1; foreach(int i in _selectedIndices) { index = i; break; }

            if (index < 0 || index >= controller.ControlPoints.Count) return;

            Handles.BeginGUI();
            
            // First time init or off-screen safety
            if (_overlayRect.x < 0 || _overlayRect.x > Screen.width) _overlayRect.x = Screen.width - 260;
            if (_overlayRect.y < 0 || _overlayRect.y > Screen.height) _overlayRect.y = Screen.height - 250;
            
            string title = ""; // Custom Header used inside
            
            // Use GUILayout.Window for a draggable, auto-layout window
            _overlayRect = GUILayout.Window(2024, _overlayRect, (id) => DrawOverlayContent(id, controller, index), title, GUILayout.Width(240));

            // Block clicks from passing through to the scene
            if (_overlayRect.Contains(Event.current.mousePosition))
            {
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
                // Also consume scroll events to prevent scene zooming while hovering window
                if (Event.current.type == EventType.ScrollWheel) Event.current.Use();
            }

            Handles.EndGUI();
        }

        private void DrawOverlayContent(int id, RopeController controller, int index)
        {
            var point = controller.ControlPoints[index];

            // --- Header & Collapse ---
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
             string title = _selectedIndices.Count > 1 ? $"Selection ({_selectedIndices.Count})" : $"Point {index}";
             GUILayout.Label(title, EditorStyles.boldLabel);
             GUILayout.FlexibleSpace();
             
             // Docking
             if (GUILayout.Button(EditorGUIUtility.IconContent("pane options"), EditorStyles.toolbarButton, GUILayout.Width(22)))
             {
                 GenericMenu menu = new GenericMenu();
                 menu.AddItem(new GUIContent("Snap Top Left"), false, () => SnapWindow(ref _overlayRect, TextAnchor.UpperLeft));
                 menu.AddItem(new GUIContent("Snap Top Right"), false, () => SnapWindow(ref _overlayRect, TextAnchor.UpperRight));
                 menu.AddItem(new GUIContent("Snap Bottom Left"), false, () => SnapWindow(ref _overlayRect, TextAnchor.LowerLeft));
                 menu.AddItem(new GUIContent("Snap Bottom Right"), false, () => SnapWindow(ref _overlayRect, TextAnchor.LowerRight));
                 menu.ShowAsContext();
             }

             if (GUILayout.Button(_overlayCollapsed ? "▼" : "▲", EditorStyles.toolbarButton, GUILayout.Width(22)))
             {
                 _overlayCollapsed = !_overlayCollapsed;
                 _overlayRect.height = 0; 
             }
            GUILayout.EndHorizontal();

            if (_overlayCollapsed) { GUI.DragWindow(); return; }

            if (_selectedIndices.Count > 1)
            {
                EditorGUILayout.HelpBox($"Multi-selection active ({_selectedIndices.Count}).", MessageType.Info);
                GUI.DragWindow();
                return; 
            }

            EditorGUI.BeginChangeCheck();

            // --- 1. Pinning Section ---
            GUILayout.Space(2);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                GUILayout.BeginHorizontal();
                 bool newPinned = EditorGUILayout.ToggleLeft("Pinned", point.IsPinned, GUILayout.Width(60));
                 GUILayout.FlexibleSpace();
                 
                 // Target Object Field
                 GUI.enabled = newPinned;
                 Transform newTarget = (Transform)EditorGUILayout.ObjectField(point.PinTarget, typeof(Transform), true);
                 GUI.enabled = true;
                 
                 // Picker
                 if (newPinned)
                 {
                     GUI.backgroundColor = (_isPickingTarget && _pickingIndex == index) ? Color.green : Color.white;
                     if (GUILayout.Button(EditorGUIUtility.IconContent("d_eyeDropper.Large"), EditorStyles.miniButton, GUILayout.Width(24), GUILayout.Height(18)))
                     {
                         if (_isPickingTarget && _pickingIndex == index) { _isPickingTarget = false; _pickingIndex = -1; }
                         else { _isPickingTarget = true; _pickingIndex = index; }
                     }
                     GUI.backgroundColor = Color.white;
                 }
                GUILayout.EndHorizontal();
                
                // Offset (Only if Pinned)
                bool offsetChanged = false;
                Vector3 editedOffset = point.PinOffset;

                if (newPinned)
                {
                    if (newTarget == null)
                    {
                        EditorGUILayout.LabelField("   Attached to World Space", EditorStyles.miniLabel);
                    }
                    else
                    {
                        // Indent for visual hierarchy
                        EditorGUI.indentLevel++;
                        editedOffset = EditorGUILayout.Vector3Field("Offset", point.PinOffset);
                        if (editedOffset != point.PinOffset) offsetChanged = true;
                        EditorGUI.indentLevel--;
                    }
                }
                
                // Handle Pin Changes immediately inside check logic later
                if (newPinned != point.IsPinned || newTarget != point.PinTarget)
                {
                    point.IsPinned = newPinned;
                    point.PinTarget = newTarget;

                    if (point.IsPinned && point.PinTarget != null) point.PinOffset = point.PinTarget.InverseTransformPoint(point.Position);
                    controller.RegeneratePinsFromControlPoints();
                }
                
                // Propagate Offset Change
                if (offsetChanged)
                {
                    point.PinOffset = editedOffset;
                    if (point.PinTarget != null) point.Position = point.PinTarget.TransformPoint(editedOffset);
                }
            }
            EditorGUILayout.EndVertical();

            // --- 2. Transform Section ---
            GUILayout.Space(2);
            GUILayout.Label("Transform", EditorStyles.miniBoldLabel);
            
            Vector3 newPos = EditorGUILayout.Vector3Field("Position", point.Position);
            
            // --- 3. Curve Control ---
            GUILayout.Space(2);
            GUILayout.Label("Curve", EditorStyles.miniBoldLabel);
            
            // Mode Toolbar
            RopeController.BezierControlMode newMode = point.Mode;
            GUILayout.BeginHorizontal();
             GUILayout.Label("Mode", GUILayout.Width(50));
             
             int currentIdx = 0; // 0=Mirrored, 1=Aligned, 2=Free
             if (point.Mode == RopeController.BezierControlMode.Aligned) currentIdx = 1;
             else if (point.Mode == RopeController.BezierControlMode.Free) currentIdx = 2;
             
             int newIdx = GUILayout.Toolbar(currentIdx, new string[] { "Mirrored", "Aligned", "Free" }, EditorStyles.miniButton, GUILayout.Height(18));
             
             if (newIdx != currentIdx)
             {
                 if (newIdx == 0) newMode = RopeController.BezierControlMode.Mirrored;
                 else if (newIdx == 1) newMode = RopeController.BezierControlMode.Aligned;
                 else newMode = RopeController.BezierControlMode.Free;
             }
            GUILayout.EndHorizontal();

            // Tangents
            EditorGUI.indentLevel++;
            Vector3 newTanIn = EditorGUILayout.Vector3Field("Tan In", point.TangentIn);
            Vector3 newTanOut = EditorGUILayout.Vector3Field("Tan Out", point.TangentOut);
            EditorGUI.indentLevel--;

            // --- 4. Actions ---
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
             if (GUILayout.Button("Focus", EditorStyles.miniButton))
             {
                 SceneView.lastActiveSceneView.Frame(new Bounds(point.Position, Vector3.one * 2f), false);
             }
             GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
             if (GUILayout.Button("Delete", EditorStyles.miniButton))
             {
                 DeletePoint(controller, index);
                 GUIUtility.ExitGUI(); // Stop drawing since index is invalid
             }
             GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();

            // --- Apply Changes ---
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(controller, "Edit Point Overlay");

                // Position Logic (with lock start check)
                if (newPos != point.Position)
                {
                    if (index == 0 && _lockStartToTransform)
                    {
                        Vector3 delta = newPos - point.Position;
                        controller.transform.position += delta;
                        for (int k = 1; k < controller.ControlPoints.Count; k++)
                            controller.ControlPoints[k].Position += delta;
                    }
                    point.Position = newPos;
                    if (point.IsPinned && point.PinTarget != null)
                        point.PinOffset = point.PinTarget.InverseTransformPoint(newPos);
                }

                // Mode Logic
                if (newMode != point.Mode)
                {
                     point.Mode = newMode;
                     EnforceTangentModes(point, true);
                }

                // Tangent Logic
                if (newTanIn != point.TangentIn)
                {
                    point.TangentIn = newTanIn;
                    EnforceTangentModes(point, true);
                }
                else if (newTanOut != point.TangentOut)
                {
                    point.TangentOut = newTanOut;
                    EnforceTangentModes(point, false);
                }

                controller.RefreshParticlesFromBezier();
                EditorUtility.SetDirty(controller);
            }

            GUI.DragWindow();
        }

        private void HandleInput(RopeController controller)
        {
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.control && e.button == 0)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                if (Physics.Raycast(ray, out RaycastHit hit))
                {
                    Undo.RecordObject(controller, "Edit Rope Points");

                    float t;
                    int segIndex = GetClosestSegment(controller, ray, out t);
                    
                    if (segIndex != -1)
                    {
                        var pPrev = controller.ControlPoints[segIndex];
                        var pNext = controller.ControlPoints[segIndex + 1];
                        Vector3 splitPos = EvaluateCubicBezierLocal(pPrev, pNext, t);
                        
                        var newPoint = new RopeController.BezierPoint(splitPos, Vector3.up, Vector3.down); 
                        Vector3 tangentDir = (pNext.Position - pPrev.Position).normalized;
                        float dist = Vector3.Distance(pPrev.Position, pNext.Position);
                        newPoint.TangentIn = -tangentDir * (dist * 0.2f);
                        newPoint.TangentOut = tangentDir * (dist * 0.2f);

                        controller.ControlPoints.Insert(segIndex + 1, newPoint);
                        _selectedIndices.Clear();
                        _selectedIndices.Add(segIndex + 1);
                        _selectionType = SelectionType.Anchor;
                    }
                    else
                    {
                        var last = controller.ControlPoints[controller.ControlPoints.Count - 1];
                        Vector3 pointPos = hit.point + hit.normal * controller.Settings.ParticleRadius;
                        Vector3 tanIn = (last.Position - hit.point).normalized * 0.5f;
                        controller.ControlPoints.Add(new RopeController.BezierPoint(pointPos, tanIn, -tanIn));
                        _selectedIndices.Clear();
                        _selectedIndices.Add(controller.ControlPoints.Count - 1);
                        _selectionType = SelectionType.Anchor;
                    }
                    
                    controller.RefreshParticlesFromBezier();
                    EditorUtility.SetDirty(controller);
                    e.Use();
                }
            }
            
            // Deselect if clicking away (and not handled by buttons)
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt && !e.control && !e.shift)
            {
            }
        }

        private void AutoSmoothSelected(RopeController c)
        {
            if (_selectedIndices.Count == 0) return;
            Undo.RecordObject(c, "Auto-Smooth");
            
            var sorted = new List<int>(_selectedIndices);
            sorted.Sort();

            foreach (int i in sorted)
            {
                if (i <= 0 || i >= c.ControlPoints.Count - 1) continue;

                var prev = c.ControlPoints[i - 1].Position;
                var next = c.ControlPoints[i + 1].Position;
                var curr = c.ControlPoints[i].Position;

                Vector3 tangentDir = (next - prev).normalized;
                float distToPrev = Vector3.Distance(curr, prev);
                float distToNext = Vector3.Distance(curr, next);

                c.ControlPoints[i].TangentIn = -tangentDir * (distToPrev * 0.33f);
                c.ControlPoints[i].TangentOut = tangentDir * (distToNext * 0.33f);
                c.ControlPoints[i].Mode = RopeController.BezierControlMode.Aligned;
            }
            c.RefreshParticlesFromBezier();
            EditorUtility.SetDirty(c);
        }

        private void StraightenSelection(RopeController c)
        {
            if (_selectedIndices.Count < 2) return;
            Undo.RecordObject(c, "Straighten Selection");
            
            var sorted = new List<int>(_selectedIndices);
            sorted.Sort();
            
            int first = sorted[0];
            int last = sorted[sorted.Count - 1];
            
            Vector3 startPos = c.ControlPoints[first].Position;
            Vector3 endPos = c.ControlPoints[last].Position;
            Vector3 lineDir = (endPos - startPos).normalized;
            float totalDist = Vector3.Distance(startPos, endPos);
            
            for (int i = 0; i < sorted.Count; i++)
            {
                int idx = sorted[i];
                float t = (float)i / (sorted.Count - 1);
                
                // Position
                c.ControlPoints[idx].Position = Vector3.Lerp(startPos, endPos, t);
                
                // Tangents
                float segmentLen = totalDist / (sorted.Count - 1);
                Vector3 tan = lineDir * (segmentLen * 0.33f);
                c.ControlPoints[idx].TangentIn = -tan;
                c.ControlPoints[idx].TangentOut = tan;
                c.ControlPoints[idx].Mode = RopeController.BezierControlMode.Aligned;
            }
            
            c.RefreshParticlesFromBezier();
            EditorUtility.SetDirty(c);
        }

        private void FlattenSelection(RopeController c, int axis)
        {
            if (_selectedIndices.Count == 0) return;
            Undo.RecordObject(c, "Flatten Axis");
            
            float avg = 0f;
            foreach(int i in _selectedIndices) avg += c.ControlPoints[i].Position[axis];
            avg /= _selectedIndices.Count;
            
            foreach(int i in _selectedIndices)
            {
                Vector3 pos = c.ControlPoints[i].Position;
                pos[axis] = avg;
                c.ControlPoints[i].Position = pos;
                
                Vector3 tIn = c.ControlPoints[i].TangentIn;
                Vector3 tOut = c.ControlPoints[i].TangentOut;
                tIn[axis] = 0;
                tOut[axis] = 0;
                c.ControlPoints[i].TangentIn = tIn;
                c.ControlPoints[i].TangentOut = tOut;
            }
            
            c.RefreshParticlesFromBezier();
            EditorUtility.SetDirty(c);
        }

        private void DeletePoint(RopeController c, int index)
        {
            if (c.ControlPoints.Count <= 2)
            {
                Debug.LogWarning("Cannot delete point. Rope must have at least 2 control points.");
                return;
            }
            
            Undo.RecordObject(c, "Delete Rope Point");
            c.ControlPoints.RemoveAt(index);
            
            // Adjust selection
            _selectedIndices.Clear();
            _selectionType = SelectionType.None;
            
            c.RefreshParticlesFromBezier();
            EditorUtility.SetDirty(c);
        }

        private void ResetTangents(RopeController c, int index)
        {
            Undo.RecordObject(c, "Reset Tangents");
            var pt = c.ControlPoints[index];
            pt.TangentIn = new Vector3(0, 1, 0);
            pt.TangentOut = new Vector3(0, -1, 0);
            pt.Mode = RopeController.BezierControlMode.Mirrored;
            
            // Try to align roughly with neighbors if possible for better default
            if (index > 0 && index < c.ControlPoints.Count - 1)
            {
                Vector3 dir = (c.ControlPoints[index + 1].Position - c.ControlPoints[index - 1].Position).normalized;
                pt.TangentIn = -dir;
                pt.TangentOut = dir;
            }
            
            c.RefreshParticlesFromBezier();
            EditorUtility.SetDirty(c);
        }

        private void SubdivideSegment(RopeController c, int segmentIndex)
        {
            Undo.RecordObject(c, "Subdivide Segment");
            
            int i0 = segmentIndex;
            int i1 = (segmentIndex + 1) % c.ControlPoints.Count;
            
            var p0 = c.ControlPoints[i0];
            var p1 = c.ControlPoints[i1];
            
            // Calculate midpoint on curve
            Vector3 midPos = EvaluateCubicBezierLocal(p0, p1, 0.5f);
            
            // Interpolate tangents roughly
            Vector3 tan = (p1.Position - p0.Position).normalized * (Vector3.Distance(p0.Position, p1.Position) * 0.2f);
            
            var newPoint = new RopeController.BezierPoint(midPos, -tan, tan);
            
            // Insert
            if (i1 == 0 && c.InitSettings.IsClosedLoop) // Appending to end effectively if segment is Last->0
            {
                c.ControlPoints.Add(newPoint);
            }
            else
            {
                c.ControlPoints.Insert(i1, newPoint);
            }
            
            c.RefreshParticlesFromBezier();
            EditorUtility.SetDirty(c);
        }

        private void AlignControlPoints(RopeController c)
        {
            if (c.ControlPoints.Count < 3) return;
            Undo.RecordObject(c, "Align Points");
            Vector3 start = c.ControlPoints[0].Position;
            Vector3 end = c.ControlPoints[c.ControlPoints.Count - 1].Position;
            for (int i = 1; i < c.ControlPoints.Count - 1; i++)
            {
                float t = (float)i / (c.ControlPoints.Count - 1);
                c.ControlPoints[i].Position = Vector3.Lerp(start, end, t);
            }
            c.RefreshParticlesFromBezier();
            EditorUtility.SetDirty(c);
        }

        private void SnapControlPoints(RopeController c)
        {
            Undo.RecordObject(c, "Snap Points Down");
            foreach (var p in c.ControlPoints)
            {
                if (Physics.Raycast(p.Position, Vector3.down, out RaycastHit hit))
                {
                    p.Position = hit.point + Vector3.up * c.Settings.ParticleRadius;
                }
            }
            c.RefreshParticlesFromBezier();
            EditorUtility.SetDirty(c);
        }

        private void SimulateGravity(RopeController controller)
        {
            // 0. Sync Pins
#if TENSIO_SPLINES_SUPPORT
            if (controller.InitMode == RopeController.RopeInitMode.Spline)
                controller.RegeneratePinsFromSpline();
            else
#endif
                controller.RegeneratePinsFromControlPoints();
            
            // 2. Setup Solver
            var solver = new RopeSolver(controller.ParticleCount, controller.CurrentLength, controller.transform.position, controller.RopeId);
            var pos = controller.EditorPreviewPositions;
            if (pos == null || pos.Length != controller.ParticleCount)
            {
#if TENSIO_SPLINES_SUPPORT
                if (controller.InitMode == RopeController.RopeInitMode.Spline)
                    controller.RefreshParticlesFromSpline();
                else
#endif
                    controller.RefreshParticlesFromBezier();
                pos = controller.EditorPreviewPositions;
            }
            if (pos == null || pos.Length != controller.ParticleCount) pos = new Vector3[controller.ParticleCount];

            var pNative = new Unity.Collections.NativeArray<float3>(controller.ParticleCount, Unity.Collections.Allocator.Temp);
            var invM = new Unity.Collections.NativeArray<float>(controller.ParticleCount, Unity.Collections.Allocator.Temp);
            
            for(int i=0; i<controller.ParticleCount; i++) {
                pNative[i] = pos[i];
                invM[i] = 1.0f; 
            }

            solver.SetState(pNative.ToArray(), pNative.ToArray(), new float3[controller.ParticleCount], invM.ToArray());

            // Apply Pin Constraints 
            if (controller.PinConstraints != null)
            {
                foreach (var pin in controller.PinConstraints)
                {
                    if (pin.ParticleIndex >= 0 && pin.ParticleIndex < controller.ParticleCount)
                    {
                        solver.SetInverseMass(pin.ParticleIndex, 0f);
                        Vector3 targetPos = (pin.Target != null) ? pin.Target.TransformPoint(pin.LocalOffset) : controller.transform.TransformPoint(pin.LocalOffset); 
                        
                        if (pin.Target != null) 
                            solver.SetParticlePosition(pin.ParticleIndex, targetPos);
                    }
                }
            }
            
            // 3. Run Simulation
            for (int i = 0; i < 120; i++) {
                solver.Step(0.016f, 4, new float3(0, -9.81f, 0), 0.001f, 0.2f, 0.05f, 0.01f, controller.PhysicsSettings.BendingStiffness, float3.zero, 0, 0, 0.1f, i*0.016f, default, 0, controller.PhysicsSettings.InterRopeStiffness, controller.PhysicsSettings.CollisionMask, controller.InitSettings.IsClosedLoop);
                solver.CompleteJobs();
            }

            var res = solver.Positions;
            Undo.RecordObject(controller, "Relax Rope");
            
            // 4. Update Control Points
            if (controller.InitMode == RopeController.RopeInitMode.Bezier)
            {
                for (int i = 0; i < controller.ControlPoints.Count; i++) {
                    float t = i / (float)(controller.ControlPoints.Count - 1);
                    // If the CP is pinned, don't move it!
                    if (controller.ControlPoints[i].IsPinned) continue;

                    int idx = Mathf.Clamp(Mathf.RoundToInt(t * (controller.ParticleCount - 1)), 0, controller.ParticleCount - 1);
                    controller.ControlPoints[i].Position = res[idx];
                }
                controller.RefreshParticlesFromBezier();
            }
#if TENSIO_SPLINES_SUPPORT
            else if (controller.InitMode == RopeController.RopeInitMode.Spline && controller.SplineReference != null)
            {
                var spline = controller.SplineReference.Spline;
                float totalLength = spline.GetLength();
                
                for (int i = 0; i < spline.Count; i++)
                {
                    if (controller.PinnedSplineKnots.Contains(i)) continue;

                    float accLen = 0f;
                    for (int k = 0; k < i; k++) accLen += spline.GetCurveLength(k);
                    
                    float t = totalLength > 0f ? accLen / totalLength : 0f;
                    int idx = Mathf.Clamp(Mathf.RoundToInt(t * (controller.ParticleCount - 1)), 0, controller.ParticleCount - 1);
                    
                    var knot = spline[i];
                    knot.Position = controller.SplineReference.transform.InverseTransformPoint(res[idx]);
                    spline[i] = knot;
                }
                controller.RefreshParticlesFromSpline();
            }
#endif
            
            EditorUtility.SetDirty(controller);
            SceneView.RepaintAll();
            
            solver.Dispose();
            pNative.Dispose();
            invM.Dispose();
        }

        private void SnapWindow(ref Rect r, TextAnchor anchor)
        {
            float padding = 10f;
            float topPadding = 24f; 
            
            float sw = Screen.width;
            float sh = Screen.height;
            
            if (SceneView.lastActiveSceneView != null)
            {
                sw = SceneView.lastActiveSceneView.position.width;
                sh = SceneView.lastActiveSceneView.position.height;
            }
            
            // Adjust for Editor chrome
            sh -= 45f; 

            // Ensure dimensions are valid for calculation
            // If window is collapsed or not yet drawn, width might be small.
            float w = r.width < 100 ? 250 : r.width;
            float h = r.height < 40 ? 100 : r.height;

            switch(anchor)
            {
                case TextAnchor.UpperLeft: 
                    r.x = padding; 
                    r.y = topPadding + padding; 
                    break;
                case TextAnchor.UpperRight: 
                    r.x = sw - w - padding; 
                    r.y = topPadding + padding; 
                    break;
                case TextAnchor.LowerLeft: 
                    r.x = padding; 
                    r.y = sh - h - padding; 
                    break;
                case TextAnchor.LowerRight: 
                    r.x = sw - w - padding; 
                    r.y = sh - h - padding; 
                    break;
            }
            
            // Ensure visibility
            r.x = Mathf.Clamp(r.x, 0, sw - 50);
            r.y = Mathf.Clamp(r.y, topPadding, sh - 50);
        }

        private Vector3[] GetParticlePositions(RopeController c)
        {
            if (c.EditorPreviewPositions != null && c.EditorPreviewPositions.Length == c.ParticleCount)
                return c.EditorPreviewPositions;
            return null;
        }

        private void UpdateRenderer(RopeController controller, RopeController.RopeInitSettings.RopeType type)
        {
            if (type == RopeController.RopeInitSettings.RopeType.Elastic)
            {
                var chain = controller.GetComponent<RopeChainRenderer>();
                if (chain) Undo.DestroyObjectImmediate(chain);
                if (!controller.GetComponent<RopeRenderer>())
                {
                    var rope = Undo.AddComponent<RopeRenderer>(controller.gameObject);
                    Material defMat = RopeTools.FindDefaultRopeMaterial();
                    if (defMat != null) rope.Material = defMat;
                }
            }
            else
            {
                var rope = controller.GetComponent<RopeRenderer>();
                if (rope) Undo.DestroyObjectImmediate(rope);
                if (!controller.GetComponent<RopeChainRenderer>())
                {
                    var chain = Undo.AddComponent<RopeChainRenderer>(controller.gameObject);
                    Material defMat = RopeTools.FindDefaultChainMaterial();
                    if (defMat != null) chain.LinkMaterial = defMat;
                    Mesh defMesh = RopeTools.FindDefaultChainMesh();
                    if (defMesh != null) chain.LinkMesh = defMesh;
                }
            }
        }

        // --- GUI DRAWING HELPERS ---
        private void DrawBrandedHeader(RopeController c)
        {
            Rect r = EditorGUILayout.GetControlRect(false, 60);
            EditorGUI.DrawRect(r, new Color(0.12f, 0.12f, 0.14f));
            
            GUIStyle title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 24, normal = { textColor = new Color(0.3f, 0.7f, 1.0f) }, alignment = TextAnchor.MiddleLeft };
            GUI.Label(new Rect(r.x + 10, r.y + 10, r.width, 30), "TENSIO", title);
            
            GUIStyle ver = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.gray } };
            GUI.Label(new Rect(r.x + 10, r.y + 40, r.width, 20), "v1.2.3 | High-Performance XPBD Rope", ver);
            
            // Performance Budget
            int cost = c.ParticleCount * c.PhysicsSettings.SubSteps;
            float t = Mathf.InverseLerp(100, 1000, cost);
            Color barColor = Color.Lerp(Color.green, Color.red, t);
            Rect barRect = new Rect(r.x + r.width - 150, r.y + 40, 140, 12);
            EditorGUI.DrawRect(barRect, new Color(0.1f, 0.1f, 0.1f));
            EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, barRect.width * t, barRect.height), barColor);
            EditorGUI.LabelField(barRect, $"Load: {cost}", new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } });
        }

        private bool DrawSectionHeader(string title, ref bool state)
        {
            EditorGUILayout.Space(2);
            Rect r = EditorGUILayout.GetControlRect(false, 24);
            
            EditorGUI.DrawRect(r, new Color(0.2f, 0.2f, 0.2f));
            EditorGUI.DrawRect(new Rect(r.x, r.y, 4, r.height), new Color(0.3f, 0.7f, 1.0f));

            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) state = !state;
            
            GUIStyle style = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }, fontSize = 11, alignment = TextAnchor.MiddleLeft };
            EditorGUI.LabelField(new Rect(r.x + 10, r.y, r.width, r.height), (state ? "▼ " : "► ") + title, style);
            
            return state;
        }

        private void InitStyles()
        {
            if (_cardStyle == null)
            {
                _cardStyle = new GUIStyle(EditorStyles.helpBox);
                _cardStyle.padding = new RectOffset(10, 10, 10, 10);
            }
        }
        
        private bool IsHoveringPoint(Vector3 worldPos, float pixelRadius)
        {
             Vector2 mousePos = Event.current.mousePosition;
             return Vector2.Distance(HandleUtility.WorldToGUIPoint(worldPos), mousePos) < pixelRadius;
        }

        private int GetClosestSegment(RopeController controller, Ray ray, out float bestT)
        {
            bestT = 0;
            int bestSeg = -1;
            float closestDist = 0.5f;

            for(int i=0; i<controller.ControlPoints.Count - 1; i++)
            {
                int samples = 10;
                Vector3 prev = controller.ControlPoints[i].Position;
                for(int s=1; s<=samples; s++)
                {
                    float t = s / (float)samples;
                    Vector3 curr = EvaluateCubicBezierLocal(controller.ControlPoints[i], controller.ControlPoints[i+1], t);
                    
                    float d = Vector3.Cross(ray.direction, curr - ray.origin).magnitude;
                    if (d < closestDist)
                    {
                        closestDist = d;
                        bestSeg = i;
                        bestT = t;
                    }
                    prev = curr;
                }
            }
            return bestSeg;
        }

        private Vector3 EvaluateCubicBezierLocal(RopeController.BezierPoint pStart, RopeController.BezierPoint pEnd, float t)
        {
            Vector3 p0 = pStart.Position;
            Vector3 p3 = pEnd.Position;
            Vector3 p1 = p0 + pStart.TangentOut;
            Vector3 p2 = p3 + pEnd.TangentIn;
            float u = 1 - t;
            return u*u*u*p0 + 3*u*u*t*p1 + 3*u*t*t*p2 + t*t*t*p3;
        }
        
        private void EnforceTangentModes(RopeController.BezierPoint point, bool movedIn)
        {
            if (point.Mode == RopeController.BezierControlMode.Free) return;
            Vector3 source = movedIn ? point.TangentIn : point.TangentOut;
            Vector3 target = movedIn ? point.TangentOut : point.TangentIn;
            if (point.Mode == RopeController.BezierControlMode.Mirrored) target = -source;
            else if (point.Mode == RopeController.BezierControlMode.Aligned)
            {
                float len = target.magnitude;
                if (len < 0.001f) len = source.magnitude;
                target = -source.normalized * len;
            }
            if (movedIn) point.TangentOut = target;
            else point.TangentIn = target;
        }

        private void SetMode(RopeController.BezierPoint point, RopeController.BezierControlMode mode)
        {
            point.Mode = mode;
            EnforceTangentModes(point, true);
        }
        
        private void DrawVolumePreview(RopeController controller)
        {
            float r = controller.Settings.ParticleRadius;
            Handles.color = new Color(0.4f, 1f, 0.4f, 0.2f);
            if (controller.EditorPreviewPositions != null)
            {
                int step = 2; 
                for(int i=0; i<controller.EditorPreviewPositions.Length - 1; i += step)
                {
                    DrawSegment(controller.EditorPreviewPositions[i], controller.EditorPreviewPositions[i+1], r);
                }
                
                // Draw Loop Segment
                if (controller.InitSettings.IsClosedLoop && controller.EditorPreviewPositions.Length > 0)
                {
                    DrawSegment(controller.EditorPreviewPositions[controller.EditorPreviewPositions.Length - 1], controller.EditorPreviewPositions[0], r);
                }
            }
        }

        private void DrawSegment(Vector3 pos, Vector3 next, float r)
        {
            Vector3 dir = (next - pos).normalized;
            if (dir == Vector3.zero) return;
            
            Handles.DrawWireDisc(pos, dir, r);
            
            Vector3 up = Vector3.Cross(dir, Vector3.right).normalized * r;
            if (up == Vector3.zero) up = Vector3.Cross(dir, Vector3.up).normalized * r;
            
            Vector3 right = Vector3.Cross(dir, up).normalized * r;
            
            Handles.DrawLine(pos + up, next + up);
            Handles.DrawLine(pos - up, next - up);
            Handles.DrawLine(pos + right, next + right);
            Handles.DrawLine(pos - right, next - right);
        }

        private void BakeToStaticMesh(RopeController controller)
        {
            Mesh staticMesh = null;

            RopeRenderer ropeRenderer = controller.GetComponent<RopeRenderer>();
            RopeChainRenderer chainRenderer = controller.GetComponent<RopeChainRenderer>();

            if (ropeRenderer != null && ropeRenderer.enabled)
            {
                staticMesh = ropeRenderer.BakeMesh();
            }
            else if (chainRenderer != null && chainRenderer.enabled)
            {
                staticMesh = chainRenderer.BakeMesh();
            }

            if (staticMesh == null)
            {
                Debug.LogWarning("No active mesh data found on RopeRenderer or RopeChainRenderer. Cannot bake. Ensure the simulation is running or has run at least once.");
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject("Save Static Mesh", "StaticRopeMesh", "asset", "Please enter a file name to save the mesh to");
            if (string.IsNullOrEmpty(path))
            {
                DestroyImmediate(staticMesh);
                return;
            }

            MeshUtility.Optimize(staticMesh);

            AssetDatabase.CreateAsset(staticMesh, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Instantiate in scene
            GameObject bakedGo = new GameObject(controller.gameObject.name + "_Baked");
            bakedGo.transform.position = controller.transform.position;
            bakedGo.transform.rotation = controller.transform.rotation;
            bakedGo.transform.localScale = controller.transform.localScale;

            MeshFilter newFilter = bakedGo.AddComponent<MeshFilter>();
            newFilter.sharedMesh = staticMesh;

            MeshRenderer newRenderer = bakedGo.AddComponent<MeshRenderer>();
            MeshRenderer oldRenderer = controller.GetComponent<MeshRenderer>();
            
            // Try to assign material properly
            if (ropeRenderer != null && ropeRenderer.enabled && ropeRenderer.Material != null)
            {
                newRenderer.sharedMaterial = ropeRenderer.Material;
            }
            else if (oldRenderer != null && oldRenderer.sharedMaterials.Length > 0)
            {
                newRenderer.sharedMaterials = oldRenderer.sharedMaterials;
            }

            // Save as Prefab so it retains material info when dragged into the scene
            string prefabPath = path.Replace(".asset", ".prefab");
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(bakedGo, prefabPath);
            
            Undo.RegisterCreatedObjectUndo(bakedGo, "Bake Static Mesh");
            
            // Delay selection to prevent breaking the layout stack in OnInspectorGUI
            EditorApplication.delayCall += () =>
            {
                Selection.activeGameObject = bakedGo;
            };
            
            Debug.Log($"Successfully baked rope mesh to {path} and prefab to {prefabPath}", prefab);
        }

        public static void FrameRope(RopeController controller)
        {
            if (controller == null) return;
            Bounds bounds;
            if (controller.ControlPoints != null && controller.ControlPoints.Count > 0)
            {
                bounds = new Bounds(controller.ControlPoints[0].Position, Vector3.one * 0.2f);
                for (int i = 1; i < controller.ControlPoints.Count; i++)
                {
                    bounds.Encapsulate(controller.ControlPoints[i].Position);
                }
            }
            else if (controller.EditorPreviewPositions != null && controller.EditorPreviewPositions.Length > 0)
            {
                bounds = new Bounds(controller.EditorPreviewPositions[0], Vector3.one * 0.2f);
                for (int i = 1; i < controller.EditorPreviewPositions.Length; i++)
                {
                    bounds.Encapsulate(controller.EditorPreviewPositions[i]);
                }
            }
            else
            {
                bounds = new Bounds(controller.transform.position, Vector3.one * 2f);
            }

            bounds.Expand(1.5f);
            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.Frame(bounds, false);
            }
        }

        public static void MoveRopeToTransform(RopeController controller)
        {
            if (controller == null || controller.ControlPoints == null || controller.ControlPoints.Count == 0) return;
            Undo.RecordObject(controller, "Snap Rope to GameObject");
            Vector3 delta = controller.transform.position - controller.ControlPoints[0].Position;
            for (int i = 0; i < controller.ControlPoints.Count; i++)
            {
                controller.ControlPoints[i].Position += delta;
            }
            controller.RefreshParticlesFromBezier();
            controller.RegeneratePinsFromControlPoints();
            EditorUtility.SetDirty(controller);
            SceneView.RepaintAll();
        }

        public static void MoveTransformToRope(RopeController controller)
        {
            if (controller == null || controller.ControlPoints == null || controller.ControlPoints.Count == 0) return;
            Undo.RecordObject(controller.transform, "Move GameObject to Rope");
            controller.transform.position = controller.ControlPoints[0].Position;
            EditorUtility.SetDirty(controller.transform);
            SceneView.RepaintAll();
        }
    }
}