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
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DS4Windows;
using WPFLocalizeExtension.Extensions;
using DS4WinWPF;

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
        private int calibrationCount = 0;          // 当前正在校准的设备数
        private bool isBlinking = false;            // 是否正在闪烁
        private string batteryIcon;                  // 当前应显示的电池图标（非闪烁时使用）
        private string gyroIcon;                     // 陀螺校准图标路径
        private readonly object calibrationLock = new object(); // 保护 calibrationCount 和 _calibratingDevices
        private readonly object blinkLock = new object();       // 保护闪烁状态
        private HashSet<DS4Device> _calibratingDevices = new HashSet<DS4Device>(); // 记录正在校准的设备

        public string TooltipText
        {
            get => tooltipText;
            set
            {
                try
                {
                    string temp = value;
                    if (value.Length > 63) temp = value.Substring(0, 63);
                    if (tooltipText == temp) return;
                    tooltipText = temp;
                    TooltipTextChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    App.LogToCrashFile(ex);
                }
            }
        }
        public event EventHandler TooltipTextChanged;

        public string IconSource
        {
            get => iconSource;
            set
            {
                try
                {
                    if (iconSource == value) return;
                    iconSource = value;
                    IconSourceChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    App.LogToCrashFile(ex);
                }
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
            try
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
                    try
                    {
                        blinkTimer = new System.Windows.Threading.DispatcherTimer();
                        blinkTimer.Interval = TimeSpan.FromMilliseconds(250); // 与陀螺仪闪烁频率一致
                        blinkTimer.Tick += BlinkTimer_Tick;

                        blinkTimeoutTimer = new System.Windows.Threading.DispatcherTimer();
                        blinkTimeoutTimer.Interval = TimeSpan.FromSeconds(6); // 超时6秒
                        blinkTimeoutTimer.Tick += BlinkTimeoutTimer_Tick;
                    }
                    catch (Exception ex)
                    {
                        App.LogToCrashFile(ex);
                    }
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
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private string GetLocalizedString(string key)
        {
            try
            {
                return LocExtension.GetLocalizedValue<string>(key);
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
                return key;
            }
        }

        private void Service_RunningChanged(object sender, EventArgs e)
        {
            try
            {
                string headerKey = controlService.running ? "ServiceStop" : "ServiceStart";
                App.Current.Dispatcher.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        changeServiceItem.Header = GetLocalizedString(headerKey);
                        changeServiceItem.IsEnabled = true;
                    }
                    catch (Exception ex)
                    {
                        App.LogToCrashFile(ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void ClearControllerList(object sender, EventArgs e)
        {
            try
            {
                _colLocker.EnterWriteLock();
                controllerList.Clear();
                _colLocker.ExitWriteLock();
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void UnhookEvents(object sender, EventArgs e)
        {
            try
            {
                _colLocker.EnterReadLock();
                foreach (ControllerHolder holder in controllerList)
                {
                    DS4Device currentDev = holder.Device;
                    RemoveDeviceEvents(currentDev);
                }
                _colLocker.ExitReadLock();
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void Service_HotplugController(ControlService sender, DS4Device device, int index)
        {
            try
            {
                SetupDeviceEvents(device);
                _colLocker.EnterWriteLock();
                controllerList.Add(new ControllerHolder(device, index));
                _colLocker.ExitWriteLock();
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void ProfileListCol_CollectionChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            try
            {
                PopulateContextMenu();
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void BuildControllerList(object sender, EventArgs e)
        {
            try
            {
                PopulateControllerList();
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        public void PopulateContextMenu()
        {
            try
            {
                contextMenu.Items.Clear();
                ItemCollection items = contextMenu.Items;
                MenuItem item;
                int idx = 0;

                using (ReadLocker locker = new ReadLocker(_colLocker))
                {
                    foreach (ControllerHolder holder in controllerList)
                    {
                        DS4Device currentDev = holder.Device;
                        item = new MenuItem() { Header = $"Controller {idx + 1}" };
                        item.Tag = idx;
                        ItemCollection subitems = item.Items;
                        string currentProfile = Global.ProfilePath[idx];
                        foreach (ProfileEntity entry in profileListHolder.ProfileListCol)
                        {
                            string name = entry.Name;
                            name = Regex.Replace(name, "_{1}", "__");
                            MenuItem temp = new MenuItem() { Header = name };
                            temp.Tag = idx;
                            temp.Click += ProfileItem_Click;
                            if (entry.Name == currentProfile)
                            {
                                temp.IsChecked = true;
                            }
                            subitems.Add(temp);
                        }
                        items.Add(item);
                        idx++;
                    }

                    item = new MenuItem() {  Header = GetLocalizedString("MenuDisconnect") };
                    idx = 0;
                    foreach (ControllerHolder holder in controllerList)
                    {
                        DS4Device tempDev = holder.Device;
                        if (tempDev.Synced && !tempDev.Charging)
                        {
                            MenuItem subitem = new MenuItem() { Header = $"Disconnect Controller {idx + 1}" };
                            subitem.Click += DisconnectMenuItem_Click;
                            subitem.Tag = idx;
                            item.Items.Add(subitem);
                        }
                        idx++;
                    }
                    if (idx == 0)
                    {
                        item.IsEnabled = false;
                    }
                }

                items.Add(item);
                items.Add(new Separator());
                PopulateStaticItems();
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void ChangeControlServiceItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                changeServiceItem.IsEnabled = false;
                RequestServiceChange?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void OpenProgramFolderItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(Global.exedirpath);
                startInfo.UseShellExecute = true;
                using (Process temp = Process.Start(startInfo)) { }
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void OpenMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                RequestOpen?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void MinimizeMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                RequestMinimize?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void ProfileItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                MenuItem item = sender as MenuItem;
                int idx = Convert.ToInt32(item.Tag);
                ControllerHolder holder = controllerList[idx];
                string tempProfileName = Regex.Replace(item.Header.ToString(), "_{2}", "_");
                ProfileSelected?.Invoke(this, holder, tempProfileName);
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void DisconnectMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
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
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void PopulateControllerList()
        {
            try
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
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void StartPopulateText(object sender, EventArgs e)
        {
            try
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        PopulateToolText();
                    }
                    catch (Exception ex)
                    {
                        App.LogToCrashFile(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void PopulateToolText()
        {
            try
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
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void SetupEvents()
        {
            try
            {
                _colLocker.EnterReadLock();
                foreach (ControllerHolder holder in controllerList)
                {
                    DS4Device currentDev = holder.Device;
                    SetupDeviceEvents(currentDev);
                }
                _colLocker.ExitReadLock();
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void SetupDeviceEvents(DS4Device device)
        {
            try
            {
                device.BatteryChanged += UpdateForBattery;
                device.ChargingChanged += UpdateForBattery;
                device.Removal += CurrentDev_Removal;

                if (device.SixAxis != null)
                {
                    device.SixAxis.CalibrationStarted += Device_CalibrationStarted;
                    device.SixAxis.CalibrationStopped += Device_CalibrationStopped;

                    // 检查设备是否已经在校准中（例如刚连接时自动校准）
                    if (device.SixAxis.CntCalibrating > 0)
                    {
                        Device_CalibrationStarted(device, EventArgs.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void RemoveDeviceEvents(DS4Device device)
        {
            try
            {
                device.BatteryChanged -= UpdateForBattery;
                device.ChargingChanged -= UpdateForBattery;
                device.Removal -= CurrentDev_Removal;

                if (device.SixAxis != null)
                {
                    device.SixAxis.CalibrationStarted -= Device_CalibrationStarted;
                    device.SixAxis.CalibrationStopped -= Device_CalibrationStopped;
                }
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        /// <summary>
        /// 陀螺仪校准开始事件处理
        /// </summary>
        private void Device_CalibrationStarted(object sender, EventArgs e)
        {
            try
            {
                DS4Device dev = sender as DS4Device;
                lock (calibrationLock)
                {
                    if (dev != null && !_calibratingDevices.Add(dev))
                        return;

                    calibrationCount++;
                    if (calibrationCount == 1 && !isBlinking)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                lock (blinkLock)
                                {
                                    if (blinkTimer != null && blinkTimeoutTimer != null)
                                    {
                                        isBlinking = true;
                                        batteryIcon = IconSource; // 保存当前图标
                                        IconSource = gyroIcon;    // 设置为陀螺图标
                                        blinkTimer.Stop();
                                        blinkTimer.Start();
                                        blinkTimeoutTimer.Stop();
                                        blinkTimeoutTimer.Start(); // 启动6秒超时
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                App.LogToCrashFile(ex);
                            }
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        /// <summary>
        /// 陀螺仪校准停止事件处理
        /// </summary>
        private void Device_CalibrationStopped(object sender, EventArgs e)
        {
            try
            {
                DS4Device dev = sender as DS4Device;
                lock (calibrationLock)
                {
                    if (dev != null)
                    {
                        _calibratingDevices.Remove(dev);
                    }

                    if (calibrationCount > 0) calibrationCount--;
                    if (calibrationCount == 0 && isBlinking)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                lock (blinkLock)
                                {
                                    if (blinkTimer != null && blinkTimeoutTimer != null)
                                    {
                                        blinkTimer.Stop();
                                        blinkTimeoutTimer.Stop();
                                        isBlinking = false;
                                        IconSource = batteryIcon ?? Global.iconChoiceResources[Global.UseIconChoice];
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                App.LogToCrashFile(ex);
                            }
                        }));
                    }
                    else if (isBlinking)
                    {
                        // 仍有设备在校准，重置超时定时器
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                lock (blinkLock)
                                {
                                    blinkTimeoutTimer?.Stop();
                                    blinkTimeoutTimer?.Start();
                                }
                            }
                            catch (Exception ex)
                            {
                                App.LogToCrashFile(ex);
                            }
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        /// <summary>
        /// 闪烁定时器 Tick 处理 - 交替显示 gyro 图标和透明
        /// </summary>
        private void BlinkTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (isBlinking)
                {
                    if (IconSource == gyroIcon)
                    {
                        IconSource = null; // 透明
                    }
                    else
                    {
                        IconSource = gyroIcon;
                    }
                }
                else
                {
                    blinkTimer.Stop();
                }
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        /// <summary>
        /// 超时定时器 Tick 处理 - 6秒内未收到校准事件，强制停止闪烁
        /// </summary>
        private void BlinkTimeoutTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                lock (blinkLock)
                {
                    if (isBlinking)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                blinkTimeoutTimer.Stop();
                                blinkTimer.Stop();
                                isBlinking = false;
                                IconSource = batteryIcon ?? Global.iconChoiceResources[Global.UseIconChoice];
                            }
                            catch (Exception ex)
                            {
                                App.LogToCrashFile(ex);
                            }
                        }));
                    }
                    else
                    {
                        blinkTimeoutTimer.Stop();
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void CurrentDev_Removal(object sender, EventArgs e)
        {
            try
            {
                DS4Device currentDev = sender as DS4Device;
                ControllerHolder item = null;
                int idx = 0;

                lock (calibrationLock)
                {
                    if (currentDev != null && _calibratingDevices.Contains(currentDev))
                    {
                        _calibratingDevices.Remove(currentDev);
                        if (calibrationCount > 0) calibrationCount--;
                        if (calibrationCount == 0 && isBlinking)
                        {
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    lock (blinkLock)
                                    {
                                        if (blinkTimer != null && blinkTimeoutTimer != null)
                                        {
                                            blinkTimer.Stop();
                                            blinkTimeoutTimer.Stop();
                                            isBlinking = false;
                                            IconSource = batteryIcon ?? Global.iconChoiceResources[Global.UseIconChoice];
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    App.LogToCrashFile(ex);
                                }
                            }));
                        }
                        else if (isBlinking)
                        {
                            // 仍有设备，重置超时
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    lock (blinkLock)
                                    {
                                        blinkTimeoutTimer?.Stop();
                                        blinkTimeoutTimer?.Start();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    App.LogToCrashFile(ex);
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
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void HookEvents(object sender, EventArgs e)
        {
            try
            {
                SetupEvents();
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void UpdateForBattery(object sender, EventArgs e)
        {
            try
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        PopulateToolText();
                    }
                    catch (Exception ex)
                    {
                        App.LogToCrashFile(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void ClearToolText(object sender, EventArgs e)
        {
            try
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        TooltipText = "DS4Windows";
                    }
                    catch (Exception ex)
                    {
                        App.LogToCrashFile(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void PopulateStaticItems()
        {
            try
            {
                ItemCollection items = contextMenu.Items;
                items.Add(changeServiceItem);
                items.Add(openItem);
                items.Add(minimizeItem);
                items.Add(openProgramItem);
                items.Add(new Separator());
                items.Add(closeItem);
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        public void ClearContextMenu()
        {
            try
            {
                contextMenu.Items.Clear();
                PopulateStaticItems();
                // 停止所有定时器
                blinkTimer?.Stop();
                blinkTimeoutTimer?.Stop();
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        /// <summary>
        /// 更新托盘图标为电池电量对应图标
        /// </summary>
        private void UpdateTrayBattery(object sender, byte percentage)
        {
            try
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
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }

        private void ExitMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                RequestShutdown?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
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
            try
            {
                this.device = device;
                this.index = index;
            }
            catch (Exception ex)
            {
                App.LogToCrashFile(ex);
            }
        }
    }
}