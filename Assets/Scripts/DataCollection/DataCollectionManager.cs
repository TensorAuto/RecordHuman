using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using Unity.XR.CoreUtils;

#if !PICO_OPENXR_SDK
using Unity.XR.PXR;
using PXREnterprise = Unity.XR.PICO.TOBSupport.PXR_Enterprise;
#endif

namespace PicoHumanData
{
    public class DataCollectionManager : MonoBehaviour
    {
        [Header("Recording Settings")]
        [Tooltip("Target capture rate in Hz. Set to 0 for every-frame capture.")]
        public float captureRateHz = 30f;

        [Header("UI Settings")]
        [Tooltip("Distance of the UI panel from the camera.")]
        public float uiDistance = 1.2f;

        [Tooltip("Scale of the UI panel.")]
        public float uiScale = 0.001f;

        const float uiFollowSpeed = 2f;
        const float uiFollowAngle = 30f;

        enum RecordingState { Idle, Countdown, Recording, Saving }
        RecordingState state = RecordingState.Idle;

        const float CountdownDuration = 5f;
        float countdownStartTime;
        float countdownRemaining;

        XRHandSubsystem handSubsystem;
        List<FrameData> recordedFrames;
        float recordingStartTime;
        float lastCaptureTime;
        float captureInterval;
        int frameCount;
        string baseSavePath;

        Canvas uiCanvas;
        Text statusText;
        Text timerText;
        Text instructionText;

        Camera xrCamera;

        bool videoRecordingActive;

        // Controller input state
        bool rightTriggerWasDown;
        bool bothGripsWereDown;
        float bothGripsHeldTime;
        const float GripHoldToStop = 1.0f;

        // Tag-along UI state
        Vector3 uiTargetPosition;
        Quaternion uiTargetRotation;
        bool uiNeedsSnap;

        void Awake()
        {
            Debug.Log("[DataCollection] ===== Awake() called =====");
        }

        IEnumerator Start()
        {
            Debug.Log("[DataCollection] ===== Start() called =====");

            SetupSavePath();
            captureInterval = captureRateHz > 0 ? 1f / captureRateHz : 0f;

            yield return StartCoroutine(FindXRCamera());

            EnablePassthrough();
            yield return StartCoroutine(FindHandSubsystem());
            InitEnterprise();

            try { CreateUI(); }
            catch (Exception e) { Debug.LogError($"[DataCollection] UI creation failed: {e}"); }

            Debug.Log($"[DataCollection] ===== Fully initialized. Save path: {baseSavePath} =====");

            LogHandTrackingDiagnostics();
        }

        void LogHandTrackingDiagnostics()
        {
            Debug.Log("[DataCollection] ===== Hand Tracking Diagnostics =====");

            bool xrSubsystemAvail = handSubsystem != null && handSubsystem.running;
            Debug.Log($"[DataCollection] XRHandSubsystem available: {xrSubsystemAvail}");

#if !PICO_OPENXR_SDK
            try
            {
                bool projectConfigEnabled = PXR_ProjectSetting.GetProjectConfig().handTracking;
                Debug.Log($"[DataCollection] Project config handTracking: {projectConfigEnabled}");

                bool systemSettingEnabled = PXR_Plugin.HandTracking.UPxr_GetHandTrackerSettingState();
                Debug.Log($"[DataCollection] System setting (native): {systemSettingEnabled}");

                ActiveInputDevice activeDevice = PXR_HandTracking.GetActiveInputDevice();
                Debug.Log($"[DataCollection] Active input device: {activeDevice}");

                var jointLocL = new HandJointLocations();
                bool leftResult = PXR_HandTracking.GetJointLocations(HandType.HandLeft, ref jointLocL);
                Debug.Log($"[DataCollection] PXR GetJointLocations LEFT: returned={leftResult}, " +
                    $"isActive={jointLocL.isActive}, jointCount={jointLocL.jointCount}, " +
                    $"hasArray={(jointLocL.jointLocations != null ? jointLocL.jointLocations.Length.ToString() : "null")}");

                var jointLocR = new HandJointLocations();
                bool rightResult = PXR_HandTracking.GetJointLocations(HandType.HandRight, ref jointLocR);
                Debug.Log($"[DataCollection] PXR GetJointLocations RIGHT: returned={rightResult}, " +
                    $"isActive={jointLocR.isActive}, jointCount={jointLocR.jointCount}, " +
                    $"hasArray={(jointLocR.jointLocations != null ? jointLocR.jointLocations.Length.ToString() : "null")}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[DataCollection] Diagnostics error: {e}");
            }
#endif

            Debug.Log("[DataCollection] ===== End Diagnostics =====");
        }

