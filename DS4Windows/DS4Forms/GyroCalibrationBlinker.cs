using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using DS4Windows;

namespace DS4WinWPF.DS4Forms
{
    /// <summary>
    /// 设备级陀螺仪校准闪烁管理器。
    /// 与 DS4Device 同时创建，内部订阅校准事件，维护当前校准状态。
    /// UI 组件通过 RegisterCallback 注册自己的显示更新逻辑。
    /// </summary>
    public class GyroCalibrationBlinker : IDisposable
    {
        private readonly DS4Device _device;
        private readonly Dispatcher _dispatcher;
        private DispatcherTimer _blinkTimer;
        private DispatcherTimer _blinkTimeoutTimer;
        private bool _isCalibrating;      // 当前是否正在校准
        private bool _blinkVisible;       // 当前闪烁状态
        private bool _disposed;
        private readonly object _lock = new object();
        private readonly List<Action<bool>> _callbacks = new List<Action<bool>>();

        public GyroCalibrationBlinker(DS4Device device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));

            // 获取 UI 线程 Dispatcher
            if (Application.Current != null && Application.Current.Dispatcher != null)
                _dispatcher = Application.Current.Dispatcher;
            else
                _dispatcher = Dispatcher.CurrentDispatcher;

            // 订阅设备校准事件
            _device.SixAxis.CalibrationStarted += OnCalibrationStarted;
            _device.SixAxis.CalibrationStopped += OnCalibrationStopped;

            // 如果设备已经在校准中，立即同步状态
            if (_device.SixAxis.CntCalibrating > 0)
                OnCalibrationStarted(null, null);

            // 在 UI 线程初始化定时器
            if (!_dispatcher.CheckAccess())
                _dispatcher.Invoke(InitializeTimers);
            else
                InitializeTimers();
        }

        private void InitializeTimers()
        {
            _blinkTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _blinkTimer.Tick += BlinkTimer_Tick;

            _blinkTimeoutTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5.25)
            };
            _blinkTimeoutTimer.Tick += (s, e) => StopCalibration();
        }

        private void BlinkTimer_Tick(object sender, EventArgs e)
        {
            if (_disposed) return;
            lock (_lock)
            {
                if (!_isCalibrating) return;
                _blinkVisible = !_blinkVisible;
                NotifyCallbacks(_blinkVisible);
            }
        }

        private void OnCalibrationStarted(object sender, EventArgs e)
        {
            if (_disposed) return;
            lock (_lock)
            {
                if (_isCalibrating) return;
                _isCalibrating = true;
                _blinkVisible = true;
                NotifyCallbacks(_blinkVisible);

                // 启动定时器（必须在 UI 线程）
                if (!_dispatcher.CheckAccess())
                    _dispatcher.BeginInvoke(() => { _blinkTimer?.Start(); _blinkTimeoutTimer?.Start(); });
                else
                { _blinkTimer?.Start(); _blinkTimeoutTimer?.Start(); }
            }
        }

        private void OnCalibrationStopped(object sender, EventArgs e)
        {
            StopCalibration();
        }

        private void StopCalibration()
        {
            if (_disposed) return;
            lock (_lock)
            {
                if (!_isCalibrating) return;
                _isCalibrating = false;

                // 停止定时器
                if (!_dispatcher.CheckAccess())
                    _dispatcher.BeginInvoke(() => { _blinkTimer?.Stop(); _blinkTimeoutTimer?.Stop(); });
                else
                { _blinkTimer?.Stop(); _blinkTimeoutTimer?.Stop(); }

                // 通知所有回调，校准结束（false 表示停止闪烁）
                NotifyCallbacks(false);
            }
        }

        /// <summary>
        /// 注册回调，当校准状态变化时调用。
        /// 注册时会立即返回当前校准状态（如果是 true，则返回当前闪烁状态，否则返回 false）。
        /// </summary>
        /// <param name="callback">回调委托，参数 true=显示闪烁/开始闪烁，false=隐藏/停止闪烁</param>
        public void RegisterCallback(Action<bool> callback)
        {
            if (callback == null) return;
            lock (_lock)
            {
                _callbacks.Add(callback);
                // 立即推送当前状态
                if (_isCalibrating) callback(_blinkVisible);
            }
        }

        /// <summary>
        /// 移除回调
        /// </summary>
        public void UnregisterCallback(Action<bool> callback)
        {
            if (callback == null) return;
            lock (_lock)
            {
                _callbacks.Remove(callback);
            }
        }

        private void NotifyCallbacks(bool visible)
        {
            Action<bool>[] callbacksCopy;
            lock (_lock)
            {
                callbacksCopy = _callbacks.ToArray();
            }
            foreach (var cb in callbacksCopy)
            {
                try
                {
                    cb(visible);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"GyroCalibrationBlinker callback error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 强制重置校准（用于手动校准）
        /// </summary>
        public void ForceResetCalibration()
        {
            _device.SixAxis.ForceResetContinuousCalibration();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_device?.SixAxis != null)
            {
                _device.SixAxis.CalibrationStarted -= OnCalibrationStarted;
                _device.SixAxis.CalibrationStopped -= OnCalibrationStopped;
            }

            StopCalibration();

            if (_blinkTimer != null)
            {
                _blinkTimer.Tick -= BlinkTimer_Tick;
                _blinkTimer = null;
            }
            if (_blinkTimeoutTimer != null)
            {
                _blinkTimeoutTimer.Tick -= null;
                _blinkTimeoutTimer = null;
            }

            lock (_lock)
            {
                _callbacks.Clear();
            }
        }
    }
}