# PICO Human Data Collection App

Records hand poses through PICO headset passthrough cameras for robot learning.

## What It Does

- Enables the PICO headset's bottom stereo cameras (video passthrough) so the user sees the real world
- Tracks both hands using PICO's hand tracking (26 joints per hand)
- Records hand joint poses + head pose at configurable framerate
- Provides a floating UI panel with **Start** and **Finish** buttons
- Saves recordings as JSON files to device storage

## Scene Setup

### 1. Open your scene (SampleScene recommended)

### 2. Create the Data Collection GameObject

1. **GameObject > Create Empty**, name it `DataCollectionManager`
2. Add the `DataCollectionManager` component (from `PicoHumanData` namespace)
3. Configure:
   - **Capture Rate Hz**: 30 (default) — how many times per second to capture hand data
   - **UI Distance**: 1.5 — how far the UI panel floats from the user
   - **UI Scale**: 0.001 — world-space scale of the UI

### 3. Camera Configuration (Critical)

The XR Origin's camera **must** have:
- **Clear Flags**: `Solid Color`
- **Background Color**: alpha = `0` (fully transparent)

This allows the passthrough camera feed to show through.

### 4. Ensure Hand Tracking is Enabled

Already done in this project (`PXR_ProjectSetting.asset` has `handTracking: 1`).

### 5. Ensure Video See-Through is Enabled

Already done (`videoSeeThrough: 1` in `PXR_ProjectSetting.asset`).

### 6. UI Interaction

For the Start/Finish buttons to respond to hand input, ensure your XR Origin has:
- **XR Ray Interactor** or **XR Poke Interactor** on the hand GameObjects
- An **EventSystem** in the scene (already present in SampleScene)

If using the "Complete XR Origin Set Up Hands Variant" prefab, UI interaction
should work out of the box with hand poke.

Alternatively, add `TrackedDeviceGraphicRaycaster` to the canvas
(replace the default `GraphicRaycaster`) for ray-based UI interaction.

## Data Format

Recordings are saved to `Application.persistentDataPath/recordings/` on the device.
On PICO, this is typically: `/storage/emulated/0/Android/data/<package_name>/files/recordings/`

Each recording is a JSON file named `YYYYMMDD_HHMMSS.json`:

```json
{
  "metadata": {
    "recording_id": "20260223_153000",
    "start_time_utc": "2026-02-23T15:30:00.0000000Z",
    "device": "PICO 4",
    "target_fps": 30,
    "total_frames": 900,
    "duration_seconds": 30.0,
    "joint_names": ["Palm", "Wrist", "ThumbMetacarpal", ...],
    "data_format": "position_xyz_rotation_xyzw"
  },
  "frames": [
    {
      "t": 0.033,
      "head_pos": [x, y, z],
      "head_rot": [qx, qy, qz, qw],
      "left_tracked": true,
      "left_joints": [x,y,z,qx,qy,qz,qw, x,y,z,qx,qy,qz,qw, ...],
      "right_tracked": true,
      "right_joints": [x,y,z,qx,qy,qz,qw, ...]
    }
  ]
}
```

Each hand has 26 joints × 7 floats (3 position + 4 quaternion rotation) = 182 floats per hand.

Joint order: Palm, Wrist, ThumbMetacarpal, ThumbProximal, ThumbDistal, ThumbTip,
IndexMetacarpal...IndexTip, MiddleMetacarpal...MiddleTip, RingMetacarpal...RingTip,
LittleMetacarpal...LittleTip.

## Retrieving Data

Connect the headset via USB and use adb:

```bash
adb pull /storage/emulated/0/Android/data/com.DefaultCompany.pico_human_data/files/recordings/ ./recordings/
```

## Loading Data in Python

```python
import json
import numpy as np

with open("20260223_153000.json") as f:
    data = json.load(f)

metadata = data["metadata"]
joint_names = metadata["joint_names"]

for frame in data["frames"]:
    t = frame["t"]
    head_pos = np.array(frame["head_pos"])
    head_rot = np.array(frame["head_rot"])

    if frame["left_tracked"]:
        left = np.array(frame["left_joints"]).reshape(26, 7)
        left_positions = left[:, :3]    # (26, 3)
        left_rotations = left[:, 3:]    # (26, 4) as quaternion xyzw

    if frame["right_tracked"]:
        right = np.array(frame["right_joints"]).reshape(26, 7)
        right_positions = right[:, :3]
        right_rotations = right[:, 3:]
```

## Build & Deploy

1. **File > Build Settings** — switch to Android
2. Set the scene in the build list
3. **Build And Run** to deploy to the connected PICO headset