        IEnumerator FindXRCamera()
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                xrCamera = Camera.main;
                if (xrCamera != null)
                {
                    Debug.Log($"[DataCollection] Found camera: {xrCamera.name} (attempt {attempt})");
                    yield break;
                }

                var origin = FindFirstObjectByType<XROrigin>();
                if (origin != null && origin.Camera != null)
                {
                    xrCamera = origin.Camera;
                    Debug.Log($"[DataCollection] Found camera via XROrigin: {xrCamera.name} (attempt {attempt})");
                    yield break;
                }

                var anyCam = FindFirstObjectByType<Camera>();
                if (anyCam != null)
                {
                    xrCamera = anyCam;
                    Debug.Log($"[DataCollection] Found camera via search: {xrCamera.name} (attempt {attempt})");
                    yield break;
                }

                if (attempt % 10 == 0)
                    Debug.Log($"[DataCollection] Searching for camera... attempt {attempt}");

                yield return null;
            }

            Debug.LogError("[DataCollection] No camera found after 60 frames!");
        }

        IEnumerator FindHandSubsystem()
        {
            var subsystems = new List<XRHandSubsystem>();

            for (int attempt = 0; attempt < 120; attempt++)
            {
                SubsystemManager.GetSubsystems(subsystems);
                if (subsystems.Count > 0)
                {
                    handSubsystem = subsystems[0];
                    if (!handSubsystem.running)
                        handSubsystem.Start();
                    Debug.Log("[DataCollection] XRHandSubsystem found and running");
                    yield break;
                }
                yield return null;
            }

            Debug.LogWarning("[DataCollection] XRHandSubsystem not found. Using PXR fallback on device.");
        }

        void Update()
        {
            if (handSubsystem == null)
            {
                var subsystems = new List<XRHandSubsystem>();
                SubsystemManager.GetSubsystems(subsystems);
                if (subsystems.Count > 0)
                {
                    handSubsystem = subsystems[0];
                    if (!handSubsystem.running) handSubsystem.Start();
                    Debug.Log("[DataCollection] XRHandSubsystem found (late)");
                }
            }

            if (state == RecordingState.Recording)
            {
                if (captureInterval <= 0 || Time.time - lastCaptureTime >= captureInterval)
                {
                    CaptureFrame();
                    lastCaptureTime = Time.time;
                }

                CheckBothGripsToStop();
            }
            else if (state == RecordingState.Countdown)
            {
                UpdateCountdown();
            }
            else if (state == RecordingState.Idle)
            {
                CheckTriggerToStart();
            }

            UpdateTagAlongUI();
        }

        #region Passthrough

        void EnablePassthrough()
        {
            try
            {
#if !PICO_OPENXR_SDK
                PXR_Manager.EnableVideoSeeThrough = true;
                Debug.Log("[DataCollection] PXR passthrough enabled");
#else
                Debug.Log("[DataCollection] OpenXR mode — configure passthrough through OpenXR features");
#endif
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DataCollection] Passthrough enable failed (expected in Editor): {e.Message}");
            }

            if (xrCamera != null)
            {
                xrCamera.clearFlags = CameraClearFlags.SolidColor;
                Color bg = xrCamera.backgroundColor;
                bg.a = 0f;
                xrCamera.backgroundColor = bg;
            }
        }

        void OnDestroy()
        {
            StopVideoRecording();
            try
            {
#if !PICO_OPENXR_SDK
                PXR_Manager.EnableVideoSeeThrough = false;
#endif
            }
            catch (Exception) { }
        }

        #endregion

        #region Enterprise Service

        void InitEnterprise()
        {
#if !PICO_OPENXR_SDK
            try
            {
                PXREnterprise.InitEnterpriseService();
                PXREnterprise.BindEnterpriseService(bound =>
                {
                    Debug.Log($"[DataCollection] Enterprise service bound: {bound}");
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DataCollection] Enterprise init failed: {e.Message}");
            }
#endif
        }

        void StartVideoRecording()
        {
#if !PICO_OPENXR_SDK
            try
            {
                PXREnterprise.Record();
                videoRecordingActive = true;
                Debug.Log("[DataCollection] Video recording STARTED");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DataCollection] Video record start failed: {e.Message}");
            }
#endif
        }

        void StopVideoRecording()
        {
#if !PICO_OPENXR_SDK
            if (!videoRecordingActive) return;
            try
            {
                PXREnterprise.Record();
                videoRecordingActive = false;
                Debug.Log("[DataCollection] Video recording STOPPED");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DataCollection] Video record stop failed: {e.Message}");
            }
