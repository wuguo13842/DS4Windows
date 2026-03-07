using System;
using System.Collections.Generic;
using DS4Windows;

namespace DS4WinWPF.DS4Control;

public class Debouncer(TimeSpan duration)
{
    private readonly Dictionary<string, DebouncerInstance> _debouncers = new();

    public void AddDebouncer(string name)
    {
        _debouncers[name] = new DebouncerInstance(duration);
    }

    public DS4State ProcessInput(DS4State cState)
    {
        // 如果去抖时间为0，直接返回原状态（不克隆，避免不必要开销）
        if (duration.TotalMilliseconds == 0) return cState;

        // 必须克隆，因为调用者可能需要保留原始状态，但我们可以复用同一个克隆对象？
        // 为安全起见，仍然克隆，但去掉反射。
        DS4State modifiedState = new DS4State();
        cState.CopyTo(modifiedState);

        // 硬编码所有需要去抖动的按钮字段
        modifiedState.Cross     = _debouncers[nameof(DS4State.Cross)].ProcessInput(modifiedState.Cross, cState.ReportTimeStamp);
        modifiedState.Triangle   = _debouncers[nameof(DS4State.Triangle)].ProcessInput(modifiedState.Triangle, cState.ReportTimeStamp);
        modifiedState.Circle     = _debouncers[nameof(DS4State.Circle)].ProcessInput(modifiedState.Circle, cState.ReportTimeStamp);
        modifiedState.Square     = _debouncers[nameof(DS4State.Square)].ProcessInput(modifiedState.Square, cState.ReportTimeStamp);
        modifiedState.R3         = _debouncers[nameof(DS4State.R3)].ProcessInput(modifiedState.R3, cState.ReportTimeStamp);
        modifiedState.L3         = _debouncers[nameof(DS4State.L3)].ProcessInput(modifiedState.L3, cState.ReportTimeStamp);
        modifiedState.Options    = _debouncers[nameof(DS4State.Options)].ProcessInput(modifiedState.Options, cState.ReportTimeStamp);
        modifiedState.Share      = _debouncers[nameof(DS4State.Share)].ProcessInput(modifiedState.Share, cState.ReportTimeStamp);
        modifiedState.R2Btn      = _debouncers[nameof(DS4State.R2Btn)].ProcessInput(modifiedState.R2Btn, cState.ReportTimeStamp);
        modifiedState.L2Btn      = _debouncers[nameof(DS4State.L2Btn)].ProcessInput(modifiedState.L2Btn, cState.ReportTimeStamp);
        modifiedState.R1         = _debouncers[nameof(DS4State.R1)].ProcessInput(modifiedState.R1, cState.ReportTimeStamp);
        modifiedState.L1         = _debouncers[nameof(DS4State.L1)].ProcessInput(modifiedState.L1, cState.ReportTimeStamp);
        modifiedState.PS         = _debouncers[nameof(DS4State.PS)].ProcessInput(modifiedState.PS, cState.ReportTimeStamp);
        modifiedState.TouchButton = _debouncers[nameof(DS4State.TouchButton)].ProcessInput(modifiedState.TouchButton, cState.ReportTimeStamp);
        modifiedState.Capture    = _debouncers[nameof(DS4State.Capture)].ProcessInput(modifiedState.Capture, cState.ReportTimeStamp);
        modifiedState.SideL      = _debouncers[nameof(DS4State.SideL)].ProcessInput(modifiedState.SideL, cState.ReportTimeStamp);
        modifiedState.SideR      = _debouncers[nameof(DS4State.SideR)].ProcessInput(modifiedState.SideR, cState.ReportTimeStamp);
        modifiedState.DpadUp     = _debouncers[nameof(DS4State.DpadUp)].ProcessInput(modifiedState.DpadUp, cState.ReportTimeStamp);
        modifiedState.DpadDown   = _debouncers[nameof(DS4State.DpadDown)].ProcessInput(modifiedState.DpadDown, cState.ReportTimeStamp);
        modifiedState.DpadLeft   = _debouncers[nameof(DS4State.DpadLeft)].ProcessInput(modifiedState.DpadLeft, cState.ReportTimeStamp);
        modifiedState.DpadRight  = _debouncers[nameof(DS4State.DpadRight)].ProcessInput(modifiedState.DpadRight, cState.ReportTimeStamp);

        return modifiedState;
    }

    public void SetDuration(TimeSpan newDuration)
    {
        foreach (var debouncer in _debouncers.Values)
        {
            debouncer.Duration = newDuration;
        }
    }

    private class DebouncerInstance(TimeSpan duration)
    {
        private bool _previousState;
        private bool _currentlyDebouncing;
        private DateTime _debounceStartTime;

        public TimeSpan Duration { get; set; } = duration;

        public bool ProcessInput(bool input, DateTime timestamp)
        {
            if (_currentlyDebouncing)
            {
                return Debounce(input, timestamp);
            }

            if (_previousState != input)
            {
                StartDebouncing(input, timestamp);
                return true; // 去抖期间视为按下状态
            }

            _previousState = input;
            return input;
        }

        private void StartDebouncing(bool input, DateTime timestamp)
        {
            _currentlyDebouncing = true;
            _debounceStartTime = timestamp;
        }

        private void StopDebouncing()
        {
            _currentlyDebouncing = false;
        }

        private bool Debounce(bool reading, DateTime timestamp)
        {
            var span = timestamp - _debounceStartTime;
            if (span.TotalMilliseconds < Duration.TotalMilliseconds) return true;

            StopDebouncing();
            return reading;
        }
    }
}