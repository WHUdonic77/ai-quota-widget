// AI 额度桌面小组件 —— 单文件自包含图形化安装与卸载程序
// 使用 Windows 自带 .NET Framework 4.x 编译，零外部依赖，免管理员权限 (Per-User)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using IOPath = System.IO.Path;
using WPath = System.Windows.Shapes.Path;

[assembly: AssemblyTitle("AI 额度桌面小组件 安装向导")]
[assembly: AssemblyDescription("AI Quota Desktop Widget Setup & Uninstaller")]
[assembly: AssemblyCompany("AIQuota")]
[assembly: AssemblyProduct("AI 额度桌面小组件")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace AIQuota.Setup
{
    static class Win32
    {
        public const uint WM_CLOSE = 0x0010;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    }

    class InstallOptions
    {
        public string InstallDir;
        public bool AutoStart = true;
        public bool DesktopShortcut = true;
        public bool StartMenuShortcut = true;
        public bool LaunchNow = true;
        public bool PortableTest = false;
    }

    static class Engine
    {
        public const string AppId = "AIQuotaWidget";
        public const string DisplayName = "AI 额度桌面小组件";
        public const string AppVersion = "1.0.0";
        public const string ShortcutFile = "AI 额度小组件.lnk";
        public const string RunRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string UninstallRegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AIQuotaWidget";
        public const string PayloadResource = "AIQuota.Setup.payload.zip";

        public static string GetExistingInstallDir()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallRegPath))
                {
                    if (k != null)
                    {
                        var loc = k.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(loc)) return loc.Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        public static string GetDefaultInstallDir()
        {
            string existing = GetExistingInstallDir();
            if (!string.IsNullOrEmpty(existing)) return existing;
            return IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppId);
        }

        public static void StopRunningWidget(string targetDir, bool portableTest)
        {
            if (portableTest) return;
            try
            {
                IntPtr hwnd = Win32.FindWindow(null, "AI 额度");
                if (hwnd != IntPtr.Zero)
                {
                    var sb = new StringBuilder(128);
                    Win32.GetClassName(hwnd, sb, sb.Capacity);
                    if (sb.ToString().StartsWith("HwndWrapper[", StringComparison.Ordinal))
                    {
                        Win32.SendMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        Thread.Sleep(350);
                    }
                }
            }
            catch { }

            try
            {
                string normTarget = string.IsNullOrEmpty(targetDir) ? null : IOPath.GetFullPath(targetDir).TrimEnd('\\') + "\\";
                foreach (var p in Process.GetProcessesByName("AIQuotaWidget"))
                {
                    try
                    {
                        if (p.Id == Process.GetCurrentProcess().Id) continue;
                        bool match = normTarget == null;
                        if (!match)
                        {
                            string mf = p.MainModule.FileName;
                            match = mf.StartsWith(normTarget, StringComparison.OrdinalIgnoreCase);
                        }
                        if (match)
                        {
                            try { p.CloseMainWindow(); } catch { }
                            if (!p.WaitForExit(500)) p.Kill();
                            p.WaitForExit(500);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static void Install(InstallOptions opt, Action<string> progress)
        {
            string dir = IOPath.GetFullPath(opt.InstallDir.Trim());
            if (progress != null) progress("正在检测运行中的小组件…");
            StopRunningWidget(dir, opt.PortableTest);

            if (progress != null) progress("正在释放程序文件…");
            Directory.CreateDirectory(dir);

            long totalBytes = 0;
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource))
            {
                if (res == null) throw new InvalidOperationException("安装包内置资源 payload.zip 缺失。");
                using (var zip = new ZipArchive(res, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        string rel = entry.FullName.Replace('/', '\\');
                        if (string.Equals(rel, "config.default.json", StringComparison.OrdinalIgnoreCase))
                        {
                            string cfgPath = IOPath.Combine(dir, "config.json");
                            if (!File.Exists(cfgPath))
                            {
                                ExtractEntry(entry, cfgPath);
                            }
                            totalBytes += entry.Length;
                            continue;
                        }
                        string dest = IOPath.Combine(dir, rel);
                        string parent = IOPath.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        ExtractEntry(entry, dest);
                        totalBytes += entry.Length;
                    }
                }
            }

            string selfExe = Process.GetCurrentProcess().MainModule.FileName;
            string uninstExe = IOPath.Combine(dir, "Uninstall.exe");
            if (!string.Equals(IOPath.GetFullPath(selfExe), IOPath.GetFullPath(uninstExe), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(selfExe, uninstExe, true);
                try { totalBytes += new FileInfo(uninstExe).Length; } catch { }
            }

            string mainExe = IOPath.Combine(dir, "AIQuotaWidget.exe");
            string iconFile = IOPath.Combine(dir, "app.ico");

            // 若目标机器缺失 exe（极端杀软拦截场景），自动调用系统内置 csc.exe 本地编译
            if (!File.Exists(mainExe))
            {
                if (progress != null) progress("正在使用系统 .NET 本地编译组件…");
                CompileLocally(dir);
            }

            if (!opt.PortableTest)
            {
                if (progress != null) progress("正在配置快捷方式与系统注册信息…");
                if (opt.DesktopShortcut)
                {
                    string desk = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFile);
                    CreateShortcut(desk, mainExe, dir, iconFile, "AI 额度桌面小组件 (Codex + Antigravity)");
                }
                if (opt.StartMenuShortcut)
                {
                    string sm = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutFile);
                    CreateShortcut(sm, mainExe, dir, iconFile, "AI 额度桌面小组件 (Codex + Antigravity)");
                }

                try
                {
                    using (var rk = Registry.CurrentUser.CreateSubKey(RunRegPath))
                    {
                        if (rk != null)
                        {
                            if (opt.AutoStart)
                                rk.SetValue(AppId, "\"" + mainExe + "\"");
                            else
                            {
                                var cur = rk.GetValue(AppId) as string;
                                if (cur != null && cur.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0)
                                    rk.DeleteValue(AppId, false);
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    using (var uk = Registry.CurrentUser.CreateSubKey(UninstallRegPath))
                    {
                        if (uk != null)
                        {
                            uk.SetValue("DisplayName", DisplayName);
                            uk.SetValue("DisplayVersion", AppVersion);
                            uk.SetValue("Publisher", "AIQuota");
                            uk.SetValue("DisplayIcon", File.Exists(iconFile) ? iconFile : mainExe);
                            uk.SetValue("InstallLocation", dir);
                            uk.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                            uk.SetValue("UninstallString", "\"" + uninstExe + "\" --uninstall");
                            uk.SetValue("QuietUninstallString", "\"" + uninstExe + "\" --uninstall --silent");
                            uk.SetValue("NoModify", 1, RegistryValueKind.DWord);
                            uk.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                            uk.SetValue("EstimatedSize", (int)Math.Max(120, totalBytes / 1024), RegistryValueKind.DWord);
                        }
                    }
                }
                catch { }
            }

            if (opt.LaunchNow && !opt.PortableTest && File.Exists(mainExe))
            {
                if (progress != null) progress("正在启动 AI 额度桌面小组件…");
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = mainExe,
                        WorkingDirectory = dir,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        static void ExtractEntry(ZipArchiveEntry entry, string destPath)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    using (var src = entry.Open())
                    using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        src.CopyTo(fs);
                    }
                    return;
                }
                catch
                {
                    if (attempt == 3) throw;
                    Thread.Sleep(200);
                }
            }
        }

        static void CompileLocally(string dir)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string fw = IOPath.Combine(win, @"Microsoft.NET\Framework64\v4.0.30319");
            if (!File.Exists(IOPath.Combine(fw, "csc.exe")))
                fw = IOPath.Combine(win, @"Microsoft.NET\Framework\v4.0.30319");
            string csc = IOPath.Combine(fw, "csc.exe");
            string cs = IOPath.Combine(dir, "AIQuotaWidget.cs");
            if (!File.Exists(csc) || !File.Exists(cs)) return;

            var sb = new StringBuilder();
            sb.Append("/nologo /codepage:65001 /target:winexe /optimize+ ");
            if (File.Exists(IOPath.Combine(dir, "app.ico"))) sb.Append("/win32icon:app.ico ");
            sb.Append("/out:AIQuotaWidget.exe ");
            sb.AppendFormat("\"/r:{0}\" ", IOPath.Combine(fw, @"WPF\PresentationFramework.dll"));
            sb.AppendFormat("\"/r:{0}\" ", IOPath.Combine(fw, @"WPF\PresentationCore.dll"));
            sb.AppendFormat("\"/r:{0}\" ", IOPath.Combine(fw, @"WPF\WindowsBase.dll"));
            sb.AppendFormat("\"/r:{0}\" ", IOPath.Combine(fw, "System.Xaml.dll"));
            sb.AppendFormat("\"/r:{0}\" ", IOPath.Combine(fw, "System.Web.Extensions.dll"));
            sb.Append("/r:System.Core.dll AIQuotaWidget.cs");
            if (File.Exists(IOPath.Combine(dir, "AssemblyInfo.cs"))) sb.Append(" AssemblyInfo.cs");

            var p = Process.Start(new ProcessStartInfo
            {
                FileName = csc,
                Arguments = sb.ToString(),
                WorkingDirectory = dir,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p != null) p.WaitForExit(15000);
        }

        public static void CreateShortcut(string lnkPath, string targetPath, string workDir, string iconPath, string desc)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { targetPath });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
                if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                    st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { iconPath + ",0" });
                if (!string.IsNullOrEmpty(desc))
                    st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { desc });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            }
            catch { }
        }

        public static void Uninstall(string installDir, bool keepUserData, bool portableTest)
        {
            string dir = string.IsNullOrEmpty(installDir) ? GetDefaultInstallDir() : IOPath.GetFullPath(installDir.Trim());
            StopRunningWidget(dir, portableTest);

            if (!portableTest)
            {
                try
                {
                    using (var rk = Registry.CurrentUser.OpenSubKey(RunRegPath, true))
                    {
                        if (rk != null) rk.DeleteValue(AppId, false);
                    }
                }
                catch { }

                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRegPath, false); } catch { }

                try
                {
                    string desk = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFile);
                    if (File.Exists(desk)) File.Delete(desk);
                }
                catch { }

                try
                {
                    string sm = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutFile);
                    if (File.Exists(sm)) File.Delete(sm);
                }
                catch { }
            }

            if (!Directory.Exists(dir)) return;

            if (keepUserData)
            {
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "config.json", "antigravity_quota.json" };
                foreach (var f in Directory.GetFiles(dir))
                {
                    if (keep.Contains(IOPath.GetFileName(f))) continue;
                    TryDeleteFile(f);
                }
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    try { Directory.Delete(sub, true); } catch { }
                }
            }
            else
            {
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        Directory.Delete(dir, true);
                        break;
                    }
                    catch
                    {
                        Thread.Sleep(200);
                    }
                }
            }
        }

        static void TryDeleteFile(string path)
        {
            for (int i = 0; i < 4; i++)
            {
                try { File.Delete(path); return; }
                catch { Thread.Sleep(150); }
            }
        }
    }

    // ======================= 深色主题安装向导界面 =======================
    class CheckRow
    {
        public Border Root;
        public bool IsChecked;
        readonly Border box;
        readonly WPath mark;

        public CheckRow(string title, string subtitle, bool initial, Brush accent, Brush cardBg)
        {
            IsChecked = initial;
            box = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1.4), VerticalAlignment = VerticalAlignment.Center
            };
            mark = new WPath
            {
                Data = Geometry.Parse("M3.2,9.2 L7.0,13.0 L14.6,5.0"),
                Stroke = SetupWindow.Fr("#08110C"), StrokeThickness = 2.1,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Visibility = Visibility.Collapsed
            };
            box.Child = mark;

            var texts = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Brushes.White, FontWeight = FontWeights.Medium });
            if (!string.IsNullOrEmpty(subtitle))
                texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Foreground = SetupWindow.Fr("#7E828C"), Margin = new Thickness(0, 1, 0, 0) });

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(box);
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);

            Root = new Border
            {
                CornerRadius = new CornerRadius(10), Background = cardBg,
                BorderBrush = SetupWindow.Fr("#23262E"), BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 7),
                Cursor = Cursors.Hand, Child = row
            };
            Root.MouseEnter += (s, e) => Root.BorderBrush = SetupWindow.Fr("#353A46");
            Root.MouseLeave += (s, e) => Root.BorderBrush = SetupWindow.Fr("#23262E");
            Root.MouseLeftButtonDown += (s, e) => e.Handled = true;
            Root.MouseLeftButtonUp += (s, e) => { e.Handled = true; IsChecked = !IsChecked; UpdateVisual(accent); };
            UpdateVisual(accent);
        }

        void UpdateVisual(Brush accent)
        {
            if (IsChecked)
            {
                box.Background = accent;
                box.BorderBrush = accent;
                mark.Visibility = Visibility.Visible;
            }
            else
            {
                box.Background = SetupWindow.Fr("#15171C");
                box.BorderBrush = SetupWindow.Fr("#3E4350");
                mark.Visibility = Visibility.Collapsed;
            }
        }
    }

    class SetupWindow : Window
    {
        public static Brush Fr(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

        static readonly Brush Green = Fr("#2EE59D"), GreenHover = Fr("#4DF0AF");
        static readonly Brush Red = Fr("#FF5D5D"), RedHover = Fr("#FF7878");
        static readonly Brush Gray = Fr("#8C909B"), CardBg = Fr("#101216"), InnerBg = Fr("#17191F");
        const string AgPath = "M2.2,21 C5.2,19.6 7.2,13 9,7.6 C10,4.6 10.9,3 12,3 C13.1,3 14,4.6 15,7.6 C16.8,13 18.8,19.6 21.8,21";

        readonly bool uninstallMode;
        readonly string initialDir;
        readonly bool snapshot;
        StackPanel bodyHost;
        TextBox dirBox;
        CheckRow chkAuto, chkDesk, chkStart, chkLaunch, chkKeepData;
        TextBlock statusText;
        Border primaryBtn, cancelBtn;
        bool working, finished;

        public SetupWindow(bool uninstallMode, string initialDir, bool snapshot)
        {
            this.uninstallMode = uninstallMode;
            this.initialDir = initialDir;
            this.snapshot = snapshot;

            Title = uninstallMode ? "卸载 AI 额度桌面小组件" : "安装 AI 额度桌面小组件";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            UseLayoutRounding = true;

            var card = new Border
            {
                CornerRadius = new CornerRadius(22), Background = CardBg,
                BorderBrush = Fr("#282C35"), BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 22, 24, 20), Margin = new Thickness(14)
            };
            if (!snapshot)
                card.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.55 };
            card.MouseLeftButtonDown += (s, e) => { if (!snapshot) { try { DragMove(); } catch { } } };

            var root = new StackPanel();
            root.Children.Add(BuildHeader());
            bodyHost = new StackPanel();
            if (uninstallMode) BuildUninstallBody();
            else BuildInstallBody();
            root.Children.Add(bodyHost);

            card.Child = root;
            Content = card;
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && !working) Close(); };
        }

        UIElement BuildHeader()
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 18) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 迷你双环徽章
            var badge = new Grid { Width = 48, Height = 48 };
            badge.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = Fr("#181B22"), BorderBrush = Fr("#2B303C"), BorderThickness = new Thickness(1) });
            badge.Children.Add(new Ellipse { Width = 34, Height = 34, Stroke = Fr("#272B35"), StrokeThickness = 3.4 });
            var arcFig = new PathFigure { StartPoint = new Point(24, 7), IsClosed = false };
            arcFig.Segments.Add(new ArcSegment(new Point(8.4, 30.8), new Size(17, 17), 0, true, SweepDirection.Clockwise, true));
            var arcGeo = new PathGeometry(); arcGeo.Figures.Add(arcFig);
            badge.Children.Add(new WPath { Width = 48, Height = 48, Data = arcGeo, Stroke = uninstallMode ? Red : Green, StrokeThickness = 3.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            badge.Children.Add(new WPath { Data = Geometry.Parse(AgPath), Stretch = Stretch.Uniform, Width = 15, Height = 15, Stroke = Brushes.White, StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
            g.Children.Add(badge);

            var titleCol = new StackPanel { Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock
            {
                Text = uninstallMode ? "卸载 AI 额度桌面小组件" : "AI 额度桌面小组件",
                FontSize = 16.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center
            });

            bool isUpgrade = !uninstallMode && Engine.GetExistingInstallDir() != null;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(6), Background = isUpgrade ? Fr("#1E332B") : Fr("#1D2028"),
                Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            };
            pill.Child = new TextBlock
            {
                Text = uninstallMode ? "移除" : (isUpgrade ? "覆盖更新 v1.0" : "v1.0 安装包"),
                FontSize = 11, Foreground = isUpgrade ? Green : Gray, FontWeight = FontWeights.SemiBold
            };
            titleRow.Children.Add(pill);
            titleCol.Children.Add(titleRow);
            titleCol.Children.Add(new TextBlock
            {
                Text = "Codex + Antigravity 实时用量悬浮窗 · 免依赖原生运行",
                FontSize = 12, Foreground = Gray, Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(titleCol, 1);
            g.Children.Add(titleCol);

            var closeBtn = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top
            };
            var xTxt = new TextBlock { Text = "✕", FontSize = 13, Foreground = Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            closeBtn.Child = xTxt;
            closeBtn.MouseEnter += (s, e) => { closeBtn.Background = Fr("#23262F"); xTxt.Foreground = Brushes.White; };
            closeBtn.MouseLeave += (s, e) => { closeBtn.Background = Brushes.Transparent; xTxt.Foreground = Gray; };
            closeBtn.MouseLeftButtonDown += (s, e) => e.Handled = true;
            closeBtn.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (!working) Close(); };
            Grid.SetColumn(closeBtn, 2);
            g.Children.Add(closeBtn);
            return g;
        }

        void BuildInstallBody()
        {
            bodyHost.Children.Clear();
            bodyHost.Children.Add(new TextBlock { Text = "安装目录（免管理员权限）", FontSize = 12, Foreground = Gray, Margin = new Thickness(0, 0, 0, 6) });

            var pathGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            dirBox = new TextBox
            {
                Text = initialDir, FontSize = 12.5, Foreground = Brushes.White, CaretBrush = Brushes.White,
                Background = InnerBg, BorderBrush = Fr("#2B2F3A"), BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7), VerticalContentAlignment = VerticalAlignment.Center
            };
            pathGrid.Children.Add(dirBox);

            var browseBtn = MakeBtn("浏览…", "#1E2129", "#2B2F3A", Brushes.White, new Thickness(12, 7, 12, 7));
            browseBtn.Margin = new Thickness(8, 0, 0, 0);
            browseBtn.MouseLeftButtonUp += (s, e) =>
            {
                if (working) return;
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "选择 AI 额度桌面小组件 安装位置";
                    dlg.SelectedPath = dirBox.Text;
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                    {
                        string p = dlg.SelectedPath.Trim();
                        if (!p.EndsWith("AIQuotaWidget", StringComparison.OrdinalIgnoreCase))
                            p = IOPath.Combine(p, "AIQuotaWidget");
                        dirBox.Text = p;
                    }
                }
            };
            Grid.SetColumn(browseBtn, 1);
            pathGrid.Children.Add(browseBtn);
            bodyHost.Children.Add(pathGrid);

            chkAuto = new CheckRow("开机自动启动", "登录 Windows 时自动在屏幕顶部显示小组件", true, Green, InnerBg);
            chkDesk = new CheckRow("创建桌面快捷方式", "在桌面生成「AI 额度小组件」启动图标", true, Green, InnerBg);
            chkStart = new CheckRow("添加到开始菜单与系统应用列表", "支持从开始菜单搜索，或从系统「设置 → 应用」随时卸载", true, Green, InnerBg);
            chkLaunch = new CheckRow("安装完成后立即启动", "完成部署后立刻打开悬浮条", true, Green, InnerBg);

            bodyHost.Children.Add(chkAuto.Root);
            bodyHost.Children.Add(chkDesk.Root);
            bodyHost.Children.Add(chkStart.Root);
            bodyHost.Children.Add(chkLaunch.Root);

            bodyHost.Children.Add(BuildFooter("立即安装", Green, GreenHover, Fr("#07130D"), DoInstall));
        }

        void BuildUninstallBody()
        {
            bodyHost.Children.Clear();
            var info = new Border
            {
                CornerRadius = new CornerRadius(12), Background = InnerBg, BorderBrush = Fr("#262A34"),
                BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 12)
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "即将从当前电脑移除以下安装内容：", FontSize = 12.5, Foreground = Brushes.White, FontWeight = FontWeights.Medium });
            sp.Children.Add(new TextBlock { Text = initialDir, FontSize = 12, Foreground = Gray, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            info.Child = sp;
            bodyHost.Children.Add(info);

            chkKeepData = new CheckRow("保留个人设置与额度校准数据", "保留 config.json 与 antigravity_quota.json 以便日后重装", false, Green, InnerBg);
            bodyHost.Children.Add(chkKeepData.Root);

            bodyHost.Children.Add(BuildFooter("确认卸载", Red, RedHover, Brushes.White, DoUninstall));
        }

        UIElement BuildFooter(string actionText, Brush mainBg, Brush hoverBg, Brush mainFg, Action onPrimary)
        {
            var g = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            statusText = new TextBlock { Text = "系统环境已就绪 (.NET Framework 4.x)", FontSize = 12, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            g.Children.Add(statusText);

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            cancelBtn = MakeBtn("取消", "#1D2028", "#2B2F3A", Brushes.White, new Thickness(16, 8, 16, 8));
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            cancelBtn.MouseLeftButtonUp += (s, e) => { if (!working) Close(); };

            primaryBtn = new Border
            {
                CornerRadius = new CornerRadius(10), Background = mainBg,
                Padding = new Thickness(20, 8, 20, 8), Cursor = Cursors.Hand
            };
            var primaryLbl = new TextBlock { Text = actionText, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = mainFg };
            primaryBtn.Child = primaryLbl;
            primaryBtn.MouseEnter += (s, e) => { if (!working) primaryBtn.Background = hoverBg; };
            primaryBtn.MouseLeave += (s, e) => { if (!working) primaryBtn.Background = mainBg; };
            primaryBtn.MouseLeftButtonDown += (s, e) => e.Handled = true;
            primaryBtn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (working) return;
                if (finished) { Close(); return; }
                onPrimary();
            };

            btns.Children.Add(cancelBtn);
            btns.Children.Add(primaryBtn);
            Grid.SetColumn(btns, 1);
            g.Children.Add(btns);
            return g;
        }

        static Border MakeBtn(string text, string bg, string border, Brush fg, Thickness pad)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(10), Background = Fr(bg),
                BorderBrush = Fr(border), BorderThickness = new Thickness(1),
                Padding = pad, Cursor = Cursors.Hand
            };
            b.Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = fg, FontWeight = FontWeights.Medium };
            b.MouseEnter += (s, e) => b.Background = Fr("#272B36");
            b.MouseLeave += (s, e) => b.Background = Fr(bg);
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            return b;
        }

        void DoInstall()
        {
            string target = dirBox.Text.Trim();
            if (string.IsNullOrEmpty(target))
            {
                statusText.Text = "请选择有效的安装路径";
                statusText.Foreground = Red;
                return;
            }
            var opt = new InstallOptions
            {
                InstallDir = target,
                AutoStart = chkAuto.IsChecked,
                DesktopShortcut = chkDesk.IsChecked,
                StartMenuShortcut = chkStart.IsChecked,
                LaunchNow = chkLaunch.IsChecked
            };
            working = true;
            primaryBtn.Opacity = 0.6;
            Task.Run(() =>
            {
                try
                {
                    Engine.Install(opt, msg => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        statusText.Foreground = Gray;
                        statusText.Text = msg;
                    })));
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        working = false;
                        finished = true;
                        primaryBtn.Opacity = 1.0;
                        ((TextBlock)primaryBtn.Child).Text = "完成";
                        cancelBtn.Visibility = Visibility.Collapsed;
                        statusText.Foreground = Green;
                        statusText.Text = "✓ 安装成功！右键悬浮条可随时调整设置";
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        working = false;
                        primaryBtn.Opacity = 1.0;
                        statusText.Foreground = Red;
                        statusText.Text = "安装失败: " + ex.Message;
                    }));
                }
            });
        }

        void DoUninstall()
        {
            bool keep = chkKeepData.IsChecked;
            working = true;
            primaryBtn.Opacity = 0.6;
            statusText.Text = "正在移除组件与快捷方式…";
            Task.Run(() =>
            {
                try
                {
                    Engine.Uninstall(initialDir, keep, false);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        working = false;
                        finished = true;
                        primaryBtn.Opacity = 1.0;
                        primaryBtn.Background = Fr("#272B36");
                        ((TextBlock)primaryBtn.Child).Text = "关闭";
                        cancelBtn.Visibility = Visibility.Collapsed;
                        statusText.Foreground = Green;
                        statusText.Text = "✓ 已成功从本机卸载";
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        working = false;
                        primaryBtn.Opacity = 1.0;
                        statusText.Foreground = Red;
                        statusText.Text = "卸载出错: " + ex.Message;
                    }));
                }
            });
        }

        public void RenderTo(string file)
        {
            var fe = (FrameworkElement)Content;
            Content = null;
            var host = new Grid { Background = Fr("#E9E9EE") };
            host.Children.Add(fe);
            host.Measure(new Size(Width, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, Width, host.DesiredSize.Height));
            host.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)(Width * 2), (int)(host.DesiredSize.Height * 2), 192, 192, PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string selfExe = Process.GetCurrentProcess().MainModule.FileName;
            string selfName = IOPath.GetFileNameWithoutExtension(selfExe);
            string selfDir = IOPath.GetDirectoryName(selfExe);

            bool uninstall = selfName.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase);
            bool silent = false, fromTemp = false, portableTest = false, keepData = false;
            bool noLaunch = false, noAutoStart = false, noShortcuts = false;
            string customDir = null, snapshot = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "/U", StringComparison.OrdinalIgnoreCase)) uninstall = true;
                else if (string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase)) silent = true;
                else if (string.Equals(a, "--from-temp", StringComparison.OrdinalIgnoreCase)) fromTemp = true;
                else if (string.Equals(a, "--portable-test", StringComparison.OrdinalIgnoreCase)) portableTest = true;
                else if (string.Equals(a, "--keep-data", StringComparison.OrdinalIgnoreCase)) keepData = true;
                else if (string.Equals(a, "--no-launch", StringComparison.OrdinalIgnoreCase)) noLaunch = true;
                else if (string.Equals(a, "--no-autostart", StringComparison.OrdinalIgnoreCase)) noAutoStart = true;
                else if (string.Equals(a, "--no-shortcuts", StringComparison.OrdinalIgnoreCase)) noShortcuts = true;
                else if (string.Equals(a, "--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) customDir = args[++i];
                else if (string.Equals(a, "--snapshot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) snapshot = args[++i];
            }

            string targetDir = customDir;
            if (string.IsNullOrEmpty(targetDir))
            {
                if (uninstall && (File.Exists(IOPath.Combine(selfDir, "AIQuotaWidget.exe")) || File.Exists(IOPath.Combine(selfDir, "AIQuotaWidget.cs"))))
                    targetDir = selfDir;
                else
                    targetDir = Engine.GetDefaultInstallDir();
            }
            targetDir = IOPath.GetFullPath(targetDir);

            if (snapshot != null)
            {
                var snapApp = new Application();
                var snapWin = new SetupWindow(uninstall, targetDir, true);
                snapWin.RenderTo(snapshot);
                return 0;
            }

            // 如果从安装目录内直接运行 Uninstall.exe，先复制到 %TEMP% 再执行，避免锁死自身目录无法删除
            if (uninstall && !fromTemp)
            {
                string normSelf = IOPath.GetFullPath(selfExe);
                string normDir = targetDir.TrimEnd('\\') + "\\";
                if (normSelf.StartsWith(normDir, StringComparison.OrdinalIgnoreCase))
                {
                    string tmpExe = IOPath.Combine(IOPath.GetTempPath(), "AIQuotaWidget-Uninst-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                    File.Copy(selfExe, tmpExe, true);
                    var sb = new StringBuilder("--uninstall --from-temp ");
                    sb.AppendFormat("\"--dir\" \"{0}\"", targetDir);
                    if (silent) sb.Append(" --silent");
                    if (portableTest) sb.Append(" --portable-test");
                    if (keepData) sb.Append(" --keep-data");
                    Process.Start(new ProcessStartInfo { FileName = tmpExe, Arguments = sb.ToString(), UseShellExecute = false });
                    return 0;
                }
            }

            try
            {
                if (silent)
                {
                    if (uninstall)
                    {
                        Engine.Uninstall(targetDir, keepData, portableTest);
                    }
                    else
                    {
                        Engine.Install(new InstallOptions
                        {
                            InstallDir = targetDir,
                            AutoStart = !noAutoStart,
                            DesktopShortcut = !noShortcuts,
                            StartMenuShortcut = !noShortcuts,
                            LaunchNow = !noLaunch,
                            PortableTest = portableTest
                        }, null);
                    }
                    return 0;
                }

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var win = new SetupWindow(uninstall, targetDir, false);
                app.Run(win);
                return 0;
            }
            finally
            {
                if (fromTemp)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = "/c ping 127.0.0.1 -n 2 > nul & del /f /q \"" + selfExe + "\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                    }
                    catch { }
                }
            }
        }
    }
}