#endif
        }

        #endregion

        #region Save Path

        void SetupSavePath()
        {
            string[] candidates =
            {
                "/sdcard/Download/RecordHuman",
                "/storage/emulated/0/Download/RecordHuman",
            };

            foreach (var path in candidates)
            {
                try
                {
                    Directory.CreateDirectory(path);
                    string testFile = Path.Combine(path, ".write_test");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                    baseSavePath = path;
                    Debug.Log($"[DataCollection] Using save path: {baseSavePath}");
                    return;
                }
                catch (Exception)
                {
                    Debug.Log($"[DataCollection] Cannot write to {path}, trying next...");
                }
            }

            baseSavePath = Path.Combine(Application.persistentDataPath, "recordings");
            Directory.CreateDirectory(baseSavePath);
            Debug.Log($"[DataCollection] Fallback save path: {baseSavePath}");
        }

        #endregion

        #region Controller Input

        void CheckTriggerToStart()
        {
            var rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (!rightDevice.isValid) return;

            rightDevice.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerDown);

            if (triggerDown && !rightTriggerWasDown)
                BeginCountdown();

            rightTriggerWasDown = triggerDown;
        }

        void CheckBothGripsToStop()
        {
            var leftDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            var rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

            bool leftGrip = false, rightGrip = false;
            if (leftDevice.isValid)
                leftDevice.TryGetFeatureValue(CommonUsages.gripButton, out leftGrip);
            if (rightDevice.isValid)
                rightDevice.TryGetFeatureValue(CommonUsages.gripButton, out rightGrip);

            bool bothDown = leftGrip && rightGrip;

            if (bothDown)
            {
                bothGripsHeldTime += Time.deltaTime;
                if (bothGripsHeldTime >= GripHoldToStop && !bothGripsWereDown)
                {
                    bothGripsWereDown = true;
                    FinishRecording();
                }
            }
            else
            {
                bothGripsHeldTime = 0f;
                bothGripsWereDown = false;
            }
        }

        #endregion

        #region Hand Tracking Data Capture

        void CaptureFrame()
        {
            if (xrCamera == null) return;

            var frame = new FrameData
            {
                t = Time.time - recordingStartTime,
                head_pos = Vec3ToArray(xrCamera.transform.position),
                head_rot = QuatToArray(xrCamera.transform.rotation),
            };

            if (handSubsystem != null && handSubsystem.running)
            {
                CaptureHandXR(handSubsystem.leftHand, out frame.left_tracked, out frame.left_joints);
                CaptureHandXR(handSubsystem.rightHand, out frame.right_tracked, out frame.right_joints);
            }

#if !PICO_OPENXR_SDK
            if (!frame.left_tracked)
                CaptureHandPXR(HandType.HandLeft, out frame.left_tracked, out frame.left_joints);
            if (!frame.right_tracked)
                CaptureHandPXR(HandType.HandRight, out frame.right_tracked, out frame.right_joints);
#endif

            if (!frame.left_tracked && frame.left_joints == null)
                frame.left_joints = new float[HandJointNames.JointCount * HandJointNames.FloatsPerJoint];
            if (!frame.right_tracked && frame.right_joints == null)
                frame.right_joints = new float[HandJointNames.JointCount * HandJointNames.FloatsPerJoint];

            if (frameCount % 90 == 0)
            {
                bool xrAvail = handSubsystem != null && handSubsystem.running;
                string diagExtra = "";
#if !PICO_OPENXR_SDK
                try
                {
                    bool sysSetting = PXR_Plugin.HandTracking.UPxr_GetHandTrackerSettingState();
                    ActiveInputDevice activeIn = PXR_HandTracking.GetActiveInputDevice();
                    diagExtra = $", systemHandTrackingEnabled={sysSetting}, activeInput={activeIn}";
                }
                catch (Exception) { diagExtra = ", PXR diag failed"; }
#endif
                Debug.Log($"[DataCollection] Frame {frameCount} tracking — " +
                    $"XRHandSubsystem: {(xrAvail ? "running" : "NOT available")}, " +
                    $"left_tracked={frame.left_tracked}, right_tracked={frame.right_tracked}, " +
                    $"left_joints_sample=({frame.left_joints[0]:F4},{frame.left_joints[1]:F4},{frame.left_joints[2]:F4}), " +
                    $"right_joints_sample=({frame.right_joints[0]:F4},{frame.right_joints[1]:F4},{frame.right_joints[2]:F4})" +
                    diagExtra);
            }

            recordedFrames.Add(frame);
            frameCount++;
        }

        void CaptureHandXR(XRHand hand, out bool tracked, out float[] joints)
        {
            joints = new float[HandJointNames.JointCount * HandJointNames.FloatsPerJoint];
            tracked = hand.isTracked;

            if (!tracked)
                return;

            for (int i = XRHandJointID.BeginMarker.ToIndex(); i < XRHandJointID.EndMarker.ToIndex(); i++)
            {
                var joint = hand.GetJoint(XRHandJointIDUtility.FromIndex(i));
                int offset = i * HandJointNames.FloatsPerJoint;

                if (joint.TryGetPose(out Pose pose))
                {
                    joints[offset + 0] = pose.position.x;
                    joints[offset + 1] = pose.position.y;
                    joints[offset + 2] = pose.position.z;
                    joints[offset + 3] = pose.rotation.x;
                    joints[offset + 4] = pose.rotation.y;
                    joints[offset + 5] = pose.rotation.z;
                    joints[offset + 6] = pose.rotation.w;
                }
            }
        }

