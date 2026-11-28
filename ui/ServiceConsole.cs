using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IiifConsole
{
    internal class Service
    {
        public string Key;
        public string Name;
        public string SubName;
        public int Port;
        public string Url;
        public string StartArgs;
        public string[] ExpectedProcesses = new string[0];
        public Label StatusLabel;
        public LinkLabel UrlLink;
        public Button StartButton;
        public Button StopButton;
        public DateTime StartRequestedAt = DateTime.MinValue;
    }

    internal class MainForm : Form
    {
        private readonly string toolsDir;
        private readonly string pwshPath;
        private readonly List<Service> services = new List<Service>();
        private readonly Timer pollTimer = new Timer();
        private readonly string logDir;
        private readonly object logLock = new object();
        private readonly string imageSourceConfPath;
        private Label imageSourceLabel;
        private Button selfCheckButton;
        private float uiScale = 1f;

        private float UiScale
        {
            get { return uiScale; }
        }

        private int S(float value)
        {
            return (int)Math.Round(value * uiScale);
        }

        public MainForm()
        {
            toolsDir = ResolveToolsDir();
            logDir = Path.Combine(toolsDir, "logs");
            try { Directory.CreateDirectory(logDir); } catch { }

            pwshPath = GetPwshPath();

            Text = "IIIF 服务控制台 v1.0.0";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // 图像服务与 SAS 都需要图片/Manifest 时，图源统一由图像服务发布
            imageSourceConfPath = Path.Combine(toolsDir, "data", "image-source.conf");

            services.Add(new Service
            {
                Key = "image",
                Name = "IIIF 图像服务",
                SubName = "Image API 2.0 Level 2 + Manifest · :8182",
                Port = 8182,
                Url = "http://localhost:8182/",
                ExpectedProcesses = new string[] { "python", "pythonw", "py" },
                StartArgs = BuildStartArgs("image", toolsDir)
            });
            services.Add(new Service
            {
                Key = "sas",
                Name = "SAS 标注服务",
                SubName = "SimpleAnnotationServer · :8888",
                Port = 8888,
                Url = "http://localhost:8888/index.html",
                ExpectedProcesses = new string[] { "java", "javaw" },
                StartArgs = BuildStartArgs("sas", toolsDir)
            });

            pollTimer.Interval = 1000;
            pollTimer.Tick += delegate { RefreshStatus(); };
            // 首次刷新放到窗口显示后（OnShown），避免在构造期做任何可能阻塞的事
        }

        private static string BuildStartArgs(string key, string toolsDir)
        {
            string sas = Path.Combine(toolsDir, "start-sas.ps1");
            string image = Path.Combine(toolsDir, "start-image.ps1");
            if (key == "sas")
            {
                return "-NoProfile -ExecutionPolicy Bypass -File \"" + sas + "\"";
            }
            if (key == "image")
            {
                return "-NoProfile -ExecutionPolicy Bypass -File \"" + image + "\"";
            }
            throw new ArgumentException("unknown service: " + key);
        }

        private static int PortForKey(string key)
        {
            if (key == "image") return 8182;
            if (key == "sas") return 8888;
            return 0;
        }

        private void StartService(Service svc)
        {
            ProcessStartInfo psi = new ProcessStartInfo(pwshPath, svc.StartArgs);
            psi.WorkingDirectory = toolsDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            Process proc = new Process();
            proc.StartInfo = psi;
            proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs a) { if (a.Data != null) AppendLog(svc.Key, a.Data); };
            proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs a) { if (a.Data != null) AppendLog(svc.Key, a.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            svc.StartRequestedAt = DateTime.Now;
            svc.StatusLabel.Text = "启动中…";
            svc.StatusLabel.ForeColor = Color.DarkOrange;
        }

        private void BuildLayout()
        {
            SuspendLayout();

            // 图源选择（图像服务按文件夹发布，SAS 直接加载它生成的 Manifest）
            Label imageCap = new Label();
            imageCap.Text = "图像服务图源：";
            imageCap.AutoSize = false;
            imageCap.TextAlign = ContentAlignment.MiddleLeft;
            imageCap.Location = new Point(S(12), S(14));
            imageCap.Size = new Size(S(122), S(24));
            Controls.Add(imageCap);

            Button pickImage = new Button();
            pickImage.Text = "选择文件夹…";
            pickImage.Location = new Point(S(142), S(10));
            pickImage.Size = new Size(S(120), S(32));
            pickImage.Click += PickImageSourceClicked;
            Controls.Add(pickImage);

            imageSourceLabel = new Label();
            imageSourceLabel.Location = new Point(S(274), S(14));
            imageSourceLabel.Size = new Size(S(414), S(24));
            imageSourceLabel.AutoEllipsis = true;
            imageSourceLabel.TextAlign = ContentAlignment.MiddleLeft;
            imageSourceLabel.Text = DescribeImageSource();
            Controls.Add(imageSourceLabel);

            // 服务行：名字 + 副标题两行，按钮右侧垂直居中
            int rowY = 54;
            Font subFont = new Font("Microsoft YaHei UI", 8F);
            Font nameFont = new Font("Microsoft YaHei UI", 10F);
            foreach (Service svc in services)
            {
                Label name = new Label();
                name.Text = svc.Name;
                name.AutoSize = false;
                name.Font = nameFont;
                name.TextAlign = ContentAlignment.MiddleLeft;
                name.Location = new Point(S(12), S(rowY));
                name.Size = new Size(S(412), S(26));
                Controls.Add(name);

                Label sub = new Label();
                sub.Text = svc.SubName;
                sub.AutoSize = false;
                sub.ForeColor = Color.DimGray;
                sub.Font = subFont;
                sub.TextAlign = ContentAlignment.MiddleLeft;
                sub.Location = new Point(S(12), S(rowY + 26));
                sub.Size = new Size(S(352), S(20));
                Controls.Add(sub);

                svc.StartButton = MakeButton("启动", S(436), S(rowY + 4), S(100), S(36), svc, StartClicked);
                svc.StopButton = MakeButton("停止", S(548), S(rowY + 4), S(100), S(36), svc, StopClicked);

                rowY += 66;
            }

            // 运行状态
            GroupBox statusBox = new GroupBox();
            statusBox.Text = "运行状态（点击链接在默认浏览器打开）";
            statusBox.Location = new Point(S(12), S(rowY + 8));
            statusBox.Size = new Size(S(676), S(132));
            Controls.Add(statusBox);

            int statusY = 30;
            foreach (Service svc in services)
            {
                Label status = new Label();
                status.Location = new Point(S(12), S(statusY));
                status.Size = new Size(S(110), S(24));
                status.Text = "检测中…";
                status.TextAlign = ContentAlignment.MiddleLeft;
                statusBox.Controls.Add(status);

                LinkLabel link = new LinkLabel();
                link.Location = new Point(S(128), S(statusY));
                link.Size = new Size(S(536), S(24));
                link.Text = svc.Url;
                link.Tag = svc;
                link.LinkClicked += LinkClicked;
                statusBox.Controls.Add(link);

                svc.StatusLabel = status;
                svc.UrlLink = link;
                statusY += 34;
            }

            Label credit = new Label();
            credit.Text = "Design by FuJiawei";
            credit.AutoSize = false;
            credit.TextAlign = ContentAlignment.BottomRight;
            credit.ForeColor = Color.Gray;
            credit.Font = new Font("Microsoft YaHei UI", 8F);
            credit.Location = new Point(S(420), S(rowY + 146));
            credit.Size = new Size(S(268), S(20));
            Controls.Add(credit);

            selfCheckButton = new Button();
            selfCheckButton.Text = "运行环境自检";
            selfCheckButton.Location = new Point(S(12), S(rowY + 144));
            selfCheckButton.Size = new Size(S(150), S(30));
            selfCheckButton.Click += SelfCheckClicked;
            Controls.Add(selfCheckButton);

            ResumeLayout(true);
        }

        private Button MakeButton(string text, int x, int y, int w, int h, Service svc, EventHandler handler)
        {
            Button button = new Button();
            button.Text = text;
            button.Location = new Point(x, y);
            button.Size = new Size(w, h);
            button.Tag = svc;
            button.Click += handler;
            Controls.Add(button);
            return button;
        }

        private string DescribeImageSource()
        {
            try
            {
                if (File.Exists(imageSourceConfPath))
                {
                    string folder = File.ReadAllText(imageSourceConfPath, Encoding.UTF8).Trim();
                    if (folder.Length > 0)
                    {
                        string display = folder;
                        try
                        {
                            string full = Path.GetFullPath(folder).TrimEnd('\\');
                            string root = Path.GetFullPath(toolsDir).TrimEnd('\\');
                            if (full.Length > root.Length &&
                                full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                            {
                                display = full.Substring(root.Length + 1);
                            }
                            else
                            {
                                display = Path.GetFileName(full);
                            }
                        }
                        catch
                        {
                        }
                        return "图源：" + display;
                    }
                }
            }
            catch { }
            return "默认：data\\demo-images（按文件夹发布）";
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" ||
                   ext == ".tif" || ext == ".tiff" || ext == ".webp";
        }

        private static int CountImages(string folder)
        {
            int count = 0;
            try
            {
                foreach (string file in Directory.GetFiles(folder))
                    if (IsImageFile(file)) count++;
                foreach (string dir in Directory.GetDirectories(folder))
                    foreach (string file in Directory.GetFiles(dir))
                        if (IsImageFile(file)) count++;
            }
            catch { }
            return count;
        }

        private void PickImageSourceClicked(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "选择图像服务的图源文件夹（子文件夹各成一个 Manifest）";
                fbd.ShowNewFolderButton = true;
                try
                {
                    if (File.Exists(imageSourceConfPath))
                    {
                        string current = File.ReadAllText(imageSourceConfPath, Encoding.UTF8).Trim();
                        if (current.Length > 0 && Directory.Exists(current)) fbd.SelectedPath = current;
                    }
                    else
                    {
                        string fallback = Path.Combine(toolsDir, "data", "demo-images");
                        if (Directory.Exists(fallback)) fbd.SelectedPath = fallback;
                    }
                }
                catch { }

                if (fbd.ShowDialog(this) != DialogResult.OK) return;

                string folder = fbd.SelectedPath;
                int count = CountImages(folder);
                if (count == 0)
                {
                    MessageBox.Show(this, "该文件夹（含一级子文件夹）里没有 jpg / png / tif / webp 图片。",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                DialogResult answer = MessageBox.Show(this,
                    "将把图像服务的图源切换为：\n" + folder + "\n\n共 " + count + " 张图片，" +
                    "重启服务后生效。是否继续？", "确认切换图源",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(imageSourceConfPath));
                    File.WriteAllText(imageSourceConfPath, folder, new UTF8Encoding(false));
                    imageSourceLabel.Text = "图源：" + folder;
                    string hint = PortOpen(8182)
                        ? "已保存。服务正在运行：点【停止】再【启动】后生效。"
                        : "已保存，下次点【启动】时生效。";
                    MessageBox.Show(this, hint, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "保存设置失败：" + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void StartClicked(object sender, EventArgs e)
        {
            Service svc = (Service)((Button)sender).Tag;
            AppendLog(svc.Key, "点击了【启动】");
            if (PortOpen(svc.Port))
            {
                List<int> owners = ListeningPids(svc.Port);
                int foreign = FindForeignOwner(svc, owners);
                if (foreign > 0 || owners.Count == 0)
                {
                    string who = owners.Count == 0 ? "另一个（受保护的）进程" : OwnerName(foreign) + "（PID " + foreign + "）";
                    MessageBox.Show(this, "端口 " + svc.Port + " 被 " + who +
                        " 占用，这不是本工作台的服务。\r\n请先关闭该程序，或告诉我更换端口。", "端口被占用",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                MessageBox.Show(this, svc.Name + " 已经在运行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                StartService(svc);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "启动失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopClicked(object sender, EventArgs e)
        {
            Service svc = (Service)((Button)sender).Tag;
            AppendLog(svc.Key, "点击了【停止】");

            List<int> pids = ListeningPids(svc.Port);
            if (pids.Count == 0)
            {
                if (PortOpen(svc.Port))
                {
                    MessageBox.Show(this, "端口 " + svc.Port + " 正在被另一个进程监听，但无法读取它的身份（可能以管理员权限运行）。\r\n为避免误杀，已取消停止操作。", "未执行",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                MessageBox.Show(this, svc.Name + " 当前没有在运行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int foreignOwner = FindForeignOwner(svc, pids);
            if (foreignOwner > 0)
            {
                MessageBox.Show(this, "端口 " + svc.Port + " 被 " + OwnerName(foreignOwner) + "（PID " + foreignOwner +
                    "）占用，这不是本工作台的服务，已取消停止操作。", "未执行",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<string> failures = new List<string>();
            KillPids(pids, failures);
            System.Threading.Thread.Sleep(400);

            // 进程可能刚好自己退出，或端口被新进程接管：再探测一次后补杀
            List<int> remaining = ListeningPids(svc.Port);
            if (remaining.Count > 0)
            {
                KillPids(remaining, failures);
                System.Threading.Thread.Sleep(400);
            }

            if (failures.Count > 0)
            {
                MessageBox.Show(this, "停止时遇到问题：\r\n" + string.Join("\r\n", failures.ToArray()),
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            svc.StartRequestedAt = DateTime.MinValue;
            svc.StatusLabel.Text = "停止中…";
            svc.StatusLabel.ForeColor = Color.DarkOrange;
            RefreshStatus();
        }

        /// <summary>结束给定进程；进程已经退出不算失败。</summary>
        private static void KillPids(List<int> pids, List<string> failures)
        {
            foreach (int pid in pids)
            {
                try
                {
                    Process p = Process.GetProcessById(pid);
                    if (p.HasExited) continue;
                    p.Kill();
                }
                catch (ArgumentException)
                {
                    // 进程已不存在（PID 过期），视为已停止
                }
                catch (InvalidOperationException)
                {
                    // 进程已退出
                }
                catch (Exception ex)
                {
                    failures.Add("进程 " + pid + "：" + ex.Message);
                }
            }
        }

        private void LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Service svc = (Service)((LinkLabel)sender).Tag;
            try
            {
                Process.Start(new ProcessStartInfo(svc.Url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开浏览器：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SelfCheckClicked(object sender, EventArgs e)
        {
            if (selfCheckButton != null) selfCheckButton.Enabled = false;
            System.Windows.Forms.Cursor oldCursor = Cursor;
            Cursor = Cursors.WaitCursor;
            Application.DoEvents();
            try
            {
                string report = SelfCheck.Run(toolsDir, pwshPath);
                Cursor = oldCursor;
                ShowSelfCheckDialog(report);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "运行环境自检失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = oldCursor;
                if (selfCheckButton != null) selfCheckButton.Enabled = true;
            }
        }

        private void ShowSelfCheckDialog(string report)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = "IIIF Service Workbench 运行环境自检";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowIcon = false;
                dialog.ClientSize = new Size(780, 560);
                dialog.Font = new Font("Microsoft YaHei UI", 9F);

                TextBox text = new TextBox();
                text.Multiline = true;
                text.ReadOnly = true;
                text.ScrollBars = ScrollBars.Both;
                text.WordWrap = false;
                text.Font = new Font("Consolas", 9F);
                text.BackColor = Color.White;
                text.HideSelection = false;
                text.Location = new Point(10, 10);
                text.Size = new Size(dialog.ClientSize.Width - 20, dialog.ClientSize.Height - 64);
                text.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                text.Text = report;
                text.SelectionStart = 0;
                text.SelectionLength = 0;

                Panel bottom = new Panel();
                bottom.Location = new Point(0, dialog.ClientSize.Height - 44);
                bottom.Size = new Size(dialog.ClientSize.Width, 44);
                bottom.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

                Button copy = new Button();
                copy.Text = "复制结果";
                copy.Size = new Size(90, 28);
                copy.Location = new Point(bottom.Width - 200, 8);
                copy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                copy.Click += delegate
                {
                    try
                    {
                        Clipboard.SetText(report);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, "复制失败：" + ex.Message, "提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };

                Button close = new Button();
                close.Text = "关闭";
                close.Size = new Size(90, 28);
                close.Location = new Point(bottom.Width - 100, 8);
                close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                close.DialogResult = DialogResult.OK;

                bottom.Controls.Add(copy);
                bottom.Controls.Add(close);
                dialog.Controls.Add(text);
                dialog.Controls.Add(bottom);
                dialog.AcceptButton = close;
                dialog.CancelButton = close;
                dialog.ShowDialog(this);
            }
        }

        private bool layoutReady;

        private void PrepareLayout()
        {
            if (layoutReady) return;
            layoutReady = true;

            // 用窗口的绘图上下文读取真实 DPI；并用字体实际行高做兜底
            // （某些环境下 DeviceDpi/DpiY 仍是 96，但字体已按缩放放大，二者取大值才不会挤）
            using (Graphics g = Graphics.FromHwnd(Handle))
            {
                float dpiFactor = Math.Max(1f, g.DpiY / 96f);
                float fontFactor = Math.Max(1f, Font.GetHeight(g) / 15.24f);
                uiScale = Math.Max(dpiFactor, fontFactor);
            }

            // 可选：tools\ui-scale.txt 里写一个数字（如 1.5）可强制界面缩放
            string scaleFile = Path.Combine(toolsDir, "ui-scale.txt");
            if (File.Exists(scaleFile))
            {
                float forced;
                string text = File.ReadAllText(scaleFile).Trim();
                if (float.TryParse(text, out forced) && forced >= 1f && forced <= 3f)
                {
                    uiScale = forced;
                }
            }
            else
            {
                // 仅供离屏测试：允许用环境变量强制缩放比例
                string envScale = Environment.GetEnvironmentVariable("IIIF_CONSOLE_SCALE");
                float parsed;
                if (!string.IsNullOrEmpty(envScale) && float.TryParse(envScale, out parsed) && parsed >= 1f)
                {
                    uiScale = parsed;
                }
            }

            ClientSize = new Size(S(700), S(54 + services.Count * 66 + 184));

            SuspendLayout();
            BuildLayout();
            ResumeLayout(true);

            // 尺寸在 OnLoad 才确定，这里显式居中到所在屏幕，避免先摆位后放大导致右下漂移
            StartPosition = FormStartPosition.Manual;
            CenterToScreen();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PrepareLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            RefreshStatus();
            pollTimer.Start();
        }

        /// <summary>返回占用端口的“非本服务”进程 PID；没有则为 0。</summary>
        private static int FindForeignOwner(Service svc, List<int> pids)
        {
            foreach (int pid in pids)
            {
                string name;
                try { name = Process.GetProcessById(pid).ProcessName; }
                catch
                {
                    // 读不到进程名（受保护/提权进程）时不能认定是本服务，
                    // 否则会误判“运行中”并尝试结束别人的进程。
                    return pid;
                }

                bool expected = false;
                foreach (string n in svc.ExpectedProcesses)
                {
                    if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { expected = true; break; }
                }
                if (!expected) return pid;
            }
            return 0;
        }

        private static string OwnerName(int pid)
        {
            try { return Process.GetProcessById(pid).ProcessName; }
            catch { return "未知进程"; }
        }

        private void RefreshStatus()
        {
            if (IsDisposed) return;
            try
            {
                foreach (Service svc in services)
                {
                    bool up = PortOpen(svc.Port);
                    if (up)
                    {
                        List<int> owners = ListeningPids(svc.Port);
                        int foreign = FindForeignOwner(svc, owners);
                        if (foreign > 0 || owners.Count == 0)
                        {
                            svc.StatusLabel.Text = owners.Count == 0 ? "端口被未知进程占用" : "端口被 " + OwnerName(foreign) + " 占用";
                            svc.StatusLabel.ForeColor = Color.Firebrick;
                            svc.UrlLink.Enabled = false;
                            svc.UrlLink.LinkColor = Color.Gray;
                            svc.UrlLink.Text = svc.Url;
                            svc.StartRequestedAt = DateTime.MinValue;
                        }
                        else
                        {
                            svc.StatusLabel.Text = "运行中";
                            svc.StatusLabel.ForeColor = Color.ForestGreen;
                            svc.UrlLink.Enabled = true;
                            svc.UrlLink.LinkColor = Color.RoyalBlue;
                            svc.UrlLink.Text = svc.Url + "  （点击打开）";
                        }
                    }
                    else if (svc.StartRequestedAt != DateTime.MinValue && (DateTime.Now - svc.StartRequestedAt).TotalSeconds < 15)
                    {
                        svc.StatusLabel.Text = "启动中…";
                        svc.StatusLabel.ForeColor = Color.DarkOrange;
                        svc.UrlLink.Enabled = false;
                        svc.UrlLink.Text = svc.Url;
                    }
                    else
                    {
                        if (svc.StartRequestedAt != DateTime.MinValue)
                        {
                            svc.StatusLabel.Text = "未运行（启动失败）";
                            svc.StatusLabel.ForeColor = Color.Firebrick;
                        }
                        else
                        {
                            svc.StatusLabel.Text = "未运行";
                            svc.StatusLabel.ForeColor = Color.Gray;
                        }
                        svc.UrlLink.Enabled = false;
                        svc.UrlLink.LinkColor = Color.Gray;
                        svc.UrlLink.Text = svc.Url;
                        svc.StartRequestedAt = DateTime.MinValue;
                    }
                }
            }
            catch (Exception)
            {
                // 状态刷新失败时静默忽略，避免在桌面等位置产生日志文件
            }
        }

        private void AppendLog(string key, string line)
        {
            try
            {
                lock (logLock)
                {
                    File.AppendAllText(Path.Combine(logDir, "ui-" + key + ".log"),
                        DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        private static string ResolveToolsDir()
        {
            // 从 exe 所在目录向上找最多三层，兼容 exe 被放在子目录里的情况。
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string dir = exeDir;
            for (int i = 0; i < 3; i++)
            {
                if (File.Exists(Path.Combine(dir, "start-image.ps1"))) return dir;
                string parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir) break;
                dir = parent.TrimEnd('\\');
            }
            return exeDir;
        }

        private static string GetPwshPath()
        {
            string onPath = SelfCheck.FindOnPath("pwsh.exe");
            if (!string.IsNullOrEmpty(onPath)) return onPath;

            string pwsh7 = @"C:\Program Files\PowerShell\7\pwsh.exe";
            if (File.Exists(pwsh7)) return pwsh7;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        }

        internal static bool PortOpen(int port)
        {
            // 直接读本地 TCP 监听表，微秒级完成；不用 TcpClient 连接（连接可能被安全软件
            // 或过滤驱动拖到数百毫秒，连续轮询会把 UI 线程占满，表现就是界面点不动）。
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (size <= 0) return false;

                buffer = Marshal.AllocHGlobal(size);
                uint result = GetExtendedTcpTable(buffer, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (result != 0) return false;

                int rows = Marshal.ReadInt32(buffer);
                long rowPtr = buffer.ToInt64() + 4;
                int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                for (int i = 0; i < rows; i++)
                {
                    MIB_TCPROW_OWNER_PID row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(
                        new IntPtr(rowPtr + i * rowSize), typeof(MIB_TCPROW_OWNER_PID));
                    int localPort = ((int)(row.localPort & 0xFF) << 8) | (int)((row.localPort >> 8) & 0xFF);
                    if (localPort == port) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, int reserved);

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

        private static List<int> ListeningPids(int port)
        {
            List<int> pids = new List<int>();
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (size <= 0) return pids;

                buffer = Marshal.AllocHGlobal(size);
                uint result = GetExtendedTcpTable(buffer, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (result != 0) return pids;

                int rows = Marshal.ReadInt32(buffer);
                long rowPtr = buffer.ToInt64() + 4;
                int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                for (int i = 0; i < rows; i++)
                {
                    MIB_TCPROW_OWNER_PID row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(
                        new IntPtr(rowPtr + i * rowSize), typeof(MIB_TCPROW_OWNER_PID));
                    int localPort = ((int)(row.localPort & 0xFF) << 8) | (int)((row.localPort >> 8) & 0xFF);
                    if (localPort == port && row.owningPid > 0 && !pids.Contains((int)row.owningPid))
                    {
                        pids.Add((int)row.owningPid);
                    }
                }
            }
            catch { }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
            return pids;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--test")
            {
                RunTest(args);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                try
                {
                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "ui-error.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + e.Exception + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            };
            Application.Run(new MainForm());
        }

        private static void RunTest(string[] args)
        {
            string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-test.log");
            try
            {
                File.WriteAllText(report, "", Encoding.UTF8);
                Log(report, "args: " + string.Join(" ", args));
                string toolsDir = ResolveToolsDir();
                if (args.Length < 2)
                {
                    Log(report, "用法: --test status|start|stop|selfcheck|dpi|layout|shot");
                }
                else if (args[1] == "version")
                {
                    Log(report, "stamp=" + IiifConsole.BuildInfo.Stamp);
                }
                else if (args[1] == "status")
                {
                    Log(report, "status: 8182=" + PortOpen(8182) + " 8888=" + PortOpen(8888));
                }
                else if (args[1] == "selfcheck")
                {
                    string checkReport = SelfCheck.Run(toolsDir, GetPwshPath());
                    Log(report, checkReport);
                    try
                    {
                        string checkLogDir = Path.Combine(toolsDir, "logs");
                        Directory.CreateDirectory(checkLogDir);
                        File.WriteAllText(Path.Combine(checkLogDir, "selfcheck.log"), checkReport, Encoding.UTF8);
                    }
                    catch
                    {
                    }
                }
                else if (args[1] == "dpi")
                {
                    using (MainForm f = new MainForm())
                    {
                        f.PrepareLayout();
                        Log(report, "DeviceDpi=" + f.DeviceDpi + " scale=" + f.UiScale.ToString("0.##") + " client=" + f.ClientSize);
                        if (f.services.Count > 0)
                        {
                            Button b = f.services[0].StartButton;
                            Log(report, "button=" + b.Bounds + " name=" + f.services[0].StatusLabel.Bounds);
                        }
                    }
                }
                else if (args[1] == "layout")
                {
                    using (MainForm f = new MainForm())
                    {
                        f.PrepareLayout();
                        f.PerformLayout();
                        Log(report, "client=" + f.ClientSize + " scale=" + f.UiScale.ToString("0.##"));
                        foreach (Control c in f.Controls)
                        {
                            Log(report, c.GetType().Name + " '" + c.Text + "' " + c.Bounds);
                        }
                    }
                }
                else if (args[1] == "shot")
                {
                    string png = Path.Combine(toolsDir, "ui", "preview.png");
                    using (MainForm f = new MainForm())
                    {
                        f.PrepareLayout();
                        f.PerformLayout();
                        EnsureHandles(f);
                        f.Show();
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(400);
                        Application.DoEvents();
                        using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    Log(report, "shot: " + png);
                }
                else if (args[1] == "stop" && args.Length >= 3)
                {
                    int port = PortForKey(args[2]);
                    Log(report, "stop " + args[2] + " begin port=" + port);
                    List<int> pids = ListeningPids(port);
                    Log(report, "pids=" + string.Join(",", pids.ConvertAll(x => x.ToString()).ToArray()));
                    foreach (int pid in pids)
                    {
                        string pname;
                        try { pname = Process.GetProcessById(pid).ProcessName; }
                        catch { Log(report, "skip " + pid + " (cannot read name, treated as foreign)"); continue; }

                        if (!(pname.Equals("java", StringComparison.OrdinalIgnoreCase) ||
                              pname.Equals("javaw", StringComparison.OrdinalIgnoreCase) ||
                              pname.Equals("python", StringComparison.OrdinalIgnoreCase) ||
                              pname.Equals("pythonw", StringComparison.OrdinalIgnoreCase)))
                        {
                            Log(report, "skip " + pid + " (" + pname + " is foreign)");
                            continue;
                        }

                        try { Process.GetProcessById(pid).Kill(); Log(report, "killed " + pid); }
                        catch (Exception ex) { Log(report, "kill " + pid + " failed: " + ex.Message); }
                    }
                    Log(report, "stop done");
                }
                else if (args[1] == "start" && args.Length >= 3)
                {
                    string key = args[2];
                    int port = PortForKey(key);
                    ProcessStartInfo psi = new ProcessStartInfo(GetPwshPath(), BuildStartArgs(key, toolsDir));
                    psi.WorkingDirectory = toolsDir;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                    Log(report, "start " + key + " launched, waiting port " + port);

                    Stopwatch sw = Stopwatch.StartNew();
                    bool up = false;
                    while (sw.Elapsed.TotalSeconds < 40)
                    {
                        if (PortOpen(port)) { up = true; break; }
                        System.Threading.Thread.Sleep(500);
                    }
                    Log(report, "start " + key + " port=" + port + " up=" + up + " after=" + sw.Elapsed.TotalSeconds.ToString("0.0") + "s");
                }
                else
                {
                    Log(report, "未知参数: " + string.Join(" ", args));
                }
            }
            catch (Exception ex) { Log(report, "ERROR " + ex); }
        }

        private static void EnsureHandles(Control c)
        {
            if (!c.IsHandleCreated) c.CreateControl();
            foreach (Control child in c.Controls) EnsureHandles(child);
        }

        private static void Log(string report, string line)
        {
            try { File.AppendAllText(report, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }
    }

    internal static class SelfCheck
    {
        private sealed class PythonInfo
        {
            public string Path;
            public string Prefix;
            public string Source;
        }

        private sealed class Report
        {
            private readonly StringBuilder text = new StringBuilder();
            public int Failures;
            public int Warnings;

            public void Line(string value)
            {
                text.AppendLine(value);
            }

            public void Ok(string name, string detail)
            {
                Line("[OK]   " + name + ": " + detail);
            }

            public void Warn(string name, string detail)
            {
                Warnings++;
                Line("[警告] " + name + ": " + detail);
            }

            public void Fail(string name, string detail)
            {
                Failures++;
                Line("[失败] " + name + ": " + detail);
            }

            public void Info(string name, string detail)
            {
                Line("[信息] " + name + ": " + detail);
            }

            public override string ToString()
            {
                return text.ToString();
            }
        }

        public static string Run(string toolsDir, string pwshPath)
        {
            Report report = new Report();
            report.Line("IIIF Service Workbench 运行环境自检");
            report.Line("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.Line("工作目录: " + toolsDir);
            report.Line("程序路径: " + Application.ExecutablePath);
            report.Line("");

            CheckToolsDir(report, toolsDir);
            CheckWindows(report);
            CheckDotNet(report);
            CheckPowerShell(report, pwshPath);
            CheckPython(report);
            CheckJava(report);
            CheckRequiredFiles(report, toolsDir);
            CheckWritableDirs(report, toolsDir);
            CheckImageSource(report, toolsDir);
            CheckPorts(report);

            report.Line("");
            if (report.Failures > 0)
            {
                report.Line("结论: 发现 " + report.Failures + " 个失败项，服务可能无法启动。请先处理标有 [失败] 的项。");
            }
            else if (report.Warnings > 0)
            {
                report.Line("结论: 未发现失败项，但有 " + report.Warnings + " 个警告；服务可能能启动，建议处理警告。");
            }
            else
            {
                report.Line("结论: 环境检查通过，可以启动两个服务。");
            }
            report.Line("提示: 若仍无法启动，查看 logs\\ui-image.log、logs\\ui-sas.log、logs\\image-server.err.log，或在 PowerShell 中手动运行 start-image.ps1 / start-sas.ps1 查看报错。");
            return report.ToString();
        }

        private static void CheckToolsDir(Report report, string toolsDir)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            try
            {
                if (!string.Equals(Path.GetFullPath(exeDir).TrimEnd('\\'),
                    Path.GetFullPath(toolsDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    report.Warn("程序位置", "exe 不在脚本目录；当前使用 " + toolsDir + "。建议把 exe 与脚本放在同一目录。");
                }
                else
                {
                    report.Ok("程序位置", "exe 与脚本在同一目录。");
                }
            }
            catch (Exception ex)
            {
                report.Warn("程序位置", "无法比较路径：" + ex.Message);
            }
        }

        private static void CheckWindows(Report report)
        {
            string bits = Environment.Is64BitOperatingSystem ? "64 位" : "32 位";
            report.Info("系统", Environment.OSVersion.VersionString + " / " + bits);
            if (!Environment.Is64BitOperatingSystem)
            {
                report.Warn("系统位数", "当前是 32 位 Windows；本项目按 64 位验证，64 位 Java/Python 可能无法运行。");
            }
            if (Environment.OSVersion.Version.Major < 10)
            {
                report.Warn("Windows 版本", "低于 Windows 10；未验证。");
            }
        }

        private static void CheckDotNet(Report report)
        {
            int release = 0;
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full",
                    "Release", null);
                if (value != null) int.TryParse(Convert.ToString(value), out release);
            }
            catch
            {
            }

            string clr = Environment.Version.ToString();
            if (release >= 528040)
            {
                report.Ok(".NET Framework", "4.8+ (Release " + release + ")，CLR " + clr);
            }
            else if (release > 0)
            {
                report.Warn(".NET Framework", "Release " + release + "，低于 4.8；控制台可能无法运行。");
            }
            else
            {
                report.Info(".NET Framework", "无法读取注册表 Release；控制台已启动，CLR " + clr);
            }
        }

        private static void CheckPowerShell(Report report, string pwshPath)
        {
            if (string.IsNullOrEmpty(pwshPath) || !File.Exists(pwshPath))
            {
                report.Fail("PowerShell", "找不到 PowerShell：" + pwshPath);
                return;
            }

            int code;
            string version = RunProcess(pwshPath,
                "-NoProfile -Command \"(Get-Host).Version.ToString()\"", 8000, out code).Trim();
            string first = FirstLine(version);
            if (code == 0 && first.StartsWith("7."))
            {
                report.Ok("PowerShell", "7.x（" + first + "）：" + pwshPath);
            }
            else if (code == 0 && first.Length > 0)
            {
                report.Warn("PowerShell", "当前为 " + first +
                    "（可能是 Windows PowerShell 5.1）；建议安装 PowerShell 7。路径：" + pwshPath);
            }
            else
            {
                report.Warn("PowerShell", "无法读取版本（退出码 " + code + "）：" + first);
            }

            string policy = RunProcess(pwshPath,
                "-NoProfile -Command \"(Get-ExecutionPolicy -Scope CurrentUser).ToString()\"",
                8000, out code).Trim();
            string policyFirst = FirstLine(policy);
            if (policyFirst.IndexOf("Restricted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                policyFirst.IndexOf("AllSigned", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                report.Warn("执行策略", "当前用户执行策略为 " + policyFirst +
                    "；控制台启动会带 Bypass，手动运行脚本可能被拦截。");
            }
            else if (policyFirst.Length > 0)
            {
                report.Info("执行策略", policyFirst);
            }
        }

        private static void CheckPython(Report report)
        {
            PythonInfo python = FindPython();
            if (python == null)
            {
                report.Fail("Python", "未找到 Python 3.10+。安装 Python 并勾选 Add to PATH，或设置环境变量 IIIF_PYTHON 指向 python.exe。");
                return;
            }

            int code;
            string versionText = RunProcess(python.Path,
                PythonArgs(python.Prefix, "--version"), 8000, out code).Trim();
            Version version = ParsePythonVersion(versionText);
            if (code != 0 || version == null)
            {
                report.Fail("Python", "无法运行 " + python.Path +
                    "（退出码 " + code + "）：" + FirstLine(versionText));
                return;
            }

            if (version.CompareTo(new Version(3, 10)) < 0)
            {
                report.Fail("Python", "版本 " + version.ToString() +
                    " 低于 3.10；请升级。路径：" + python.Path);
            }
            else
            {
                report.Ok("Python", version.ToString() + "（" + python.Source + "）：" + python.Path);
            }

            string pillowCode = "import PIL; print(getattr(PIL, '__version__', '?'))";
            string pillowText = RunProcess(python.Path,
                PythonArgs(python.Prefix, "-c", pillowCode),
                8000, out code).Trim();
            if (code != 0 ||
                pillowText.IndexOf("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pillowText.IndexOf("ImportError", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                report.Fail("Pillow", "未安装或无法导入。请运行：" +
                    PythonCommand(python) + " -m pip install Pillow==12.3.0");
            }
            else
            {
                report.Ok("Pillow", FirstLine(pillowText) + "（" + python.Path + "）");
            }
        }

        private static void CheckJava(Report report)
        {
            string java = FindJava();
            if (java == null)
            {
                report.Fail("Java", "未找到 java.exe；安装 JRE/JDK 11+ 并加入 PATH（或设置 JAVA_HOME）。");
                return;
            }

            int code;
            string output = RunProcess(java, "-version", 8000, out code).Trim();
            string version = ExtractJavaVersion(output);
            int major = ParseJavaMajor(version);
            if (code != 0 || version.Length == 0)
            {
                report.Fail("Java", "无法运行 java -version（退出码 " + code + "）：" + FirstLine(output));
                return;
            }

            if (major < 11)
            {
                report.Fail("Java", "版本 " + version + " 低于 11；建议安装 JRE/JDK 11+。路径：" + java);
            }
            else
            {
                report.Ok("Java", version + "（" + java + "）");
            }

            if (FindOnPath("javaw.exe") == null)
            {
                report.Warn("javaw", "未找到 javaw.exe；launch-sas.ps1 会回退 java，可能弹出控制台窗口。");
            }
        }

        private static void CheckRequiredFiles(Report report, string toolsDir)
        {
            string[] files = new string[]
            {
                "iiif_image_server.py",
                "start-image.ps1",
                "start-sas.ps1",
                "stop-services.ps1",
                @"sas\simpleAnnotationStore.war",
                @"sas\dependency\jetty-runner.jar"
            };

            int missing = 0;
            foreach (string rel in files)
            {
                if (!File.Exists(Path.Combine(toolsDir, rel)))
                {
                    missing++;
                    report.Fail("必需文件", "缺少 " + rel);
                }
            }
            if (missing == 0) report.Ok("必需文件", "全部存在。");
            if (!Directory.Exists(Path.Combine(toolsDir, "sas")))
            {
                report.Fail("SAS 目录", "缺少 sas 目录。");
            }
        }

        private static void CheckWritableDirs(Report report, string toolsDir)
        {
            string[] dirs = new string[] { "logs", "data", @"sas\data", @"sas\tmp" };
            foreach (string rel in dirs)
            {
                string error;
                if (CanWrite(Path.Combine(toolsDir, rel), out error))
                {
                    report.Ok("目录写入", rel);
                }
                else
                {
                    report.Fail("目录写入", rel + " 不可写：" + error +
                        "（检查权限、只读盘或安全软件）");
                }
            }
        }

        private static void CheckImageSource(Report report, string toolsDir)
        {
            string conf = Path.Combine(toolsDir, "data", "image-source.conf");
            string source = null;
            if (File.Exists(conf))
            {
                try
                {
                    source = File.ReadAllText(conf, Encoding.UTF8).Trim();
                }
                catch (Exception ex)
                {
                    report.Fail("图源配置", "无法读取 " + conf + "：" + ex.Message);
                    return;
                }
            }

            if (string.IsNullOrEmpty(source))
            {
                source = Path.Combine(toolsDir, "data", "demo-images");
            }

            if (!Directory.Exists(source))
            {
                report.Fail("图像图源", "目录不存在：" + source +
                    "。在控制台重新选择图源，或修正 data\\image-source.conf。");
                return;
            }

            int count = CountImages(source);
            if (count == 0)
            {
                report.Warn("图像图源", "目录中没有 jpg/png/tif/webp 图片：" + source);
            }
            else
            {
                report.Ok("图像图源", count + " 张图片：" + source);
            }
        }

        private static void CheckPorts(Report report)
        {
            CheckPort(report, "8182 图像服务", 8182);
            CheckPort(report, "8888 SAS", 8888);
        }

        private static void CheckPort(Report report, string name, int port)
        {
            if (MainForm.PortOpen(port))
            {
                report.Warn("端口 " + port, name + " 已被占用；如果服务已运行则正常，" +
                    "若启动失败请用 netstat -ano | findstr :" + port + " 查占用进程。");
            }
            else
            {
                report.Ok("端口 " + port, name + " 可用。");
            }
        }

        private static PythonInfo FindPython()
        {
            string env = Environment.GetEnvironmentVariable("IIIF_PYTHON");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            {
                return new PythonInfo
                {
                    Path = env,
                    Prefix = IsPyLauncher(env) ? "-3" : "",
                    Source = "IIIF_PYTHON"
                };
            }

            string path = FindOnPath("python.exe");
            if (IsUsablePython(path))
            {
                return new PythonInfo { Path = path, Prefix = "", Source = "PATH" };
            }

            path = FindOnPath("python3.exe");
            if (IsUsablePython(path))
            {
                return new PythonInfo { Path = path, Prefix = "", Source = "PATH" };
            }

            string[] roots = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Python")
            };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string[] dirs = Directory.GetDirectories(root, "Python*");
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = dirs.Length - 1; i >= 0; i--)
                {
                    string candidate = Path.Combine(dirs[i], "python.exe");
                    if (File.Exists(candidate))
                    {
                        return new PythonInfo { Path = candidate, Prefix = "", Source = "常见安装目录" };
                    }
                }
            }

            string py = FindOnPath("py.exe");
            if (IsUsablePython(py))
            {
                return new PythonInfo { Path = py, Prefix = "-3", Source = "py -3" };
            }
            return null;
        }

        private static bool IsUsablePython(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            return path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool IsPyLauncher(string path)
        {
            return string.Equals(Path.GetFileName(path), "py.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static string FindJava()
        {
            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
            {
                string candidate = Path.Combine(javaHome, "bin", "java.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return FindOnPath("java.exe");
        }

        internal static string FindOnPath(string fileName)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;
            foreach (string raw in path.Split(';'))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    string full = Path.Combine(dir, fileName);
                    if (File.Exists(full)) return full;
                }
                catch
                {
                }
            }
            return null;
        }

        private static string PythonArgs(string prefix, string value)
        {
            return string.IsNullOrEmpty(prefix) ? value : prefix + " " + value;
        }

        private static string PythonArgs(string prefix, string flag, string value)
        {
            string head = string.IsNullOrEmpty(prefix) ? "" : prefix + " ";
            return head + flag + " \"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string PythonCommand(PythonInfo python)
        {
            string command = "\"" + python.Path + "\"";
            if (!string.IsNullOrEmpty(python.Prefix)) command += " " + python.Prefix;
            return command;
        }

        private static Version ParsePythonVersion(string text)
        {
            Match match = Regex.Match(text, @"Python\s+(\d+)\.(\d+)(?:\.(\d+))?");
            if (!match.Success) return null;
            int major = int.Parse(match.Groups[1].Value);
            int minor = int.Parse(match.Groups[2].Value);
            int patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
            return new Version(major, minor, patch);
        }

        private static string ExtractJavaVersion(string text)
        {
            Match match = Regex.Match(text, "version \"([^\"]+)\"");
            if (match.Success) return match.Groups[1].Value;
            return FirstLine(text);
        }

        private static int ParseJavaMajor(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;
            Match match = Regex.Match(version, @"^(\d+)(?:\.(\d+))?");
            if (!match.Success) return 0;
            int first = int.Parse(match.Groups[1].Value);
            if (first == 1 && match.Groups[2].Success)
            {
                return int.Parse(match.Groups[2].Value);
            }
            return first;
        }

        private static int CountImages(string folder)
        {
            int count = 0;
            try
            {
                foreach (string file in Directory.GetFiles(folder))
                {
                    if (IsImageFile(file)) count++;
                }
                foreach (string dir in Directory.GetDirectories(folder))
                {
                    foreach (string file in Directory.GetFiles(dir))
                    {
                        if (IsImageFile(file)) count++;
                    }
                }
            }
            catch
            {
            }
            return count;
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" ||
                   ext == ".tif" || ext == ".tiff" || ext == ".webp";
        }

        private static bool CanWrite(string dir, out string error)
        {
            error = "";
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".selfcheck-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "ok", Encoding.UTF8);
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string RunProcess(string fileName, string arguments, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(fileName, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                using (Process process = new Process())
                {
                    StringBuilder stdout = new StringBuilder();
                    StringBuilder stderr = new StringBuilder();
                    process.StartInfo = psi;
                    process.OutputDataReceived += delegate(object s, DataReceivedEventArgs a)
                    {
                        if (a.Data != null) stdout.AppendLine(a.Data);
                    };
                    process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs a)
                    {
                        if (a.Data != null) stderr.AppendLine(a.Data);
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        exitCode = -2;
                        return (stdout.ToString() + "\n" + stderr.ToString() + "\n[超时]").Trim();
                    }
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                    return (stdout.ToString() + "\n" + stderr.ToString()).Trim();
                }
            }
            catch (Exception ex)
            {
                exitCode = -3;
                return ex.Message;
            }
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string[] lines = value.Replace("\r", "").Split('\n');
            foreach (string line in lines)
            {
                if (line.Trim().Length > 0) return line.Trim();
            }
            return "";
        }
    }
}

