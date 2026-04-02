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
using System.Linq;
using DS4WinWPF.DS4Forms; // 添加此引用以使用 GyroCalibrationBlinker

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

        // 闪烁相关字段 - 使用 GyroCalibrationBlinker
        private GyroCalibrationBlinker _blinker; // 陀螺校准管理器
        private string gyroIcon;                     // 陀螺校准图标路径
        private readonly object calibrationLock = new object(); // 保护 _calibratingDevices
        private HashSet<DS4Device> _calibratingDevices = new HashSet<DS4Device>(); // 记录正在校准的设备（用于闪烁）

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
            
            // 将“打开程序文件夹”放在最上面
            // items.Add(openProgramItem);
            
            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                foreach (ControllerHolder holder in controllerList)
                {
                    DS4Device currentDev = holder.Device;
                    string macAddress = currentDev.MacAddress; // 唯一标识
                    
                    MenuItem controllerItem = new MenuItem() 
                    { 
                        Header = GetLocalizedString("Controllers") + " " + (holder.Index + 1)
                    };
                    controllerItem.Tag = macAddress; // 存储 MAC 地址
                    ItemCollection subitems = controllerItem.Items;

                    // 配置文件子菜单
                    string currentProfile = Global.ProfilePath[holder.Index];
                    foreach (ProfileEntity entry in profileListHolder.ProfileListCol)
                    {
                        string name = entry.Name;
                        name = Regex.Replace(name, "_{1}", "__");
                        MenuItem profileItem = new MenuItem() { Header = name };
                        profileItem.Tag = macAddress; // 存储 MAC 地址
                        profileItem.Click += ProfileItem_Click;
                        if (entry.Name == currentProfile)
                        {
                            profileItem.IsChecked = true;
                        }
                        subitems.Add(profileItem);
                    }

                    if (profileListHolder.ProfileListCol.Count > 0)
                    {
                        subitems.Add(new Separator());
                    }

                    // 断开连接菜单项
                    if (currentDev.CanDisconnect)
                    {
                        MenuItem disconnectItem = new MenuItem() 
                        { 
                            Header = GetLocalizedString("Disconnect")
                        };
                        disconnectItem.Click += DisconnectMenuItem_Click;
                        disconnectItem.Tag = macAddress;
                        subitems.Add(disconnectItem);
                    }

                    // 陀螺仪校准菜单项
                    if (currentDev?.SixAxis != null)
                    {
                        MenuItem gyroItem = new MenuItem() 
                        { 
                            Header = GetLocalizedString("GyroCalibration")
                        };
                        gyroItem.Click += CalibrateGyroMenuItem_Click;
                        gyroItem.Tag = macAddress;
                        subitems.Add(gyroItem);
                    }

                    items.Add(controllerItem);
                }
                
                // 仅当有手柄时才添加分隔符
                if (controllerList.Count > 0)
                {
                    items.Add(new Separator());
                }
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
            string macAddress = item.Tag as string;
            if (string.IsNullOrEmpty(macAddress))
                return;

            ControllerHolder holder = null;
            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                holder = controllerList.FirstOrDefault(h => h.Device.MacAddress == macAddress);
            }

            if (holder == null) return;

            int idx = holder.Index;
            string tempProfileName = Regex.Replace(item.Header.ToString(), "_{2}", "_");
            ProfileSelected?.Invoke(this, holder, tempProfileName);
        }

        private void DisconnectMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            string macAddress = item.Tag as string;
            if (string.IsNullOrEmpty(macAddress))
                return;

            ControllerHolder holder = null;
            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                holder = controllerList.FirstOrDefault(h => h.Device.MacAddress == macAddress);
            }

            if (holder == null) return;

            DS4Device tempDev = holder.Device;
            // 修改：使用 CanDisconnect 属性（无线且同步即可，不检查充电）
            if (tempDev != null && tempDev.CanDisconnect)
            {
                if (tempDev.ConnectionType == ConnectionType.BT)
                    tempDev.DisconnectBT();
                else if (tempDev.ConnectionType == ConnectionType.SONYWA)
                    tempDev.DisconnectDongle();
            }
        }

        /// <summary>
        /// 陀螺仪校准菜单项点击事件
        /// </summary>
        private void CalibrateGyroMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            string macAddress = item.Tag as string;
            if (string.IsNullOrEmpty(macAddress))
                return;

            ControllerHolder holder = null;
            using (ReadLocker locker = new ReadLocker(_colLocker))
            {
                holder = controllerList.FirstOrDefault(h => h.Device.MacAddress == macAddress);
            }

            if (holder == null)
            {
                PopulateContextMenu();
                System.Diagnostics.Debug.WriteLine($"校准失败：未找到设备 {macAddress}");
                return;
            }

            DS4Device device = holder.Device;
            int idx = holder.Index;
            if (device != null)
            {
                string message = string.Format(Strings.GyroCalibrationStarted, idx + 1);
                AppLogger.LogToTray(message, false);

                // 使用 ForceResetContinuousCalibration 强制重新校准
                device.SixAxis.ForceResetContinuousCalibration();
                if (device.JointDeviceSlotNumber != DS4Device.DEFAULT_JOINT_SLOT_NUMBER)
                {
                    DS4Device tempDev = controlService.DS4Controllers[device.JointDeviceSlotNumber];
                    tempDev?.SixAxis.ForceResetContinuousCalibration();
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
				Application.Current.Dispatcher.Invoke(() =>
				{
					var blinker = new GyroCalibrationBlinker(device,
						onBlinkUpdate: (visible) =>
						{
							// 交替闪烁：visible 为 true 时显示陀螺图标，false 时透明（null）
							if (visible)
								IconSource = gyroIcon;
							else
								IconSource = null;
						},
						onStopped: () =>
						{
							// 校准完全停止，恢复为设置中选定的图标
							IconSource = Global.iconChoiceResources[Global.UseIconChoice];
						}
					);
					lock (_deviceBlinkers)
					{
						_deviceBlinkers[device] = blinker;
					}
				});
			}
		}

        private void RemoveDeviceEvents(DS4Device device)
        {
            device.BatteryChanged -= UpdateForBattery;
            device.ChargingChanged -= UpdateForBattery;
            device.Removal -= CurrentDev_Removal;

            if (device.SixAxis != null)
            {
                // 释放闪烁管理器资源
                lock (_deviceBlinkers)
                {
                    if (_deviceBlinkers.TryGetValue(device, out var blinker))
                    {
                        blinker.Dispose();
                        _deviceBlinkers.Remove(device);
                    }
                }
            }
        }

        // ==================== 闪烁控制核心方法 ====================

        // 存储每个设备的闪烁管理器
        private Dictionary<DS4Device, GyroCalibrationBlinker> _deviceBlinkers = new Dictionary<DS4Device, GyroCalibrationBlinker>();

        private void CurrentDev_Removal(object sender, EventArgs e)
        {
            DS4Device currentDev = sender as DS4Device;
            ControllerHolder item = null;
            int idx = 0;

            // 清理闪烁管理器
            if (currentDev != null)
            {
                lock (_deviceBlinkers)
                {
                    if (_deviceBlinkers.TryGetValue(currentDev, out var blinker))
                    {
                        blinker.Dispose();
                        _deviceBlinkers.Remove(currentDev);
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
            
            // 同步刷新托盘菜单，确保设备移除后菜单立即更新
            Application.Current.Dispatcher.Invoke(new Action(() =>
            {
                PopulateContextMenu();
            }));
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
            // items.Add(openProgramItem);
            items.Add(new Separator());
            items.Add(closeItem);
        }

        public void ClearContextMenu()
        {
            contextMenu.Items.Clear();
            PopulateContextMenu();
            // 停止所有闪烁管理器
            lock (_deviceBlinkers)
            {
                foreach (var blinker in _deviceBlinkers.Values)
                {
                    blinker.Dispose();
                }
                _deviceBlinkers.Clear();
            }
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

            if (!_deviceBlinkers.Any() || !_deviceBlinkers.Values.Any(b => true))
            {
                // 没有设备在校准时，直接更新图标
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