#if !PICO_OPENXR_SDK
        void CaptureHandPXR(HandType handType, out bool tracked, out float[] joints)
        {
            joints = new float[HandJointNames.JointCount * HandJointNames.FloatsPerJoint];
            tracked = false;

            try
            {
                var jointLocations = new HandJointLocations();
                if (!PXR_HandTracking.GetJointLocations(handType, ref jointLocations))
                    return;

                tracked = jointLocations.isActive > 0;
                if (!tracked || jointLocations.jointLocations == null)
                    return;

                int count = Mathf.Min(jointLocations.jointLocations.Length, HandJointNames.JointCount);
                for (int i = 0; i < count; i++)
                {
                    var loc = jointLocations.jointLocations[i];
                    int offset = i * HandJointNames.FloatsPerJoint;

                    Vector3 pos = loc.pose.Position.ToVector3();
                    Quaternion rot = loc.pose.Orientation.ToQuat();

                    joints[offset + 0] = pos.x;
                    joints[offset + 1] = pos.y;
                    joints[offset + 2] = pos.z;
                    joints[offset + 3] = rot.x;
                    joints[offset + 4] = rot.y;
                    joints[offset + 5] = rot.z;
                    joints[offset + 6] = rot.w;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DataCollection] PXR hand tracking error: {e.Message}");
            }
        }
