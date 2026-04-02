using System;
using System.Windows;
using System.Windows.Threading;
using DS4Windows;

namespace DS4WinWPF.DS4Forms
{
    /// <summary>
    /// 为指定的 DS4Device 管理陀螺仪校准时的 UI 闪烁效果。
    /// 监听设备的 CalibrationStarted / CalibrationStopped 事件，控制闪烁定时器，
    /// 并通过两个回调通知 UI：
    /// - onBlinkUpdate：在校准过程中每 250ms 调用一次，参数为交替的 true/false，用于实现闪烁效果。
    /// - onStopped：校准完全停止时调用一次，用于恢复 UI 到默认状态（如恢复托盘图标）。
    /// 注意：所有 UI 更新都会通过 Dispatcher 封送到 UI 线程。
    /// </summary>
    public class GyroCalibrationBlinker : IDisposable
    {
        private readonly DS4Device _device;
        private readonly Action<bool> _onBlinkUpdate;
        private readonly Action _onStopped;
        private readonly Dispatcher _dispatcher;
        private DispatcherTimer _blinkTimer;
        private DispatcherTimer _blinkTimeoutTimer;
        private bool _isBlinking;
        private bool _blinkVisible;
        private bool _disposed;
        private readonly object _timerLock = new object();

        public GyroCalibrationBlinker(DS4Device device, Action<bool> onBlinkUpdate, Action onStopped = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _onBlinkUpdate = onBlinkUpdate ?? throw new ArgumentNullException(nameof(onBlinkUpdate));
            _onStopped = onStopped;

            // 获取 UI 线程的 Dispatcher
            if (Application.Current != null && Application.Current.Dispatcher != null)
                _dispatcher = Application.Current.Dispatcher;
            else
                _dispatcher = Dispatcher.CurrentDispatcher;

            // 必须在 UI 线程上初始化定时器
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(new Action(InitializeTimers));
            }
            else
            {
                InitializeTimers();
            }

            // 订阅设备校准事件（这些事件可能从后台线程触发）
            _device.SixAxis.CalibrationStarted += OnCalibrationStarted;
            _device.SixAxis.CalibrationStopped += OnCalibrationStopped;

            // 如果设备已经在校准中，立即启动闪烁
            if (_device.SixAxis.CntCalibrating > 0)
            {
                // 确保在 UI 线程上启动
                if (!_dispatcher.CheckAccess())
                    _dispatcher.BeginInvoke(new Action(StartBlinking));
                else
                    StartBlinking();
            }
        }

        private void InitializeTimers()
        {
            lock (_timerLock)
            {
                // 创建 UI 线程定时器 - 闪烁间隔 250ms
                _blinkTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _blinkTimer.Tick += BlinkTimer_Tick;

                // 创建超时定时器 - 5.25 秒后强制停止闪烁
                _blinkTimeoutTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(5.25)
                };
                _blinkTimeoutTimer.Tick += (s, e) => StopBlinking();
            }
        }

        private void BlinkTimer_Tick(object sender, EventArgs e)
        {
            if (_disposed) return;
            if (!_isBlinking) return;

            _blinkVisible = !_blinkVisible;
            InvokeBlinkUpdate(_blinkVisible);
        }

        private void OnCalibrationStarted(object sender, EventArgs e)
        {
            // 确保在 UI 线程上启动闪烁
            if (!_dispatcher.CheckAccess())
                _dispatcher.BeginInvoke(new Action(StartBlinking));
            else
                StartBlinking();
        }

        private void OnCalibrationStopped(object sender, EventArgs e)
        {
            // 确保在 UI 线程上停止闪烁
            if (!_dispatcher.CheckAccess())
                _dispatcher.BeginInvoke(new Action(StopBlinking));
            else
                StopBlinking();
        }

        private void StartBlinking()
        {
            if (_disposed) return;
            if (_isBlinking) return;

            _isBlinking = true;
            _blinkVisible = true;
            InvokeBlinkUpdate(_blinkVisible);

            lock (_timerLock)
            {
                if (_blinkTimer != null)
                    _blinkTimer.Start();
                if (_blinkTimeoutTimer != null)
                    _blinkTimeoutTimer.Start();
            }
        }

        private void StopBlinking()
        {
            if (_disposed) return;
            if (!_isBlinking) return;

            _isBlinking = false;

            // 停止定时器
            lock (_timerLock)
            {
                if (_blinkTimer != null)
                    _blinkTimer.Stop();
                if (_blinkTimeoutTimer != null)
                    _blinkTimeoutTimer.Stop();
            }

			// 关键修改：如果有停止回调，调用它恢复 UI；否则调用一次闪烁更新来隐藏
			if (_onStopped != null) InvokeStopped();
			else InvokeBlinkUpdate(false);
        }

        private void InvokeBlinkUpdate(bool visible)
        {
            if (_disposed) return;
            if (_dispatcher.CheckAccess())
                _onBlinkUpdate(visible);
            else
                _dispatcher.BeginInvoke((Action)(() => _onBlinkUpdate(visible)));
        }

        private void InvokeStopped()
        {
            if (_disposed || _onStopped == null) return;
            if (_dispatcher.CheckAccess())
                _onStopped();
            else
                _dispatcher.BeginInvoke((Action)(() => _onStopped()));
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

            StopBlinking();

            lock (_timerLock)
            {
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
            }
        }
    }
}