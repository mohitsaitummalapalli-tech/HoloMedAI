using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoloMedAI
{
    /// <summary>
    /// HoloMed AI - World-Space LineRenderer Callouts, 5-Item Heart Information Card,
    /// and "End Check" Topic Progress Tracker.
    /// </summary>
    public class HologramCalloutHUD : MonoBehaviour
    {
        [System.Serializable]
        public class AnatomyCallout
        {
            public string key;
            public string displayName;
            public Vector3 localAnchorPoint;
            public Vector3 worldOffset;
            public LineRenderer lineRenderer;
            public bool isSelected;
        }

        [Header("Target 3D Heart")]
        public Transform heartTransform;

        [Header("Styling & Colors")]
        public Color normalLineColor = new Color(0f, 0.8f, 1f, 0.6f);      // Cyan
        public Color selectedLineColor = new Color(0f, 1f, 0.4f, 1.0f);     // Neon Green / Glow
        public Material calloutLineMaterial;

        [Header("Anatomy Callouts")]
        public List<AnatomyCallout> callouts = new List<AnatomyCallout>();

        // 5-Item Heart Information Card Data
        [System.Serializable]
        public class TopicItem
        {
            public string id;
            public string title;
            public string summary;
            public bool isChecked;
        }

        [Header("Heart Information Card & End Check Tracker")]
        public List<TopicItem> informationTopics = new List<TopicItem>();
        public int activeTopicIndex = 0;

        private GUIStyle _cardBoxStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _checkStyle;
        private Texture2D _cardBgTex;
        private Texture2D _progressFilledTex;
        private Texture2D _progressEmptyTex;

        void Awake()
        {
            if (heartTransform == null)
            {
                GameObject hObj = GameObject.Find("Human Heart");
                if (hObj != null) heartTransform = hObj.transform;
                else heartTransform = this.transform;
            }

            SetupDefaultCallouts();
            SetupDefaultTopics();
            CreateMaterialsAndStyles();
        }

        void OnEnable()
        {
            UdpTelemetryReceiver.OnChamberSelected += HandleChamberSelected;
            UdpTelemetryReceiver.OnTelemetryReceived += HandleTelemetry;
        }

        void OnDisable()
        {
            UdpTelemetryReceiver.OnChamberSelected -= HandleChamberSelected;
            UdpTelemetryReceiver.OnTelemetryReceived -= HandleTelemetry;
        }

        private void SetupDefaultCallouts()
        {
            if (callouts.Count == 0)
            {
                // Key anatomical landmarks on the human heart
                callouts.Add(new AnatomyCallout
                {
                    key = "AORTA",
                    displayName = "AORTA",
                    localAnchorPoint = new Vector3(0.05f, 0.45f, 0.0f),
                    worldOffset = new Vector3(0.55f, 0.35f, 0.0f)
                });
                callouts.Add(new AnatomyCallout
                {
                    key = "SUPERIOR_VENA_CAVA",
                    displayName = "SUPERIOR VENA CAVA",
                    localAnchorPoint = new Vector3(-0.25f, 0.35f, -0.05f),
                    worldOffset = new Vector3(-0.65f, 0.30f, 0.0f)
                });
                callouts.Add(new AnatomyCallout
                {
                    key = "RIGHT_VENTRICLE",
                    displayName = "RIGHT VENTRICLE",
                    localAnchorPoint = new Vector3(-0.15f, -0.15f, 0.15f),
                    worldOffset = new Vector3(-0.60f, -0.25f, 0.0f)
                });
                callouts.Add(new AnatomyCallout
                {
                    key = "LEFT_VENTRICLE",
                    displayName = "LEFT VENTRICLE",
                    localAnchorPoint = new Vector3(0.20f, -0.25f, 0.05f),
                    worldOffset = new Vector3(0.60f, -0.30f, 0.0f)
                });
            }

            // Create LineRenderers dynamically
            foreach (var c in callouts)
            {
                GameObject lineObj = new GameObject($"Line_{c.key}");
                lineObj.transform.SetParent(this.transform);
                LineRenderer lr = lineObj.AddComponent<LineRenderer>();
                lr.positionCount = 2;
                lr.startWidth = 0.008f;
                lr.endWidth = 0.004f;
                lr.useWorldSpace = true;
                lr.material = calloutLineMaterial != null ? calloutLineMaterial : CreateDefaultLineMaterial();
                lr.startColor = normalLineColor;
                lr.endColor = normalLineColor;
                c.lineRenderer = lr;
            }
        }

        private Material CreateDefaultLineMaterial()
        {
            Shader s = Shader.Find("Sprites/Default");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Unlit");
            if (s == null) s = Shader.Find("Unlit/Color");
            return new Material(s);
        }

        private void SetupDefaultTopics()
        {
            if (informationTopics.Count == 0)
            {
                informationTopics.Add(new TopicItem
                {
                    id = "chambers",
                    title = "1. Chambers",
                    summary = "4 Muscular Chambers: Right Atrium (receives deox blood), Right Ventricle (pumps to lungs), Left Atrium (receives oxygenated blood), Left Ventricle (thickest wall, pumps to aorta).",
                    isChecked = false
                });
                informationTopics.Add(new TopicItem
                {
                    id = "function",
                    title = "2. Function",
                    summary = "Dual-circulation muscular pump: Pulses continuously via SA node rhythmic electrical action potentials to supply metabolic oxygen & nutrients to all organs.",
                    isChecked = false
                });
                informationTopics.Add(new TopicItem
                {
                    id = "blood_flow",
                    title = "3. Blood Flow",
                    summary = "Deox blood: Vena Cava -> RA -> Tricuspid Valve -> RV -> Pulmonary Artery -> Lungs. Ox blood: Pulmonary Veins -> LA -> Mitral Valve -> LV -> Aortic Valve -> Systemic Aorta.",
                    isChecked = false
                });
                informationTopics.Add(new TopicItem
                {
                    id = "major_vessels",
                    title = "4. Major Vessels",
                    summary = "Aorta (largest artery, high-pressure outflow), Superior & Inferior Vena Cava (large venous return), Pulmonary Trunk/Arteries, and 4 Pulmonary Veins.",
                    isChecked = false
                });
                informationTopics.Add(new TopicItem
                {
                    id = "heart_rate",
                    title = "5. Avg. Heart Rate",
                    summary = "Normal resting adult sinus rhythm: 60 - 100 bpm (~72 bpm nominal). Cardiac output averages ~5.0 L/min at rest, surging up to 25 L/min during peak physical exertion.",
                    isChecked = false
                });
            }
        }

        private void CreateMaterialsAndStyles()
        {
            _cardBgTex = MakeColorTex(new Color(0.04f, 0.07f, 0.12f, 0.90f));
            _progressFilledTex = MakeColorTex(new Color(0f, 0.9f, 0.5f, 1f));
            _progressEmptyTex = MakeColorTex(new Color(0.15f, 0.20f, 0.25f, 0.9f));
        }

        private Texture2D MakeColorTex(Color col)
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, col);
            tex.Apply();
            return tex;
        }

        void LateUpdate()
        {
            UpdateWorldSpaceLineCallouts();
        }

        private void UpdateWorldSpaceLineCallouts()
        {
            if (heartTransform == null) return;

            foreach (var c in callouts)
            {
                if (c.lineRenderer == null) continue;

                Vector3 startPos = heartTransform.TransformPoint(c.localAnchorPoint);
                Vector3 endPos = startPos + c.worldOffset;

                c.lineRenderer.SetPosition(0, startPos);
                c.lineRenderer.SetPosition(1, endPos);

                Color targetCol = c.isSelected ? selectedLineColor : normalLineColor;
                c.lineRenderer.startColor = Color.Lerp(c.lineRenderer.startColor, targetCol, Time.deltaTime * 10f);
                c.lineRenderer.endColor = Color.Lerp(c.lineRenderer.endColor, targetCol, Time.deltaTime * 10f);
                c.lineRenderer.startWidth = c.isSelected ? 0.012f : 0.006f;
            }
        }

        private void HandleChamberSelected(string chamberName)
        {
            string upper = chamberName.ToUpperInvariant();

            // Match against callouts
            foreach (var c in callouts)
            {
                bool matches = false;
                if (c.key == upper) matches = true;
                else if (c.key == "RIGHT_VENTRICLE" && upper == "RV") matches = true;
                else if (c.key == "LEFT_VENTRICLE" && upper == "LV") matches = true;
                else if (c.key == "SUPERIOR_VENA_CAVA" && (upper == "SVC" || upper == "SUPERIOR_VENA_CAVA")) matches = true;
                else if (c.key == "AORTA" && upper == "AORTA") matches = true;

                c.isSelected = matches;
            }

            // Check off topics dynamically
            if (upper == "RA" || upper == "RV" || upper == "LA" || upper == "LV")
            {
                MarkTopicChecked("chambers");
                activeTopicIndex = 0;
            }
            else if (upper == "AORTA" || upper == "SUPERIOR_VENA_CAVA")
            {
                MarkTopicChecked("major_vessels");
                activeTopicIndex = 3;
            }
        }

        private void HandleTelemetry(UdpTelemetryReceiver.UnifiedTelemetryPacket packet)
        {
            if (packet == null) return;

            // When cross-section is active, mark Blood Flow topic as checked
            if (packet.is_cross_section)
            {
                MarkTopicChecked("blood_flow");
            }
        }

        public void MarkTopicChecked(string id)
        {
            foreach (var t in informationTopics)
            {
                if (t.id == id)
                {
                    t.isChecked = true;
                }
            }
        }

        public float GetCompletionPercentage()
        {
            if (informationTopics.Count == 0) return 0f;
            int checkedCount = 0;
            foreach (var t in informationTopics)
            {
                if (t.isChecked) checkedCount++;
            }
            return (float)checkedCount / informationTopics.Count * 100f;
        }

        void OnGUI()
        {
            InitStyles();

            // 1. Draw Floating World-Space Text Labels for 4 Callouts
            DrawWorldSpaceCalloutLabels();

            // 2. Draw 5-Item "Heart Information" Card & End Check Progress Tracker
            DrawHeartInformationCard();
        }

        private void InitStyles()
        {
            if (_cardBoxStyle == null)
            {
                _cardBoxStyle = new GUIStyle(GUI.skin.box)
                {
                    normal = { background = _cardBgTex }
                };

                _headerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(0f, 0.95f, 1f) }
                };

                _titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(1f, 1f, 1f) }
                };

                _bodyStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 11,
                    wordWrap = true,
                    normal = { textColor = new Color(0.85f, 0.9f, 0.95f) }
                };

                _checkStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(0.1f, 1.0f, 0.4f) }
                };
            }
        }

        private void DrawWorldSpaceCalloutLabels()
        {
            if (Camera.main == null || heartTransform == null) return;

            foreach (var c in callouts)
            {
                Vector3 startPos = heartTransform.TransformPoint(c.localAnchorPoint);
                Vector3 endPos = startPos + c.worldOffset;

                Vector3 screenPos = Camera.main.WorldToScreenPoint(endPos);
                if (screenPos.z > 0)
                {
                    float guiY = Screen.height - screenPos.y;
                    Rect labelRect = new Rect(screenPos.x - 70, guiY - 15, 140, 30);

                    // Glowing backdrop if selected
                    if (c.isSelected)
                    {
                        GUI.color = new Color(0f, 1f, 0.5f, 0.4f);
                        GUI.Box(new Rect(labelRect.x - 4, labelRect.y - 2, labelRect.width + 8, labelRect.height + 4), GUIContent.none);
                        GUI.color = Color.white;
                    }

                    GUI.Box(labelRect, GUIContent.none, _cardBoxStyle);
                    GUI.Label(labelRect, c.displayName, c.isSelected ? _checkStyle : _headerStyle);
                }
            }
        }

        private void DrawHeartInformationCard()
        {
            // Positioned elegantly on the bottom-right of the screen
            float cardWidth = 360f;
            float cardHeight = 310f;
            float posX = Screen.width - cardWidth - 20f;
            float posY = Screen.height - cardHeight - 20f;

            Rect cardRect = new Rect(posX, posY, cardWidth, cardHeight);
            GUI.Box(cardRect, GUIContent.none, _cardBoxStyle);

            GUILayout.BeginArea(new Rect(posX + 15, posY + 12, cardWidth - 30, cardHeight - 24));

            // Header Banner
            GUILayout.Label("ANATOMY & PHYSIOLOGY", _headerStyle);
            GUILayout.Space(4);

            // 5 Topic Navigation Buttons with [x] Checkmarks
            GUILayout.BeginHorizontal();
            for (int i = 0; i < informationTopics.Count; i++)
            {
                var topic = informationTopics[i];
                string checkPrefix = topic.isChecked ? "[X]" : "[ ]";
                bool isCurrent = (i == activeTopicIndex);

                GUI.backgroundColor = isCurrent ? new Color(0f, 0.8f, 1f) : new Color(0.2f, 0.25f, 0.35f);
                if (GUILayout.Button($"{checkPrefix} #{i + 1}", GUILayout.Height(24)))
                {
                    activeTopicIndex = i;
                    topic.isChecked = true; // Reviewing marks as checked
                }
            }
            GUI.backgroundColor = Color.white;
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            // Active Topic Information Box
            if (activeTopicIndex >= 0 && activeTopicIndex < informationTopics.Count)
            {
                var activeTopic = informationTopics[activeTopicIndex];
                GUILayout.Label(activeTopic.title, _titleStyle);
                GUILayout.Label(activeTopic.summary, _bodyStyle);
            }

            GUILayout.FlexibleSpace();

            // "End Check" Topic Progress Tracker
            float completion = GetCompletionPercentage();
            GUILayout.BeginHorizontal();
            GUILayout.Label("END CHECK PROGRESS:", _headerStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{completion:F0}% COMPLETED", _checkStyle);
            GUILayout.EndHorizontal();

            // Progress Bar
            Rect pBarRect = GUILayoutUtility.GetRect(cardWidth - 30, 10);
            GUI.DrawTexture(pBarRect, _progressEmptyTex);
            Rect pFilledRect = new Rect(pBarRect.x, pBarRect.y, pBarRect.width * (completion / 100f), pBarRect.height);
            GUI.DrawTexture(pFilledRect, _progressFilledTex);

            GUILayout.EndArea();
        }
    }
}
