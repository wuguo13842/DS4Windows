﻿/*
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
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DS4Windows;
using WPFLocalizeExtension.Extensions;
using DS4WinWPF.Translations; // 添加此命名空间以使用 Strings

namespace DS4WinWPF.DS4Forms.ViewModels
{
    public class TrayIconViewModel
    {
        private string tooltipText = "DS4Windows";
        private string iconSource;
        public const string ballonTitle = "DS4Windows";
        public static string trayTitle = $"DS4Windows v{Global.exeversion}";
        private ContextMenu contextMenu;
        private MenuItem changeServiceItem;
        private MenuItem openItem;
        private MenuItem minimizeItem;
        private MenuItem openProgramItem;
        private MenuItem closeItem;
        private int? prevBattery = null;

        // 闪烁相关字段
        private System.Windows.Threading.DispatcherTimer blinkTimer;          // 闪烁定时器（250ms）
        private System.Windows.Threading.DispatcherTimer blinkTimeoutTimer;  // 超时定时器（6秒）
        private bool isBlinking = false;            // 是否正在闪烁
        private string batteryIcon;                  // 当前应显示的电池图标（非闪烁时使用）
        private string gyroIcon;                     // 陀螺校准图标路径
        private readonly object calibrationLock = new object(); // 保护 _calibratingDevices
        private readonly object blinkLock = new object();       // 保护闪烁状态
        private HashSet<DS4Device> _calibratingDevices = new HashSet<DS4Device>(); // 记录正在校准的设备

        public string TooltipText
        {
            get => tooltipText;
            set
            {
                string temp = value;
                if (value.Length > 63) temp = value.Substring(0, 63);
                if (tooltipText == temp) return;
                tooltipText = temp;
                try
                {
                    TooltipTextChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (InvalidOperationException) { }
            }
        }
        public event EventHandler TooltipTextChanged;

        public string IconSource
        {
            get => iconSource;
            set
            {
                if (iconSource == value) return;
                iconSource = value;
                IconSourceChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public ContextMenu ContextMenu { get => contextMenu; }

        public event EventHandler IconSourceChanged;
        public event EventHandler RequestShutdown;
        public event EventHandler RequestOpen;
        public event EventHandler RequestMinimize;
        public event EventHandler RequestServiceChange;

        private ReaderWriterLockSlim _colLocker = new ReaderWriterLockSlim();
        private List<ControllerHolder> controllerList = new List<ControllerHolder>();
        private ProfileList profileListHolder;
        private ControlService controlService;

        public delegate void ProfileSelectedHandler(TrayIconViewModel sender,
            ControllerHolder item, string profile);
        public event ProfileSelectedHandler ProfileSelected;

        public TrayIconViewModel(ControlService service, ProfileList profileListHolder)
        {
            this.profileListHolder = profileListHolder;
            this.controlService = service;
            contextMenu = new ContextMenu();
            iconSource = Global.iconChoiceResources[Global.UseIconChoice];
            gyroIcon = $"{Global.RESOURCES_PREFIX}/gyro.ico"; // 假设 gyro.ico 位于 Resources 文件夹
            Global.BatteryChanged += UpdateTrayBattery;

            // 初始化闪烁定时器和超时定时器（在 UI 线程上创建）
            Application.Current.Dispatcher.Invoke(() =>
            {
                blinkTimer = new System.Windows.Threading.DispatcherTimer();
                blinkTimer.Interval = TimeSpan.FromMilliseconds(250); // 与陀螺仪闪烁频率一致
                blinkTimer.Tick += BlinkTimer_Tick;

                blinkTimeoutTimer = new System.Windows.Threading.DispatcherTimer();
                blinkTimeoutTimer.Interval = TimeSpan.FromSeconds(5.25); // 超时6秒
                blinkTimeoutTimer.Tick += BlinkTimeoutTimer_Tick;
            });

            // 初始化菜单项
            changeServiceItem = new MenuItem()
            {
                Header = GetLocalizedString("ServiceStart"),
                IsEnabled = false
            };
            changeServiceItem.Click += ChangeControlServiceItem_Click;
            openItem = new MenuItem() {  Header = GetLocalizedString("MenuOpen"),
                FontWeight = FontWeights.Bold };
            openItem.Click += OpenMenuItem_Click;
            minimizeItem = new MenuItem() { Header = GetLocalizedString("MenuMinimize") };
            minimizeItem.Click += MinimizeMenuItem_Click;
            openProgramItem = new MenuItem() { Header = GetLocalizedString("MenuOpenProgramFolder") };
            openProgramItem.Click += OpenProgramFolderItem_Click;
            closeItem = new MenuItem()  { Header = GetLocalizedString("MenuExit") };
            closeItem.Click += ExitMenuItem_Click;

            PopulateControllerList();
            PopulateToolText();
            PopulateContextMenu();
            SetupEvents();
            profileListHolder.ProfileListCol.CollectionChanged += ProfileListCol_CollectionChanged;

            service.ServiceStarted += BuildControllerList;
            service.ServiceStarted += HookEvents;
            service.ServiceStarted += StartPopulateText;
            service.PreServiceStop += ClearToolText;
            service.PreServiceStop += UnhookEvents;
            service.PreServiceStop += ClearControllerList;
            service.RunningChanged += Service_RunningChanged;
            service.HotplugController += Service_HotplugController;
        }

        private string GetLocalizedString(string key)
        {
            return LocExtension.GetLocalizedValue<string>(key);
        }

        private void Service_RunningChanged(object sender, EventArgs e)
        {
            string headerKey = controlService.running ? "ServiceStop" : "ServiceStart";
            App.Current.Dispatcher.BeginInvoke((Action)(() =>
            {
                changeServiceItem.Header = GetLocalizedString(headerKey);
                changeServiceItem.IsEnabled = true;
            }));
        }

        private void ClearControllerList(object sender, EventArgs e)
        {
            _colLocker.EnterWriteLock();
            controllerList.Clear();
            _colLocker.ExitWriteLock();
        }

        private void UnhookEvents(object sender, EventArgs e)
        {
            _colLocker.EnterReadLock();
            foreach (ControllerHolder holder in controllerList)
            {
                DS4Device currentDev = holder.Device;
                RemoveDeviceEvents(currentDev);
            }
            _colLocker.ExitReadLock();
        }

        private void Service_HotplugController(ControlService sender, DS4Device device, int index)
        {
            SetupDeviceEvents(device);
            _colLocker.EnterWriteLock();
            controllerList.Add(new ControllerHolder(device, index));
            _colLocker.ExitWriteLock();
        }

        private void ProfileListCol_CollectionChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            PopulateContextMenu();
        }

        private void BuildControllerList(object sender, EventArgs e)
        {
            PopulateControllerList();
        }

        /// <summary>
        /// 构建托盘图标右键菜单
        /// 将配置文件切换、断开连接、陀螺仪校准功能整合到每个控制器的子菜单中
        /// </summary>
        public void PopulateContextMenu()
        {
            contextMenu.Items.Clear();
            ItemCollection items = contextMenu.Items;
            int idx = 0;

            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                // 为每个控制器创建独立的子菜单
                foreach (ControllerHolder holder in controllerList)
                {
                    DS4Device currentDev = holder.Device;
                    
                    // 创建控制器主菜单项
                    MenuItem controllerItem = new MenuItem() 
                    { 
                        Header = GetLocalizedString("Controllers") + " " + (idx + 1) 
                    };
                    controllerItem.Tag = idx;
                    ItemCollection subitems = controllerItem.Items;

                    // 1. 添加配置文件切换子菜单项
                    string currentProfile = Global.ProfilePath[idx];
                    foreach (ProfileEntity entry in profileListHolder.ProfileListCol)
                    {
                        string name = entry.Name;
                        name = Regex.Replace(name, "_{1}", "__");
                        MenuItem profileItem = new MenuItem() { Header = name };
                        profileItem.Tag = idx;
                        profileItem.Click += ProfileItem_Click;
                        if (entry.Name == currentProfile)
                        {
                            profileItem.IsChecked = true;
                        }
                        subitems.Add(profileItem);
                    }

                    // 2. 如果有配置文件列表，添加分隔符
                    if (profileListHolder.ProfileListCol.Count > 0)
                    {
                        subitems.Add(new Separator());
                    }

                    // 3. 添加断开连接菜单项（仅当设备支持断开时显示）
                    if (currentDev.Synced && !currentDev.Charging)
                    {
                        MenuItem disconnectItem = new MenuItem() 
                        { 
                            Header = GetLocalizedString("DisconnectController")
                        };
                        disconnectItem.Click += DisconnectMenuItem_Click;
                        disconnectItem.Tag = idx;
                        subitems.Add(disconnectItem);
                    }

                    // 4. 添加陀螺仪校准菜单项（仅当设备支持陀螺仪时显示）
                    if (currentDev?.SixAxis != null)
                    {
                        MenuItem gyroItem = new MenuItem() 
                        { 
                            Header = GetLocalizedString("GyroCalibration")
                        };
                        gyroItem.Click += CalibrateGyroMenuItem_Click;
                        gyroItem.Tag = idx;
                        subitems.Add(gyroItem);
                    }

                    // 将控制器菜单项添加到主菜单
                    items.Add(controllerItem);
                    idx++;
                }

                // 添加分隔符
                items.Add(new Separator());
                
                // 添加静态菜单项（服务控制、打开、最小化、打开程序文件夹、退出）
                PopulateStaticItems();
            }
        }

        private void ChangeControlServiceItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            changeServiceItem.IsEnabled = false;
            RequestServiceChange?.Invoke(this, EventArgs.Empty);
        }

        private void OpenProgramFolderItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(Global.exedirpath);
            startInfo.UseShellExecute = true;
            using (Process temp = Process.Start(startInfo)) { }
        }

        private void OpenMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RequestOpen?.Invoke(this, EventArgs.Empty);
        }

        private void MinimizeMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RequestMinimize?.Invoke(this, EventArgs.Empty);
        }

        private void ProfileItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            int idx = Convert.ToInt32(item.Tag);
            ControllerHolder holder = controllerList[idx];
            string tempProfileName = Regex.Replace(item.Header.ToString(), "_{2}", "_");
            ProfileSelected?.Invoke(this, holder, tempProfileName);
        }

        private void DisconnectMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            int idx = Convert.ToInt32(item.Tag);
            ControllerHolder holder = controllerList[idx];
            DS4Device tempDev = holder?.Device;
            if (tempDev != null && tempDev.Synced && !tempDev.Charging)
            {
                if (tempDev.ConnectionType == ConnectionType.BT)
                {
                    tempDev.DisconnectBT();
                }
                else if (tempDev.ConnectionType == ConnectionType.SONYWA)
                {
                    tempDev.DisconnectDongle();
                }
            }
        }

        /// <summary>
        /// 陀螺仪校准菜单项点击事件
        /// </summary>
        private void CalibrateGyroMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            int idx = Convert.ToInt32(item.Tag);
            ControllerHolder holder = controllerList[idx];
            DS4Device device = holder?.Device;
            if (device != null)
            {
                // 发送系统通知（普通通知）
                string message = string.Format(Strings.GyroCalibrationStarted, idx + 1);
                AppLogger.LogToTray(message, false);

                // 执行陀螺仪校准重置
                device.SixAxis.ResetContinuousCalibration();
                if (device.JointDeviceSlotNumber != DS4Device.DEFAULT_JOINT_SLOT_NUMBER)
                {
                    DS4Device tempDev = controlService.DS4Controllers[device.JointDeviceSlotNumber];
                    tempDev?.SixAxis.ResetContinuousCalibration();
                }
            }
        }

        private void PopulateControllerList()
        {
            int idx = 0;
            _colLocker.EnterWriteLock();
            foreach (DS4Device currentDev in controlService.slotManager.ControllerColl)
            {
                controllerList.Add(new ControllerHolder(currentDev, idx));
                idx++;
            }
            _colLocker.ExitWriteLock();
        }

        private void StartPopulateText(object sender, EventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                PopulateToolText();
            });
        }

        private void PopulateToolText()
        {
            List<string> items = new List<string>();
            items.Add(trayTitle);
            int idx = 1;
            _colLocker.EnterReadLock();
            foreach (ControllerHolder holder in controllerList)
            {
                DS4Device currentDev = holder.Device;
                items.Add($"{idx}: {currentDev.ConnectionType} {currentDev.Battery}%{(currentDev.Charging ? "+" : "")}");
                idx++;
            }
            _colLocker.ExitReadLock();
            TooltipText = string.Join("\n", items);
        }

        private void SetupEvents()
        {
            _colLocker.EnterReadLock();
            foreach (ControllerHolder holder in controllerList)
            {
                DS4Device currentDev = holder.Device;
                SetupDeviceEvents(currentDev);
            }
            _colLocker.ExitReadLock();
        }

        private void SetupDeviceEvents(DS4Device device)
        {
            device.BatteryChanged += UpdateForBattery;
            device.ChargingChanged += UpdateForBattery;
            device.Removal += CurrentDev_Removal;

            if (device?.SixAxis != null)
            {
                // 使用 lambda 捕获 device 对象，确保事件处理时能直接使用正确的设备
                device.SixAxis.CalibrationStarted += (s, e) => Device_CalibrationStarted(device, e);
                device.SixAxis.CalibrationStopped += (s, e) => Device_CalibrationStopped(device, e);

                // 如果设备已经在校准中，手动触发开始事件（传入设备对象）
                if (device.SixAxis.CntCalibrating > 0)
                {
                    Device_CalibrationStarted(device, EventArgs.Empty);
                }
            }
        }

        private void RemoveDeviceEvents(DS4Device device)
        {
            device.BatteryChanged -= UpdateForBattery;
            device.ChargingChanged -= UpdateForBattery;
            device.Removal -= CurrentDev_Removal;

            if (device.SixAxis != null)
            {
                device.SixAxis.CalibrationStarted -= (s, e) => Device_CalibrationStarted(device, e);
                device.SixAxis.CalibrationStopped -= (s, e) => Device_CalibrationStopped(device, e);
            }
        }

        // ==================== 闪烁控制核心方法 ====================

        /// <summary>
        /// 启动托盘闪烁
        /// </summary>
        private void StartBlinking()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (blinkLock)
                {
                    if (blinkTimer != null && blinkTimeoutTimer != null)
                    {
                        isBlinking = true;
                        // 不再保存 batteryIcon，闪烁结束时直接从设置读取
                        IconSource = gyroIcon;                     // 设置为陀螺图标
                        blinkTimer.Stop();
                        blinkTimer.Start();                 // 启动闪烁定时器
                        blinkTimeoutTimer.Stop();
                        blinkTimeoutTimer.Start();          // 启动6秒超时
                        DS4Windows.AppLogger.LogToGui("托盘闪烁启动", false);
                    }
                }
            }));
        }

        /// <summary>
        /// 停止托盘闪烁
        /// </summary>
        private void StopBlinking()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (blinkLock)
                {
                    if (blinkTimer != null && blinkTimeoutTimer != null)
                    {
                        blinkTimer.Stop();
                        blinkTimeoutTimer.Stop();
                        isBlinking = false;
                        // 恢复为当前设置的图标（从设置中读取），确保与用户设置一致
                        IconSource = Global.iconChoiceResources[Global.UseIconChoice];
                        DS4Windows.AppLogger.LogToGui("托盘闪烁停止", false);
                    }
                }
            }));
        }

        /// <summary>
        /// 重置超时定时器（当还有设备在校准时调用）
        /// </summary>
        private void ResetTimeout()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (blinkLock)
                {
                    if (blinkTimeoutTimer != null)
                    {
                        blinkTimeoutTimer.Stop();
                        blinkTimeoutTimer.Start();
                        DS4Windows.AppLogger.LogToGui("重置超时定时器", false);
                    }
                }
            }));
        }

        // ==================== 事件处理方法 ====================

        /// <summary>
        /// 陀螺仪校准开始事件处理
        /// </summary>
        private void Device_CalibrationStarted(object sender, EventArgs e)
        {
            DS4Device dev = sender as DS4Device;
            if (dev == null)
            {
                DS4Windows.AppLogger.LogToGui("校准开始: sender为空，忽略", true);
                return;
            }

            lock (calibrationLock)
            {
                // 尝试将设备加入集合
                if (_calibratingDevices.Add(dev))
                {
                    DS4Windows.AppLogger.LogToGui($"校准开始: 设备={dev.MacAddress}, 当前校准设备数={_calibratingDevices.Count}", false);
                        
                    if (_calibratingDevices.Count == 1 && !isBlinking)
                    {
                        // 第一个设备开始校准，启动闪烁
                        StartBlinking();
                    }
                    else if (isBlinking)
                    {
                        // 已经有设备在校准，重置超时
                        ResetTimeout();
                    }
                }
                else
                {
                    DS4Windows.AppLogger.LogToGui($"校准开始: 设备={dev.MacAddress} 已在校准集合中，忽略", true);
                }
            }
        }

        /// <summary>
        /// 陀螺仪校准停止事件处理
        /// </summary>
        private void Device_CalibrationStopped(object sender, EventArgs e)
        {
            DS4Device dev = sender as DS4Device;
            if (dev == null)
            {
                DS4Windows.AppLogger.LogToGui("校准停止: sender为空，忽略", true);
                return;
            }

            lock (calibrationLock)
            {
                if (_calibratingDevices.Remove(dev))
                {
                    DS4Windows.AppLogger.LogToGui($"校准停止: 设备={dev.MacAddress}, 剩余校准设备数={_calibratingDevices.Count}", false);
                        
                    if (_calibratingDevices.Count == 0 && isBlinking)
                    {
                        // 所有设备都停止校准，停止闪烁
                        StopBlinking();
                    }
                    else if (_calibratingDevices.Count > 0 && isBlinking)
                    {
                        // 还有设备在校准，重置超时
                        ResetTimeout();
                    }
                }
                else
                {
                    DS4Windows.AppLogger.LogToGui($"校准停止: 设备={dev.MacAddress} 不在校准集合中，无法移除", false);
                }
            }
        }

        // ==================== 定时器 Tick 事件 ====================

        /// <summary>
        /// 闪烁定时器 Tick 处理 - 交替显示 gyro 图标和透明
        /// </summary>
        private void BlinkTimer_Tick(object sender, EventArgs e)
        {
            if (!isBlinking)
            {
                blinkTimer.Stop();
                // 检查当前图标是否为陀螺图标，若是则恢复（防御性）
                if (IconSource == gyroIcon)
                {
                    IconSource = Global.iconChoiceResources[Global.UseIconChoice];
                }
                return;
            }

            // 交替显示：当前为陀螺图标则设为 null（透明），否则设为陀螺图标
            IconSource = (IconSource == gyroIcon) ? null : gyroIcon;
        }

        /// <summary>
        /// 超时定时器 Tick 处理 - 6秒内未收到校准事件，强制停止闪烁
        /// </summary>
        private void BlinkTimeoutTimer_Tick(object sender, EventArgs e)
        {
            lock (blinkLock)
            {
                if (isBlinking)
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        blinkTimeoutTimer.Stop();
                        blinkTimer.Stop();
                        isBlinking = false;
                        // 强制停止时也从设置读取当前图标
                        IconSource = Global.iconChoiceResources[Global.UseIconChoice];
                    }));
                }
                else
                {
                    blinkTimeoutTimer.Stop();
                }
            }
        }

        private void CurrentDev_Removal(object sender, EventArgs e)
        {
            DS4Device currentDev = sender as DS4Device;
            ControllerHolder item = null;
            int idx = 0;

            lock (calibrationLock)
            {
                if (currentDev != null && _calibratingDevices.Contains(currentDev))
                {
                    _calibratingDevices.Remove(currentDev);
                    if (_calibratingDevices.Count == 0 && isBlinking)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            lock (blinkLock)
                            {
                                if (blinkTimer != null && blinkTimeoutTimer != null)
                                {
                                    blinkTimer.Stop();
                                    blinkTimeoutTimer.Stop();
                                    isBlinking = false;
                                    IconSource = Global.iconChoiceResources[Global.UseIconChoice];
                                }
                            }
                        }));
                    }
                    else if (_calibratingDevices.Count > 0 && isBlinking)
                    {
                        // 仍有设备，重置超时
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            lock (blinkLock)
                            {
                                blinkTimeoutTimer?.Stop();
                                blinkTimeoutTimer?.Start();
                            }
                        }));
                    }
                }
            }

            using (WriteLocker locker = new WriteLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    if (currentDev == holder.Device)
                    {
                        item = holder;
                        break;
                    }
                    idx++;
                }

                if (item != null)
                {
                    controllerList.RemoveAt(idx);
                    RemoveDeviceEvents(currentDev);
                }
            }

            PopulateToolText();
        }

        private void HookEvents(object sender, EventArgs e)
        {
            SetupEvents();
        }

        private void UpdateForBattery(object sender, EventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                PopulateToolText();
            });
        }

        private void ClearToolText(object sender, EventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                TooltipText = "DS4Windows";
            });
        }

        /// <summary>
        /// 添加静态菜单项（服务控制、打开、最小化、打开程序文件夹、退出）
        /// </summary>
        private void PopulateStaticItems()
        {
            ItemCollection items = contextMenu.Items;
            items.Add(changeServiceItem);
            items.Add(openItem);
            items.Add(minimizeItem);
            items.Add(openProgramItem);
            items.Add(new Separator());
            items.Add(closeItem);
        }

        public void ClearContextMenu()
        {
            contextMenu.Items.Clear();
            PopulateStaticItems();
            // 停止所有定时器
            blinkTimer?.Stop();
            blinkTimeoutTimer?.Stop();
        }

        /// <summary>
        /// 更新托盘图标为电池电量对应图标
        /// </summary>
        private void UpdateTrayBattery(object sender, byte percentage)
        {
            string newIcon = percentage switch
            {
                < 10 => $"{Global.RESOURCES_PREFIX}/0.ico",
                >= 10 and < 20 => $"{Global.RESOURCES_PREFIX}/10.ico",
                >= 20 and < 30 => $"{Global.RESOURCES_PREFIX}/20.ico",
                >= 30 and < 40 => $"{Global.RESOURCES_PREFIX}/30.ico",
                >= 40 and < 50 => $"{Global.RESOURCES_PREFIX}/40.ico",
                >= 50 and < 60 => $"{Global.RESOURCES_PREFIX}/50.ico",
                >= 60 and < 70 => $"{Global.RESOURCES_PREFIX}/60.ico",
                >= 70 and < 80 => $"{Global.RESOURCES_PREFIX}/70.ico",
                >= 80 and < 90 => $"{Global.RESOURCES_PREFIX}/80.ico",
                >= 90 and < 100 => $"{Global.RESOURCES_PREFIX}/90.ico",
                100 => $"{Global.RESOURCES_PREFIX}/100.ico",
                _ => $"{Global.RESOURCES_PREFIX}/DS4W.ico"
            };

            batteryIcon = newIcon;

            if (!isBlinking)
            {
                IconSource = newIcon;
            }
        }

        private void ExitMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            RequestShutdown?.Invoke(this, EventArgs.Empty);
        }
    }

    public class ControllerHolder
    {
        private DS4Device device;
        private int index;
        public DS4Device Device { get => device; }
        public int Index { get => index; }

        public ControllerHolder(DS4Device device, int index)
        {
            this.device = device;
            this.index = index;
        }
    }
}