#endif

        #endregion

        #region Recording Control

        void BeginCountdown()
        {
            if (state != RecordingState.Idle) return;

            countdownStartTime = Time.time;
            countdownRemaining = CountdownDuration;
            state = RecordingState.Countdown;

            if (statusText != null)
            {
                statusText.text = $"PUT CONTROLLERS DOWN!\nRecording in {CountdownDuration:F0}...";
                statusText.color = new Color(1f, 0.8f, 0.2f);
            }
            if (instructionText != null)
                instructionText.text = "Set controllers aside so hands can be tracked";

            Debug.Log($"[DataCollection] Countdown started ({CountdownDuration}s)");
        }

        void UpdateCountdown()
        {
            countdownRemaining = CountdownDuration - (Time.time - countdownStartTime);

            if (countdownRemaining <= 0f)
            {
                StartRecording();
                return;
            }

            if (statusText != null)
            {
                statusText.text = $"PUT CONTROLLERS DOWN!\nRecording in {Mathf.CeilToInt(countdownRemaining)}...";
            }
        }

        void StartRecording()
        {
            recordedFrames = new List<FrameData>(4096);
            recordingStartTime = Time.time;
            lastCaptureTime = 0f;
            frameCount = 0;

            SetUIVisible(false);
            StartVideoRecording();

            state = RecordingState.Recording;
            bothGripsHeldTime = 0f;
            bothGripsWereDown = false;

            Debug.Log("[DataCollection] Recording started");
        }

        void FinishRecording()
        {
            if (state != RecordingState.Recording) return;

            StopVideoRecording();

            state = RecordingState.Saving;
            float duration = Time.time - recordingStartTime;

            string recordingId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            try
            {
                SaveRecording(recordingId, duration);
                statusText.text = $"Saved: {recordingId}\n{frameCount} frames, {duration:F1}s";
                statusText.color = new Color(0.3f, 1f, 0.3f);
                Debug.Log($"[DataCollection] Saved {recordingId}: {frameCount} frames, {duration:F1}s");
            }
            catch (Exception e)
            {
                Debug.LogError($"[DataCollection] SAVE FAILED: {e}");
                statusText.text = $"SAVE FAILED: {e.Message}";
                statusText.color = new Color(1f, 0.3f, 0.3f);
            }

            SetUIVisible(true);
            uiNeedsSnap = true;

            timerText.text = "";
            instructionText.text = "Pull RIGHT TRIGGER to start new recording";

            state = RecordingState.Idle;
        }

        void SaveRecording(string recordingId, float duration)
        {
            var session = new RecordingSession
            {
                metadata = new RecordingMetadata
                {
                    recording_id = recordingId,
                    start_time_utc = DateTime.UtcNow.ToString("o"),
                    device = SystemInfo.deviceModel,
                    target_fps = captureRateHz,
                    total_frames = frameCount,
                    duration_seconds = duration,
                    joint_names = HandJointNames.Names,
                    data_format = "position_xyz_rotation_xyzw",
                },
                frames = recordedFrames
            };

            string json = JsonUtility.ToJson(session, false);
            string filePath = Path.Combine(baseSavePath, $"{recordingId}.json");
            File.WriteAllText(filePath, json);

            Debug.Log($"[DataCollection] JSON written: {filePath} ({json.Length / 1024}KB)");
        }

        #endregion

        #region Tag-Along UI

        void UpdateTagAlongUI()
        {
            if (uiCanvas == null || !uiCanvas.gameObject.activeSelf || xrCamera == null)
                return;

            Vector3 headPos = xrCamera.transform.position;
            Vector3 headFwd = xrCamera.transform.forward;
            headFwd.y = 0;
            if (headFwd.sqrMagnitude < 0.001f) headFwd = Vector3.forward;
            headFwd.Normalize();

            Vector3 desiredPos = headPos + headFwd * uiDistance;
            desiredPos.y = headPos.y - 0.15f;
            Quaternion desiredRot = Quaternion.LookRotation(headFwd);

            if (uiNeedsSnap)
            {
                uiCanvas.transform.position = desiredPos;
                uiCanvas.transform.rotation = desiredRot;
                uiTargetPosition = desiredPos;
                uiTargetRotation = desiredRot;
                uiNeedsSnap = false;
                return;
            }

            float angleDiff = Quaternion.Angle(uiCanvas.transform.rotation, desiredRot);
            float distDiff = Vector3.Distance(uiCanvas.transform.position, desiredPos);

            if (angleDiff > uiFollowAngle || distDiff > uiDistance * 0.5f)
            {
                uiTargetPosition = desiredPos;
                uiTargetRotation = desiredRot;
            }

            float t = uiFollowSpeed * Time.deltaTime;
            uiCanvas.transform.position = Vector3.Lerp(uiCanvas.transform.position, uiTargetPosition, t);
            uiCanvas.transform.rotation = Quaternion.Slerp(uiCanvas.transform.rotation, uiTargetRotation, t);
        }

        #endregion

        #region UI

        void SetUIVisible(bool visible)
        {
            if (uiCanvas != null)
                uiCanvas.gameObject.SetActive(visible);
        }

        void CreateUI()
        {
            var canvasGO = new GameObject("DataCollectionCanvas");
            canvasGO.transform.SetParent(transform);
            uiCanvas = canvasGO.AddComponent<Canvas>();
            uiCanvas.renderMode = RenderMode.WorldSpace;

            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10f;

            var rectTransform = canvasGO.GetComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(700, 350);
            rectTransform.localScale = Vector3.one * uiScale;

            CreateBackground(rectTransform);

            statusText = CreateText(rectTransform, "Status", "Ready to record",
                new Vector2(0, 80), new Vector2(650, 60), 36);
            statusText.color = Color.white;

            timerText = CreateText(rectTransform, "Timer", "",
                new Vector2(0, 20), new Vector2(650, 40), 28);
            timerText.color = new Color(0.8f, 0.8f, 0.8f);

            instructionText = CreateText(rectTransform, "Instructions",
                "Pull RIGHT TRIGGER to start recording\nSqueeze BOTH GRIPS for 1s to stop",
                new Vector2(0, -60), new Vector2(650, 70), 24);
            instructionText.color = new Color(0.7f, 0.7f, 0.3f);

            var pathText = CreateText(rectTransform, "PathInfo",
                $"Saves to: {baseSavePath}",
                new Vector2(0, -130), new Vector2(650, 30), 18);
            pathText.color = new Color(0.5f, 0.5f, 0.5f);

            uiNeedsSnap = true;

            Debug.Log("[DataCollection] UI created");
        }

        void CreateBackground(RectTransform parent)
        {
            var bgGO = new GameObject("Background");
            bgGO.transform.SetParent(parent);
            var bgRect = bgGO.AddComponent<RectTransform>();
            bgRect.anchoredPosition = Vector2.zero;
            bgRect.sizeDelta = parent.sizeDelta;
            bgRect.localScale = Vector3.one;
            bgRect.localPosition = Vector3.zero;

            var bgImage = bgGO.AddComponent<Image>();
            bgImage.color = new Color(0.05f, 0.05f, 0.1f, 0.85f);
        }

        Text CreateText(RectTransform parent, string name, string content,
            Vector2 position, Vector2 size, int fontSize)
        {
            var textGO = new GameObject(name);
            textGO.transform.SetParent(parent);
            var rect = textGO.AddComponent<RectTransform>();
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;
            rect.localPosition = new Vector3(position.x, position.y, 0);

            var text = textGO.AddComponent<Text>();
            text.text = content;
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.font = GetDefaultFont();
            return text;
        }

        #endregion

        #region Helpers

        static float[] Vec3ToArray(Vector3 v)
        {
            return new float[] { v.x, v.y, v.z };
        }

        static float[] QuatToArray(Quaternion q)
        {
            return new float[] { q.x, q.y, q.z, q.w };
        }

        static Font _cachedFont;

        static Font GetDefaultFont()
        {
            if (_cachedFont != null) return _cachedFont;

            string[] fontNames = { "LegacyRuntime.ttf", "Arial.ttf" };
            foreach (var name in fontNames)
            {
                var f = Resources.GetBuiltinResource<Font>(name);
                if (f != null)
                {
                    _cachedFont = f;
                    return f;
                }
            }

            _cachedFont = Font.CreateDynamicFontFromOSFont("Arial", 14);
            if (_cachedFont == null)
            {
                string[] osFonts = Font.GetOSInstalledFontNames();
                if (osFonts.Length > 0)
                    _cachedFont = Font.CreateDynamicFontFromOSFont(osFonts[0], 14);
            }

            return _cachedFont;
        }

        #endregion
    }
}
