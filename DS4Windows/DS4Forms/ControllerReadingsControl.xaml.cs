/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using NonFormTimer = System.Timers.Timer;
using DS4Windows;

namespace DS4WinWPF.DS4Forms
{
    /// <summary>
    /// Interaction logic for ControllerReadingsControl.xaml
    /// </summary>
    public partial class ControllerReadingsControl : UserControl
    {
        private enum LatencyWarnMode : uint
        {
            None,
            Caution,
            Warn,
        }

        private int deviceNum;
        private int profileDeviceNum;
        private event EventHandler DeviceNumChanged;
        private NonFormTimer readingTimer;
        private bool useTimer;
        private double lsDeadX;
        private double lsDeadY;
        private double rsDeadX;
        private double rsDeadY;

        private double sixAxisXDead;
        private double sixAxisZDead;
        private double l2Dead;
        private double r2Dead;

        private sbyte lsDriftX;
        private sbyte lsDriftY;
        private sbyte rsDriftX;
        private sbyte rsDriftY;

        // 闪烁相关字段
        private System.Windows.Threading.DispatcherTimer blinkTimer;
        private System.Windows.Threading.DispatcherTimer blinkTimeoutTimer; // 6秒超时定时器
        private bool isCalibrating;
        private bool isUnloaded; // 标记控件是否已卸载，防止后续操作
        private readonly object blinkLock = new object(); // 保护闪烁状态

