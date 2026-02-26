using System;
using System.Collections.Generic;
using UnityEngine;

namespace PicoHumanData
{
    [Serializable]
    public class RecordingMetadata
    {
        public string recording_id;
        public string start_time_utc;
        public string device;
        public float target_fps;
        public int total_frames;
        public float duration_seconds;
        public string[] joint_names;
        public string data_format;
    }

    [Serializable]
    public class FrameData
    {
        public float t;
        public float[] head_pos;
        public float[] head_rot;
        public bool left_tracked;
        public float[] left_joints;
        public bool right_tracked;
        public float[] right_joints;
    }

    [Serializable]
    public class RecordingSession
    {
        public RecordingMetadata metadata;
        public List<FrameData> frames;
    }

    public static class HandJointNames
    {
        public static readonly string[] Names = new string[]
        {
            "Palm", "Wrist",
            "ThumbMetacarpal", "ThumbProximal", "ThumbDistal", "ThumbTip",
            "IndexMetacarpal", "IndexProximal", "IndexIntermediate", "IndexDistal", "IndexTip",
            "MiddleMetacarpal", "MiddleProximal", "MiddleIntermediate", "MiddleDistal", "MiddleTip",
            "RingMetacarpal", "RingProximal", "RingIntermediate", "RingDistal", "RingTip",
            "LittleMetacarpal", "LittleProximal", "LittleIntermediate", "LittleDistal", "LittleTip"
        };

        public const int JointCount = 26;
        public const int FloatsPerJoint = 7; // pos(3) + rot(4)
    }
}
