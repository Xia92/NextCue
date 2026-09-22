using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using SystemIcons = System.Drawing.SystemIcons;
using TextBox = System.Windows.Controls.TextBox;

namespace NextCue
{
    public partial class MainWindow : Window
    {
        private const double ExpandedDefaultWidth = 310;
        private const double ExpandedDefaultHeight = 700;
        private const double ExpandedMinWidth = 280;
        private const double ExpandedMaxWidth = 420;
        private const double ExpandedMinHeight = 500;
        private const double CollapsedDefaultWidth = 170;
        private const double CollapsedMinWidth = 145;
        private const double CollapsedMaxWidth = 260;
        private const double CollapsedMinHeight = 240;
        private const double CollapsedResizeBorder = 6;
        private const double DockMargin = 8;
        private const uint MonitorDefaultToNearest = 2;
        private const string StartupRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupRegistryValueName = "NextCue";
        private static readonly string ApplicationIconRelativePath = Path.Combine("Assets", "Icons", "NextCue.ico");

        private const string PlanningPromptTemplate = """
            你是我的任务拆解助手。

            我现在面对一个较大、较难启动的任务。请把它拆解成非常具体、可以立即执行的小步骤。

            请严格遵守以下要求：

            1. 只输出编号步骤，格式必须是：

            2. ...

            3. ...

            4. ...

            5. ...

            6. 不要写前言、总结、解释或额外建议，只输出步骤列表。

            7. 每一步只包含一个明确动作，不要把多个动作塞进同一步。

            8. 每一步最好可以在 5–20 分钟内完成；如果仍然太大，请继续拆细。

            9. 第一步必须是我现在立刻就能开始做的动作，最好 2 分钟内可以启动。

            10. 避免使用“研究一下”“思考一下”“完善一下”“处理一下”这类模糊表达。
                请明确告诉我：

            - 打开什么
            - 看哪里
            - 写什么
            - 记录什么
            - 比较什么
            - 得到什么具体结果

            7. 如果任务涉及论文、科研、阅读或写作，请尽量指出具体对象和具体产出。

            8. 不要一次规划太远。优先拆解当前阶段，通常控制在 5–15 步。

            9. 如果存在必须先完成的前置步骤，请按照真实执行顺序排列。

            我的当前任务是：

            {USER_TASK}
            """;

        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(176, 42, 55));
        private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(63, 119, 90));
        private static readonly Brush PrimaryTextBrush = new SolidColorBrush(Color.FromRgb(23, 23, 23));
        private static readonly Brush SecondaryTextBrush = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        private static readonly Brush MutedTextBrush = new SolidColorBrush(Color.FromRgb(156, 163, 175));
        private static readonly Brush CompletedTaskTextBrush = new SolidColorBrush(Color.FromRgb(184, 190, 199));
        private static readonly Brush CompletedTaskHandleBrush = new SolidColorBrush(Color.FromRgb(198, 203, 210));
        private static readonly Brush CompletedTaskIconBrush = new SolidColorBrush(Color.FromRgb(191, 197, 205));
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(49, 95, 140));
        private static readonly Brush BorderLineBrush = new SolidColorBrush(Color.FromRgb(223, 226, 229));
        private static readonly Brush WindowBackgroundBrush = new SolidColorBrush(Color.FromRgb(247, 247, 245));
        private static readonly Regex StepLineRegex = new(@"^\s*(?:(?<number>\d+)[\.\)、)]|(?<bullet>[-•]))\s*(?<text>.+?)\s*$", RegexOptions.Compiled);

        private readonly AppStateStore _stateStore = new();
        private readonly ShortcutService _shortcutService = new();
        private readonly DispatcherTimer _feedbackTimer;
        private readonly DispatcherTimer _executionFeedbackTimer;
        private readonly DispatcherTimer _stepAdvanceTimer;
        private readonly DispatcherTimer _completionTimer;
        private Forms.NotifyIcon? _notifyIcon;
        private Forms.ToolStripMenuItem? _trayCollapseToggleItem;
        private DrawingIcon? _notifyIconImage;
        private AppState _state = new();
        private Plan? _activePlan;
        private MainPage _currentPage = MainPage.Task;
        private bool _isCreatingNewPlan;
        private bool _isApplyingSettings;
        private bool _isApplyingWindowSize;
        private bool _isCompactDragCandidate;
        private bool _didDragCompactWindow;
        private bool _completionStartedFromCollapsed;
        private bool _isRealExitRequested;
        private DataDirectoryUnavailableException? _pendingDataDirectoryError;
        private Point _compactDragStart;

        public MainWindow()
        {
            InitializeComponent();

            _feedbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1800)
            };
            _feedbackTimer.Tick += FeedbackTimer_Tick;

            _executionFeedbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1800)
            };
            _executionFeedbackTimer.Tick += ExecutionFeedbackTimer_Tick;

            _stepAdvanceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _stepAdvanceTimer.Tick += StepAdvanceTimer_Tick;

            _completionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _completionTimer.Tick += CompletionTimer_Tick;

            try
            {
                _state = _stateStore.Load();
                EnsureCurrentPlanSelection();
            }
            catch (DataDirectoryUnavailableException ex)
            {
                _pendingDataDirectoryError = ex;
                _state = new AppState();
            }

            InitializeTrayIcon();

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyApplicationIcon();

            if (!ResolveStartupDataDirectoryIfNeeded())
            {
                _isRealExitRequested = true;
                Close();
                return;
            }

            EnsureCurrentPlanSelection();
            RenderDataLocationSection();
            Topmost = _state.Settings.AlwaysOnTop;
            TryConfigureStartupLaunch(_state.Settings.StartWithWindows);

            SyncSettingsControls();
            UpdateAlwaysOnTopVisualState();
            ShowPage(MainPage.Task);

            bool shouldStartCollapsed = _state.Settings.StartupCollapsed || _state.Settings.IsCollapsed;
            ApplyCollapsedState(shouldStartCollapsed, saveSetting: true, captureCurrentGeometry: false);

            if (!shouldStartCollapsed && (_activePlan is null || _isCreatingNewPlan))
            {
                Dispatcher.BeginInvoke(MoveFocusAwayFromTaskInput, DispatcherPriority.ContextIdle);
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            if (WindowState == WindowState.Minimized)
            {
                ShowInTaskbar = true;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isRealExitRequested && _state.Settings.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            DisposeTrayIcon();
            base.OnClosed(e);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);

            if (!IsLoaded || _isApplyingWindowSize)
            {
                return;
            }

            if (_state.Settings.IsCollapsed)
            {
                CaptureCompactGeometry();
            }
            else
            {
                CaptureExpandedGeometry();
            }

            SaveState();
        }

        private void TaskTab_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(MainPage.Task);
        }

        private void StatisticsTab_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(MainPage.Statistics);
        }

        private void SettingsTab_Click(object sender, RoutedEventArgs e)
        {
            ShowPage(MainPage.Settings);
        }

        private void ToggleAlwaysOnTop_Click(object sender, RoutedEventArgs e)
        {
            _state.Settings.AlwaysOnTop = !_state.Settings.AlwaysOnTop;
            Topmost = _state.Settings.AlwaysOnTop;
            SyncSettingsControls();
            UpdateAlwaysOnTopVisualState();
            SaveState();
        }

        private void CollapseSidebar_Click(object sender, RoutedEventArgs e)
        {
            ApplyCollapsedState(collapsed: true, saveSetting: true);
        }

        private void ExpandSidebar_Click(object sender, RoutedEventArgs e)
        {
            ApplyCollapsedState(collapsed: false, saveSetting: true);
        }

        private void CollapsedBrandIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ApplyCollapsedState(collapsed: false, saveSetting: true);
            e.Handled = true;
        }

        private void CollapsedHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_state.Settings.IsCollapsed || IsInsideInteractiveElement(e.OriginalSource as DependencyObject))
            {
                return;
            }

            _compactDragStart = e.GetPosition(this);
            _isCompactDragCandidate = true;
            _didDragCompactWindow = false;
            CollapsedHeader.CaptureMouse();
        }

        private void CollapsedHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isCompactDragCandidate || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point currentPosition = e.GetPosition(this);

            if (Math.Abs(currentPosition.X - _compactDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(currentPosition.Y - _compactDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _isCompactDragCandidate = false;
            _didDragCompactWindow = true;
            CollapsedHeader.ReleaseMouseCapture();

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            ClampWindowToCurrentWorkArea();
            CaptureCompactGeometry();
            SaveState();
        }

        private void CollapsedHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_state.Settings.IsCollapsed || IsInsideInteractiveElement(e.OriginalSource as DependencyObject))
            {
                ResetCompactHeaderDragState();
                return;
            }

            bool shouldExpand = _isCompactDragCandidate && !_didDragCompactWindow;
            ResetCompactHeaderDragState();

            if (shouldExpand)
            {
                ApplyCollapsedState(collapsed: false, saveSetting: true);
            }
        }

        private void CopyPlanningPrompt_Click(object sender, RoutedEventArgs e)
        {
            string taskText = TaskTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(taskText))
            {
                ShowFeedback("请先输入任务", ErrorBrush);
                return;
            }

            try
            {
                Clipboard.SetText(BuildPlanningPrompt(taskText));
                ShowFeedback("已复制", SuccessBrush);
            }
            catch
            {
                ShowFeedback("复制失败", ErrorBrush);
            }
        }

        private void OpenChatGpt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://chatgpt.com/",
                    UseShellExecute = true
                });
            }
            catch
            {
                ShowFeedback("无法打开浏览器", ErrorBrush);
            }
        }

        private void OpenPlanLibrary_Click(object sender, RoutedEventArgs e)
        {
            ShowTaskLibraryDialog();
        }

        private void OpenTaskHistory_Click(object sender, RoutedEventArgs e)
        {
            ShowTaskHistoryDialog();
        }

        private void ReturnToCurrentPlan_Click(object sender, RoutedEventArgs e)
        {
            if (EnsureCurrentPlanSelection() is null)
            {
                return;
            }

            _isCreatingNewPlan = false;
            ImportPanel.Visibility = Visibility.Collapsed;
            ShowPage(MainPage.Task);
        }

        private void InitializeTrayIcon()
        {
            if (_notifyIcon is not null)
            {
                return;
            }

            _notifyIconImage = LoadNotifyIcon();
            _trayCollapseToggleItem = new Forms.ToolStripMenuItem("收起", null, (_, _) => Dispatcher.Invoke(ToggleCollapsedFromTray));

            Forms.ContextMenuStrip menu = new();
            menu.Items.Add(new Forms.ToolStripMenuItem("打开 NextCue", null, (_, _) => Dispatcher.Invoke(ShowMainWindowFromTray)));
            menu.Items.Add(_trayCollapseToggleItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => Dispatcher.Invoke(ExitFromTray)));

            _notifyIcon = new Forms.NotifyIcon
            {
                Text = "NextCue",
                Icon = _notifyIconImage ?? SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true
            };
            _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindowFromTray);
            UpdateTrayMenuState();
        }

        private void ShowMainWindowFromTray()
        {
            ShowInTaskbar = true;

            if (!IsVisible)
            {
                Show();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            ApplyCollapsedState(_state.Settings.IsCollapsed, saveSetting: false);

            Activate();
        }

        private void ToggleCollapsedFromTray()
        {
            ShowInTaskbar = true;

            if (!IsVisible)
            {
                Show();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            ApplyCollapsedState(!_state.Settings.IsCollapsed, saveSetting: true);
            Activate();
        }

        private void HideToTray()
        {
            if (_isRealExitRequested)
            {
                return;
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            if (_state.Settings.IsCollapsed)
            {
                CaptureCompactGeometry();
            }
            else
            {
                CaptureExpandedGeometry();
            }

            ShowInTaskbar = false;
            Hide();
        }

        private void ExitFromTray()
        {
            _isRealExitRequested = true;
            Close();
        }

        private void DisposeTrayIcon()
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            _notifyIconImage?.Dispose();
            _notifyIconImage = null;
        }

        private void UpdateTrayMenuState()
        {
            if (_trayCollapseToggleItem is not null)
            {
                _trayCollapseToggleItem.Text = _state.Settings.IsCollapsed ? "展开" : "收起";
            }
        }

        private void ApplyApplicationIcon()
        {
            ImageSource? iconSource = LoadApplicationIconImageSource();

            if (iconSource is null)
            {
                return;
            }

            Icon = iconSource;
            CollapsedBrandIcon.Source = iconSource;
        }

        private static ImageSource? LoadApplicationIconImageSource()
        {
            string iconPath = ResolveApplicationIconPath();

            if (!File.Exists(iconPath))
            {
                return null;
            }

            try
            {
                IconBitmapDecoder decoder = new(
                    new Uri(iconPath, UriKind.Absolute),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                BitmapFrame? frame = decoder.Frames
                    .OrderBy(candidate => Math.Abs(candidate.PixelWidth - 32))
                    .ThenBy(candidate => Math.Abs(candidate.PixelHeight - 32))
                    .FirstOrDefault();

                frame?.Freeze();
                return frame;
            }
            catch
            {
                // Icon loading is visual polish; NextCue should still run if it fails.
                return null;
            }
        }

        private static DrawingIcon? LoadNotifyIcon()
        {
            string iconPath = ResolveApplicationIconPath();

            if (!File.Exists(iconPath))
            {
                return null;
            }

            try
            {
                return new DrawingIcon(iconPath);
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveApplicationIconPath()
        {
            string outputPath = Path.Combine(AppContext.BaseDirectory, ApplicationIconRelativePath);

            if (File.Exists(outputPath))
            {
                return outputPath;
            }

            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ApplicationIconRelativePath));
        }

        private static void TryConfigureStartupLaunch(bool isEnabled)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(StartupRegistryKeyPath);

                if (key is null)
                {
                    return;
                }

                if (isEnabled)
                {
                    string? executablePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;

                    if (!string.IsNullOrWhiteSpace(executablePath))
                    {
                        key.SetValue(StartupRegistryValueName, $"\"{executablePath}\"");
                    }
                }
                else
                {
                    key.DeleteValue(StartupRegistryValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
                // Startup registration is best-effort and must not prevent NextCue from running.
            }
        }

        private void OpenImportPanel_Click(object sender, RoutedEventArgs e)
        {
            ImportTextBox.Clear();
            ImportFeedbackTextBlock.Visibility = Visibility.Collapsed;
            ImportPanel.Visibility = Visibility.Visible;
            ImportTextBox.Focus();
        }

        private void CancelImport_Click(object sender, RoutedEventArgs e)
        {
            ImportPanel.Visibility = Visibility.Collapsed;
        }

        private void GenerateTaskList_Click(object sender, RoutedEventArgs e)
        {
            List<string> parsedSteps = ParseSteps(ImportTextBox.Text);

            if (parsedSteps.Count == 0)
            {
                ImportFeedbackTextBlock.Visibility = Visibility.Visible;
                return;
            }

            _completionTimer.Stop();
            DateTimeOffset now = DateTimeOffset.Now;
            Plan newPlan = new()
            {
                Id = Guid.NewGuid(),
                OverallTask = GetInputOverallTaskText(),
                CreatedAt = now,
                LastAccessedAt = now,
                Status = PlanStatus.Ongoing,
                Steps = parsedSteps.Select(stepText => new PlanStep
                {
                    Id = Guid.NewGuid(),
                    Text = stepText
                }).ToList()
            };

            _state.OngoingPlans.Add(newPlan);
            SelectCurrentPlan(newPlan.Id, saveImmediately: false);
            _isCreatingNewPlan = false;
            SaveState();

            ImportPanel.Visibility = Visibility.Collapsed;
            TaskTextBox.Clear();
            ShowPage(MainPage.Task);
        }

        private void CompleteCurrentStep_Click(object sender, RoutedEventArgs e)
        {
            if (_activePlan is null || _stepAdvanceTimer.IsEnabled)
            {
                return;
            }

            int currentIndex = GetCurrentStepIndex(_activePlan);

            if (currentIndex < 0)
            {
                return;
            }

            SetStepCompleted(_activePlan.Steps[currentIndex], true);
            SaveCurrentPlanChange();

            if (IsPlanFullyCompleted(_activePlan))
            {
                CompleteActivePlan();
                return;
            }

            ShowStepCompletionFeedback(currentIndex);
        }

        private void EditCurrentStep_Click(object sender, RoutedEventArgs e)
        {
            PlanStep? currentStep = GetCurrentStep();

            if (currentStep is null)
            {
                return;
            }

            EditStepText(currentStep);
        }

        private void StuckButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activePlan is null)
            {
                return;
            }

            int currentIndex = GetCurrentStepIndex(_activePlan);

            if (currentIndex >= 0)
            {
                ShowStuckDialog(currentIndex);
            }
        }

        private void PasteRefinedSteps_Click(object sender, RoutedEventArgs e)
        {
            if (_activePlan is null)
            {
                return;
            }

            int currentIndex = GetCurrentStepIndex(_activePlan);

            if (currentIndex < 0)
            {
                return;
            }

            List<string>? refinedSteps = ShowRefineCurrentStepDialog(currentIndex);

            if (refinedSteps is null)
            {
                return;
            }

            ReplaceCurrentStepWithRefinedSteps(currentIndex, refinedSteps);
        }

        private void ManageTaskAxis_Click(object sender, RoutedEventArgs e)
        {
            ShowTaskManagementDialog();
        }

        private void CopyTaskAxis_Click(object sender, RoutedEventArgs e)
        {
            if (_activePlan is null)
            {
                return;
            }

            try
            {
                Clipboard.SetText(BuildTaskAxisClipboardText(_activePlan));
                ShowExecutionFeedback("已复制任务轴", SuccessBrush, autoHide: true);
            }
            catch
            {
                ShowExecutionFeedback("复制失败", ErrorBrush, autoHide: true);
            }
        }

        private void EndCurrentPlan_Click(object sender, RoutedEventArgs e)
        {
            if (_activePlan is null)
            {
                return;
            }

            if (ShowEndPlanConfirmation())
            {
                EndActivePlan();
            }
        }

        private void AlwaysOnTopCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings)
            {
                return;
            }

            _state.Settings.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;
            Topmost = _state.Settings.AlwaysOnTop;
            UpdateAlwaysOnTopVisualState();
            SaveState();
        }

        private void StartupCollapsedCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings)
            {
                return;
            }

            _state.Settings.StartupCollapsed = StartupCollapsedCheckBox.IsChecked == true;
            SaveState();
        }

        private void MinimizeToTrayOnCloseCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings)
            {
                return;
            }

            _state.Settings.MinimizeToTrayOnClose = MinimizeToTrayOnCloseCheckBox.IsChecked == true;
            SaveState();
        }

        private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings)
            {
                return;
            }

            _state.Settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
            TryConfigureStartupLaunch(_state.Settings.StartWithWindows);
            SaveState();
        }

        private void CreateDesktopShortcut_Click(object sender, RoutedEventArgs e)
        {
            ShortcutOperationResult result = _shortcutService.CreateShortcut(ShortcutLocation.Desktop);

            if (result.Success)
            {
                RenderDesktopShortcutStatus();
                return;
            }

            DesktopShortcutStatusTextBlock.Text = result.Message;
            DesktopShortcutStatusTextBlock.Foreground = ErrorBrush;
            CreateDesktopShortcutButton.Visibility = Visibility.Visible;
        }

        private void RemoveDesktopShortcut_Click(object sender, RoutedEventArgs e)
        {
            ShortcutOperationResult result = _shortcutService.RemoveShortcut(ShortcutLocation.Desktop);

            if (result.Success)
            {
                RenderDesktopShortcutStatus();
                return;
            }

            DesktopShortcutStatusTextBlock.Text = result.Message;
            DesktopShortcutStatusTextBlock.Foreground = ErrorBrush;
        }

        private void ChangeDataLocation_Click(object sender, RoutedEventArgs e)
        {
            string? selectedDirectory = SelectDataDirectory(_stateStore.StateDirectory);

            if (string.IsNullOrWhiteSpace(selectedDirectory))
            {
                return;
            }

            _state.ActivePlan = null;
            EnsureCurrentPlanSelection();

            DataLocationChangeResult result = _stateStore.ChangeDataDirectory(selectedDirectory, _state);

            if (result.Success)
            {
                RenderDataLocationSection(result.Message, SuccessBrush);
                return;
            }

            RenderDataLocationSection(result.Message, ErrorBrush);
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_stateStore.IsUsingCustomDataDirectory && !Directory.Exists(_stateStore.StateDirectory))
                {
                    RenderDataLocationSection("无法访问当前数据文件夹。", ErrorBrush);
                    return;
                }

                Directory.CreateDirectory(_stateStore.StateDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = _stateStore.StateDirectory,
                    UseShellExecute = true
                });
            }
            catch
            {
                RenderDataLocationSection("无法打开数据文件夹。", ErrorBrush);
            }
        }

        private void RestoreDefaultDataLocation_Click(object sender, RoutedEventArgs e)
        {
            _state.ActivePlan = null;
            EnsureCurrentPlanSelection();

            DataLocationChangeResult result = _stateStore.ChangeDataDirectory(_stateStore.DefaultStateDirectory, _state);

            if (result.Success)
            {
                RenderDataLocationSection("已恢复默认数据存储位置。", SuccessBrush);
                return;
            }

            RenderDataLocationSection(result.Message, ErrorBrush);
        }

        private void DockSideRadioButton_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings)
            {
                return;
            }

            _state.Settings.DockSide = DockLeftRadioButton.IsChecked == true ? DockSide.Left : DockSide.Right;
            _state.Settings.CompactLeft = null;
            _state.Settings.CompactTop = null;
            UpdateCollapsedArrow();

            if (_state.Settings.IsCollapsed)
            {
                PlaceCompactWindow();
            }
            else
            {
                PlaceWindowAtDockEdge();
                CaptureExpandedGeometry();
            }

            SaveState();
        }

        private void TaskTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePlaceholderVisibility();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsLoaded || _isApplyingWindowSize)
            {
                return;
            }

            if (_state.Settings.IsCollapsed)
            {
                _isApplyingWindowSize = true;

                try
                {
                    ClampWindowToCurrentWorkArea();
                    CaptureCompactGeometry();
                }
                finally
                {
                    _isApplyingWindowSize = false;
                }

                SaveState();
                return;
            }

            CaptureExpandedGeometry();
            SaveState();
        }

        private void ShowPage(MainPage page)
        {
            _currentPage = page;

            TaskPage.Visibility = page == MainPage.Task ? Visibility.Visible : Visibility.Collapsed;
            StatisticsPage.Visibility = page == MainPage.Statistics ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = page == MainPage.Settings ? Visibility.Visible : Visibility.Collapsed;

            if (page == MainPage.Task)
            {
                RenderTaskPage();
            }
            else if (page == MainPage.Statistics)
            {
                RenderStatisticsPage();
            }
            else
            {
                SyncSettingsControls();
                RenderDesktopShortcutStatus();
                RenderDataLocationSection();
            }

            UpdateNavigationVisualState();
        }

        private void UpdateTaskInputNavigation()
        {
            OpenPlanLibraryFromInputButton.Visibility = Visibility.Visible;
            ReturnToCurrentPlanButton.Visibility = _isCreatingNewPlan && _activePlan is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateCurrentPlanSelector()
        {
            if (_activePlan is null)
            {
                CurrentPlanSelectorButton.Content = "任务库";
                return;
            }

            CurrentPlanSelectorButton.Content = new TextBlock
            {
                Text = $"{GetPlanDisplayName(_activePlan)}  ▾",
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = AccentBrush,
                FontWeight = FontWeights.SemiBold
            };
        }

        private void RenderTaskPage()
        {
            CompletionView.Visibility = Visibility.Collapsed;

            EnsureCurrentPlanSelection();

            if (_activePlan is null || _isCreatingNewPlan)
            {
                TaskInputView.Visibility = Visibility.Visible;
                ExecutionView.Visibility = Visibility.Collapsed;
                ImportPanel.Visibility = Visibility.Collapsed;
                UpdateTaskInputNavigation();
                UpdatePlaceholderVisibility();
                return;
            }

            TaskInputView.Visibility = Visibility.Collapsed;
            ExecutionView.Visibility = Visibility.Visible;
            RenderExecutionView();
        }

        private void RenderExecutionView()
        {
            if (_activePlan is null)
            {
                return;
            }

            ClearExecutionFeedback();
            UpdateCurrentPlanSelector();

            int completedCount = _activePlan.Steps.Count(step => step.IsCompleted);
            int currentIndex = GetCurrentStepIndex(_activePlan);

            ProgressTextBlock.Text = $"{completedCount} / {_activePlan.Steps.Count}";
            TaskProgressBar.Maximum = Math.Max(_activePlan.Steps.Count, 1);
            TaskProgressBar.Value = completedCount;

            if (currentIndex >= 0)
            {
                CurrentStepNumberTextBlock.Text = FormatStepNumber(currentIndex);
                CurrentStepNumberTextBlock.Foreground = AccentBrush;
                CurrentStepTextBlock.Text = _activePlan.Steps[currentIndex].Text;
                CurrentStepTextBlock.BeginAnimation(OpacityProperty, null);
                CurrentStepTextBlock.Opacity = 1;
                CompleteCurrentStepButton.Content = "完成";
                CompleteCurrentStepButton.IsEnabled = true;
                EditCurrentStepButton.IsEnabled = true;
                StuckButton.IsEnabled = true;
                PasteRefinedStepsButton.IsEnabled = true;
                CopyTaskAxisButton.IsEnabled = true;
                ManageTaskAxisButton.IsEnabled = true;
                EndCurrentPlanButton.IsEnabled = true;
            }
            else
            {
                CurrentStepNumberTextBlock.Text = "";
                CurrentStepTextBlock.Text = "";
                CompleteCurrentStepButton.Content = "完成";
                CompleteCurrentStepButton.IsEnabled = false;
                EditCurrentStepButton.IsEnabled = false;
                StuckButton.IsEnabled = false;
                PasteRefinedStepsButton.IsEnabled = false;
                CopyTaskAxisButton.IsEnabled = false;
                ManageTaskAxisButton.IsEnabled = false;
                EndCurrentPlanButton.IsEnabled = false;
            }

            RenderTaskAxis(currentIndex);
        }

        private void RenderTaskAxis(int currentIndex)
        {
            if (_activePlan is null)
            {
                return;
            }

            TaskAxisPanel.Children.Clear();

            for (int i = 0; i < _activePlan.Steps.Count; i++)
            {
                int stepIndex = i;
                PlanStep step = _activePlan.Steps[i];
                string marker = GetStepMarker(stepIndex, currentIndex);

                TextBlock rowText = new()
                {
                    Text = $"{marker} {FormatStepNumber(stepIndex)} {step.Text}",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = GetAxisRowBrush(stepIndex, currentIndex),
                    FontWeight = stepIndex == currentIndex ? FontWeights.SemiBold : FontWeights.Normal,
                    Margin = new Thickness(0, 0, 0, 8)
                };

                System.Windows.Automation.AutomationProperties.SetAutomationId(rowText, $"TaskAxisRow{stepIndex + 1}");
                rowText.ContextMenu = CreateStepContextMenu(stepIndex);
                TaskAxisPanel.Children.Add(rowText);
            }
        }

        private ContextMenu CreateStepContextMenu(int stepIndex)
        {
            ContextMenu menu = new();

            AddMenuItem(menu, "编辑", (_, _) => EditStepAt(stepIndex));
            AddMenuItem(menu, "删除", (_, _) => DeleteStep(stepIndex), _activePlan?.Steps.Count > 1);
            AddMenuItem(menu, "在前面插入", (_, _) => InsertStep(stepIndex));
            AddMenuItem(menu, "在后面插入", (_, _) => InsertStep(stepIndex + 1));
            AddMenuItem(menu, "上移", (_, _) => MoveStep(stepIndex, stepIndex - 1), stepIndex > 0);
            AddMenuItem(menu, "下移", (_, _) => MoveStep(stepIndex, stepIndex + 1), _activePlan is not null && stepIndex < _activePlan.Steps.Count - 1);

            if (_activePlan is not null)
            {
                AddMenuItem(
                    menu,
                    _activePlan.Steps[stepIndex].IsCompleted ? "标记为未完成" : "标记为完成",
                    (_, _) => ToggleStepCompleted(stepIndex));
            }

            return menu;
        }

        private static void AddMenuItem(ContextMenu menu, string header, RoutedEventHandler handler, bool isEnabled = true)
        {
            MenuItem item = new()
            {
                Header = header,
                IsEnabled = isEnabled
            };
            item.Click += handler;
            menu.Items.Add(item);
        }

        private void EditStepAt(int stepIndex)
        {
            if (_activePlan is null || stepIndex < 0 || stepIndex >= _activePlan.Steps.Count)
            {
                return;
            }

            EditStepText(_activePlan.Steps[stepIndex]);
        }

        private void EditStepText(PlanStep step)
        {
            string? newText = ShowStepTextDialog("编辑任务步骤", step.Text);

            if (newText is null)
            {
                return;
            }

            step.Text = newText;
            SaveCurrentPlanChange();
            RenderExecutionView();
        }

        private void InsertStep(int insertIndex)
        {
            if (_activePlan is null)
            {
                return;
            }

            string? newText = ShowStepTextDialog("新增任务步骤", "");

            if (newText is null)
            {
                return;
            }

            insertIndex = Math.Clamp(insertIndex, 0, _activePlan.Steps.Count);
            _activePlan.Steps.Insert(insertIndex, new PlanStep
            {
                Id = Guid.NewGuid(),
                Text = newText
            });

            SaveCurrentPlanChange();
            RenderExecutionView();
        }

        private void DeleteStep(int stepIndex)
        {
            if (_activePlan is null || _activePlan.Steps.Count <= 1 || stepIndex < 0 || stepIndex >= _activePlan.Steps.Count)
            {
                return;
            }

            _activePlan.Steps.RemoveAt(stepIndex);
            SaveAndRenderAfterPlanChange();
        }

        private void MoveStep(int fromIndex, int toIndex)
        {
            if (_activePlan is null || toIndex < 0 || toIndex >= _activePlan.Steps.Count)
            {
                return;
            }

            PlanStep step = _activePlan.Steps[fromIndex];
            _activePlan.Steps.RemoveAt(fromIndex);
            _activePlan.Steps.Insert(toIndex, step);

            SaveCurrentPlanChange();
            RenderExecutionView();
        }

        private void ToggleStepCompleted(int stepIndex)
        {
            if (_activePlan is null || stepIndex < 0 || stepIndex >= _activePlan.Steps.Count)
            {
                return;
            }

            PlanStep step = _activePlan.Steps[stepIndex];
            SetStepCompleted(step, !step.IsCompleted);
            SaveAndRenderAfterPlanChange();
        }

        private void SaveAndRenderAfterPlanChange()
        {
            if (_activePlan is null)
            {
                return;
            }

            if (IsPlanFullyCompleted(_activePlan))
            {
                CompleteActivePlan();
                return;
            }

            SaveCurrentPlanChange();
            RenderExecutionView();
        }

        private void ShowStepCompletionFeedback(int completedIndex)
        {
            _stepAdvanceTimer.Stop();

            if (_state.Settings.IsCollapsed)
            {
                CollapsedCurrentStepTextBlock.BeginAnimation(OpacityProperty, new DoubleAnimation(0.58, TimeSpan.FromMilliseconds(160)));
                CollapsedCompleteCurrentStepButton.Content = "✓";
                CollapsedCompleteCurrentStepButton.BorderBrush = SuccessBrush;
                CollapsedCompleteCurrentStepButton.Background = new SolidColorBrush(Color.FromArgb(22, 63, 119, 90));
                CollapsedCompleteCurrentStepButton.IsEnabled = false;
            }
            else
            {
                CurrentStepNumberTextBlock.Text = "✓";
                CurrentStepNumberTextBlock.Foreground = SuccessBrush;
                CurrentStepTextBlock.BeginAnimation(OpacityProperty, new DoubleAnimation(0.52, TimeSpan.FromMilliseconds(160)));
                CompleteCurrentStepButton.Content = "已完成";
                CompleteCurrentStepButton.IsEnabled = false;
                EditCurrentStepButton.IsEnabled = false;
                StuckButton.IsEnabled = false;
                PasteRefinedStepsButton.IsEnabled = false;
                CopyTaskAxisButton.IsEnabled = false;
                ManageTaskAxisButton.IsEnabled = false;
                RenderTaskAxis(completedIndex);
            }

            _stepAdvanceTimer.Start();
        }

        private void StepAdvanceTimer_Tick(object? sender, EventArgs e)
        {
            _stepAdvanceTimer.Stop();

            if (_activePlan is null)
            {
                return;
            }

            if (_state.Settings.IsCollapsed)
            {
                RenderCollapsedView();
                return;
            }

            if (_currentPage == MainPage.Task)
            {
                RenderExecutionView();
            }
            else if (_currentPage == MainPage.Statistics)
            {
                RenderStatisticsPage();
            }
        }

        private void CompleteActivePlan()
        {
            if (_activePlan is null)
            {
                return;
            }

            _stepAdvanceTimer.Stop();
            DateTimeOffset now = DateTimeOffset.Now;
            Plan completedPlan = _activePlan;

            foreach (PlanStep step in completedPlan.Steps.Where(step => step.IsCompleted && step.CompletedAt is null))
            {
                step.CompletedAt = now;
            }

            completedPlan.Status = PlanStatus.Completed;
            completedPlan.CompletedAt = now;
            completedPlan.EndedAt = null;

            RemoveOngoingPlan(completedPlan.Id);
            AddPlanToHistory(completedPlan);
            SelectMostRecentOngoingPlan(touchSelection: false);
            SaveState();

            ShowCompletionState();
        }

        private void EndActivePlan()
        {
            if (_activePlan is null)
            {
                return;
            }

            _stepAdvanceTimer.Stop();
            Plan endedPlan = _activePlan;
            endedPlan.Status = PlanStatus.Ended;
            endedPlan.EndedAt = DateTimeOffset.Now;
            endedPlan.CompletedAt = null;

            RemoveOngoingPlan(endedPlan.Id);
            AddPlanToHistory(endedPlan);
            SelectMostRecentOngoingPlan(touchSelection: false);
            SaveState();

            ResetToPlanningScreen(clearTaskInput: true);
        }

        private void AddPlanToHistory(Plan plan)
        {
            int existingIndex = _state.PlanHistory.FindIndex(historyPlan => historyPlan.Id == plan.Id);

            if (existingIndex >= 0)
            {
                _state.PlanHistory[existingIndex] = plan;
            }
            else
            {
                _state.PlanHistory.Add(plan);
            }
        }

        private void ShowCompletionState()
        {
            _completionTimer.Stop();
            _completionStartedFromCollapsed = _state.Settings.IsCollapsed;
            _currentPage = MainPage.Task;
            TaskPage.Visibility = Visibility.Visible;
            StatisticsPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            TaskInputView.Visibility = Visibility.Collapsed;
            ExecutionView.Visibility = Visibility.Collapsed;
            ImportPanel.Visibility = Visibility.Collapsed;
            CompletionView.Visibility = _completionStartedFromCollapsed ? Visibility.Collapsed : Visibility.Visible;

            if (_completionStartedFromCollapsed)
            {
                CollapsedNoActivePlanTextBlock.Visibility = Visibility.Collapsed;
                CollapsedPlanPanel.Visibility = Visibility.Collapsed;
                CollapsedCompletionPanel.Visibility = Visibility.Visible;
            }

            UpdateNavigationVisualState();
            _completionTimer.Start();
        }

        private void CompletionTimer_Tick(object? sender, EventArgs e)
        {
            _completionTimer.Stop();
            bool shouldExpandAfterCompactCompletion = _completionStartedFromCollapsed;
            _completionStartedFromCollapsed = false;

            if (shouldExpandAfterCompactCompletion)
            {
                ApplyCollapsedState(collapsed: false, saveSetting: true);
            }

            ResetToPlanningScreen(clearTaskInput: true);
        }

        private void ResetToPlanningScreen(bool clearTaskInput)
        {
            _completionTimer.Stop();
            _stepAdvanceTimer.Stop();
            _executionFeedbackTimer.Stop();

            if (clearTaskInput)
            {
                TaskTextBox.Clear();
            }

            TaskAxisPanel.Children.Clear();
            CurrentStepNumberTextBlock.Text = "";
            CurrentStepTextBlock.Text = "";
            ProgressTextBlock.Text = "";
            TaskProgressBar.Value = 0;

            EnsureCurrentPlanSelection();
            ShowPage(MainPage.Task);
            ClearExecutionFeedback();
            UpdatePlaceholderVisibility();

            if (_state.Settings.IsCollapsed)
            {
                RenderCollapsedView();
            }
            else
            {
                Dispatcher.BeginInvoke(MoveFocusAwayFromTaskInput, DispatcherPriority.ContextIdle);
            }
        }

        private void ShowTaskLibraryDialog()
        {
            EnsureCurrentPlanSelection();

            bool createNewRequested = false;
            Window dialog = CreateDialogShell("任务库", 340, 500, 300, 360);

            Grid root = new()
            {
                Margin = new Thickness(18)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock titleText = new()
            {
                Text = "任务库",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = PrimaryTextBrush
            };
            Grid.SetRow(titleText, 0);
            root.Children.Add(titleText);

            StackPanel planListPanel = new()
            {
                Margin = new Thickness(0, 14, 0, 0)
            };

            ScrollViewer scrollViewer = new()
            {
                Content = planListPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(scrollViewer, 1);
            root.Children.Add(scrollViewer);

            Grid footer = new()
            {
                Margin = new Thickness(0, 14, 0, 0)
            };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Button newTaskButton = CreateDialogButton("+ 新建任务", false, 96);
            System.Windows.Automation.AutomationProperties.SetAutomationId(newTaskButton, "PlanLibraryNewTaskButton");
            newTaskButton.HorizontalAlignment = HorizontalAlignment.Left;
            newTaskButton.Click += (_, _) =>
            {
                createNewRequested = true;
                dialog.DialogResult = false;
            };
            Grid.SetColumn(newTaskButton, 0);
            footer.Children.Add(newTaskButton);

            Button closeButton = CreateDialogButton("关闭", true, 72);
            System.Windows.Automation.AutomationProperties.SetAutomationId(closeButton, "PlanLibraryCloseButton");
            closeButton.Click += (_, _) =>
            {
                dialog.DialogResult = false;
            };
            Grid.SetColumn(closeButton, 1);
            footer.Children.Add(closeButton);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            dialog.Content = root;
            RenderPlanLibraryRows();
            dialog.ShowDialog();

            if (createNewRequested)
            {
                BeginNewPlanCreation();
            }

            void RenderPlanLibraryRows()
            {
                planListPanel.Children.Clear();
                List<Plan> ongoingPlans = GetOrderedOngoingPlans().ToList();

                if (ongoingPlans.Count == 0)
                {
                    planListPanel.Children.Add(new TextBlock
                    {
                        Text = "暂无进行中的任务",
                        Foreground = SecondaryTextBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 8)
                    });
                    return;
                }

                foreach (Plan plan in ongoingPlans)
                {
                    bool isCurrent = _state.CurrentPlanId == plan.Id;
                    Button planButton = CreatePlanLibraryRow(plan, isCurrent);
                    planButton.Click += (_, _) =>
                    {
                        SelectCurrentPlan(plan.Id, saveImmediately: true);
                        dialog.DialogResult = true;
                        ShowPage(MainPage.Task);
                    };
                    planListPanel.Children.Add(planButton);
                }
            }
        }

        private void ShowTaskHistoryDialog()
        {
            Window dialog = CreateDialogShell("任务历史", 380, 600, 320, 420);

            Grid root = new()
            {
                Margin = new Thickness(18)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock titleText = new()
            {
                Text = "任务历史",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = PrimaryTextBrush
            };
            Grid.SetRow(titleText, 0);
            root.Children.Add(titleText);

            StackPanel historyListPanel = new()
            {
                Margin = new Thickness(0, 14, 0, 0)
            };

            ScrollViewer scrollViewer = new()
            {
                Content = historyListPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(scrollViewer, 1);
            root.Children.Add(scrollViewer);

            StackPanel footer = CreateDialogButtonRow();
            Button closeButton = CreateDialogButton("关闭", true, 72);
            System.Windows.Automation.AutomationProperties.SetAutomationId(closeButton, "TaskHistoryCloseButton");
            closeButton.Click += (_, _) =>
            {
                dialog.DialogResult = false;
            };
            footer.Children.Add(closeButton);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            dialog.Content = root;
            RenderHistoryRows();
            dialog.ShowDialog();

            void RenderHistoryRows()
            {
                historyListPanel.Children.Clear();

                List<Plan> historyPlans = _state.PlanHistory
                    .OrderByDescending(plan => plan.CompletedAt ?? plan.EndedAt ?? plan.LastAccessedAt)
                    .ThenByDescending(plan => plan.CreatedAt)
                    .ToList();

                if (historyPlans.Count == 0)
                {
                    historyListPanel.Children.Add(new TextBlock
                    {
                        Text = "暂无历史任务",
                        Foreground = SecondaryTextBrush,
                        TextWrapping = TextWrapping.Wrap
                    });
                    return;
                }

                foreach (Plan plan in historyPlans)
                {
                    Button row = CreateHistoryPlanRow(plan);
                    row.Click += (_, _) =>
                    {
                        bool deleted = ShowHistoryPlanDetailsDialog(plan);

                        if (deleted)
                        {
                            RenderHistoryRows();
                        }
                    };
                    historyListPanel.Children.Add(row);
                }
            }
        }

        private bool ShowHistoryPlanDetailsDialog(Plan plan)
        {
            bool deleted = false;
            Window dialog = CreateDialogShell("历史任务详情", 380, 620, 320, 420);

            Grid root = new()
            {
                Margin = new Thickness(18)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock titleText = new()
            {
                Text = "历史任务详情",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = PrimaryTextBrush
            };
            Grid.SetRow(titleText, 0);
            root.Children.Add(titleText);

            StackPanel detailPanel = new()
            {
                Margin = new Thickness(0, 14, 0, 0)
            };

            AddHistoryDetailField(detailPanel, "任务名称", GetPlanDisplayName(plan));
            AddHistoryDetailField(detailPanel, "状态", FormatHistoryStatus(plan));
            AddHistoryDetailField(detailPanel, "开始时间", FormatHistoryDate(plan.CreatedAt));

            if (plan.Status == PlanStatus.Completed)
            {
                AddHistoryDetailField(detailPanel, "完成时间", FormatHistoryDate(plan.CompletedAt));
            }
            else
            {
                AddHistoryDetailField(detailPanel, "结束时间", FormatHistoryDate(plan.EndedAt));
            }

            detailPanel.Children.Add(new TextBlock
            {
                Text = "任务步骤",
                Margin = new Thickness(0, 12, 0, 8),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = SecondaryTextBrush
            });

            if (plan.Steps.Count == 0)
            {
                detailPanel.Children.Add(new TextBlock
                {
                    Text = "暂无任务步骤",
                    Foreground = SecondaryTextBrush,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else
            {
                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    PlanStep step = plan.Steps[i];
                    detailPanel.Children.Add(new TextBlock
                    {
                        Text = $"{(step.IsCompleted ? "✓" : "○")} {FormatStepNumber(i)} {step.Text}",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = step.IsCompleted ? SuccessBrush : SecondaryTextBrush,
                        Margin = new Thickness(0, 0, 0, 8)
                    });
                }
            }

            ScrollViewer scrollViewer = new()
            {
                Content = detailPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(scrollViewer, 1);
            root.Children.Add(scrollViewer);

            Grid footer = new()
            {
                Margin = new Thickness(0, 14, 0, 0)
            };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Button deleteButton = CreateDialogButton("删除记录", false, 86);
            deleteButton.Foreground = ErrorBrush;
            System.Windows.Automation.AutomationProperties.SetAutomationId(deleteButton, "DeleteHistoryPlanButton");
            deleteButton.HorizontalAlignment = HorizontalAlignment.Left;
            deleteButton.Click += (_, _) =>
            {
                bool confirmed = ShowConfirmationDialog(
                    "删除这条历史任务？",
                    "删除后将无法恢复，并会影响统计数据。",
                    "删除",
                    "ConfirmDeleteHistoryPlanButton",
                    "CancelDeleteHistoryPlanButton");

                if (!confirmed)
                {
                    return;
                }

                _state.PlanHistory.RemoveAll(historyPlan => historyPlan.Id == plan.Id);
                SaveState();
                deleted = true;
                dialog.DialogResult = true;
            };
            Grid.SetColumn(deleteButton, 0);
            footer.Children.Add(deleteButton);

            Button closeButton = CreateDialogButton("关闭", true, 72);
            System.Windows.Automation.AutomationProperties.SetAutomationId(closeButton, "HistoryDetailCloseButton");
            closeButton.Click += (_, _) =>
            {
                dialog.DialogResult = false;
            };
            Grid.SetColumn(closeButton, 1);
            footer.Children.Add(closeButton);

            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            dialog.Content = root;
            dialog.ShowDialog();
            return deleted;
        }

        private void ShowTaskManagementDialog()
        {
            if (_activePlan is null)
            {
                return;
            }

            ObservableCollection<StepDraft> drafts = new(_activePlan.Steps.Select(StepDraft.FromStep));
            Window dialog = CreateDialogShell("调整任务轴", 430, 620, 340, 420);
            const string DraftDragDataFormat = "NextCueStepDraftId";

            Grid root = new()
            {
                Margin = new Thickness(18),
                Focusable = true
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock titleText = new()
            {
                Text = "调整任务轴",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = PrimaryTextBrush
            };
            Grid.SetRow(titleText, 0);
            root.Children.Add(titleText);

            StackPanel rowsPanel = new()
            {
                Margin = new Thickness(0, 14, 0, 0),
                AllowDrop = true
            };

            ScrollViewer scrollViewer = new()
            {
                Content = rowsPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                AllowDrop = true
            };
            Grid.SetRow(scrollViewer, 1);
            root.Children.Add(scrollViewer);

            const double DragAutoScrollEdge = 48;
            const double DragAutoScrollMaxDelta = 12;
            double dragAutoScrollDelta = 0;
            Point lastDragPointInScrollViewer = new();
            int insertionIndex = -1;
            Border? activeInsertionLine = null;
            Dictionary<int, Border> draftInsertionLines = [];
            Dictionary<Guid, Border> draftRowsById = [];
            Dictionary<Guid, Action<int>> syncDraftRowIndexById = [];
            List<Border> insertionLineElements = [];
            List<(Border Row, int Index, Guid DraftId)> draftRowTargets = [];
            bool suppressDraftBringIntoView = false;
            bool isViewportRestorePending = false;
            Guid? activeDraggedDraftId = null;
            DispatcherTimer dragAutoScrollTimer = new()
            {
                Interval = TimeSpan.FromMilliseconds(45)
            };
            dragAutoScrollTimer.Tick += (_, _) =>
            {
                if (Math.Abs(dragAutoScrollDelta) < 0.1)
                {
                    StopDragAutoScroll();
                    return;
                }

                double currentOffset = scrollViewer.VerticalOffset;
                double nextOffset = ClampToRange(currentOffset + dragAutoScrollDelta, 0, scrollViewer.ScrollableHeight);

                if (Math.Abs(nextOffset - currentOffset) < 0.1)
                {
                    StopDragAutoScroll();
                    return;
                }

                scrollViewer.ScrollToVerticalOffset(nextOffset);
                UpdateInsertionTargetFromScrollViewerPoint(lastDragPointInScrollViewer);
            };

            TextBlock validationText = CreateDialogFeedbackText("", ErrorBrush);
            validationText.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(validationText, 2);
            root.Children.Add(validationText);

            scrollViewer.DragOver += (_, args) => HandleDraftDragOver(args);
            scrollViewer.Drop += (_, args) => HandleDraftDrop(args);
            scrollViewer.RequestBringIntoView += SuppressSortableBringIntoView;
            scrollViewer.DragLeave += (_, _) =>
            {
                StopDragAutoScroll();
                HideInsertionLine();
                insertionIndex = -1;
            };
            rowsPanel.DragOver += (_, args) => HandleDraftDragOver(args);
            rowsPanel.Drop += (_, args) => HandleDraftDrop(args);
            rowsPanel.RequestBringIntoView += SuppressSortableBringIntoView;

            Grid footer = new();
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Button addButton = CreateDialogButton("+ 添加步骤", false, 96);
            System.Windows.Automation.AutomationProperties.SetAutomationId(addButton, "TaskManagerAddStepButton");
            addButton.HorizontalAlignment = HorizontalAlignment.Left;
            addButton.Click += (_, _) =>
            {
                drafts.Add(new StepDraft());
                RenderDraftRows(drafts.Count - 1);
            };
            Grid.SetColumn(addButton, 0);
            footer.Children.Add(addButton);

            StackPanel actionButtons = CreateDialogButtonRow();
            actionButtons.Margin = new Thickness(0);

            Button cancelButton = CreateDialogButton("取消", false);
            System.Windows.Automation.AutomationProperties.SetAutomationId(cancelButton, "TaskManagerCancelButton");
            cancelButton.Click += (_, _) =>
            {
                dialog.DialogResult = false;
            };

            bool saveRequested = false;
            Button saveButton = CreateDialogButton("保存", true);
            System.Windows.Automation.AutomationProperties.SetAutomationId(saveButton, "TaskManagerSaveButton");
            saveButton.Click += (_, _) =>
            {
                if (drafts.Count == 0)
                {
                    validationText.Text = "请至少保留一个任务步骤。";
                    validationText.Visibility = Visibility.Visible;
                    return;
                }

                if (drafts.Any(draft => string.IsNullOrWhiteSpace(draft.Text)))
                {
                    validationText.Text = "请填写所有任务步骤。";
                    validationText.Visibility = Visibility.Visible;
                    return;
                }

                saveRequested = true;
                dialog.DialogResult = true;
            };

            actionButtons.Children.Add(cancelButton);
            actionButtons.Children.Add(saveButton);
            Grid.SetColumn(actionButtons, 1);
            footer.Children.Add(actionButtons);
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            dialog.Content = root;
            dialog.Closed += (_, _) => StopDragAutoScroll();
            RenderDraftRows();
            dialog.ShowDialog();

            if (saveRequested)
            {
                ApplyTaskManagementDrafts(drafts);
            }

            void HandleDraftDragOver(DragEventArgs args, Border? targetRow = null, int targetRowIndex = -1)
            {
                if (!args.Data.GetDataPresent(DraftDragDataFormat))
                {
                    return;
                }

                Point pointInScrollViewer = args.GetPosition(scrollViewer);
                UpdateDragAutoScroll(pointInScrollViewer);

                if (targetRow is not null && targetRowIndex >= 0)
                {
                    bool insertBefore = args.GetPosition(targetRow).Y < targetRow.ActualHeight / 2;
                    SetInsertionTarget(insertBefore ? targetRowIndex : targetRowIndex + 1);
                }
                else
                {
                    UpdateInsertionTargetFromScrollViewerPoint(pointInScrollViewer);
                }

                args.Effects = DragDropEffects.Move;
                args.Handled = true;
            }

            void HandleDraftDrop(DragEventArgs args)
            {
                StopDragAutoScroll();
                double savedOffset = scrollViewer.VerticalOffset;

                if (!TryGetDraggedDraftId(args, out Guid draggedId))
                {
                    HideInsertionLine();
                    insertionIndex = -1;
                    EndDraftReorderTransaction();
                    return;
                }

                suppressDraftBringIntoView = true;
                activeDraggedDraftId = draggedId;
                MoveKeyboardFocusToDialogRoot();
                UpdateInsertionTargetFromScrollViewerPoint(args.GetPosition(scrollViewer));
                ReorderDraft(draggedId, insertionIndex);
                HideInsertionLine();
                insertionIndex = -1;
                RestoreViewportAfterReorder(savedOffset);
                args.Handled = true;
            }

            void UpdateDragAutoScroll(Point pointInScrollViewer)
            {
                lastDragPointInScrollViewer = pointInScrollViewer;

                if (scrollViewer.ActualHeight <= 0 || scrollViewer.ScrollableHeight <= 0)
                {
                    StopDragAutoScroll();
                    return;
                }

                if (pointInScrollViewer.Y < DragAutoScrollEdge && scrollViewer.VerticalOffset > 0)
                {
                    double intensity = (DragAutoScrollEdge - Math.Max(pointInScrollViewer.Y, 0)) / DragAutoScrollEdge;
                    dragAutoScrollDelta = -Math.Max(3, intensity * DragAutoScrollMaxDelta);
                    dragAutoScrollTimer.Start();
                    return;
                }

                if (pointInScrollViewer.Y > scrollViewer.ActualHeight - DragAutoScrollEdge &&
                    scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight)
                {
                    double distanceFromBottom = Math.Max(scrollViewer.ActualHeight - pointInScrollViewer.Y, 0);
                    double intensity = (DragAutoScrollEdge - distanceFromBottom) / DragAutoScrollEdge;
                    dragAutoScrollDelta = Math.Max(3, intensity * DragAutoScrollMaxDelta);
                    dragAutoScrollTimer.Start();
                    return;
                }

                StopDragAutoScroll();
            }

            void StopDragAutoScroll()
            {
                dragAutoScrollDelta = 0;
                lastDragPointInScrollViewer = new Point();
                dragAutoScrollTimer.Stop();
            }

            void SuppressSortableBringIntoView(object sender, RequestBringIntoViewEventArgs args)
            {
                if (!suppressDraftBringIntoView)
                {
                    return;
                }

                args.Handled = true;
            }

            void MoveKeyboardFocusToDialogRoot()
            {
                Keyboard.ClearFocus();
                FocusManager.SetFocusedElement(dialog, root);
                root.Focus();
            }

            void UpdateInsertionTargetFromScrollViewerPoint(Point pointInScrollViewer)
            {
                Point pointInRowsPanel = scrollViewer.TranslatePoint(pointInScrollViewer, rowsPanel);
                SetInsertionTarget(CalculateInsertionIndex(pointInRowsPanel.Y));
            }

            int CalculateInsertionIndex(double pointerY)
            {
                foreach ((Border row, int index, Guid _) in draftRowTargets.OrderBy(target => target.Index))
                {
                    double rowTop = row.TranslatePoint(new Point(0, 0), rowsPanel).Y;
                    double rowMidpoint = rowTop + (row.ActualHeight / 2);

                    if (pointerY < rowMidpoint)
                    {
                        return index;
                    }
                }

                return drafts.Count;
            }

            void SetInsertionTarget(int targetIndex)
            {
                insertionIndex = Math.Clamp(targetIndex, 0, drafts.Count);

                if (draftInsertionLines.TryGetValue(insertionIndex, out Border? line))
                {
                    ShowInsertionLine(line);
                }
            }

            void RenderDraftRows(int? editIndexAfterRender = null)
            {
                rowsPanel.Children.Clear();
                draftInsertionLines.Clear();
                draftRowsById.Clear();
                syncDraftRowIndexById.Clear();
                insertionLineElements.Clear();
                draftRowTargets.Clear();
                activeInsertionLine = null;
                insertionIndex = -1;
                validationText.Visibility = Visibility.Collapsed;

                for (int i = 0; i < drafts.Count; i++)
                {
                    StepDraft draft = drafts[i];
                    int index = i;
                    Border topInsertionLine = CreateInsertionLine(index);

                    Border row = new()
                    {
                        Padding = new Thickness(10, 9, 8, 9),
                        Margin = new Thickness(0, 0, 0, 6),
                        Background = Brushes.White,
                        BorderBrush = BorderLineBrush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        AllowDrop = true,
                        Cursor = Cursors.Arrow
                    };
                    System.Windows.Automation.AutomationProperties.SetAutomationId(row, $"TaskManagerRow{index + 1}");

                    Point dragStart = new();
                    bool canStartDrag = false;

                    row.DragOver += (_, args) => HandleDraftDragOver(args, row, drafts.IndexOf(draft));
                    row.Drop += (_, args) => HandleDraftDrop(args);
                    row.RequestBringIntoView += SuppressSortableBringIntoView;

                    Grid rowGrid = new();
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

                    Border dragHandle = new()
                    {
                        Background = Brushes.Transparent,
                        Cursor = Cursors.SizeAll
                    };
                    System.Windows.Automation.AutomationProperties.SetAutomationId(dragHandle, $"TaskManagerDragHandle{index + 1}");

                    dragHandle.PreviewMouseLeftButtonDown += (_, args) =>
                    {
                        canStartDrag = true;
                        dragStart = args.GetPosition(dragHandle);
                        dragHandle.CaptureMouse();
                        args.Handled = true;
                    };

                    dragHandle.MouseMove += (_, args) =>
                    {
                        if (!canStartDrag || args.LeftButton != MouseButtonState.Pressed)
                        {
                            return;
                        }

                        Point currentPosition = args.GetPosition(dragHandle);

                        if (Math.Abs(currentPosition.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                            Math.Abs(currentPosition.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                        {
                            return;
                        }

                        row.Opacity = 0.55;
                        suppressDraftBringIntoView = true;
                        activeDraggedDraftId = draft.Id;
                        MoveKeyboardFocusToDialogRoot();
                        dragHandle.ReleaseMouseCapture();
                        DataObject data = new(DraftDragDataFormat, draft.Id.ToString());
                        DragDrop.DoDragDrop(dragHandle, data, DragDropEffects.Move);
                        row.Opacity = 1;
                        StopDragAutoScroll();
                        HideInsertionLine();
                        canStartDrag = false;

                        if (!isViewportRestorePending)
                        {
                            EndDraftReorderTransaction();
                        }
                    };

                    dragHandle.MouseLeftButtonUp += (_, _) =>
                    {
                        canStartDrag = false;
                        dragHandle.ReleaseMouseCapture();
                    };

                     TextBlock handleText = new()
                     {
                         Text = "⋮⋮",
                         Foreground = SecondaryTextBrush,
                         FontSize = 17,
                         VerticalAlignment = VerticalAlignment.Center,
                         HorizontalAlignment = HorizontalAlignment.Center,
                         Cursor = Cursors.SizeAll
                    };
                    dragHandle.Child = handleText;
                    Grid.SetColumn(dragHandle, 0);
                    rowGrid.Children.Add(dragHandle);

                     TextBlock numberText = new()
                     {
                         Text = FormatStepNumber(index),
                         FontWeight = FontWeights.SemiBold,
                         VerticalAlignment = VerticalAlignment.Center,
                         Cursor = Cursors.Arrow
                    };
                    Grid.SetColumn(numberText, 1);
                    rowGrid.Children.Add(numberText);

                    Grid textHost = new();
                    textHost.VerticalAlignment = VerticalAlignment.Center;
                    textHost.Cursor = Cursors.Arrow;

                     TextBlock textBlock = new()
                     {
                         Text = string.IsNullOrWhiteSpace(draft.Text) ? "双击输入任务步骤" : draft.Text,
                         TextWrapping = TextWrapping.Wrap,
                         VerticalAlignment = VerticalAlignment.Center,
                         Cursor = Cursors.Arrow
                    };
                    System.Windows.Automation.AutomationProperties.SetAutomationId(textBlock, $"TaskManagerStepText{index + 1}");

                    TextBox editBox = CreateInlineEditTextBox(draft.Text);
                    System.Windows.Automation.AutomationProperties.SetAutomationId(editBox, $"TaskManagerStepTextBox{index + 1}");
                    editBox.Visibility = Visibility.Collapsed;
                    string editOriginalText = draft.Text;

                    editBox.TextChanged += (_, _) =>
                    {
                        draft.Text = editBox.Text;
                    };

                    textHost.Children.Add(textBlock);
                    textHost.Children.Add(editBox);
                    Grid.SetColumn(textHost, 2);
                    rowGrid.Children.Add(textHost);

                     Button editButton = CreateIconDialogButton("✎", "编辑");
                     System.Windows.Automation.AutomationProperties.SetAutomationId(editButton, $"TaskManagerEditButton{index + 1}");
                     editButton.Click += (_, _) => StartInlineEdit();
                    Grid.SetColumn(editButton, 3);
                    rowGrid.Children.Add(editButton);

                     Button completionButton = CreateIconDialogButton(draft.IsCompleted ? "✓" : "○", draft.IsCompleted ? "标记为未完成" : "标记为完成");
                     System.Windows.Automation.AutomationProperties.SetAutomationId(completionButton, $"TaskManagerCompletedButton{index + 1}");
                     completionButton.FontSize = 16;
                     Grid.SetColumn(completionButton, 4);
                     rowGrid.Children.Add(completionButton);

                     Button deleteButton = CreateIconDialogButton("×", "删除");
                    System.Windows.Automation.AutomationProperties.SetAutomationId(deleteButton, $"TaskManagerDeleteButton{index + 1}");
                    deleteButton.Click += (_, _) =>
                    {
                        int deleteIndex = drafts.IndexOf(draft);

                        if (drafts.Count <= 1)
                        {
                            validationText.Text = "请至少保留一个任务步骤。";
                            validationText.Visibility = Visibility.Visible;
                            return;
                        }

                        if (deleteIndex < 0)
                        {
                            return;
                        }

                        drafts.RemoveAt(deleteIndex);
                        RenderDraftRows();
                    };
                    Grid.SetColumn(deleteButton, 5);
                    rowGrid.Children.Add(deleteButton);

                     completionButton.Click += (_, _) =>
                     {
                         draft.IsCompleted = !draft.IsCompleted;
                         draft.CompletedAt = draft.IsCompleted ? draft.CompletedAt ?? DateTimeOffset.Now : null;
                         ApplyDraftRowVisualState();
                     };

                     draftRowsById[draft.Id] = row;
                     syncDraftRowIndexById[draft.Id] = SyncDraftRowIndex;
                     ApplyDraftRowVisualState();

                    row.Child = rowGrid;
                    draftRowTargets.Add((row, index, draft.Id));
                    rowsPanel.Children.Add(topInsertionLine);
                    rowsPanel.Children.Add(row);

                    if (editIndexAfterRender == index)
                    {
                        Dispatcher.BeginInvoke(StartInlineEdit, DispatcherPriority.Background);
                    }

                    void StartInlineEdit()
                    {
                        editOriginalText = draft.Text;
                        row.Cursor = Cursors.Arrow;
                        textBlock.Visibility = Visibility.Collapsed;
                        editBox.Text = draft.Text;
                        editBox.Visibility = Visibility.Visible;
                        editBox.Focus();
                        editBox.SelectAll();
                    }

                    void CommitInlineEdit()
                    {
                        if (string.IsNullOrWhiteSpace(editBox.Text))
                        {
                            validationText.Text = "请填写所有任务步骤。";
                            validationText.Visibility = Visibility.Visible;
                            return;
                        }

                         draft.Text = editBox.Text.Trim();
                         textBlock.Text = draft.Text;
                         ApplyDraftRowVisualState();
                         editBox.Visibility = Visibility.Collapsed;
                         textBlock.Visibility = Visibility.Visible;
                         row.Cursor = Cursors.Arrow;
                         validationText.Visibility = Visibility.Collapsed;
                    }

                    void CancelInlineEdit()
                    {
                        draft.Text = editOriginalText;
                        editBox.Text = editOriginalText;
                        editBox.Visibility = Visibility.Collapsed;
                         textBlock.Visibility = Visibility.Visible;
                         row.Cursor = Cursors.Arrow;
                     }

                     void ApplyDraftRowVisualState()
                     {
                         bool hasText = !string.IsNullOrWhiteSpace(draft.Text);
                         Brush normalIconBrush = SecondaryTextBrush;

                         numberText.Foreground = draft.IsCompleted ? CompletedTaskTextBrush : AccentBrush;
                         textBlock.Foreground = !hasText
                             ? MutedTextBrush
                             : draft.IsCompleted
                                 ? CompletedTaskTextBrush
                                 : PrimaryTextBrush;
                         handleText.Foreground = draft.IsCompleted ? CompletedTaskHandleBrush : normalIconBrush;
                         editButton.Foreground = draft.IsCompleted ? CompletedTaskIconBrush : normalIconBrush;
                         deleteButton.Foreground = draft.IsCompleted ? CompletedTaskIconBrush : normalIconBrush;
                         completionButton.Content = draft.IsCompleted ? "✓" : "○";
                         completionButton.ToolTip = draft.IsCompleted ? "标记为未完成" : "标记为完成";
                         completionButton.Foreground = draft.IsCompleted ? SuccessBrush : normalIconBrush;
                     }

                    void SyncDraftRowIndex(int displayIndex)
                    {
                        System.Windows.Automation.AutomationProperties.SetAutomationId(row, $"TaskManagerRow{displayIndex + 1}");
                        System.Windows.Automation.AutomationProperties.SetAutomationId(dragHandle, $"TaskManagerDragHandle{displayIndex + 1}");
                        System.Windows.Automation.AutomationProperties.SetAutomationId(textBlock, $"TaskManagerStepText{displayIndex + 1}");
                        System.Windows.Automation.AutomationProperties.SetAutomationId(editBox, $"TaskManagerStepTextBox{displayIndex + 1}");
                        System.Windows.Automation.AutomationProperties.SetAutomationId(editButton, $"TaskManagerEditButton{displayIndex + 1}");
                        System.Windows.Automation.AutomationProperties.SetAutomationId(completionButton, $"TaskManagerCompletedButton{displayIndex + 1}");
                        System.Windows.Automation.AutomationProperties.SetAutomationId(deleteButton, $"TaskManagerDeleteButton{displayIndex + 1}");
                        numberText.Text = FormatStepNumber(displayIndex);
                    }

                     editBox.PreviewKeyDown += (_, args) =>
                     {
                         if (args.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
                         {
                             CommitInlineEdit();
                             args.Handled = true;
                         }
                         else if (args.Key == Key.Escape)
                         {
                             CancelInlineEdit();
                             args.Handled = true;
                         }
                     };

                     textBlock.MouseLeftButtonDown += (_, args) =>
                     {
                         if (args.ClickCount >= 2)
                         {
                             StartInlineEdit();
                             args.Handled = true;
                         }
                     };
                 }

                rowsPanel.Children.Add(CreateInsertionLine(drafts.Count));

                    Border CreateInsertionLine(int targetIndex)
                    {
                        Border line = new()
                    {
                        Tag = "InsertionLine",
                        Height = 2,
                        Margin = new Thickness(8, 0, 8, 6),
                        Background = AccentBrush,
                        CornerRadius = new CornerRadius(1),
                        Visibility = Visibility.Collapsed,
                        IsHitTestVisible = false
                        };
                        draftInsertionLines[targetIndex] = line;
                        insertionLineElements.Add(line);
                        return line;
                    }
                }

            void ShowInsertionLine(Border line)
            {
                if (activeInsertionLine == line)
                {
                    return;
                }

                HideInsertionLine();
                activeInsertionLine = line;
                activeInsertionLine.Visibility = Visibility.Visible;
            }

            void HideInsertionLine()
            {
                if (activeInsertionLine is null)
                {
                    return;
                }

                activeInsertionLine.Visibility = Visibility.Collapsed;
                activeInsertionLine = null;
            }

            bool TryGetDraggedDraftId(DragEventArgs args, out Guid draggedId)
            {
                draggedId = Guid.Empty;

                if (!args.Data.GetDataPresent(DraftDragDataFormat))
                {
                    return false;
                }

                string? value = args.Data.GetData(DraftDragDataFormat)?.ToString();
                return Guid.TryParse(value, out draggedId);
            }

            void ReorderDraft(Guid draggedId, int targetInsertionIndex)
            {
                StepDraft? draggedDraft = drafts.FirstOrDefault(draft => draft.Id == draggedId);

                if (draggedDraft is null || targetInsertionIndex < 0)
                {
                    return;
                }

                int oldIndex = drafts.IndexOf(draggedDraft);
                int newIndex = Math.Clamp(targetInsertionIndex, 0, drafts.Count);

                if (newIndex > oldIndex)
                {
                    newIndex--;
                }

                newIndex = Math.Clamp(newIndex, 0, drafts.Count - 1);

                if (newIndex == oldIndex)
                {
                    return;
                }

                drafts.Move(oldIndex, newIndex);
                ArrangeDraftRowsFromCollection();
            }

            void ArrangeDraftRowsFromCollection()
            {
                draftInsertionLines.Clear();
                draftRowTargets.Clear();

                int desiredChildIndex = 0;

                for (int i = 0; i < drafts.Count; i++)
                {
                    Border insertionLine = insertionLineElements[i];
                    MoveChildToIndex(insertionLine, desiredChildIndex++);
                    draftInsertionLines[i] = insertionLine;

                    if (draftRowsById.TryGetValue(drafts[i].Id, out Border? row))
                    {
                        MoveChildToIndex(row, desiredChildIndex++);
                        draftRowTargets.Add((row, i, drafts[i].Id));
                    }

                    if (syncDraftRowIndexById.TryGetValue(drafts[i].Id, out Action<int>? syncIndex))
                    {
                        syncIndex(i);
                    }
                }

                Border finalInsertionLine = insertionLineElements[drafts.Count];
                MoveChildToIndex(finalInsertionLine, desiredChildIndex++);
                draftInsertionLines[drafts.Count] = finalInsertionLine;

                while (rowsPanel.Children.Count > desiredChildIndex)
                {
                    rowsPanel.Children.RemoveAt(rowsPanel.Children.Count - 1);
                }
            }

            void MoveChildToIndex(UIElement element, int targetIndex)
            {
                if (targetIndex < rowsPanel.Children.Count && rowsPanel.Children[targetIndex] == element)
                {
                    return;
                }

                int currentIndex = rowsPanel.Children.IndexOf(element);

                if (currentIndex >= 0)
                {
                    rowsPanel.Children.RemoveAt(currentIndex);
                }

                rowsPanel.Children.Insert(Math.Min(targetIndex, rowsPanel.Children.Count), element);
            }

            void RestoreViewportAfterReorder(double verticalOffset)
            {
                isViewportRestorePending = true;

                scrollViewer.Dispatcher.BeginInvoke(() =>
                {
                    scrollViewer.UpdateLayout();
                    scrollViewer.ScrollToVerticalOffset(ClampToRange(
                        verticalOffset,
                        0,
                        scrollViewer.ScrollableHeight));
                }, DispatcherPriority.Loaded);

                scrollViewer.Dispatcher.BeginInvoke(() =>
                {
                    scrollViewer.UpdateLayout();
                    scrollViewer.ScrollToVerticalOffset(ClampToRange(
                        verticalOffset,
                        0,
                        scrollViewer.ScrollableHeight));
                    isViewportRestorePending = false;
                    EndDraftReorderTransaction();
                }, DispatcherPriority.ContextIdle);
            }

            void EndDraftReorderTransaction()
            {
                suppressDraftBringIntoView = false;
                activeDraggedDraftId = null;
                isViewportRestorePending = false;
            }
        }

        private void ApplyTaskManagementDrafts(IEnumerable<StepDraft> drafts)
        {
            if (_activePlan is null)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.Now;
            _activePlan.Steps = drafts.Select(draft => new PlanStep
            {
                Id = draft.Id == Guid.Empty ? Guid.NewGuid() : draft.Id,
                Text = draft.Text.Trim(),
                IsCompleted = draft.IsCompleted,
                CompletedAt = draft.IsCompleted ? draft.CompletedAt ?? now : null
            }).ToList();

            SaveAndRenderAfterPlanChange();
        }

        private List<string>? ShowRefineCurrentStepDialog(int currentIndex)
        {
            if (_activePlan is null || currentIndex < 0 || currentIndex >= _activePlan.Steps.Count)
            {
                return null;
            }

            Window dialog = CreateDialogShell("细化当前步骤", 360, 520, 320, 420);
            List<string>? refinedSteps = null;

            Grid root = new()
            {
                Margin = new Thickness(18)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock titleText = new()
            {
                Text = "细化当前步骤",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = PrimaryTextBrush
            };
            Grid.SetRow(titleText, 0);
            root.Children.Add(titleText);

            Border currentStepCard = new()
            {
                Margin = new Thickness(0, 14, 0, 14),
                Padding = new Thickness(12),
                Background = Brushes.White,
                BorderBrush = BorderLineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };

            StackPanel currentStepPanel = new();
            currentStepPanel.Children.Add(new TextBlock
            {
                Text = "当前步骤",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = SecondaryTextBrush
            });
            currentStepPanel.Children.Add(new TextBlock
            {
                Text = _activePlan.Steps[currentIndex].Text,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = PrimaryTextBrush,
                FontWeight = FontWeights.SemiBold
            });
            currentStepCard.Child = currentStepPanel;
            Grid.SetRow(currentStepCard, 1);
            root.Children.Add(currentStepCard);

            TextBlock editorLabel = new()
            {
                Text = "输入更小步骤（支持粘贴或手动输入）",
                FontSize = 12,
                Foreground = SecondaryTextBrush,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(editorLabel, 2);
            root.Children.Add(editorLabel);

            TextBox refineTextBox = CreateDialogTextBox("");
            refineTextBox.AcceptsReturn = true;
            refineTextBox.TextWrapping = TextWrapping.Wrap;
            System.Windows.Automation.AutomationProperties.SetAutomationId(refineTextBox, "RefineStepsTextBox");
            Grid.SetRow(refineTextBox, 3);
            root.Children.Add(refineTextBox);

            TextBlock dialogFeedback = CreateDialogFeedbackText("", ErrorBrush);
            dialogFeedback.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(dialogFeedback, 4);
            root.Children.Add(dialogFeedback);

            Grid footer = new()
            {
                Margin = new Thickness(0, 14, 0, 0)
            };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Button readClipboardButton = CreateDialogButton("从剪贴板读取", false, 108);
            System.Windows.Automation.AutomationProperties.SetAutomationId(readClipboardButton, "ReadRefineClipboardButton");
            readClipboardButton.HorizontalAlignment = HorizontalAlignment.Left;
            readClipboardButton.Click += (_, _) =>
            {
                string clipboardText;

                try
                {
                    clipboardText = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                }
                catch
                {
                    dialogFeedback.Text = "未识别到可用的任务步骤。";
                    dialogFeedback.Foreground = ErrorBrush;
                    dialogFeedback.Visibility = Visibility.Visible;
                    return;
                }

                List<string> clipboardSteps = ParseSteps(clipboardText);

                if (clipboardSteps.Count == 0)
                {
                    dialogFeedback.Text = "未识别到可用的任务步骤。";
                    dialogFeedback.Foreground = ErrorBrush;
                    dialogFeedback.Visibility = Visibility.Visible;
                    return;
                }

                refineTextBox.Text = FormatStepsForEditing(clipboardSteps);
                dialogFeedback.Text = "已读取剪贴板内容，可继续编辑。";
                dialogFeedback.Foreground = SuccessBrush;
                dialogFeedback.Visibility = Visibility.Visible;
                refineTextBox.Focus();
                refineTextBox.CaretIndex = refineTextBox.Text.Length;
            };
            Grid.SetColumn(readClipboardButton, 0);
            footer.Children.Add(readClipboardButton);

            StackPanel actionButtons = CreateDialogButtonRow();
            actionButtons.Margin = new Thickness(0);

            Button cancelButton = CreateDialogButton("取消", false);
            System.Windows.Automation.AutomationProperties.SetAutomationId(cancelButton, "CancelRefineDialogButton");
            cancelButton.Click += (_, _) =>
            {
                dialog.DialogResult = false;
            };

            Button saveButton = CreateDialogButton("保存替换", true, 88);
            System.Windows.Automation.AutomationProperties.SetAutomationId(saveButton, "SaveRefineDialogButton");
            saveButton.Click += (_, _) =>
            {
                List<string> manualSteps = ParseRefinementSteps(refineTextBox.Text);

                if (manualSteps.Count == 0)
                {
                    dialogFeedback.Text = "未识别到可用的任务步骤。";
                    dialogFeedback.Foreground = ErrorBrush;
                    dialogFeedback.Visibility = Visibility.Visible;
                    return;
                }

                refinedSteps = manualSteps;
                dialog.DialogResult = true;
            };

            actionButtons.Children.Add(cancelButton);
            actionButtons.Children.Add(saveButton);
            Grid.SetColumn(actionButtons, 1);
            footer.Children.Add(actionButtons);
            Grid.SetRow(footer, 5);
            root.Children.Add(footer);

            dialog.Content = root;
            dialog.ShowDialog();

            return refinedSteps;
        }

        private void ReplaceCurrentStepWithRefinedSteps(int currentIndex, List<string> refinedSteps)
        {
            if (_activePlan is null || currentIndex < 0 || currentIndex >= _activePlan.Steps.Count || refinedSteps.Count == 0)
            {
                return;
            }

            _activePlan.Steps.RemoveAt(currentIndex);
            _activePlan.Steps.InsertRange(currentIndex, refinedSteps.Select(stepText => new PlanStep
            {
                Id = Guid.NewGuid(),
                Text = stepText
            }));

            SaveCurrentPlanChange();

            if (_state.Settings.IsCollapsed)
            {
                RenderCollapsedView();
            }
            else
            {
                RenderExecutionView();
            }
        }

        private void ShowStuckDialog(int currentIndex)
        {
            if (_activePlan is null)
            {
                return;
            }

            Window dialog = CreateDialogShell("卡在哪里？", 340, 520, 320, 460);

            Grid root = new()
            {
                Margin = new Thickness(18)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock titleText = new()
            {
                Text = "卡在哪里？",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = PrimaryTextBrush
            };
            Grid.SetRow(titleText, 0);
            root.Children.Add(titleText);

            StackPanel optionsPanel = new()
            {
                Margin = new Thickness(0, 14, 0, 16)
            };

            string[] stuckReasons =
            [
                "这一步还是太大",
                "我不知道怎么做",
                "我缺少信息或材料",
                "我不知道做到什么算完成",
                "我只是很难开始"
            ];

            List<RadioButton> reasonButtons = [];

            for (int i = 0; i < stuckReasons.Length; i++)
            {
                RadioButton reasonButton = new()
                {
                    Content = stuckReasons[i],
                    GroupName = "StuckReason",
                    Foreground = PrimaryTextBrush,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0)
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(reasonButton, $"StuckReason{i + 1}");
                reasonButtons.Add(reasonButton);
                optionsPanel.Children.Add(reasonButton);
            }

            Grid.SetRow(optionsPanel, 1);
            root.Children.Add(optionsPanel);

            TextBlock noteLabel = new()
            {
                Text = "补充说明（可选）",
                FontSize = 12,
                Foreground = SecondaryTextBrush,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(noteLabel, 2);
            root.Children.Add(noteLabel);

            TextBox noteTextBox = CreateDialogTextBox("");
            System.Windows.Automation.AutomationProperties.SetAutomationId(noteTextBox, "StuckNoteTextBox");
            Grid.SetRow(noteTextBox, 3);
            root.Children.Add(noteTextBox);

            TextBlock dialogFeedback = CreateDialogFeedbackText("", ErrorBrush);
            dialogFeedback.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(dialogFeedback, 4);
            root.Children.Add(dialogFeedback);

            StackPanel buttons = CreateDialogButtonRow();
            buttons.Margin = new Thickness(0, 14, 0, 0);

            Button cancelButton = CreateDialogButton("取消", false);
            System.Windows.Automation.AutomationProperties.SetAutomationId(cancelButton, "CancelStuckDialogButton");
            cancelButton.Click += (_, _) =>
            {
                dialog.DialogResult = false;
            };

            Button copyButton = CreateDialogButton("复制求助指令", true, 108);
            System.Windows.Automation.AutomationProperties.SetAutomationId(copyButton, "CopyStuckPromptButton");
            copyButton.Click += (_, _) =>
            {
                RadioButton? selectedReason = reasonButtons.FirstOrDefault(button => button.IsChecked == true);

                if (selectedReason is null)
                {
                    dialogFeedback.Text = "请选择卡点原因";
                    dialogFeedback.Foreground = ErrorBrush;
                    dialogFeedback.Visibility = Visibility.Visible;
                    return;
                }

                string prompt = BuildStuckHelpPrompt(currentIndex, selectedReason.Content?.ToString() ?? "", noteTextBox.Text);

                try
                {
                    Clipboard.SetText(prompt);
                    dialogFeedback.Text = "已复制求助指令";
                    dialogFeedback.Foreground = SuccessBrush;
                    dialogFeedback.Visibility = Visibility.Visible;
                }
                catch
                {
                    dialogFeedback.Text = "复制失败";
                    dialogFeedback.Foreground = ErrorBrush;
                    dialogFeedback.Visibility = Visibility.Visible;
                }
            };

            buttons.Children.Add(cancelButton);
            buttons.Children.Add(copyButton);
            Grid.SetRow(buttons, 5);
            root.Children.Add(buttons);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        private string BuildStuckHelpPrompt(int currentIndex, string stuckReason, string optionalNote)
        {
            if (_activePlan is null)
            {
                return "";
            }

            StringBuilder prompt = new();
            string currentStep = _activePlan.Steps[currentIndex].Text;
            List<string> completedPreviousSteps = _activePlan.Steps
                .Take(currentIndex)
                .Select((step, index) => new { Step = step, Index = index })
                .Where(item => item.Step.IsCompleted)
                .Select(item => $"{FormatStepNumber(item.Index)} {item.Step.Text}")
                .ToList();

            prompt.AppendLine("你是我的任务执行助手。");
            prompt.AppendLine();
            prompt.AppendLine("我正在完成一个较大的任务：");
            prompt.AppendLine();
            prompt.AppendLine("【总体任务】");
            prompt.AppendLine(GetOverallTaskText());
            prompt.AppendLine();
            prompt.AppendLine("我目前进行到这一步：");
            prompt.AppendLine();
            prompt.AppendLine("【当前步骤】");
            prompt.AppendLine(currentStep);

            if (completedPreviousSteps.Count > 0)
            {
                prompt.AppendLine();
                prompt.AppendLine("【已完成的前置步骤】");
                foreach (string completedStep in completedPreviousSteps)
                {
                    prompt.AppendLine(completedStep);
                }
            }

            prompt.AppendLine();
            prompt.AppendLine("我现在卡住的原因是：");
            prompt.AppendLine();
            prompt.AppendLine("【卡点】");
            prompt.AppendLine(stuckReason);
            prompt.AppendLine();
            prompt.AppendLine("【补充说明】");
            prompt.AppendLine(string.IsNullOrWhiteSpace(optionalNote) ? "无" : optionalNote.Trim());
            prompt.AppendLine();
            prompt.AppendLine("请不要重新规划整个项目。");
            prompt.AppendLine();
            prompt.AppendLine("只处理“当前步骤”。");
            prompt.AppendLine();
            prompt.AppendLine("请把当前步骤进一步拆成更小、更明确、能够立即执行的动作。");
            prompt.AppendLine();
            prompt.AppendLine("要求：");
            prompt.AppendLine();
            prompt.AppendLine("1. 只输出编号步骤：");
            prompt.AppendLine();
            prompt.AppendLine("2. ...");
            prompt.AppendLine();
            prompt.AppendLine("3. ...");
            prompt.AppendLine();
            prompt.AppendLine("4. ...");
            prompt.AppendLine();
            prompt.AppendLine("5. 不要输出前言、总结或解释。");
            prompt.AppendLine();
            prompt.AppendLine("6. 每一步只包含一个动作。");
            prompt.AppendLine();
            prompt.AppendLine("7. 每一步最好在 5–15 分钟内可以完成。");
            prompt.AppendLine();
            prompt.AppendLine("8. 第一步必须是我现在立刻可以开始做的动作。");
            prompt.AppendLine();
            prompt.AppendLine("9. 避免“研究一下、思考一下、完善一下”等模糊表达。");
            prompt.AppendLine();
            prompt.AppendLine("10. 明确告诉我需要打开什么、查看什么、写什么、记录什么或得到什么结果。");
            prompt.AppendLine();
            prompt.AppendLine("11. 一般只需要 3–8 个步骤。");

            return prompt.ToString();
        }

        private string? ShowStepTextDialog(string title, string initialText)
        {
            Window dialog = CreateDialogShell(title, 320, 230, 300, 210);

            Grid root = new()
            {
                Margin = new Thickness(16)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBox textBox = CreateDialogTextBox(initialText);
            System.Windows.Automation.AutomationProperties.SetAutomationId(textBox, "StepEditTextBox");
            Grid.SetRow(textBox, 0);
            root.Children.Add(textBox);

            TextBlock validationText = CreateDialogFeedbackText("请先输入任务", ErrorBrush);
            Grid.SetRow(validationText, 1);
            root.Children.Add(validationText);

            StackPanel buttons = CreateDialogButtonRow();

            Button cancelButton = CreateDialogButton("取消", false);
            System.Windows.Automation.AutomationProperties.SetAutomationId(cancelButton, "StepEditCancelButton");
            cancelButton.Click += (_, _) =>
            {
                dialog.DialogResult = false;
            };

            Button saveButton = CreateDialogButton("保存", true);
            System.Windows.Automation.AutomationProperties.SetAutomationId(saveButton, "StepEditSaveButton");
            saveButton.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(textBox.Text))
                {
                    validationText.Visibility = Visibility.Visible;
                    return;
                }

                dialog.DialogResult = true;
            };

            buttons.Children.Add(cancelButton);
            buttons.Children.Add(saveButton);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            dialog.Content = root;
            dialog.Loaded += (_, _) =>
            {
                textBox.Focus();
                textBox.SelectAll();
            };

            bool? result = dialog.ShowDialog();
            return result == true ? textBox.Text.Trim() : null;
        }

        private bool ShowReplaceCurrentStepConfirmation(int refinedStepCount)
        {
            return ShowConfirmationDialog(
                "替换当前步骤",
                $"将当前步骤替换为 {refinedStepCount} 个更小的步骤？",
                "替换",
                "ConfirmReplaceButton",
                "CancelReplaceButton");
        }

        private bool ShowEndPlanConfirmation()
        {
            return ShowConfirmationDialog(
                "结束本轮任务？",
                "未完成的步骤将保留在历史记录中，本轮任务将标记为已结束。",
                "结束本轮",
                "ConfirmEndPlanButton",
                "CancelEndPlanButton");
        }

        private bool ShowConfirmationDialog(string title, string message, string confirmText, string confirmAutomationId, string cancelAutomationId)
        {
            Window dialog = CreateDialogShell(title, 320, 180, 300, 160);

            Grid root = new()
            {
                Margin = new Thickness(18)
            };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock messageText = new()
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                Foreground = PrimaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(messageText, 0);
            root.Children.Add(messageText);

            StackPanel buttons = CreateDialogButtonRow();

            Button cancelButton = CreateDialogButton("取消", false);
            System.Windows.Automation.AutomationProperties.SetAutomationId(cancelButton, cancelAutomationId);
            cancelButton.Click += (_, _) =>
            {
                dialog.DialogResult = false;
            };

            Button confirmButton = CreateDialogButton(confirmText, true, 84);
            System.Windows.Automation.AutomationProperties.SetAutomationId(confirmButton, confirmAutomationId);
            confirmButton.Click += (_, _) =>
            {
                dialog.DialogResult = true;
            };

            buttons.Children.Add(cancelButton);
            buttons.Children.Add(confirmButton);
            Grid.SetRow(buttons, 1);
            root.Children.Add(buttons);

            dialog.Content = root;
            return dialog.ShowDialog() == true;
        }

        private void RenderStatisticsPage()
        {
            DateTime today = DateTime.Now.Date;
            DateTime weekStart = today.AddDays(-GetDaysSinceMonday(today.DayOfWeek));
            DateTime weekEnd = weekStart.AddDays(7);

            IEnumerable<PlanStep> completedStepSource = _state.OngoingPlans
                .Concat(_state.PlanHistory)
                .SelectMany(plan => plan.Steps)
                .GroupBy(step => step.Id)
                .Select(group => group.First());

            int todayCompletedSteps = completedStepSource.Count(step => IsLocalDate(step.CompletedAt, today));
            int weekCompletedSteps = completedStepSource.Count(step => IsInLocalDateRange(step.CompletedAt, weekStart, weekEnd));
            int completedPlans = _state.PlanHistory.Count(plan => plan.Status == PlanStatus.Completed);
            int endedPlans = _state.PlanHistory.Count(plan => plan.Status == PlanStatus.Ended);

            TodayCompletedStepsTextBlock.Text = todayCompletedSteps.ToString();
            WeekCompletedStepsTextBlock.Text = weekCompletedSteps.ToString();
            CompletedPlansTextBlock.Text = completedPlans.ToString();
            EndedPlansTextBlock.Text = endedPlans.ToString();

            RecentPlanHistoryPanel.Children.Clear();

            List<Plan> recentPlans = _state.PlanHistory
                .OrderByDescending(plan => plan.CompletedAt ?? plan.EndedAt ?? plan.CreatedAt)
                .Take(5)
                .ToList();

            if (recentPlans.Count == 0)
            {
                RecentPlanHistoryPanel.Children.Add(new TextBlock
                {
                    Text = "暂无历史任务",
                    Foreground = SecondaryTextBrush,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (Plan plan in recentPlans)
            {
                RecentPlanHistoryPanel.Children.Add(new TextBlock
                {
                    Text = $"{GetHistoryMarker(plan)} {plan.OverallTask}",
                    Foreground = plan.Status == PlanStatus.Completed ? SuccessBrush : SecondaryTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }
        }

        private void ApplyCollapsedState(bool collapsed, bool saveSetting, bool captureCurrentGeometry = true)
        {
            _isApplyingWindowSize = true;
            bool wasCollapsed = _state.Settings.IsCollapsed;

            try
            {
                if (captureCurrentGeometry && collapsed && !wasCollapsed)
                {
                    CaptureExpandedGeometry();
                }
                else if (captureCurrentGeometry && !collapsed && wasCollapsed)
                {
                    CaptureCompactGeometry();
                }

                _state.Settings.IsCollapsed = collapsed;

                if (collapsed)
                {
                    RenderCollapsedView();
                    ExpandedRoot.Visibility = Visibility.Collapsed;
                    CollapsedRoot.Visibility = Visibility.Visible;
                    ApplyCollapsedWindowChrome();
                    PlaceCompactWindow();
                }
                else
                {
                    CollapsedRoot.Visibility = Visibility.Collapsed;
                    ExpandedRoot.Visibility = Visibility.Visible;
                    ApplyExpandedWindowChrome();
                    MinWidth = ExpandedMinWidth;
                    MaxWidth = ExpandedMaxWidth;
                    MinHeight = ExpandedMinHeight;
                    MaxHeight = double.PositiveInfinity;
                    ShowPage(_currentPage);
                    PlaceExpandedWindow();
                }

                UpdateCollapsedArrow();
                UpdateTrayMenuState();
            }
            finally
            {
                _isApplyingWindowSize = false;
            }

            if (saveSetting)
            {
                SaveState();
            }
        }

        private void ApplyCollapsedWindowChrome()
        {
            if (WindowStyle != WindowStyle.None)
            {
                WindowStyle = WindowStyle.None;
            }

            if (WindowChrome.GetWindowChrome(this) is null)
            {
                WindowChrome.SetWindowChrome(this, CreateCollapsedWindowChrome());
            }

            if (ResizeMode != ResizeMode.CanResize)
            {
                ResizeMode = ResizeMode.CanResize;
            }
        }

        private void ApplyExpandedWindowChrome()
        {
            if (WindowStyle != WindowStyle.SingleBorderWindow)
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
            }

            if (WindowChrome.GetWindowChrome(this) is not null)
            {
                WindowChrome.SetWindowChrome(this, null);
            }

            if (ResizeMode != ResizeMode.CanResize)
            {
                ResizeMode = ResizeMode.CanResize;
            }
        }

        private void PlaceWindowAtDockEdge()
        {
            Rect workArea = GetCurrentMonitorWorkArea();
            double currentHeight = double.IsNaN(Height) || Height <= 0 ? ActualHeight : Height;
            double maxAllowedHeight = Math.Max(MinHeight, workArea.Height - (DockMargin * 2));
            double targetHeight = Math.Clamp(currentHeight, MinHeight, maxAllowedHeight);

            if (!double.IsNaN(targetHeight) && targetHeight > 0 && Math.Abs(Height - targetHeight) > 0.5)
            {
                Height = targetHeight;
            }

            Top = workArea.Top + Math.Max(DockMargin, (workArea.Height - Height) / 2);
            Left = _state.Settings.DockSide == DockSide.Right
                ? workArea.Right - Width - DockMargin
                : workArea.Left + DockMargin;
        }

        private void PlaceExpandedWindow()
        {
            Rect workArea = GetCurrentMonitorWorkArea();
            MinWidth = ExpandedMinWidth;
            MaxWidth = ExpandedMaxWidth;
            MinHeight = ExpandedMinHeight;
            MaxHeight = Math.Max(ExpandedMinHeight, workArea.Height - (DockMargin * 2));

            Width = Math.Clamp(GetPositiveOrDefault(_state.Settings.ExpandedWidth, _state.Settings.WindowWidth), ExpandedMinWidth, ExpandedMaxWidth);
            Height = Math.Clamp(GetPositiveOrDefault(_state.Settings.ExpandedHeight, ExpandedDefaultHeight), ExpandedMinHeight, MaxHeight);

            if (_state.Settings.ExpandedLeft.HasValue && _state.Settings.ExpandedTop.HasValue)
            {
                Left = _state.Settings.ExpandedLeft.Value;
                Top = _state.Settings.ExpandedTop.Value;
                ClampWindowToCurrentWorkArea();
            }
            else
            {
                PlaceWindowAtDockEdge();
            }

            CaptureExpandedGeometry();
        }

        private void PlaceCompactWindow()
        {
            Rect workArea = GetCurrentMonitorWorkArea();
            MinWidth = CollapsedMinWidth;
            MaxWidth = CollapsedMaxWidth;
            MinHeight = CollapsedMinHeight;
            MaxHeight = Math.Max(CollapsedMinHeight, workArea.Height - (DockMargin * 2));

            Width = Math.Clamp(GetPositiveOrDefault(_state.Settings.CompactWidth, CollapsedDefaultWidth), CollapsedMinWidth, CollapsedMaxWidth);
            Height = Math.Clamp(GetPositiveOrDefault(_state.Settings.CompactHeight, Height), CollapsedMinHeight, MaxHeight);

            if (_state.Settings.CompactLeft.HasValue && _state.Settings.CompactTop.HasValue)
            {
                Left = _state.Settings.CompactLeft.Value;
                Top = _state.Settings.CompactTop.Value;
                ClampWindowToCurrentWorkArea();
            }
            else
            {
                PlaceWindowAtDockEdge();
            }

            CaptureCompactGeometry();
        }

        private void ClampWindowToCurrentWorkArea()
        {
            Rect workArea = GetCurrentMonitorWorkArea();
            double maxVisibleWidth = Math.Max(MinWidth, workArea.Width - (DockMargin * 2));
            double maxVisibleHeight = Math.Max(MinHeight, workArea.Height - (DockMargin * 2));

            if (Width > maxVisibleWidth)
            {
                Width = maxVisibleWidth;
            }

            if (Height > maxVisibleHeight)
            {
                Height = maxVisibleHeight;
            }

            Left = ClampToRange(Left, workArea.Left + DockMargin, workArea.Right - Width - DockMargin);
            Top = ClampToRange(Top, workArea.Top + DockMargin, workArea.Bottom - Height - DockMargin);
        }

        private void CaptureExpandedGeometry()
        {
            if (_state.Settings.IsCollapsed || WindowState != WindowState.Normal)
            {
                return;
            }

            _state.Settings.ExpandedWidth = Math.Clamp(Width, ExpandedMinWidth, ExpandedMaxWidth);
            _state.Settings.WindowWidth = _state.Settings.ExpandedWidth;
            _state.Settings.ExpandedHeight = Math.Max(ExpandedMinHeight, Height);
            _state.Settings.ExpandedLeft = Left;
            _state.Settings.ExpandedTop = Top;
        }

        private void CaptureCompactGeometry()
        {
            if (!_state.Settings.IsCollapsed)
            {
                return;
            }

            _state.Settings.CompactWidth = Math.Clamp(Width, CollapsedMinWidth, CollapsedMaxWidth);
            _state.Settings.CompactHeight = Math.Clamp(Height, CollapsedMinHeight, MaxHeight);
            _state.Settings.CompactLeft = Left;
            _state.Settings.CompactTop = Top;
        }

        private static WindowChrome CreateCollapsedWindowChrome()
        {
            return new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(CollapsedResizeBorder),
                UseAeroCaptionButtons = false
            };
        }

        private Rect GetCurrentMonitorWorkArea()
        {
            try
            {
                IntPtr windowHandle = new WindowInteropHelper(this).Handle;

                if (windowHandle != IntPtr.Zero)
                {
                    IntPtr monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);

                    if (monitorHandle != IntPtr.Zero)
                    {
                        MonitorInfo monitorInfo = new()
                        {
                            cbSize = Marshal.SizeOf<MonitorInfo>()
                        };

                        if (GetMonitorInfo(monitorHandle, ref monitorInfo))
                        {
                            return DeviceRectToDip(monitorInfo.rcWork);
                        }
                    }
                }
            }
            catch
            {
                // Fall back to WPF's primary working area if monitor lookup fails.
            }

            return SystemParameters.WorkArea;
        }

        private Rect DeviceRectToDip(NativeRect rect)
        {
            Point topLeft = new(rect.Left, rect.Top);
            Point bottomRight = new(rect.Right, rect.Bottom);
            PresentationSource? source = PresentationSource.FromVisual(this);

            if (source is not null)
            {
                Matrix transform = source.CompositionTarget.TransformFromDevice;
                topLeft = transform.Transform(topLeft);
                bottomRight = transform.Transform(bottomRight);
            }

            return new Rect(topLeft, bottomRight);
        }

        private void ResetCompactHeaderDragState()
        {
            _isCompactDragCandidate = false;
            _didDragCompactWindow = false;

            if (CollapsedHeader.IsMouseCaptured)
            {
                CollapsedHeader.ReleaseMouseCapture();
            }
        }

        private static double ClampToRange(double value, double min, double max)
        {
            return max < min ? min : Math.Clamp(value, min, max);
        }

        private static double GetPositiveOrDefault(double value, double defaultValue)
        {
            return double.IsFinite(value) && value > 0 ? value : defaultValue;
        }

        private void UpdateNavigationVisualState()
        {
            SetNavButtonState(TaskTabButton, _currentPage == MainPage.Task);
            SetNavButtonState(StatisticsTabButton, _currentPage == MainPage.Statistics);
            SetNavButtonState(SettingsTabButton, _currentPage == MainPage.Settings);
        }

        private static void SetNavButtonState(Button button, bool isActive)
        {
            button.Background = isActive ? AccentBrush : Brushes.White;
            button.BorderBrush = isActive ? AccentBrush : BorderLineBrush;
            button.Foreground = isActive ? Brushes.White : PrimaryTextBrush;
            button.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void UpdateAlwaysOnTopVisualState()
        {
            bool isEnabled = _state.Settings.AlwaysOnTop;
            AlwaysOnTopButton.Background = isEnabled ? AccentBrush : Brushes.White;
            AlwaysOnTopButton.BorderBrush = isEnabled ? AccentBrush : BorderLineBrush;
            AlwaysOnTopButton.Foreground = isEnabled ? Brushes.White : PrimaryTextBrush;
            AlwaysOnTopButton.FontWeight = isEnabled ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void UpdateCollapsedArrow()
        {
            CollapsedArrowTextBlock.Text = _state.Settings.DockSide == DockSide.Right ? "<" : ">";
        }

        private void RenderCollapsedView()
        {
            EnsureCurrentPlanSelection();

            CollapsedCompletionPanel.Visibility = Visibility.Collapsed;
            CollapsedCurrentStepTextBlock.BeginAnimation(OpacityProperty, null);
            CollapsedCurrentStepTextBlock.Opacity = 1;
            CollapsedCompleteCurrentStepButton.Content = "";
            CollapsedCompleteCurrentStepButton.BorderBrush = MutedTextBrush;
            CollapsedCompleteCurrentStepButton.Background = Brushes.Transparent;

            if (_activePlan is null)
            {
                SetCollapsedNextStepVisible(false);
                CollapsedPlanPanel.Visibility = Visibility.Collapsed;
                CollapsedNoActivePlanTextBlock.Visibility = Visibility.Visible;
                CollapsedNoActivePlanTextBlock.Text = "暂无进行中的任务";
                CollapsedCurrentStepTextBlock.Text = "";
                CollapsedCurrentStepTextBlock.ToolTip = null;
                CollapsedNextStepTextBlock.Text = "";
                CollapsedNextStepTextBlock.ToolTip = null;
                CollapsedCompleteCurrentStepButton.IsEnabled = false;
                return;
            }

            int currentIndex = GetCurrentStepIndex(_activePlan);

            if (currentIndex < 0)
            {
                SetCollapsedNextStepVisible(false);
                CollapsedPlanPanel.Visibility = Visibility.Collapsed;
                CollapsedNoActivePlanTextBlock.Visibility = Visibility.Visible;
                CollapsedNoActivePlanTextBlock.Text = "暂无进行中的任务";
                CollapsedCompleteCurrentStepButton.IsEnabled = false;
                return;
            }

            PlanStep currentStep = _activePlan.Steps[currentIndex];
            PlanStep? nextStep = _activePlan.Steps
                .Skip(currentIndex + 1)
                .FirstOrDefault(step => !step.IsCompleted);

            CollapsedNoActivePlanTextBlock.Visibility = Visibility.Collapsed;
            CollapsedPlanPanel.Visibility = Visibility.Visible;
            CollapsedCurrentStepTextBlock.Text = currentStep.Text;
            CollapsedCurrentStepTextBlock.ToolTip = currentStep.Text;
            CollapsedCompleteCurrentStepButton.IsEnabled = true;

            if (nextStep is null)
            {
                SetCollapsedNextStepVisible(false);
                CollapsedNextStepTextBlock.Text = "";
                CollapsedNextStepTextBlock.ToolTip = null;
            }
            else
            {
                SetCollapsedNextStepVisible(true);
                CollapsedNextStepTextBlock.Text = nextStep.Text;
                CollapsedNextStepTextBlock.ToolTip = nextStep.Text;
            }
        }

        private void SetCollapsedNextStepVisible(bool isVisible)
        {
            Visibility visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            CollapsedNextStepDivider.Visibility = visibility;
            CollapsedNextStepLabelTextBlock.Visibility = visibility;
            CollapsedNextStepTextBlock.Visibility = visibility;
            CollapsedCurrentStepRow.Height = new GridLength(1, GridUnitType.Star);
        }

        private void SyncSettingsControls()
        {
            _isApplyingSettings = true;

            try
            {
                AlwaysOnTopCheckBox.IsChecked = _state.Settings.AlwaysOnTop;
                StartupCollapsedCheckBox.IsChecked = _state.Settings.StartupCollapsed;
                MinimizeToTrayOnCloseCheckBox.IsChecked = _state.Settings.MinimizeToTrayOnClose;
                StartWithWindowsCheckBox.IsChecked = _state.Settings.StartWithWindows;
                DockLeftRadioButton.IsChecked = _state.Settings.DockSide == DockSide.Left;
                DockRightRadioButton.IsChecked = _state.Settings.DockSide == DockSide.Right;
            }
            finally
            {
                _isApplyingSettings = false;
            }
        }

        private void RenderDesktopShortcutStatus()
        {
            ShortcutStatus status = _shortcutService.GetShortcutStatus(ShortcutLocation.Desktop);

            if (status.IsValid)
            {
                DesktopShortcutStatusTextBlock.Text = "已创建";
                DesktopShortcutStatusTextBlock.Foreground = SuccessBrush;
                CreateDesktopShortcutButton.Visibility = Visibility.Collapsed;
                RemoveDesktopShortcutButton.Visibility = Visibility.Visible;
            }
            else if (status.FileExists)
            {
                DesktopShortcutStatusTextBlock.Text = "快捷方式需要更新。";
                DesktopShortcutStatusTextBlock.Foreground = SecondaryTextBrush;
                CreateDesktopShortcutButton.Visibility = Visibility.Visible;
                RemoveDesktopShortcutButton.Visibility = Visibility.Visible;
            }
            else
            {
                DesktopShortcutStatusTextBlock.Text = "未创建";
                DesktopShortcutStatusTextBlock.Foreground = SecondaryTextBrush;
                CreateDesktopShortcutButton.Visibility = Visibility.Visible;
                RemoveDesktopShortcutButton.Visibility = Visibility.Collapsed;
            }
        }

        private void RenderDataLocationSection(string? message = null, Brush? foreground = null)
        {
            DataLocationTextBlock.Text = _stateStore.StateDirectory;
            RestoreDefaultDataLocationButton.Visibility = _stateStore.IsUsingCustomDataDirectory
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(message))
            {
                DataLocationStatusTextBlock.Text = "";
                DataLocationStatusTextBlock.Visibility = Visibility.Collapsed;
                return;
            }

            DataLocationStatusTextBlock.Text = message;
            DataLocationStatusTextBlock.Foreground = foreground ?? SecondaryTextBrush;
            DataLocationStatusTextBlock.Visibility = Visibility.Visible;
        }

        private bool ResolveStartupDataDirectoryIfNeeded()
        {
            while (_pendingDataDirectoryError is not null)
            {
                StartupDataDirectoryAction action = ShowUnavailableDataDirectoryDialog(_pendingDataDirectoryError.DataDirectory);

                if (action == StartupDataDirectoryAction.Exit)
                {
                    return false;
                }

                if (action == StartupDataDirectoryAction.Retry)
                {
                    _stateStore.ReloadBootstrapConfig();

                    if (TryLoadStateFromStore(out DataDirectoryUnavailableException? retryError))
                    {
                        _pendingDataDirectoryError = null;
                        return true;
                    }

                    _pendingDataDirectoryError = retryError;
                    continue;
                }

                string? selectedDirectory = action == StartupDataDirectoryAction.ChooseOther
                    ? SelectDataDirectory(_stateStore.DefaultStateDirectory)
                    : _stateStore.DefaultStateDirectory;

                if (string.IsNullOrWhiteSpace(selectedDirectory))
                {
                    continue;
                }

                DataLocationChangeResult switchResult = _stateStore.UseDataDirectoryWithoutMigration(selectedDirectory);

                if (!switchResult.Success)
                {
                    ShowDataLocationStartupErrorDialog(switchResult.Message);
                    continue;
                }

                if (TryLoadStateFromStore(out DataDirectoryUnavailableException? switchError))
                {
                    _pendingDataDirectoryError = null;
                    return true;
                }

                _pendingDataDirectoryError = switchError;
            }

            return true;
        }

        private bool TryLoadStateFromStore(out DataDirectoryUnavailableException? unavailableError)
        {
            try
            {
                _state = _stateStore.Load();
                unavailableError = null;
                return true;
            }
            catch (DataDirectoryUnavailableException ex)
            {
                _state = new AppState();
                unavailableError = ex;
                return false;
            }
        }

        private StartupDataDirectoryAction ShowUnavailableDataDirectoryDialog(string dataDirectory)
        {
            StartupDataDirectoryAction action = StartupDataDirectoryAction.Exit;
            Window dialog = CreateDialogShell("无法访问数据位置", 360, 230, 320, 210);
            dialog.ResizeMode = ResizeMode.NoResize;

            StackPanel root = new()
            {
                Margin = new Thickness(18)
            };

            root.Children.Add(new TextBlock
            {
                Text = "无法访问数据位置",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = PrimaryTextBrush
            });
            root.Children.Add(new TextBlock
            {
                Text = $"NextCue 无法访问：\n{dataDirectory}",
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = SecondaryTextBrush
            });

            StackPanel buttons = CreateDialogButtonRow();

            Button retryButton = CreateDialogButton("重试", true, 72);
            retryButton.Margin = new Thickness(0, 0, 8, 0);
            retryButton.Click += (_, _) =>
            {
                action = StartupDataDirectoryAction.Retry;
                dialog.DialogResult = true;
            };
            buttons.Children.Add(retryButton);

            Button chooseButton = CreateDialogButton("选择其他位置", false, 108);
            chooseButton.Click += (_, _) =>
            {
                action = StartupDataDirectoryAction.ChooseOther;
                dialog.DialogResult = true;
            };
            buttons.Children.Add(chooseButton);

            Button defaultButton = CreateDialogButton("使用默认位置", false, 108);
            defaultButton.Margin = new Thickness(0);
            defaultButton.Click += (_, _) =>
            {
                action = StartupDataDirectoryAction.UseDefault;
                dialog.DialogResult = true;
            };
            buttons.Children.Add(defaultButton);
            root.Children.Add(buttons);

            dialog.Content = root;
            dialog.ShowDialog();
            return action;
        }

        private void ShowDataLocationStartupErrorDialog(string message)
        {
            Window dialog = CreateDialogShell("数据位置", 320, 180, 300, 160);
            dialog.ResizeMode = ResizeMode.NoResize;

            StackPanel root = new()
            {
                Margin = new Thickness(18)
            };

            root.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = PrimaryTextBrush
            });

            StackPanel buttons = CreateDialogButtonRow();
            Button okButton = CreateDialogButton("知道了", true, 72);
            okButton.Click += (_, _) =>
            {
                dialog.DialogResult = true;
            };
            buttons.Children.Add(okButton);
            root.Children.Add(buttons);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        private string? SelectDataDirectory(string initialDirectory)
        {
            using Forms.FolderBrowserDialog dialog = new()
            {
                Description = "选择 NextCue 数据文件夹",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            if (Directory.Exists(initialDirectory))
            {
                dialog.SelectedPath = initialDirectory;
            }
            else if (Directory.Exists(_stateStore.DefaultStateDirectory))
            {
                dialog.SelectedPath = _stateStore.DefaultStateDirectory;
            }

            return dialog.ShowDialog() == Forms.DialogResult.OK
                ? dialog.SelectedPath
                : null;
        }

        private Plan? EnsureCurrentPlanSelection()
        {
            _state.OngoingPlans ??= [];

            if (_state.CurrentPlanId.HasValue)
            {
                Plan? selectedPlan = _state.OngoingPlans.FirstOrDefault(plan => plan.Id == _state.CurrentPlanId.Value);

                if (selectedPlan is not null)
                {
                    selectedPlan.Status = PlanStatus.Ongoing;
                    _activePlan = selectedPlan;
                    return _activePlan;
                }
            }

            return SelectMostRecentOngoingPlan(touchSelection: false);
        }

        private void SelectCurrentPlan(Guid planId, bool saveImmediately, bool touchSelection = true)
        {
            Plan? plan = _state.OngoingPlans.FirstOrDefault(ongoingPlan => ongoingPlan.Id == planId);

            if (plan is null)
            {
                EnsureCurrentPlanSelection();
                return;
            }

            plan.Status = PlanStatus.Ongoing;
            _state.CurrentPlanId = plan.Id;
            _activePlan = plan;
            _isCreatingNewPlan = false;

            if (touchSelection)
            {
                TouchCurrentPlan();
            }

            if (saveImmediately)
            {
                SaveState();
            }
        }

        private Plan? SelectMostRecentOngoingPlan(bool touchSelection)
        {
            Plan? plan = _state.OngoingPlans
                .Where(ongoingPlan => ongoingPlan.Status is PlanStatus.Ongoing or PlanStatus.Active)
                .OrderByDescending(ongoingPlan => ongoingPlan.LastAccessedAt)
                .ThenByDescending(ongoingPlan => ongoingPlan.CreatedAt)
                .FirstOrDefault();

            if (plan is null)
            {
                _state.CurrentPlanId = null;
                _activePlan = null;
                return null;
            }

            plan.Status = PlanStatus.Ongoing;
            _state.CurrentPlanId = plan.Id;
            _activePlan = plan;

            if (touchSelection)
            {
                TouchCurrentPlan();
            }

            return _activePlan;
        }

        private void RemoveOngoingPlan(Guid planId)
        {
            _state.OngoingPlans.RemoveAll(plan => plan.Id == planId);

            if (_state.CurrentPlanId == planId)
            {
                _state.CurrentPlanId = null;
            }
        }

        private void BeginNewPlanCreation()
        {
            _isCreatingNewPlan = true;
            TaskTextBox.Clear();
            ImportPanel.Visibility = Visibility.Collapsed;
            ShowPage(MainPage.Task);
            Dispatcher.BeginInvoke(MoveFocusAwayFromTaskInput, DispatcherPriority.ContextIdle);
        }

        private void TouchCurrentPlan()
        {
            if (_activePlan is not null)
            {
                _activePlan.LastAccessedAt = DateTimeOffset.Now;
            }
        }

        private void SaveCurrentPlanChange()
        {
            TouchCurrentPlan();
            SaveState();
        }

        private void SaveState()
        {
            _state.ActivePlan = null;
            EnsureCurrentPlanSelection();
            _stateStore.Save(_state);

            if (_currentPage == MainPage.Statistics)
            {
                RenderStatisticsPage();
            }
        }

        private static string BuildPlanningPrompt(string userTask)
        {
            return PlanningPromptTemplate.Replace("{USER_TASK}", userTask);
        }

        private static string BuildTaskAxisClipboardText(Plan plan)
        {
            StringBuilder text = new();
            int currentIndex = GetCurrentStepIndex(plan);

            text.AppendLine("总体任务：");
            text.AppendLine(string.IsNullOrWhiteSpace(plan.OverallTask) ? "未填写" : plan.OverallTask);
            text.AppendLine();

            for (int i = 0; i < plan.Steps.Count; i++)
            {
                PlanStep step = plan.Steps[i];
                string stateMarker = step.IsCompleted ? "[✓]" : i == currentIndex ? "[→]" : "[ ]";
                text.AppendLine($"{i + 1}. {stateMarker} {step.Text}");
            }

            return text.ToString();
        }

        private static List<string> ParseSteps(string text)
        {
            List<string> steps = [];

            foreach (string line in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
            {
                string trimmedLine = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmedLine))
                {
                    continue;
                }

                Match match = StepLineRegex.Match(trimmedLine);

                if (!match.Success)
                {
                    continue;
                }

                string stepText = match.Groups["text"].Value.Trim();

                if (!string.IsNullOrWhiteSpace(stepText))
                {
                    steps.Add(stepText);
                }
            }

            return steps;
        }

        private static List<string> ParseRefinementSteps(string text)
        {
            List<string> structuredSteps = ParseSteps(text);

            if (structuredSteps.Count > 0)
            {
                return structuredSteps;
            }

            return text
                .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        private static string FormatStepsForEditing(IReadOnlyList<string> steps)
        {
            return string.Join(Environment.NewLine, steps.Select((step, index) => $"{index + 1}. {step}"));
        }

        private string GetInputOverallTaskText()
        {
            string taskText = TaskTextBox.Text.Trim();
            return string.IsNullOrWhiteSpace(taskText) ? "未填写" : taskText;
        }

        private string GetOverallTaskText()
        {
            string overallTask = _activePlan?.OverallTask ?? TaskTextBox.Text.Trim();
            return string.IsNullOrWhiteSpace(overallTask) ? "未填写" : overallTask;
        }

        private IEnumerable<Plan> GetOrderedOngoingPlans()
        {
            Guid? currentPlanId = _state.CurrentPlanId;

            return _state.OngoingPlans
                .Where(plan => plan.Status is PlanStatus.Ongoing or PlanStatus.Active)
                .OrderByDescending(plan => currentPlanId.HasValue && plan.Id == currentPlanId.Value)
                .ThenByDescending(plan => plan.LastAccessedAt)
                .ThenByDescending(plan => plan.CreatedAt);
        }

        private static string GetPlanDisplayName(Plan plan)
        {
            return string.IsNullOrWhiteSpace(plan.OverallTask) ? "未填写" : plan.OverallTask.Trim();
        }

        private static string GetCurrentStepPreview(Plan plan)
        {
            PlanStep? currentStep = plan.Steps.FirstOrDefault(step => !step.IsCompleted);
            return currentStep is null || string.IsNullOrWhiteSpace(currentStep.Text)
                ? "暂无下一步"
                : currentStep.Text.Trim();
        }

        private PlanStep? GetCurrentStep()
        {
            if (_activePlan is null)
            {
                return null;
            }

            int currentIndex = GetCurrentStepIndex(_activePlan);
            return currentIndex >= 0 ? _activePlan.Steps[currentIndex] : null;
        }

        private static int GetCurrentStepIndex(Plan plan)
        {
            return plan.Steps.FindIndex(step => !step.IsCompleted);
        }

        private static bool IsPlanFullyCompleted(Plan plan)
        {
            return plan.Steps.Count > 0 && plan.Steps.All(step => step.IsCompleted);
        }

        private static void SetStepCompleted(PlanStep step, bool isCompleted)
        {
            step.IsCompleted = isCompleted;
            step.CompletedAt = isCompleted ? step.CompletedAt ?? DateTimeOffset.Now : null;
        }

        private static void MoveDraft(List<StepDraft> drafts, int fromIndex, int toIndex)
        {
            StepDraft draft = drafts[fromIndex];
            drafts.RemoveAt(fromIndex);
            drafts.Insert(toIndex, draft);
        }

        private static bool IsLocalDate(DateTimeOffset? value, DateTime date)
        {
            return value?.LocalDateTime.Date == date;
        }

        private static bool IsInLocalDateRange(DateTimeOffset? value, DateTime startInclusive, DateTime endExclusive)
        {
            if (value is null)
            {
                return false;
            }

            DateTime localDate = value.Value.LocalDateTime.Date;
            return localDate >= startInclusive && localDate < endExclusive;
        }

        private static int GetDaysSinceMonday(DayOfWeek dayOfWeek)
        {
            return ((int)dayOfWeek + 6) % 7;
        }

        private static string GetHistoryMarker(Plan plan)
        {
            return plan.Status == PlanStatus.Completed ? "✓" : "○";
        }

        private static string FormatHistoryStatus(Plan plan)
        {
            return plan.Status == PlanStatus.Completed ? "已完成" : "已结束";
        }

        private static string FormatHistoryDate(DateTimeOffset? value)
        {
            return value is null ? "未记录" : value.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        }

        private static string FormatStepNumber(int index)
        {
            return (index + 1).ToString("D2");
        }

        private Brush GetAxisRowBrush(int stepIndex, int currentIndex)
        {
            if (_activePlan is null)
            {
                return SecondaryTextBrush;
            }

            if (_activePlan.Steps[stepIndex].IsCompleted)
            {
                return MutedTextBrush;
            }

            return stepIndex == currentIndex ? AccentBrush : SecondaryTextBrush;
        }

        private string GetStepMarker(int stepIndex, int currentIndex)
        {
            if (_activePlan is null)
            {
                return "○";
            }

            if (_activePlan.Steps[stepIndex].IsCompleted)
            {
                return "✓";
            }

            return stepIndex == currentIndex ? "→" : "○";
        }

        private Window CreateDialogShell(string title, double width, double height, double minWidth, double minHeight)
        {
            return new Window
            {
                Title = title,
                Width = width,
                Height = height,
                MinWidth = minWidth,
                MinHeight = minHeight,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                Topmost = Topmost,
                Background = WindowBackgroundBrush,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = FontSize
            };
        }

        private static TextBox CreateDialogTextBox(string initialText)
        {
            return new TextBox
            {
                Text = initialText,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10),
                BorderBrush = BorderLineBrush,
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Foreground = PrimaryTextBrush,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
        }

        private static TextBox CreateInlineEditTextBox(string initialText)
        {
            return new TextBox
            {
                Text = initialText,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 44,
                MaxHeight = 108,
                Padding = new Thickness(8),
                BorderBrush = BorderLineBrush,
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                Foreground = PrimaryTextBrush,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
        }

        private static Button CreateIconDialogButton(string text, string tooltip)
        {
            return new Button
            {
                Content = text,
                Width = 26,
                Height = 28,
                Margin = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = SecondaryTextBrush,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 13,
                ToolTip = tooltip,
                Cursor = Cursors.Hand
            };
        }

        private static bool IsInsideInteractiveElement(DependencyObject? source)
        {
            while (source is not null)
            {
                if (source is TextBox or Button or CheckBox or RadioButton or Image)
                {
                    return true;
                }

                DependencyObject? parent = null;

                try
                {
                    parent = VisualTreeHelper.GetParent(source);
                }
                catch
                {
                    parent = null;
                }

                parent ??= LogicalTreeHelper.GetParent(source);
                source = parent;
            }

            return false;
        }

        private static TextBlock CreateDialogFeedbackText(string text, Brush foreground)
        {
            return new TextBlock
            {
                Text = text,
                Visibility = Visibility.Collapsed,
                Foreground = foreground,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            };
        }

        private static StackPanel CreateDialogButtonRow()
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
        }

        private static Button CreateDialogButton(string text, bool isPrimary, double width = 72)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 34,
                Margin = new Thickness(0, 0, isPrimary ? 0 : 8, 0),
                Padding = new Thickness(10, 0, 10, 0),
                Background = isPrimary ? AccentBrush : Brushes.White,
                BorderBrush = isPrimary ? AccentBrush : BorderLineBrush,
                BorderThickness = new Thickness(1),
                Foreground = isPrimary ? Brushes.White : PrimaryTextBrush,
                FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
        }

        private Button CreatePlanLibraryRow(Plan plan, bool isCurrent)
        {
            Button button = new()
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 10, 12, 10),
                Background = Brushes.White,
                BorderBrush = isCurrent ? AccentBrush : BorderLineBrush,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, $"PlanLibraryPlanButton{plan.Id:N}");

            Grid row = new();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock marker = new()
            {
                Text = isCurrent ? "●" : "○",
                Foreground = isCurrent ? AccentBrush : SecondaryTextBrush,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0)
            };
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            StackPanel textPanel = new();
            textPanel.Children.Add(new TextBlock
            {
                Text = GetPlanDisplayName(plan),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = PrimaryTextBrush
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = $"当前：{GetCurrentStepPreview(plan)}",
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = SecondaryTextBrush
            });
            Grid.SetColumn(textPanel, 1);
            row.Children.Add(textPanel);

            button.Content = row;
            return button;
        }

        private Button CreateHistoryPlanRow(Plan plan)
        {
            Button button = new()
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 10, 12, 10),
                Background = Brushes.White,
                BorderBrush = BorderLineBrush,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, $"HistoryPlanButton{plan.Id:N}");

            int completedSteps = plan.Steps.Count(step => step.IsCompleted);
            string dateText = FormatHistoryDate(plan.CompletedAt ?? plan.EndedAt ?? plan.CreatedAt);

            StackPanel content = new();
            content.Children.Add(new TextBlock
            {
                Text = $"{GetHistoryMarker(plan)} {GetPlanDisplayName(plan)}",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Foreground = plan.Status == PlanStatus.Completed ? SuccessBrush : PrimaryTextBrush
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{FormatHistoryStatus(plan)} · {dateText} · {completedSteps}/{plan.Steps.Count} 步",
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = SecondaryTextBrush
            });

            button.Content = content;
            return button;
        }

        private static void AddHistoryDetailField(Panel panel, string label, string value)
        {
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = SecondaryTextBrush
            });
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value) ? "未记录" : value,
                Margin = new Thickness(0, 4, 0, 12),
                TextWrapping = TextWrapping.Wrap,
                Foreground = PrimaryTextBrush
            });
        }

        private static Button CreateSmallDialogButton(string text)
        {
            return new Button
            {
                Content = text,
                MinWidth = 48,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 0, 8, 0),
                Background = Brushes.White,
                BorderBrush = BorderLineBrush,
                BorderThickness = new Thickness(1),
                Foreground = PrimaryTextBrush,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 12
            };
        }

        private void ShowFeedback(string message, Brush foreground)
        {
            _feedbackTimer.Stop();

            FeedbackTextBlock.Text = message;
            FeedbackTextBlock.Foreground = foreground;
            FeedbackTextBlock.Visibility = Visibility.Visible;

            _feedbackTimer.Start();
        }

        private void ShowExecutionFeedback(string message, Brush foreground, bool autoHide = false)
        {
            _executionFeedbackTimer.Stop();
            ExecutionFeedbackTextBlock.Text = message;
            ExecutionFeedbackTextBlock.Foreground = foreground;
            ExecutionFeedbackTextBlock.Visibility = Visibility.Visible;

            if (autoHide)
            {
                _executionFeedbackTimer.Start();
            }
        }

        private void ClearExecutionFeedback()
        {
            _executionFeedbackTimer.Stop();
            ExecutionFeedbackTextBlock.Text = "";
            ExecutionFeedbackTextBlock.Visibility = Visibility.Collapsed;
        }

        private void FeedbackTimer_Tick(object? sender, EventArgs e)
        {
            _feedbackTimer.Stop();
            FeedbackTextBlock.Visibility = Visibility.Collapsed;
        }

        private void ExecutionFeedbackTimer_Tick(object? sender, EventArgs e)
        {
            _executionFeedbackTimer.Stop();
            ExecutionFeedbackTextBlock.Visibility = Visibility.Collapsed;
        }

        private void UpdatePlaceholderVisibility()
        {
            TaskPlaceholderText.Visibility = string.IsNullOrEmpty(TaskTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void MoveFocusAwayFromTaskInput()
        {
            if (!IsLoaded || (!_isCreatingNewPlan && _activePlan is not null) || _state.Settings.IsCollapsed || !string.IsNullOrEmpty(TaskTextBox.Text))
            {
                return;
            }

            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, ExpandedRoot);
            ExpandedRoot.Focus();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int cbSize;
            public NativeRect rcMonitor;
            public NativeRect rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private enum MainPage
        {
            Task,
            Statistics,
            Settings
        }

        private enum StartupDataDirectoryAction
        {
            Exit,
            Retry,
            ChooseOther,
            UseDefault
        }

        private sealed class StepDraft
        {
            public Guid Id { get; set; } = Guid.NewGuid();

            public string Text { get; set; } = "";

            public bool IsCompleted { get; set; }

            public DateTimeOffset? CompletedAt { get; set; }

            public static StepDraft FromStep(PlanStep step)
            {
                return new StepDraft
                {
                    Id = step.Id,
                    Text = step.Text,
                    IsCompleted = step.IsCompleted,
                    CompletedAt = step.CompletedAt
                };
            }
        }
    }
}