        public double LsDeadX
        {
            get => lsDeadX;
            set
            {
                lsDeadX = value;
                LsDeadXChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler LsDeadXChanged;

        public double LsDeadY
        {
            get => lsDeadY;
            set
            {
                lsDeadY = value;
                LsDeadYChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler LsDeadYChanged;

        public double RsDeadX
        {
            get => rsDeadX;
            set
            {
                rsDeadX = value;
                RsDeadXChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler RsDeadXChanged;

        public double RsDeadY
        {
            get => rsDeadY;
            set
            {
                rsDeadY = value;
                RsDeadYChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler RsDeadYChanged;

        public double SixAxisXDead
        {
            get => sixAxisXDead;
            set
            {
                sixAxisXDead = value;
                SixAxisDeadXChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler SixAxisDeadXChanged;

        public double SixAxisZDead
        {
            get => sixAxisZDead;
            set
            {
                sixAxisZDead = value;
                SixAxisDeadZChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler SixAxisDeadZChanged;

        public double L2Dead
        {
            get => l2Dead;
            set
            {
                l2Dead = value;
                L2DeadChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler L2DeadChanged;

        public double R2Dead
        {
            get => r2Dead;
            set
            {
                r2Dead = value;
                R2DeadChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler R2DeadChanged;


        public sbyte LsDriftX
        {
            get => lsDriftX;
            set
            {
                lsDriftX = value;
                LsDriftXChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler LsDriftXChanged;

        public sbyte LsDriftY
        {
            get => lsDriftY;
            set
            {
                lsDriftY = value;
                LsDriftYChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler LsDriftYChanged;

        public sbyte RsDriftX
        {
            get => rsDriftX;
            set
            {
                rsDriftX = value;
                RsDriftXChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler RsDriftXChanged;

        public sbyte RsDriftY
        {
            get => rsDriftY;
            set
            {
                rsDriftY = value;
                RsDriftYChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler RsDriftYChanged;


        private LatencyWarnMode warnMode;
        private LatencyWarnMode prevWarnMode;
        private DS4State baseState = new DS4State();
        private DS4State interState = new DS4State();
        private DS4StateExposed exposeState;
        private const int CANVAS_WIDTH = 130;
        private const int CANVAS_MIDPOINT = CANVAS_WIDTH / 2;
        private const double TRIG_LB_TRANSFORM_OFFSETY = 66.0;

        public ControllerReadingsControl()
        {
            InitializeComponent();
            inputContNum.Content = $"#{deviceNum + 1}";
            exposeState = new DS4StateExposed(baseState);

            readingTimer = new NonFormTimer();
            readingTimer.Interval = 1000 / 60.0;

            // 初始化闪烁定时器和超时定时器
            blinkTimer = new System.Windows.Threading.DispatcherTimer();
            blinkTimer.Interval = TimeSpan.FromMilliseconds(250); // 与原闪烁频率一致
            blinkTimer.Tick += BlinkTimer_Tick;

            blinkTimeoutTimer = new System.Windows.Threading.DispatcherTimer();
            blinkTimeoutTimer.Interval = TimeSpan.FromSeconds(6); // 6秒超时
            blinkTimeoutTimer.Tick += BlinkTimeoutTimer_Tick;

            LsDeadXChanged += ChangeLsDeadControls;
            LsDeadYChanged += ChangeLsDeadControls;
            LsDeadXChanged += ChangeLsDriftControls;
            LsDeadYChanged += ChangeLsDriftControls;

            RsDeadXChanged += ChangeRsDeadControls;
            RsDeadYChanged += ChangeRsDeadControls;
            RsDeadXChanged += ChangeRsDriftControls;
            RsDeadYChanged += ChangeRsDriftControls;

            LsDriftXChanged += ChangeLsDriftControls;
            LsDriftYChanged += ChangeLsDriftControls;
            RsDriftXChanged += ChangeRsDriftControls;
            RsDriftYChanged += ChangeRsDriftControls;

            SixAxisDeadXChanged += ChangeSixAxisDeadControls;
            SixAxisDeadZChanged += ChangeSixAxisDeadControls;

            DeviceNumChanged += ControllerReadingsControl_DeviceNumChanged;

            // 订阅 Unloaded 事件以进行清理
            this.Unloaded += ControllerReadingsControl_Unloaded;
        }

        /// <summary>
        /// 闪烁定时器的 Tick 事件处理
        /// 切换椭圆的可见性实现闪烁效果
        /// </summary>
        private void BlinkTimer_Tick(object sender, EventArgs e)
        {
            if (isUnloaded) return;

            lock (blinkLock)
            {
                if (isCalibrating)
                {
                    gyroCalEllipse.Visibility = gyroCalEllipse.Visibility == Visibility.Visible 
                        ? Visibility.Hidden 
                        : Visibility.Visible;
                }
                else
                {
                    blinkTimer.Stop();
                }
            }
        }

        /// <summary>
        /// 超时定时器的 Tick 事件处理
        /// 6秒内未收到校准停止事件，强制停止闪烁
        /// </summary>
        private void BlinkTimeoutTimer_Tick(object sender, EventArgs e)
        {
            if (isUnloaded) return;

            lock (blinkLock)
            {
                if (isCalibrating)
                {
                    // 6秒超时，强制停止闪烁
                    isCalibrating = false;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        lock (blinkLock)
                        {
                            if (isUnloaded) return;
                            blinkTimer.Stop();
                            blinkTimeoutTimer.Stop();
                            gyroCalEllipse.Visibility = Visibility.Hidden;
                        }
                    }));
                }
                else
                {
                    blinkTimeoutTimer.Stop();
                }
            }
        }

        private void ControllerReadingsControl_DeviceNumChanged(object sender, EventArgs e)
        {
            if (isUnloaded) return;
            inputContNum.Content = $"#{deviceNum + 1}";
        }

        private void ChangeSixAxisDeadControls(object sender, EventArgs e)
        {
            if (isUnloaded) return;
            sixAxisDeadEllipse.Width = sixAxisXDead * CANVAS_WIDTH;
            sixAxisDeadEllipse.Height = sixAxisZDead * CANVAS_WIDTH;
            Canvas.SetLeft(sixAxisDeadEllipse, CANVAS_MIDPOINT - (sixAxisXDead * CANVAS_WIDTH / 2.0));
            Canvas.SetTop(sixAxisDeadEllipse, CANVAS_MIDPOINT - (sixAxisZDead * CANVAS_WIDTH / 2.0));
        }

        private void ChangeRsDriftControls(object sender, EventArgs e)
        {
            if (isUnloaded) return;
            rsDriftEllipse.Width = rsDeadX * CANVAS_WIDTH;
            rsDriftEllipse.Height = rsDeadY * CANVAS_WIDTH;
            Canvas.SetLeft(rsDriftEllipse, (1 + (RsDriftX / 127.0) - rsDeadX) * CANVAS_MIDPOINT);
            Canvas.SetTop(rsDriftEllipse, (1 + (RsDriftY / 127.0) - rsDeadY) * CANVAS_MIDPOINT);
        }

        private void ChangeLsDriftControls(object sender, EventArgs e)
        {
            if (isUnloaded) return;
            lsDriftEllipse.Width = lsDeadX * CANVAS_WIDTH;
            lsDriftEllipse.Height = lsDeadY * CANVAS_WIDTH;
            Canvas.SetLeft(lsDriftEllipse, (1 + (LsDriftX / 127.0) - lsDeadX) * CANVAS_MIDPOINT);
            Canvas.SetTop(lsDriftEllipse, (1 + (LsDriftY / 127.0) - lsDeadY) * CANVAS_MIDPOINT);
        }

        private void ChangeRsDeadControls(object sender, EventArgs e)
        {
            if (isUnloaded) return;
            rsDeadEllipse.Width = rsDeadX * CANVAS_WIDTH;
            rsDeadEllipse.Height = rsDeadY * CANVAS_WIDTH;
            Canvas.SetLeft(rsDeadEllipse, CANVAS_MIDPOINT - (rsDeadX * CANVAS_WIDTH / 2.0));
            Canvas.SetTop(rsDeadEllipse, CANVAS_MIDPOINT - (rsDeadY * CANVAS_WIDTH / 2.0));
        }

        private void ChangeLsDeadControls(object sender, EventArgs e)
        {
            if (isUnloaded) return;
            lsDeadEllipse.Width = lsDeadX * CANVAS_WIDTH;
            lsDeadEllipse.Height = lsDeadY * CANVAS_WIDTH;
            Canvas.SetLeft(lsDeadEllipse, CANVAS_MIDPOINT - (lsDeadX * CANVAS_WIDTH / 2.0));
            Canvas.SetTop(lsDeadEllipse, CANVAS_MIDPOINT - (lsDeadY * CANVAS_WIDTH / 2.0));
        }

        public void UseDevice(int index, int profileDevIdx)
        {
            if (isUnloaded) return;

            // 先取消旧设备的事件订阅，防止内存泄漏
            if (deviceNum >= 0 && deviceNum < Program.rootHub.DS4Controllers.Length)
            {
                var oldDev = Program.rootHub.DS4Controllers[deviceNum];
                if (oldDev?.SixAxis != null)
                {
                    oldDev.SixAxis.CalibrationStarted -= SixAxis_CalibrationStarted;
                    oldDev.SixAxis.CalibrationStopped -= SixAxis_CalibrationStopped;
                }
            }

            deviceNum = index;
            profileDeviceNum = profileDevIdx;
            DeviceNumChanged?.Invoke(this, EventArgs.Empty);

            // 订阅新设备的事件
            var newDev = Program.rootHub.DS4Controllers[deviceNum];
            if (newDev?.SixAxis != null)
            {
                newDev.SixAxis.CalibrationStarted += SixAxis_CalibrationStarted;
                newDev.SixAxis.CalibrationStopped += SixAxis_CalibrationStopped;

                // 如果当前设备已经在校准中，触发开始事件
                if (newDev.SixAxis.CntCalibrating > 0)
                {
                    SixAxis_CalibrationStarted(null, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// 陀螺仪校准开始事件处理
        /// </summary>
        private void SixAxis_CalibrationStarted(object sender, EventArgs e)
        {
            if (isUnloaded) return;

            lock (blinkLock)
            {
                isCalibrating = true;
                
                // 在 UI 线程上启动闪烁定时器和超时定时器
                if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        lock (blinkLock)
                        {
                            if (isUnloaded || blinkTimer == null || blinkTimeoutTimer == null) return;
                            
                            blinkTimer.Stop();
                            gyroCalEllipse.Visibility = Visibility.Visible; // 先显示
                            blinkTimer.Start();
                            
                            // 重置并启动超时定时器
                            blinkTimeoutTimer.Stop();
                            blinkTimeoutTimer.Start();
                        }
                    }));
                }
            }
        }

        /// <summary>
        /// 陀螺仪校准停止事件处理
        /// </summary>
        private void SixAxis_CalibrationStopped(object sender, EventArgs e)
        {
            if (isUnloaded) return;

            lock (blinkLock)
            {
                isCalibrating = false;
                
                if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        lock (blinkLock)
                        {
                            if (isUnloaded || blinkTimer == null || blinkTimeoutTimer == null) return;
                            
                            blinkTimer.Stop();
                            blinkTimeoutTimer.Stop();
                            gyroCalEllipse.Visibility = Visibility.Hidden; // 隐藏
                        }
                    }));
                }
            }
        }

        public void EnableControl(bool state)
        {
            if (isUnloaded) return;

            if (state)
            {
                IsEnabled = true;
                useTimer = true;
                readingTimer.Elapsed += ControllerReadingTimer_Elapsed;
                readingTimer.Start();

                // 如果当前设备已经在校准中，需要同步状态
                var dev = Program.rootHub.DS4Controllers[deviceNum];
                if (dev?.SixAxis?.CntCalibrating > 0)
                {
                    SixAxis_CalibrationStarted(null, EventArgs.Empty);
                }
            }
            else
            {
                IsEnabled = false;
                useTimer = false;
                readingTimer.Elapsed -= ControllerReadingTimer_Elapsed;
                readingTimer.Stop();

                // 停止闪烁并隐藏椭圆
                lock (blinkLock)
                {
                    if (blinkTimer != null)
                    {
                        blinkTimer.Stop();
                    }
                    if (blinkTimeoutTimer != null)
                    {
                        blinkTimeoutTimer.Stop();
                    }
                    gyroCalEllipse.Visibility = Visibility.Hidden;
                }
            }
        }

        /// <summary>
        /// 控件卸载时的清理工作（通过事件处理）
        /// </summary>
        private void ControllerReadingsControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (isUnloaded) return;
            isUnloaded = true;

            // 停止所有定时器
            lock (blinkLock)
            {
                if (blinkTimer != null)
                {
                    blinkTimer.Stop();
                    blinkTimer.Tick -= BlinkTimer_Tick;
                    blinkTimer = null;
                }
                if (blinkTimeoutTimer != null)
                {
                    blinkTimeoutTimer.Stop();
                    blinkTimeoutTimer.Tick -= BlinkTimeoutTimer_Tick;
                    blinkTimeoutTimer = null;
                }
            }

            if (readingTimer != null)
            {
                readingTimer.Stop();
                readingTimer.Elapsed -= ControllerReadingTimer_Elapsed;
                readingTimer.Dispose();
                readingTimer = null;
            }

            // 取消事件订阅
            if (deviceNum >= 0 && deviceNum < Program.rootHub.DS4Controllers.Length)
            {
                var dev = Program.rootHub.DS4Controllers[deviceNum];
                if (dev?.SixAxis != null)
                {
                    dev.SixAxis.CalibrationStarted -= SixAxis_CalibrationStarted;
                    dev.SixAxis.CalibrationStopped -= SixAxis_CalibrationStopped;
                }
            }
        }

        private void ControllerReadingTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            // 如果控件已卸载，停止定时器并返回
            if (isUnloaded)
            {
                readingTimer?.Stop();
                return;
            }

            readingTimer.Stop();

            DS4Device ds = Program.rootHub.DS4Controllers[deviceNum];
            if (ds != null)
            {
                DS4State tmpbaseState = Program.rootHub.getDS4State(deviceNum);
                DS4State tmpinterState = Program.rootHub.getDS4StateTemp(deviceNum);

                // Wait for controller to be in a wait period
                ds.ReadWaitEv.Wait();
                ds.ReadWaitEv.Reset();

                // Make copy of current state values for UI thread
                tmpbaseState.CopyTo(baseState);
                tmpinterState.CopyTo(interState);

                if (deviceNum != profileDeviceNum)
                    Mapping.SetCurveAndDeadzone(profileDeviceNum, baseState, interState);

                // Done with copying. Allow input thread to resume
                ds.ReadWaitEv.Set();

                if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (isUnloaded) return;

                        int x = baseState.LX;
                        int y = baseState.LY;

                        Canvas.SetLeft(lsValRec, x / 255.0 * CANVAS_WIDTH - 3);
                        Canvas.SetTop(lsValRec, y / 255.0 * CANVAS_WIDTH - 3);
                        Canvas.SetLeft(lsMapValRec, interState.LX / 255.0 * CANVAS_WIDTH - 3);
                        Canvas.SetTop(lsMapValRec, interState.LY / 255.0 * CANVAS_WIDTH - 3);

                        x = baseState.RX;
                        y = baseState.RY;
                        Canvas.SetLeft(rsValRec, x / 255.0 * CANVAS_WIDTH - 3);
                        Canvas.SetTop(rsValRec, y / 255.0 * CANVAS_WIDTH - 3);
                        Canvas.SetLeft(rsMapValRec, interState.RX / 255.0 * CANVAS_WIDTH - 3);
                        Canvas.SetTop(rsMapValRec, interState.RY / 255.0 * CANVAS_WIDTH - 3);

                        x = exposeState.getAccelX() + 127;
                        y = exposeState.getAccelZ() + 127;
                        Canvas.SetLeft(sixAxisValRec, x / 255.0 * CANVAS_WIDTH - 3);
                        Canvas.SetTop(sixAxisValRec, y / 255.0 * CANVAS_WIDTH - 3);
                        Canvas.SetLeft(sixAxisMapValRec, Math.Min(Math.Max(interState.Motion.outputAccelX + 127.0, 0), 255.0) / 255.0 * CANVAS_WIDTH - 3);
                        Canvas.SetTop(sixAxisMapValRec, Math.Min(Math.Max(interState.Motion.outputAccelZ + 127.0, 0), 255.0) / 255.0 * CANVAS_WIDTH - 3);

                        l2Slider.Value = baseState.L2;
                        l2ValLbTrans.Y = Math.Min(interState.L2, Math.Max(0, 255)) / 255.0 * -70.0 + TRIG_LB_TRANSFORM_OFFSETY;
                        if (interState.L2 >= 255)
                        {
                            l2ValLbBrush.Color = Colors.Green;
                        }
                        else if (interState.L2 == 0)
                        {
                            l2ValLbBrush.Color = Colors.Red;
                        }
                        else
                        {
                            l2ValLbBrush.Color = Colors.Black;
                        }

                        r2Slider.Value = baseState.R2;
                        r2ValLbTrans.Y = Math.Min(interState.R2, Math.Max(0, 255)) / 255.0 * -70.0 + TRIG_LB_TRANSFORM_OFFSETY;
                        if (interState.R2 >= 255)
                        {
                            r2ValLbBrush.Color = Colors.Green;
                        }
                        else if (interState.R2 == 0)
                        {
                            r2ValLbBrush.Color = Colors.Red;
                        }
                        else
                        {
                            r2ValLbBrush.Color = Colors.Black;
                        }

                        gyroYawSlider.Value = baseState.Motion.gyroYawFull;
                        gyroPitchSlider.Value = baseState.Motion.gyroPitchFull;
                        gyroRollSlider.Value = baseState.Motion.gyroRollFull;

                        accelXSlider.Value = exposeState.getAccelX();
                        accelYSlider.Value = exposeState.getAccelY();
                        accelZSlider.Value = exposeState.getAccelZ();

                        touchXValLb.Content = baseState.TrackPadTouch0.X;
                        touchYValLb.Content = baseState.TrackPadTouch0.Y;

                        double latency = ds.Latency;
                        int warnInterval = ds.getWarnInterval();
                        inputDelayLb.Content = string.Format(Properties.Resources.InputDelay,
                            latency.ToString());

                        if (latency > warnInterval)
                        {
                            warnMode = LatencyWarnMode.Warn;
                            inpuDelayBackBrush.Color = Colors.Red;
                            inpuDelayForeBrush.Color = Colors.White;
                        }
                        else if (latency > (warnInterval * 0.5))
                        {
                            warnMode = LatencyWarnMode.Caution;
                            inpuDelayBackBrush.Color = Colors.Yellow;
                            inpuDelayForeBrush.Color = Colors.Black;
                        }
                        else
                        {
                            warnMode = LatencyWarnMode.None;
                            inpuDelayBackBrush.Color = Colors.Transparent;
                            inpuDelayForeBrush.Color = SystemColors.WindowTextColor;
                        }

                        prevWarnMode = warnMode;

                        batteryLvlLb.Content = $"{Translations.Strings.Battery}: {baseState.Battery}%";

                        UpdateCoordLabels(baseState, interState, exposeState);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }

            if (useTimer && !isUnloaded)
            {
                readingTimer.Start();
            }
        }

        private void UpdateCoordLabels(DS4State inState, DS4State mapState,
            DS4StateExposed exposeState)
        {
            if (isUnloaded) return;
            lxInValLb.Content = inState.LX;
            lxOutValLb.Content = mapState.LX;
            lyInValLb.Content = inState.LY;
            lyOutValLb.Content = mapState.LY;

            rxInValLb.Content = inState.RX;
            rxOutValLb.Content = mapState.RX;
            ryInValLb.Content = inState.RY;
            ryOutValLb.Content = mapState.RY;

            sixAxisXInValLb.Content = exposeState.AccelX;
            sixAxisXOutValLb.Content = mapState.Motion.outputAccelX;
            sixAxisZInValLb.Content = exposeState.AccelZ;
            sixAxisZOutValLb.Content = mapState.Motion.outputAccelZ;

            l2InValLb.Content = inState.L2;
            l2OutValLb.Content = mapState.L2;
            r2InValLb.Content = inState.R2;
            r2OutValLb.Content = mapState.R2;
        }
    }
}