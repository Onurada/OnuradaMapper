using System;
using BeatSaber.Haptics;
using UnityEngine;
using UnityEngine.XR;

namespace OnuradaLyrics
{
    /// <summary>Heartbeat rumble in both controllers during dream sections ("lub-dub" pairs from lyrics.json "heartbeat").</summary>
    public partial class Plugin
    {
        HapticFeedbackManager _haptics;
        HapticPresetSO _lub, _dub;
        float _hbLastT = -99f, _nextHapticSearch;
        int _hbIdx;

        static HapticPresetSO Preset(float duration, float strength, float frequency)
        {
            var p = ScriptableObject.CreateInstance<HapticPresetSO>();
            p._duration = duration; p._strength = strength; p._frequency = frequency;
            p._continuous = false; p._useAdvancedHapticsOnSupportedPlatforms = false;
            return p;
        }

        void InitHeartbeat()
        {
            _haptics = null; _hbIdx = 0; _hbLastT = -99f;
            if (_lub == null) { _lub = Preset(0.11f, 0.9f, 0.25f); _dub = Preset(0.07f, 0.5f, 0.2f); }
        }

        /// strength of the most recent heartbeat (0..1), used to make the dream dust throb with it
        float HeartPulse(float t)
        {
            var hb = _lyrics?.heartbeat;
            if (hb == null || hb.Length == 0) return 0f;
            int i = Array.BinarySearch(hb, t);
            if (i < 0) i = ~i - 1;
            if (i < 0 || t - hb[i] > 0.6f) return 0f;
            return Mathf.Exp(-(t - hb[i]) * 9f);
        }

        void RenderHeartbeat(float t)
        {
            var hb = _lyrics?.heartbeat;
            if (hb == null || hb.Length == 0) return;
            if (_haptics == null && Time.unscaledTime >= _nextHapticSearch)
            {
                _nextHapticSearch = Time.unscaledTime + 0.5f;
                var f = FindObjectsByType<HapticFeedbackManager>(FindObjectsSortMode.None);
                if (f != null && f.Length > 0) _haptics = f[0];
            }
            if (t < _hbLastT - 0.25f || _hbLastT < -90f)
            {
                // first frame or a seek backwards: continue from the next beat after now
                _hbIdx = Array.BinarySearch(hb, t);
                if (_hbIdx < 0) _hbIdx = ~_hbIdx;
            }
            _hbLastT = t;
            while (_hbIdx < hb.Length && hb[_hbIdx] <= t)
            {
                // even entries are the "lub", odd ones the softer "dub"
                if (_haptics != null && t - hb[_hbIdx] < 0.1f)
                {
                    var p = _lyrics.heartbeatDub != null && _hbIdx < _lyrics.heartbeatDub.Length && _lyrics.heartbeatDub[_hbIdx] ? _dub : _lub;
                    try { _haptics.PlayHapticFeedback(XRNode.LeftHand, p); _haptics.PlayHapticFeedback(XRNode.RightHand, p); }
                    catch (Exception e) { Logger.LogError(e); _lyrics.heartbeat = null; return; }
                }
                _hbIdx++;
            }
        }
    }
}
