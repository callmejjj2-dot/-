// 八重神子桌宠 - C# WinForms 单进程原生版
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Media;
using NAudio.Wave;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Timer = System.Windows.Forms.Timer;

namespace ZhuChong
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool isUpdatedRestart = false;
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg == "--updated") isUpdatedRestart = true;
            }
            bool createdNew = false;
            Mutex mutex = new Mutex(true, "ZhuoChongPet_Mutex", out createdNew);
            if (!createdNew && isUpdatedRestart)
            {
                // 自动更新重启时，等待旧进程释放单实例锁（最多等待 6 秒）
                for (int retry = 0; retry < 30 && !createdNew; retry++)
                {
                    try { mutex.Close(); } catch { }
                    Thread.Sleep(200);
                    mutex = new Mutex(true, "ZhuoChongPet_Mutex", out createdNew);
                }
            }
            using (mutex)
            {
                if (!createdNew) return; // 单实例运行
                // 全局异常捕获，防止闪退
                Application.ThreadException += (s, e) =>
                {
                    try { MessageBox.Show("出错了：" + e.Exception.Message + "\n\n" + e.Exception.StackTrace, "桌宠错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { }
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    try { MessageBox.Show("严重错误：" + ((Exception)e.ExceptionObject).Message, "桌宠崩溃", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { }
                };
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var pet = new PetForm();
                foreach (string a in Environment.GetCommandLineArgs())
                    if (a == "--panel") pet.ShowPanel();
                Application.Run(pet);
            }
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }

    class GifClip
    {
        public List<Bitmap> Frames = new List<Bitmap>();
        public List<int> Delays = new List<int>(); // 毫秒
    }

    class PetIcon
    {
        public string Name, Path;
        public PetIcon(string n, string p) { Name = n; Path = p; }
    }

    class TodoItem
    {
        public string Text;
        public bool Done;
        public TodoItem(string t, bool d) { Text = t; Done = d; }
    }

    // =========================================================================
    // 独立透明扇形图标层：彻底消除窗口 resize 导致的黑闪，且桌宠主体坐标完全不动
    // =========================================================================
    class RingOverlayForm : Form
    {
        PetForm pet;
        List<Button> iconBtns = new List<Button>();
        Timer ringTimer;
        // 拖动换位
        int dragIdx = -1;
        Point dragStart;
        bool dragging = false;
        int hoverIdx = -1;

        public const int EW = 560;
        public const int EH = 550;

        public RingOverlayForm(PetForm owner)
        {
            pet = owner;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Size = new Size(EW, EH);

            ringTimer = new Timer { Interval = 15000 };
            ringTimer.Tick += (s, e) => HideRing();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; } // 展开时不抢占当前前台窗口焦点
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: 不在 Alt+Tab 中出现
                return cp;
            }
        }

        public void ShowRing()
        {
            // 窗口位置固定居中在桌宠，圆心偏移只改窗口内坐标
            var wa = Screen.FromControl(pet).WorkingArea;
            int petCenterX = pet.Location.X + pet.Width / 2;
            int petCenterY = pet.Location.Y + pet.FootY - pet.PetH / 2;

            int rx = petCenterX - EW / 2;
            int ry = petCenterY - EH / 2;

            // 限制在当前屏幕工作区内，确保图标完整可见
            if (rx < wa.Left) rx = wa.Left;
            if (rx + EW > wa.Right) rx = wa.Right - EW;
            if (ry < wa.Top) ry = wa.Top;
            if (ry + EH > wa.Bottom) ry = wa.Bottom - EH;

            Location = new Point(rx, ry);
            RebuildIcons();

            if (!Visible)
            {
                Show();
            }
            BringToFront();
            if (pet != null && !pet.IsDisposed) pet.EnsureTopMostZOrder();

            ringTimer.Stop();
            ringTimer.Start();
        }

        public void HideRing()
        {
            ringTimer.Stop();
            if (Visible)
            {
                Hide();
            }
            pet.OnRingClosed();
        }

        // 拖动桌宠时实时同步窗口位置，图标按钮不用重建，只移窗口
        public void SyncToPet()
        {
            var wa = Screen.FromControl(pet).WorkingArea;
            int petCenterX = pet.Location.X + pet.Width / 2;
            int petCenterY = pet.Location.Y + pet.FootY - pet.PetH / 2;
            int rx = petCenterX - EW / 2;
            int ry = petCenterY - EH / 2;
            if (rx < wa.Left) rx = wa.Left;
            if (rx + EW > wa.Right) rx = wa.Right - EW;
            if (ry < wa.Top) ry = wa.Top;
            if (ry + EH > wa.Bottom) ry = wa.Bottom - EH;
            Location = new Point(rx, ry);
        }

        public void RebuildIcons()
        {
            foreach (var b in iconBtns)
            {
                if (b.Region != null) { b.Region.Dispose(); }
                b.Dispose();
            }
            iconBtns.Clear();

            int n = pet.Icons.Count;
            if (n == 0) return;

            // 窗口位置固定居中在桌宠，圆心偏移只改窗口内坐标，这样滑块拖动才真正生效
            var wa = Screen.FromControl(pet).WorkingArea;
            int petCenterX = pet.Location.X + pet.Width / 2;
            int petCenterY = pet.Location.Y + pet.FootY - pet.PetH / 2;
            int rx = petCenterX - EW / 2;
            int ry = petCenterY - EH / 2;
            if (rx < wa.Left) rx = wa.Left;
            if (rx + EW > wa.Right) rx = wa.Right - EW;
            if (ry < wa.Top) ry = wa.Top;
            if (ry + EH > wa.Bottom) ry = wa.Bottom - EH;
            if (Location.X != rx || Location.Y != ry) Location = new Point(rx, ry);

            // 窗口内圆心坐标：补偿边界钳制，同时加用户偏移
            int cx = petCenterX - rx + pet.RingCxAdj;
            int cy = petCenterY - ry + pet.RingCyAdj;
            int[] layers = { pet.RingBase, pet.RingBase + pet.RingGap, pet.RingBase + pet.RingGap * 2 };
            int perLayer = 8;

            for (int i = 0; i < n; i++)
            {
                int layer = Math.Min(i / perLayer, layers.Length - 1);
                int idxInLayer = i % perLayer;
                int layerCount = Math.Min(perLayer, n - layer * perLayer);
                if (layerCount <= 0) break;

                double angMin = -pet.RingSpan * Math.PI / 180;
                double angMax = pet.RingSpan * Math.PI / 180;
                double ang;
                if (layerCount == 1)
                {
                    ang = 0; // 单个图标居中位于正上方
                }
                else
                {
                    ang = angMin + (angMax - angMin) * idxInLayer / (layerCount - 1);
                }

                int r = layers[layer];
                int ix = cx + (int)(r * Math.Sin(ang));
                int iy = cy - (int)(r * Math.Cos(ang));

                var btn = MakeIconButton(pet.Icons[i], ix - pet.IconSize / 2, iy - pet.IconSize / 2, i);
                Controls.Add(btn);
                btn.BringToFront();
                iconBtns.Add(btn);
            }
        }

        Button MakeIconButton(PetIcon ic, int x, int y, int idx)
        {
            var b = new Button();
            int s = pet.IconSize;
            b.SetBounds(x, y, s, s);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.FromArgb(40, 24, 66);
            b.Cursor = Cursors.Hand;
            b.Text = "";

            // 圆形外框，正确释放 GraphicsPath
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(1, 1, s - 2, s - 2);
                b.Region = new Region(path);
            }

            // 图标提取，正确释放 HICON
            try
            {
                using (Icon ico = Icon.ExtractAssociatedIcon(ic.Path))
                {
                    if (ico != null)
                    {
                        var img = ico.ToBitmap();
                        int ps = Math.Max(16, s - 16);
                        var inner = new PictureBox
                        {
                            Size = new Size(ps, ps),
                            Location = new Point((s - ps) / 2, (s - ps) / 2),
                            BackColor = Color.Transparent,
                            SizeMode = PictureBoxSizeMode.Zoom,
                            Image = img,
                            Enabled = false,
                            Cursor = Cursors.Hand
                        };
                        b.Controls.Add(inner);
                    }
                }
            }
            catch
            {
                b.Text = ic.Name.Length > 3 ? ic.Name.Substring(0, 3) : ic.Name;
            }

            DateTime lastIconClick = DateTime.MinValue;
            b.Click += (se, ea) =>
            {
                if (dragging) return; // 拖动后不触发点击
                var now = DateTime.Now;
                if ((now - lastIconClick).TotalMilliseconds < 450)
                {
                    lastIconClick = DateTime.MinValue;
                    try
                    {
                        Process.Start(ic.Path);
                        HideRing();
                    }
                    catch
                    {
                        pet.ShowBubble("打不开这个图标。", 2500);
                    }
                }
                else
                {
                    lastIconClick = now;
                }
            };

            // 拖动换位逻辑（使用全局屏幕坐标，解决按钮捕获鼠标后父窗口收不到移动事件及局部坐标不一致问题）
            b.MouseDown += (se, ea) =>
            {
                if (ea.Button == MouseButtons.Left)
                {
                    dragIdx = idx;
                    dragStart = Cursor.Position;
                    dragging = false;
                    ringTimer.Stop();
                }
            };
            b.MouseMove += (se, ea) =>
            {
                HandleIconDragMove();
            };
            b.MouseUp += (se, ea) =>
            {
                if (ea.Button == MouseButtons.Left && dragIdx == idx)
                {
                    int targetIdx = hoverIdx;
                    int srcIdx = dragIdx;
                    bool wasDragging = dragging;
                    dragging = false;
                    dragIdx = -1;
                    hoverIdx = -1;

                    if (wasDragging && targetIdx >= 0 && targetIdx != srcIdx &&
                        srcIdx < pet.Icons.Count && targetIdx < pet.Icons.Count)
                    {
                        // 交换两个图标位置
                        var tmp = pet.Icons[srcIdx];
                        pet.Icons[srcIdx] = pet.Icons[targetIdx];
                        pet.Icons[targetIdx] = tmp;
                        pet.SaveConfig();
                        BeginInvoke(new Action(() => RebuildIcons()));
                    }
                    else
                    {
                        // 恢复颜色
                        for (int i = 0; i < iconBtns.Count; i++)
                        {
                            iconBtns[i].BackColor = Color.FromArgb(40, 24, 66);
                        }
                    }
                    ringTimer.Start();
                }
            };
            b.MouseLeave += (se, ea) => { if (!dragging) b.BackColor = Color.FromArgb(40, 24, 66); };

            return b;
        }

        void HandleIconDragMove()
        {
            ringTimer.Stop();
            ringTimer.Start();

            if (dragIdx >= 0 && (Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left)
            {
                Point curScreen = Cursor.Position;
                int dx = Math.Abs(curScreen.X - dragStart.X);
                int dy = Math.Abs(curScreen.Y - dragStart.Y);
                if (!dragging && (dx > 8 || dy > 8))
                {
                    dragging = true;
                    if (dragIdx < iconBtns.Count)
                        iconBtns[dragIdx].BackColor = Color.FromArgb(100, 180, 255);
                }
                if (dragging)
                {
                    Point clientPt = PointToClient(curScreen);
                    int newHover = -1;
                    for (int i = 0; i < iconBtns.Count; i++)
                    {
                        if (i == dragIdx) continue;
                        var r = new Rectangle(iconBtns[i].Location, iconBtns[i].Size);
                        if (r.Contains(clientPt)) { newHover = i; break; }
                    }
                    if (newHover != hoverIdx)
                    {
                        hoverIdx = newHover;
                        for (int i = 0; i < iconBtns.Count; i++)
                        {
                            if (i == dragIdx) continue;
                            iconBtns[i].BackColor = i == hoverIdx ? Color.FromArgb(80, 200, 120) : Color.FromArgb(40, 24, 66);
                        }
                    }
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            HandleIconDragMove();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (ringTimer != null) { ringTimer.Dispose(); ringTimer = null; }
                foreach (var b in iconBtns)
                {
                    if (b.Region != null) { b.Region.Dispose(); }
                    b.Dispose();
                }
                iconBtns.Clear();
            }
            base.Dispose(disposing);
        }
    }

    // =========================================================================
    // 音频可视化：基于 UpdateLayeredWindow 逐像素 Alpha 透明的双包络环形蓝紫发光频谱
    // =========================================================================
    class AudioVizForm : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WinPoint
        {
            public int x;
            public int y;
            public WinPoint(int x, int y) { this.x = x; this.y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WinSize
        {
            public int cx;
            public int cy;
            public WinSize(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref WinPoint pptDst,
            ref WinSize psize, IntPtr hdcSrc, ref WinPoint pptSrc, uint crKey,
            [In] ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        PetForm pet;
        WasapiLoopbackCapture capture;
        Timer vizTimer;
        Bitmap frameBmp;

        const int FFT_SIZE = 1024;
        const int BIN_COUNT = 60;
        const int SEGS = 240;

        readonly object bufLock = new object();
        float[] sampleBuf = new float[FFT_SIZE];
        int samplePos = 0;
        int lastAudioTick = 0;
        float[] spectrum = new float[BIN_COUNT];
        float[] smoothBins = new float[BIN_COUNT];
        float[] fftReal = new float[FFT_SIZE];
        float[] fftImag = new float[FFT_SIZE];
        float[] window = new float[FFT_SIZE];
        double phase = 0.0;

        public AudioVizForm(PetForm owner)
        {
            pet = owner;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;

            int side = 470;
            if (owner != null && owner.PetBox != null)
            {
                side = Math.Max(owner.PetBox.Width, owner.PetBox.Height) + 150;
            }
            Size = new Size(side, side);

            for (int i = 0; i < FFT_SIZE; i++)
            {
                window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FFT_SIZE - 1))));
            }

            vizTimer = new Timer { Interval = 50 };
            vizTimer.Tick += (s, e) => { RenderLayeredFrame(); };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_LAYERED (0x80000) | WS_EX_TRANSPARENT (0x20) | WS_EX_TOOLWINDOW (0x80) | WS_EX_NOACTIVATE (0x08000000)
                cp.ExStyle |= 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000;
                return cp;
            }
        }

        void Fft()
        {
            lock (bufLock)
            {
                // 当系统暂停音乐或无音频流输出时，WasapiLoopbackCapture 不再触发 DataAvailable，
                // 需对超时残留的环形缓冲区做衰减清零，避免频谱冻结及一直误判为正在唱歌
                bool audioStale = (lastAudioTick == 0) || (unchecked(Environment.TickCount - lastAudioTick) > 350);
                if (audioStale)
                {
                    for (int i = 0; i < FFT_SIZE; i++)
                    {
                        sampleBuf[i] *= 0.25f;
                        if (Math.Abs(sampleBuf[i]) < 1e-5f) sampleBuf[i] = 0f;
                    }
                }

                int pos = samplePos;
                for (int i = 0; i < FFT_SIZE; i++)
                {
                    fftReal[i] = sampleBuf[(pos + i) % FFT_SIZE] * window[i];
                    fftImag[i] = 0f;
                }
            }

            int j = 0;
            for (int i = 0; i < FFT_SIZE; i++)
            {
                if (i < j)
                {
                    float tr = fftReal[i];
                    fftReal[i] = fftReal[j];
                    fftReal[j] = tr;
                    float ti = fftImag[i];
                    fftImag[i] = fftImag[j];
                    fftImag[j] = ti;
                }
                int m = FFT_SIZE >> 1;
                while (m >= 1 && j >= m)
                {
                    j -= m;
                    m >>= 1;
                }
                j += m;
            }

            for (int size = 2; size <= FFT_SIZE; size <<= 1)
            {
                int half = size >> 1;
                float ang = -2f * (float)Math.PI / size;
                float wpr = (float)Math.Cos(ang);
                float wpi = (float)Math.Sin(ang);
                for (int i = 0; i < FFT_SIZE; i += size)
                {
                    float wr = 1f;
                    float wi = 0f;
                    for (int k = 0; k < half; k++)
                    {
                        int evenIdx = i + k;
                        int oddIdx = evenIdx + half;
                        float ur = fftReal[evenIdx];
                        float ui = fftImag[evenIdx];
                        float vr = fftReal[oddIdx] * wr - fftImag[oddIdx] * wi;
                        float vi = fftReal[oddIdx] * wi + fftImag[oddIdx] * wr;

                        fftReal[evenIdx] = ur + vr;
                        fftImag[evenIdx] = ui + vi;
                        fftReal[oddIdx] = ur - vr;
                        fftImag[oddIdx] = ui - vi;

                        float nwr = wr * wpr - wi * wpi;
                        wi = wr * wpi + wi * wpr;
                        wr = nwr;
                    }
                }
            }
        }

        void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            var buffer = e.Buffer;
            int bytesRead = e.BytesRecorded;
            if (buffer == null || bytesRead < 4) return;
            int samples = bytesRead / 4;
            lock (bufLock)
            {
                lastAudioTick = Environment.TickCount;
                for (int i = 0; i < samples; i++)
                {
                    float sample = BitConverter.ToSingle(buffer, i * 4);
                    if (float.IsNaN(sample) || float.IsInfinity(sample)) sample = 0f;
                    sampleBuf[samplePos] = sample;
                    samplePos = (samplePos + 1) % FFT_SIZE;
                }
            }
        }

        public void Start()
        {
            if (Owner != null) Owner = null; // 解除 Owner 强制置前约束，以便排在桌宠正后方
            SyncToPet();
            if (capture == null)
            {
                try
                {
                    capture = new WasapiLoopbackCapture();
                    capture.DataAvailable += OnDataAvailable;
                    capture.StartRecording();
                }
                catch
                {
                    if (capture != null)
                    {
                        try { capture.Dispose(); } catch { }
                        capture = null;
                    }
                }
            }
            vizTimer.Start();
            RenderLayeredFrame();
        }

        public void Stop()
        {
            if (vizTimer != null) vizTimer.Stop();
            if (capture != null)
            {
                try
                {
                    capture.DataAvailable -= OnDataAvailable;
                    capture.StopRecording();
                }
                catch { }
                try { capture.Dispose(); } catch { }
                capture = null;
            }
            if (Visible) Hide();
        }

        public float GetSpectrumEnergy()
        {
            try
            {
                if (lastAudioTick == 0 || unchecked(Environment.TickCount - lastAudioTick) > 600)
                    return 0f;
                float sum = 0f;
                lock (bufLock)
                {
                    for (int i = 0; i < BIN_COUNT; i++)
                        sum += smoothBins[i];
                }
                return sum / BIN_COUNT;
            }
            catch { return 0f; }
        }

        public void SyncToPet()
        {
            if (pet == null || pet.PetBox == null) return;
            var pb = pet.PetBox;
            int side = Math.Max(pb.Width, pb.Height) + 150;
            if (Width != side || Height != side)
            {
                Size = new Size(side, side);
            }
            var screenPt = pet.PointToScreen(new Point(pb.Left + pb.Width / 2, pb.Top + pb.Height / 2));
            Location = new Point(screenPt.X - side / 2 + pet.AudioVizCx, screenPt.Y - side / 2 + pet.AudioVizCy);
            if (Visible && IsHandleCreated)
            {
                UpdateZOrderBehindPet();
            }
        }

        void UpdateZOrderBehindPet()
        {
            if (pet != null && !pet.IsDisposed && pet.Visible && pet.IsHandleCreated && IsHandleCreated)
            {
                pet.EnsureTopMostZOrder();
            }
        }

        static float LobeValue(double t, double center, double halfWidth, bool sharpDiamond, bool isInner)
        {
            double d = Math.Abs(t - center);
            if (d > 0.5) d = 1.0 - d;
            if (d >= halfWidth) return 0f;
            double u = 1.0 - (d / halfWidth); // 0 at node, 1 at crest
            if (sharpDiamond)
            {
                double p = isInner ? 1.25 : 1.35;
                return (float)Math.Pow(u, p);
            }
            double s = Math.Sin(u * Math.PI * 0.5);
            return (float)(s * s);
        }

        float BandEnergy(int startBin, int endBin)
        {
            if (startBin < 0) startBin = 0;
            if (endBin >= BIN_COUNT) endBin = BIN_COUNT - 1;
            if (endBin < startBin) return 0f;
            float sum = 0f;
            float maxV = 0f;
            int count = 0;
            for (int i = startBin; i <= endBin; i++)
            {
                float v = smoothBins[i];
                sum += v;
                if (v > maxV) maxV = v;
                count++;
            }
            float avg = count > 0 ? (sum / count) : 0f;
            return maxV * 0.68f + avg * 0.32f;
        }

        void DrawSpectrumToBitmap(Graphics g, int w, int h)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            Fft();
            phase += 0.05;
            if (phase > Math.PI * 200.0) phase -= Math.PI * 200.0;

            for (int i = 0; i < BIN_COUNT; i++)
            {
                int binIndex = (int)(Math.Pow(2.0, (double)i / BIN_COUNT * 6.0) * 2.0);
                if (binIndex >= FFT_SIZE / 2) binIndex = FFT_SIZE / 2 - 1;
                float real = fftReal[binIndex];
                float imag = fftImag[binIndex];
                float mag = (float)Math.Sqrt(real * real + imag * imag);

                // 相比第一版灵敏度适度提升约 10%~15%，兼顾节拍弹性与轮廓协调感
                float tilt = 1.0f + i * 0.048f;
                float norm = (mag / 46.0f) * tilt;
                float newValue = (float)Math.Log10(1.0 + norm * 9.5);
                if (newValue < 0f) newValue = 0f;
                if (newValue > 1.05f) newValue = 1.05f;

                if (newValue > spectrum[i])
                {
                    float spd = pet != null ? pet.AudioSpeed / 100f : 1f;
                    float attack = Math.Min(0.98f, 0.90f * spd);
                    spectrum[i] = spectrum[i] * (1f - attack) + newValue * attack;
                }
                else
                {
                    float spd = pet != null ? pet.AudioSpeed / 100f : 1f;
                    float release = Math.Min(0.5f, 0.22f * spd);
                    spectrum[i] = spectrum[i] * (1f - release) + newValue * release;
                }
            }

            // 邻域平滑滤波，消除毛刺锯齿（扩大窗口让尖峰圆滑）
            for (int i = 0; i < BIN_COUNT; i++)
            {
                float s = 0f;
                float wSum = 0f;
                for (int k = -3; k <= 3; k++)
                {
                    int idx = i + k;
                    if (idx < 0) idx = 0;
                    if (idx >= BIN_COUNT) idx = BIN_COUNT - 1;
                    float wt = (k == 0) ? 0.30f : (Math.Abs(k) == 1 ? 0.20f : (Math.Abs(k) == 2 ? 0.10f : 0.05f));
                    s += spectrum[idx] * wt;
                    wSum += wt;
                }
                smoothBins[i] = s / wSum;
            }

            // 柔和待机呼吸保底 + 五大频段驱动（在上一版协调轮廓基础上适度增加约 10% 起伏）
            float breath = (float)(0.18 + 0.035 * Math.Sin(phase));
            float bass = Math.Max(breath * 1.15f, BandEnergy(0, 9) * 1.08f);
            float lowMid = Math.Max(breath * 0.90f, BandEnergy(10, 21) * 1.08f);
            float mid = Math.Max(breath * 0.85f, BandEnergy(22, 35) * 1.08f);
            float highMid = Math.Max(breath * 0.75f, BandEnergy(36, 47) * 1.06f);
            float treble = Math.Max(breath * 0.65f, BandEnergy(48, 59) * 1.06f);

            float cx = w * 0.5f;
            float cy = h * 0.5f;
            float scaleRatio = (pet != null ? pet.AudioVizSize : 100) / 100f;
            float baseR = Math.Min(w, h) * 0.5f - 71f;
            if (baseR < 60f) baseR = 60f;

            // 内部三层极淡同心光晕暗环（偏蓝的宝石蓝紫色调）
            int[] ringOffsets = new int[] { -10, -26, -44 };
            int[] ringAlphas = new int[] { 20, 13, 7 };
            for (int rIdx = 0; rIdx < ringOffsets.Length; rIdx++)
            {
                float rr = baseR + ringOffsets[rIdx];
                if (rr > 10f)
                {
                    using (var ringPen = new Pen(Color.FromArgb(ringAlphas[rIdx], 70, 105, 245), 2.5f))
                    {
                        g.DrawEllipse(ringPen, cx - rr, cy - rr, rr * 2f, rr * 2f);
                    }
                }
            }

            // 在上版 (44 / 31) 基础上增加约 10% 幅度 -> (48.5 / 34.0)
            float outAmpRatio = (pet != null ? pet.AudioOutAmp : 100) / 100f;
            float inAmpRatio = (pet != null ? pet.AudioInAmp : 100) / 100f;
            float outAmp = 56.1f * scaleRatio * outAmpRatio;
            float inAmp = 37.4f * scaleRatio * inAmpRatio;
            PointF[] outPts = new PointF[SEGS];
            PointF[] inPts = new PointF[SEGS];
            float[] lobeEnv = new float[SEGS];

            // 波包分布配置：保持第一版协调的波包宽度与比例，幅值上限小幅上调约 10%
            double[] centers = new double[] { 0.042, 0.132, 0.208, 0.285, 0.542, 0.628, 0.765 };
            double[] widths  = new double[] { 0.053, 0.041, 0.043, 0.039, 0.053, 0.043, 0.135 };
            float[] amps     = new float[] {
                Math.Min(1.08f, bass * 1.10f),
                Math.Min(0.68f, lowMid * 0.56f),
                Math.Min(0.72f, mid * 0.60f),
                Math.Min(0.55f, highMid * 0.45f),
                Math.Min(1.05f, bass * 1.05f),
                Math.Min(0.64f, lowMid * 0.52f),
                Math.Min(0.38f, treble * 0.36f)
            };
            bool[] isSharp   = new bool[] { true, false, false, false, true, false, false };

            for (int i = 0; i < SEGS; i++)
            {
                double t = (double)i / SEGS;
                double ang = -Math.PI * 0.5 + (Math.PI * 2.0 * i) / SEGS;

                float vo = 0.012f;
                float vi = 0.012f;
                for (int k = 0; k < centers.Length; k++)
                {
                    vo += amps[k] * LobeValue(t, centers[k], widths[k], isSharp[k], false);
                    vi += amps[k] * LobeValue(t, centers[k], widths[k], isSharp[k], true);
                }

                // 叠加对应角度的细粒度频谱微起伏
                int binIdx = (int)((t < 0.5 ? t * 2.0 : (1.0 - t) * 2.0) * (BIN_COUNT - 1));
                if (binIdx < 0) binIdx = 0;
                if (binIdx >= BIN_COUNT) binIdx = BIN_COUNT - 1;
                float fine = smoothBins[binIdx] * 0.075f * (float)Math.Sin(t * Math.PI * 14.0 + phase);
                vo = Math.Max(0.01f, Math.Min(1.10f, vo + fine));
                vi = Math.Max(0.01f, Math.Min(1.06f, vi + fine * 0.8f));

                lobeEnv[i] = vo;
                float rOut = baseR + vo * outAmp;
                float rIn = baseR - vi * inAmp;
                float cosA = (float)Math.Cos(ang);
                float sinA = (float)Math.Sin(ang);
                outPts[i] = new PointF(cx + rOut * cosA, cy + rOut * sinA);
                inPts[i] = new PointF(cx + rIn * cosA, cy + rIn * sinA);
            }

            // 对最终点坐标做二次平滑，消除尖峰棱角
            PointF[] smoothOut = new PointF[SEGS];
            PointF[] smoothIn = new PointF[SEGS];
            for (int i = 0; i < SEGS; i++)
            {
                float sx = 0f, sy = 0f, sw = 0f;
                float ix = 0f, iy = 0f, iw = 0f;
                for (int k = -4; k <= 4; k++)
                {
                    int idx = (i + k + SEGS) % SEGS;
                    float wt = (k == 0) ? 0.20f : (Math.Abs(k) == 1 ? 0.18f : (Math.Abs(k) == 2 ? 0.12f : (Math.Abs(k) == 3 ? 0.06f : 0.03f)));
                    sx += outPts[idx].X * wt;
                    sy += outPts[idx].Y * wt;
                    sw += wt;
                    ix += inPts[idx].X * wt;
                    iy += inPts[idx].Y * wt;
                    iw += wt;
                }
                smoothOut[i] = new PointF(sx / sw, sy / sw);
                smoothIn[i] = new PointF(ix / iw, iy / iw);
            }
            outPts = smoothOut;
            inPts = smoothIn;

            // 1. 填充内外包络线之间的半透明宝石蓝紫光带
            using (var bandPath = new GraphicsPath(FillMode.Alternate))
            {
                bandPath.AddClosedCurve(outPts, 0.05f);
                bandPath.AddClosedCurve(inPts, 0.05f);
                using (var fillBrush = new SolidBrush(Color.FromArgb(54, 65, 82, 235)))
                {
                    g.FillPath(fillBrush, bandPath);
                }
            }

            // 2. 绘制包络线内部的径向刻度网格竖线
            for (int i = 0; i < SEGS; i += 3)
            {
                float v = lobeEnv[i];
                if (v > 0.025f)
                {
                    double t = (double)i / SEGS;
                    bool isLeftBand = (t >= 0.66 && t <= 0.94);
                    int alpha = Math.Min(210, (int)(58 + v * 165));
                    Color ribColor = isLeftBand
                        ? Color.FromArgb(alpha, 85, 112, 252)
                        : Color.FromArgb(alpha, 95, 142, 255);
                    using (var ribPen = new Pen(ribColor, 1.15f))
                    {
                        g.DrawLine(ribPen, inPts[i], outPts[i]);
                    }
                }
            }

            // 3. 多层柔和宝石蓝紫（偏蓝的蓝紫色）外发光
            int[] glowWidths = new int[] { 11, 7, 4 };
            int[] glowAlphasOut = new int[] { 20, 42, 80 };
            int[] glowAlphasIn = new int[] { 15, 32, 62 };
            for (int gIdx = 0; gIdx < glowWidths.Length; gIdx++)
            {
                using (var penOut = new Pen(Color.FromArgb(glowAlphasOut[gIdx], 82, 92, 255), glowWidths[gIdx]))
                using (var penIn = new Pen(Color.FromArgb(glowAlphasIn[gIdx], 82, 92, 255), Math.Max(2, glowWidths[gIdx] - 2)))
                {
                    penOut.LineJoin = LineJoin.Round;
                    penIn.LineJoin = LineJoin.Round;
                    g.DrawClosedCurve(penOut, outPts, 0.05f, FillMode.Winding);
                    g.DrawClosedCurve(penIn, inPts, 0.05f, FillMode.Winding);
                }
            }

            // 4. 中层主轮廓线与内层高光细芯线（平滑曲线，不用逐段直线）
            using (var midOutPen = new Pen(Color.FromArgb(215, 100, 125, 255), 2.6f))
            using (var midInPen = new Pen(Color.FromArgb(185, 100, 125, 255), 2.0f))
            using (var coreOutPen = new Pen(Color.FromArgb(245, 165, 185, 255), 1.1f))
            using (var coreInPen = new Pen(Color.FromArgb(215, 165, 185, 255), 1.0f))
            {
                midOutPen.LineJoin = LineJoin.Round;
                midInPen.LineJoin = LineJoin.Round;
                coreOutPen.LineJoin = LineJoin.Round;
                coreInPen.LineJoin = LineJoin.Round;
                g.DrawClosedCurve(midOutPen, outPts, 0.05f, FillMode.Winding);
                g.DrawClosedCurve(midInPen, inPts, 0.05f, FillMode.Winding);
                g.DrawClosedCurve(coreOutPen, outPts, 0.05f, FillMode.Winding);
                g.DrawClosedCurve(coreInPen, inPts, 0.05f, FillMode.Winding);
            }
        }

        public void RenderLayeredFrame()
        {
            if (IsDisposed || !IsHandleCreated || !Visible || Width <= 0 || Height <= 0) return;

            if (frameBmp == null || frameBmp.Width != Width || frameBmp.Height != Height)
            {
                if (frameBmp != null) frameBmp.Dispose();
                frameBmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            }

            using (var g = Graphics.FromImage(frameBmp))
            {
                DrawSpectrumToBitmap(g, Width, Height);
            }

            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                screenDc = GetDC(IntPtr.Zero);
                memDc = CreateCompatibleDC(screenDc);
                hBitmap = frameBmp.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);

                WinSize size = new WinSize(Width, Height);
                WinPoint pointSource = new WinPoint(0, 0);
                WinPoint topPos = new WinPoint(Left, Top);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = 0; // AC_SRC_OVER
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1; // AC_SRC_ALPHA

                UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref pointSource, 0, ref blend, 2);
                UpdateZOrderBehindPet();
            }
            catch { }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop();
                if (vizTimer != null)
                {
                    vizTimer.Dispose();
                    vizTimer = null;
                }
                if (frameBmp != null)
                {
                    frameBmp.Dispose();
                    frameBmp = null;
                }
            }
            base.Dispose(disposing);
        }
    }

    // =========================================================================
    // 桌宠主体窗体：保持固定尺寸，不随展开而 resize，杜绝黑闪与乱跳
    // =========================================================================
    class PetForm : Form
    {
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint GW_OWNER = 4;
        [DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        const int WH_KEYBOARD_LL = 13;
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        // ---------- 路径 ----------
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string gifDir, voiceDir, cfgDir;

        // ---------- 配置 ----------
        bool clockOn = true, outlineOn = true, audioVizOn = true;
        bool fullscreenHide = false;
        bool isHiddenByFullscreen = false;
        Timer fsTimer;
        // GitHub 直链自动更新配置与状态
        bool autoUpdateOn = true;
        string updateUrl = "";
        Timer updateTimer;
        bool isCheckingUpdate = false;
        // 自动动作检测
        Timer autoTimer;
        IntPtr kbHook = IntPtr.Zero;
        LowLevelKeyboardProc kbProc;
        DateTime lastKeyTime = DateTime.MinValue;
        bool manualMode = false; // 用户手动选择模式时不自动切换
        AudioVizForm audioViz;
        public AudioVizForm AudioViz { get { return audioViz; } }
        string city = "changzhou";
        List<PetIcon> icons = new List<PetIcon>();
        List<TodoItem> todos = new List<TodoItem>();
        string cfgPath;

        public List<PetIcon> Icons { get { return icons; } }

        // ---------- 可调参数（控制面板） ----------
        public double BubbleScale = 1.0;      // 气泡宽度倍率
        public int BubbleDepth = 60;          // 气泡深浅 0-100
        public int BubbleOffset = 0;          // 气泡垂直偏移
        public int BubbleDur = 4;             // 气泡显示秒数
        public int IconSize = 48;             // 图标尺寸
        public int RingBase = 150;            // 扇形基准半径
        public int RingGap = 100;             // 层距
        public int RingSpan = 70;             // 图标角度跨度（度，越大越散开）
        public int RingCyAdj = 0;             // 圆心垂直调整
        public int RingCxAdj = 0;             // 圆心水平调整
        public int AudioVizCx = 0;            // 音频波形水平偏移
        public int AudioVizCy = 0;            // 音频波形垂直偏移
        public int AudioVizSize = 100;         // 音频波形大小（百分比）
        public int AudioOutAmp = 100;         // 外层振幅倍率（百分比）
        public int AudioInAmp = 100;          // 内层振幅倍率（百分比）
        public int AudioSpeed = 100;          // 音频响应速度（百分比）
        public int ClockBrightness = 100;      // 时间框颜色明度（百分比）
        public bool WalkEnabled = true;       // 走路开关
        public int WalkSpeed = 6;             // 走路速度
        public List<string> ExtraLines = new List<string>(); // 附加语录

        // ---------- 状态 ----------
        string mode = "idle";
        int facing = 1; // 1 右, -1 左
        bool dragging = false, dragMoved = false;
        Point dragStartScreen; // 使用全局屏幕坐标拖拽，杜绝坐标跳动
        Point formStartLoc;
        DateTime lastClickAt = DateTime.MinValue;
        Point lastClickScreen;
        Timer clickDelayTimer;

        Dictionary<string, GifClip> clips = new Dictionary<string, GifClip>();
        int frameIdx = 0;
        Timer animTimer, walkTimer, clockTimer, idleTimer;
        int walkEndTick = 0;
        bool ringOpen = false;
        RingOverlayForm ringOverlay;

        Panel bubble;
        Label bubbleText, clockLabel;
        PictureBox petBox;
        public PictureBox PetBox { get { return petBox; } }
        int tomatoLeft = 0; // 番茄钟剩余秒
        Timer tomatoTimer;
        Label tomatoLabel;
        ContextMenuStrip menu;
        Random rnd = new Random();

        // ---------- 窗口几何（随桌宠大小缩放） ----------
        public double PetScale = 1.0;        // 桌宠大小 0.4-2.0
        public int Nw { get { return Math.Max(120, (int)(240 * PetScale)); } }
        public int Nh { get { return Math.Max(200, (int)(430 * PetScale)); } }
        public int FootY { get { return Math.Max(180, (int)(400 * PetScale)); } }
        public int PetH { get { return Math.Max(140, (int)(320 * PetScale)); } }
        const int MID_H = 480;               // GIF 帧缓存中间高度，播放时按 PetScale 再缩放

        public PetForm()
        {
            gifDir = Path.Combine(baseDir, "assets", "gifs");
            string altGif1 = Path.Combine(baseDir, "gif");
            string altGif2 = Path.Combine(baseDir, "assets", "gif");
            if ((!Directory.Exists(gifDir) || Directory.GetFiles(gifDir, "*.gif").Length < 5))
            {
                if (Directory.Exists(altGif1) && Directory.GetFiles(altGif1, "*.gif").Length > 0)
                    gifDir = altGif1;
                else if (Directory.Exists(altGif2) && Directory.GetFiles(altGif2, "*.gif").Length > 0)
                    gifDir = altGif2;
            }
            voiceDir = Path.Combine(baseDir, "assets", "voices");
            cfgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZhuoChong");
            Directory.CreateDirectory(cfgDir);
            cfgPath = Path.Combine(cfgDir, "config.xml");

            LoadConfig();
            LoadGifs();

            // 窗体
            Text = "桌宠";
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            StartPosition = FormStartPosition.Manual;

            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Nw - 40, wa.Bottom - Nh - 60);
            Size = new Size(Nw, Nh);
            DoubleBuffered = true;

            // 角色显示
            petBox = new PictureBox
            {
                BackColor = Color.Magenta,
                Size = new Size(Nw, PetH),
                Location = new Point(0, FootY - PetH),
                SizeMode = PictureBoxSizeMode.StretchImage
            };
            petBox.MouseDown += OnPetDown;
            petBox.MouseMove += OnPetMove;
            petBox.MouseUp += OnPetUp;
            Controls.Add(petBox);

            // 时间徽章（紧挨角色脚下）
            clockLabel = new Label
            {
                BackColor = Color.FromArgb(110, 80, 160),
                ForeColor = Color.FromArgb(255, 240, 180),
                Font = new Font("Microsoft YaHei", 9, FontStyle.Regular),
                AutoSize = true,
                Padding = new Padding(8, 3, 8, 3),
                Location = new Point(Nw / 2, FootY + 2),
                Text = "--:--"
            };
            Controls.Add(clockLabel);

            // 气泡
            bubble = new Panel
            {
                BackColor = Color.FromArgb(50, 30, 80),
                Visible = false,
                AutoSize = false,
                MaximumSize = new Size(Nw - 20, 0)
            };
            bubbleText = new Label
            {
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 10),
                BackColor = Color.FromArgb(50, 30, 80),
                AutoSize = true,
                MaximumSize = new Size(Nw - 44, 0),
                Padding = new Padding(0)
            };
            bubble.Controls.Add(bubbleText);
            Controls.Add(bubble);
            bubble.Click += (s, e) => HideBubble();
            bubbleText.Click += (s, e) => HideBubble();
            bubble.BringToFront();

            // 番茄钟标签
            tomatoLabel = new Label
            {
                BackColor = Color.FromArgb(110, 80, 160),
                ForeColor = Color.FromArgb(255, 180, 195),
                Font = new Font("Microsoft YaHei", 9),
                AutoSize = true,
                Padding = new Padding(8, 3, 8, 3),
                Location = new Point(Nw / 2 - 30, FootY + 2),
                Visible = false
            };
            Controls.Add(tomatoLabel);

            // 右键菜单
            menu = new ContextMenuStrip();
            menu.RenderMode = ToolStripRenderMode.System;
            menu.BackColor = Color.FromArgb(42, 24, 66);
            menu.ForeColor = Color.White;
            BuildMenu();

            // 扇形图标覆盖层
            ringOverlay = new RingOverlayForm(this);

            // 定时器
            animTimer = new Timer { Interval = 50 };
            animTimer.Tick += (s, e) => PlayFrame();
            animTimer.Start();

            clockTimer = new Timer { Interval = 1000 };
            clockTimer.Tick += (s, e) => UpdateClock();
            clockTimer.Start();

            // 80ms 平滑位移定时器（适度缩短屏幕移动距离，与 animTimer 完全独立，不改动 GIF 帧速率）
            walkTimer = new Timer { Interval = 80 };
            walkTimer.Tick += (s, e) => StepWalk();

            idleTimer = new Timer { Interval = 9000 };
            idleTimer.Tick += (s, e) => RandomAction();
            idleTimer.Start();

            tomatoTimer = new Timer { Interval = 1000 };
            tomatoTimer.Tick += (s, e) => TomatoTick();

            // 全屏检测定时器
            fsTimer = new Timer { Interval = 500 };
            fsTimer.Tick += (s, e) => CheckFullscreen();
            if (fullscreenHide) fsTimer.Start();

            // 自动动作检测定时器（200ms 高精度驱动唱歌 20s-8s-20s 循环与打字状态）
            autoTimer = new Timer { Interval = 200 };
            autoTimer.Tick += (s, e) => AutoModeCheck();
            autoTimer.Start();

            // 全局键盘钩子
            kbProc = OnLowLevelKeyboard;
            kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, kbProc, System.Diagnostics.Process.GetCurrentProcess().MainModule.BaseAddress, 0);

            // GitHub 自动更新定时器（每 15 分钟后台静默检测一次）
            updateTimer = new Timer { Interval = 15 * 60 * 1000 };
            updateTimer.Tick += (s, e) =>
            {
                if (autoUpdateOn && !string.IsNullOrWhiteSpace(updateUrl))
                    CheckGitHubUpdateAsync(false);
            };
            if (autoUpdateOn) updateTimer.Start();

            SetMode("idle");
            Shown += (s, e) =>
            {
                if (audioVizOn)
                {
                    if (audioViz == null || audioViz.IsDisposed) audioViz = new AudioVizForm(this);
                    audioViz.SyncToPet();
                    audioViz.Show();
                    audioViz.Start();
                }
                CleanupOldUpdateBackup();
                foreach (string a in Environment.GetCommandLineArgs())
                {
                    if (a == "--updated")
                    {
                        ShowBubble("✨ 已通过 GitHub 自动同步至最新版本！", 4000);
                        break;
                    }
                }
                // 启动 6 秒后后台静默检查一次 GitHub 更新
                if (autoUpdateOn && !string.IsNullOrWhiteSpace(updateUrl))
                {
                    var startCheckTimer = new Timer { Interval = 6000 };
                    startCheckTimer.Tick += (se, ea) =>
                    {
                        startCheckTimer.Stop();
                        startCheckTimer.Dispose();
                        if (autoUpdateOn && !string.IsNullOrWhiteSpace(updateUrl))
                            CheckGitHubUpdateAsync(false);
                    };
                    startCheckTimer.Start();
                }
            };
        }

        // ---------- 配置持久化 ----------
        void LoadConfig()
        {
            try
            {
                if (!File.Exists(cfgPath)) return;
                var doc = new XmlDocument();
                doc.Load(cfgPath);
                var root = doc.DocumentElement;
                if (root == null) return;

                clockOn = GetXml(root, "clock", "1") != "0";
                outlineOn = GetXml(root, "outline", "1") != "0";
                audioVizOn = GetXml(root, "audioViz", "1") != "0";
                fullscreenHide = GetXml(root, "fullscreenHide", "0") != "0";
                autoUpdateOn = GetXml(root, "autoUpdate", "1") != "0";
                updateUrl = GetXml(root, "updateUrl", "").Trim();
                city = GetXml(root, "city", "changzhou");

                icons.Clear();
                var ic = root.SelectSingleNode("icons");
                if (ic != null)
                {
                    foreach (XmlNode n in ic.ChildNodes)
                        if (n.Name == "icon")
                            icons.Add(new PetIcon(GetXml(n, "name", ""), GetXml(n, "path", "")));
                }

                todos.Clear();
                var td = root.SelectSingleNode("todos");
                if (td != null)
                {
                    foreach (XmlNode n in td.ChildNodes)
                        if (n.Name == "todo")
                            todos.Add(new TodoItem(GetXml(n, "text", ""), GetXml(n, "done", "0") == "1"));
                }

                BubbleScale = DoubleXml(root, "bubbleScale", 1.0);
                BubbleDepth = IntXml(root, "bubbleDepth", 60);
                BubbleOffset = IntXml(root, "bubbleOffset", 0);
                BubbleDur = IntXml(root, "bubbleDur", 4);
                IconSize = IntXml(root, "iconSize", 48);
                RingBase = IntXml(root, "ringBase", 150);
                RingGap = IntXml(root, "ringGap", 100);
                RingSpan = IntXml(root, "ringSpan", 70);
                RingCyAdj = IntXml(root, "ringCyAdj", 0);
                RingCxAdj = IntXml(root, "ringCxAdj", 0);
                AudioVizCx = IntXml(root, "audioVizCx", 0);
                AudioVizCy = IntXml(root, "audioVizCy", 0);
                AudioVizSize = IntXml(root, "audioVizSize", 100);
                AudioOutAmp = IntXml(root, "audioOutAmp", 100);
                AudioInAmp = IntXml(root, "audioInAmp", 100);
                AudioSpeed = IntXml(root, "audioSpeed", 100);
                ClockBrightness = IntXml(root, "clockBrightness", 100);
                WalkEnabled = GetXml(root, "walkEnabled", "1") != "0";
                WalkSpeed = IntXml(root, "walkSpeed", 6);
                PetScale = DoubleXml(root, "petScale", 1.0);
                if (PetScale < 0.4) PetScale = 1.0;

                ExtraLines.Clear();
                var el = root.SelectSingleNode("extraLines");
                if (el != null)
                {
                    foreach (XmlNode n in el.ChildNodes)
                        if (n.Name == "line" && !string.IsNullOrWhiteSpace(n.InnerText))
                            ExtraLines.Add(n.InnerText);
                }
            }
            catch { }
            LoadUpdateUrlFromFileIfEmpty();
        }

        static int IntXml(XmlNode n, string name, int def)
        {
            int v; return int.TryParse(GetXml(n, name, def.ToString()), out v) ? v : def;
        }

        static double DoubleXml(XmlNode n, string name, double def)
        {
            double v;
            return double.TryParse(GetXml(n, name, def.ToString(CultureInfo.InvariantCulture)),
                NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        static string GetXml(XmlNode n, string name, string def)
        {
            var c = n.SelectSingleNode(name);
            return c == null ? def : c.InnerText;
        }

        public void SaveConfig()
        {
            try
            {
                var doc = new XmlDocument();
                var root = doc.CreateElement("config");
                doc.AppendChild(root);
                AppendXml(root, "clock", clockOn ? "1" : "0");
                AppendXml(root, "outline", outlineOn ? "1" : "0");
                AppendXml(root, "audioViz", audioVizOn ? "1" : "0");
                AppendXml(root, "fullscreenHide", fullscreenHide ? "1" : "0");
                AppendXml(root, "autoUpdate", autoUpdateOn ? "1" : "0");
                AppendXml(root, "updateUrl", updateUrl);
                AppendXml(root, "city", city);

                var ic = doc.CreateElement("icons");
                root.AppendChild(ic);
                foreach (var p in icons)
                {
                    var n = doc.CreateElement("icon");
                    AppendXml(n, "name", p.Name);
                    AppendXml(n, "path", p.Path);
                    ic.AppendChild(n);
                }

                var td = doc.CreateElement("todos");
                root.AppendChild(td);
                foreach (var t in todos)
                {
                    var n = doc.CreateElement("todo");
                    AppendXml(n, "text", t.Text);
                    AppendXml(n, "done", t.Done ? "1" : "0");
                    td.AppendChild(n);
                }

                AppendXml(root, "bubbleScale", BubbleScale.ToString("0.##", CultureInfo.InvariantCulture));
                AppendXml(root, "bubbleDepth", BubbleDepth.ToString());
                AppendXml(root, "bubbleOffset", BubbleOffset.ToString());
                AppendXml(root, "bubbleDur", BubbleDur.ToString());
                AppendXml(root, "iconSize", IconSize.ToString());
                AppendXml(root, "ringBase", RingBase.ToString());
                AppendXml(root, "ringGap", RingGap.ToString());
                AppendXml(root, "ringSpan", RingSpan.ToString());
                AppendXml(root, "ringCyAdj", RingCyAdj.ToString());
                AppendXml(root, "ringCxAdj", RingCxAdj.ToString());
                AppendXml(root, "audioVizCx", AudioVizCx.ToString());
                AppendXml(root, "audioVizCy", AudioVizCy.ToString());
                AppendXml(root, "audioVizSize", AudioVizSize.ToString());
                AppendXml(root, "audioOutAmp", AudioOutAmp.ToString());
                AppendXml(root, "audioInAmp", AudioInAmp.ToString());
                AppendXml(root, "audioSpeed", AudioSpeed.ToString());
                AppendXml(root, "clockBrightness", ClockBrightness.ToString());
                AppendXml(root, "walkEnabled", WalkEnabled ? "1" : "0");
                AppendXml(root, "walkSpeed", WalkSpeed.ToString());
                AppendXml(root, "petScale", PetScale.ToString("0.##", CultureInfo.InvariantCulture));

                var el = doc.CreateElement("extraLines");
                root.AppendChild(el);
                foreach (var ln in ExtraLines)
                {
                    var n = doc.CreateElement("line");
                    n.InnerText = ln;
                    el.AppendChild(n);
                }

                doc.Save(cfgPath);
            }
            catch { }
        }

        void AppendXml(XmlNode n, string name, string val)
        {
            var doc = n.OwnerDocument;
            var c = doc.CreateElement(name);
            c.InnerText = val ?? "";
            n.AppendChild(c);
        }

        // ---------- GIF 加载与边缘半透明二值化 ----------
        static Bitmap KillSemiAlpha(Bitmap src)
        {
            int w = src.Width, h = src.Height;
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var srcData = src.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dstData = dst.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int bytes = srcData.Stride * h;
            var buf = new byte[bytes];
            Marshal.Copy(srcData.Scan0, buf, 0, bytes);
            for (int y = 0; y < h; y++)
            {
                int row = y * srcData.Stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    byte a = buf[i + 3];
                    byte r = buf[i + 2], g = buf[i + 1], b = buf[i];
                    bool edgeNoise = false;
                    // 只删真正纯白的噪点（RGB都很高），浅灰色头发不删
                    if (a == 255 && r > 225 && g > 225 && b > 225)
                    {
                        int transparentCount = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int ny = y + dy, nx = x + dx;
                                if (ny < 0 || ny >= h || nx < 0 || nx >= w) continue;
                                int j = ny * srcData.Stride + nx * 4;
                                if (buf[j] == 255 && buf[j+1] == 0 && buf[j+2] == 255) transparentCount++;
                            }
                        }
                        // 纯白噪点：8邻域至少3个透明邻居才算（不要误删头发）
                        if (transparentCount >= 3) edgeNoise = true;
                    }
                    bool isPinkEdge = r > 180 && b > 180 && g < 180 && Math.Abs(r - b) < 60;
                    if (a < 160 || edgeNoise || isPinkEdge)
                    {
                        buf[i] = 255; buf[i + 1] = 0; buf[i + 2] = 255; buf[i + 3] = 255;
                    }
                    else if (a != 255)
                    {
                        buf[i + 3] = 255;
                    }
                }
            }
            Marshal.Copy(buf, 0, dstData.Scan0, bytes);
            src.UnlockBits(srcData);
            dst.UnlockBits(dstData);
            return dst;
        }

        void LoadGifs()
        {
            if (petBox != null) petBox.Image = null;
            // 先释放旧 GIF 帧，防止重加载时内存泄漏
            foreach (var oldClip in clips.Values)
            {
                foreach (var f in oldClip.Frames) f.Dispose();
                oldClip.Frames.Clear();
            }
            clips.Clear();

            // 加载所有GIF模式（在加载阶段直接缩放至显示尺寸 PetH 并完成边缘去半透明处理，立即释放原始大图，内存占用降低约 85%~90%）
            int targetH = PetH;
            var gifMap = new Dictionary<string, string[]>
            {
                { "idle", new string[] { "待机.gif", "idle.gif" } },
                { "idle2", new string[] { "站姿待机1.gif", "idle2.gif" } },
                { "eat", new string[] { "咀嚼.gif", "eat.gif" } },
                { "sit", new string[] { "蹲.gif", "sit.gif" } },
                { "sit_read", new string[] { "蹲姿看书.gif", "sit_read.gif" } },
                { "walk", new string[] { "右走.gif", "walk.gif" } },
                { "walk_left", new string[] { "左走.gif", "walk_left.gif" } },
                { "sing", new string[] { "唱歌.gif", "sing.gif" } },
                { "typing", new string[] { "打字.gif", "typing.gif" } },
                { "typing1", new string[] { "打字1.gif", "typing1.gif" } },
            };
            string[] searchDirs = new string[]
            {
                gifDir,
                Path.Combine(baseDir, "assets", "gifs"),
                Path.Combine(baseDir, "gif"),
                Path.Combine(baseDir, "assets", "gif"),
                baseDir
            };
            foreach (var kv in gifMap)
            {
                string m = kv.Key;
                string path = null;
                foreach (string dir in searchDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (string candidate in kv.Value)
                    {
                        string p = Path.Combine(dir, candidate);
                        if (File.Exists(p)) { path = p; break; }
                    }
                    if (path != null) break;
                }
                if (path == null) continue;
                try
                {
                    using (var img = Image.FromFile(path))
                    {
                        var dim = new FrameDimension(img.FrameDimensionsList[0]);
                        int n = img.GetFrameCount(dim);
                        byte[] delayBytes = null;
                        try
                        {
                            if (img.PropertyIdList.Length > 0)
                            {
                                var prop = img.GetPropertyItem(0x5100);
                                if (prop != null) delayBytes = prop.Value;
                            }
                        }
                        catch { }

                        int dh = targetH;
                        int dw = Math.Max(10, (int)((long)img.Width * dh / img.Height));
                        var clip = new GifClip();
                        for (int i = 0; i < n; i++)
                        {
                            img.SelectActiveFrame(dim, i);
                            var scaled = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
                            using (var g = Graphics.FromImage(scaled))
                            {
                                g.Clear(Color.Magenta);
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.DrawImage(img, 0, 0, dw, dh);
                            }
                            if (outlineOn)
                            {
                                var clean = KillSemiAlpha(scaled);
                                scaled.Dispose();
                                scaled = clean;
                            }
                            clip.Frames.Add(scaled);

                            int d = 100;
                            if (delayBytes != null && delayBytes.Length >= (i + 1) * 4)
                            {
                                int v = BitConverter.ToInt32(delayBytes, i * 4);
                                d = Math.Max(10, v * 10);
                            }
                            clip.Delays.Add(d);
                        }
                        clips[m] = clip;
                    }
                }
                catch { }
            }
            // 主动触发垃圾回收，立即归还解码过程中的大对象堆内存
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // ---------- 动画播放 ----------
        void SetMode(string m)
        {
            if (m == "walk" && !clips.ContainsKey("walk") && !clips.ContainsKey("idle"))
                m = "idle";
            mode = m;
            frameIdx = 0;
            if (mode == "walk" || mode == "walk_left")
            {
                walkTimer.Start();
            }
            else
            {
                walkTimer.Stop();
                walkEndTick = 0;
            }
        }

        void PlayFrame()
        {
            string animMode = mode;
            if (!clips.ContainsKey(animMode))
            {
                if (clips.ContainsKey("idle")) animMode = "idle";
                else return;
            }
            var clip = clips[animMode];
            if (clip.Frames.Count == 0) return;

            frameIdx %= clip.Frames.Count;
            // 直接使用加载阶段预缩放并去半透明的缓存帧，零内存分配、零重复计算
            var bmp = clip.Frames[frameIdx];

            if (petBox != null)
            {
                petBox.Image = bmp;
                if (petBox.Width != bmp.Width || petBox.Height != bmp.Height)
                {
                    petBox.Size = new Size(bmp.Width, bmp.Height);
                    petBox.Location = new Point((Nw - bmp.Width) / 2, FootY - bmp.Height);
                }
            }

            frameIdx++;
            int d = clip.Delays[frameIdx - 1];
            if (d >= 30) { animTimer.Interval = d; }
        }

        // ---------- 走路 ----------
        void StepWalk()
        {
            if ((mode != "walk" && mode != "walk_left") || ringOpen || dragging) return;
            // 打字时严格禁止移动或切换走路状态
            if (IsTypingActive) return;

            // 普通非唱歌状态下的步行时间结束则自动切回 idle（唱歌穿插阶段的 8 秒走路严格由 AutoModeCheck 的 singPhaseStart 控制）
            if (!isSingingActive && walkEndTick != 0 && Environment.TickCount >= walkEndTick)
            {
                walkEndTick = 0;
                SetMode("idle");
                return;
            }

            // 微调缩短单步位移（比上一版缩短约 65%，比最早版本略快，且完全不改动 GIF 迈步帧率）
            int stepPx = Math.Max(1, (int)Math.Round(WalkSpeed * 0.55 * PetScale));
            int x = Location.X;
            var wa = Screen.FromControl(this).WorkingArea;
            // 先判断边界，决定方向
            int testX = x + facing * stepPx;
            if (testX <= wa.X + 4) { facing = 1; }
            else if (testX + Width >= wa.X + wa.Width - 4) { facing = -1; }
            // 再按最终方向移动
            Location = new Point(Location.X + facing * stepPx, Location.Y);
            // 根据方向选择走路GIF（不同方向用不同GIF，不翻转画面）
            string walkMode = facing == 1 ? "walk" : "walk_left";
            if (mode != walkMode && clips.ContainsKey(walkMode))
            {
                mode = walkMode;
                frameIdx = 0;
                if (isSingingActive && singPhase == SingPhase.Interlude)
                {
                    interludeMode = walkMode;
                }
            }
            if (audioVizOn && audioViz != null && !audioViz.IsDisposed && audioViz.Visible)
            {
                audioViz.SyncToPet();
            }
        }

        bool IsTypingActive
        {
            get { return (DateTime.Now - lastKeyTime).TotalSeconds < 1.5; }
        }

        // ---------- 随机行为（仅在既不打字也不唱歌的普通待机状态下生效） ----------
        void RandomAction()
        {
            if (ringOpen || dragging || isHiddenByFullscreen || manualMode) return;
            // 打字状态或唱歌循环状态（含20s唱歌与8s穿插阶段）下绝对禁止触发普通随机动作！
            if (IsTypingActive || mode == "typing" || mode == "typing1") return;
            if (isSingingActive || mode == "sing") return;
            if (audioViz != null && !audioViz.IsDisposed && audioVizOn)
            {
                try
                {
                    if (audioViz.GetSpectrumEnergy() > 0.012f || (DateTime.Now - lastSoundActiveTime).TotalSeconds < 3.5)
                        return;
                }
                catch { }
            }

            string nextMode = PickRandomPerformMode();
            if (nextMode == "walk" || nextMode == "walk_left")
            {
                if (!WalkEnabled)
                {
                    SetMode("idle");
                }
                else
                {
                    facing = (nextMode == "walk") ? 1 : -1;
                    SetMode(nextMode);
                    int duration = 2500 + rnd.Next(4000);
                    walkEndTick = Environment.TickCount + duration;
                }
            }
            else
            {
                SetMode(nextMode);
            }

            if (rnd.NextDouble() < 0.28 && (mode == "idle" || mode == "idle2" || mode == "eat" || mode == "sit" || mode == "sit_read"))
            {
                ShowBubble(Lines.Greeting(ExtraLines), 4000);
            }
            idleTimer.Interval = 6000 + rnd.Next(7000);
        }

        // 从 待机.gif / 站姿待机1.gif / 咀嚼.gif / 右走.gif / 左走.gif / 蹲.gif / 蹲姿看书.gif 中随机抽取动作
        string PickRandomPerformMode(bool forceIncludeWalk = false)
        {
            var pool = new List<string>();
            if (clips.ContainsKey("idle")) pool.Add("idle");
            if (clips.ContainsKey("idle2")) pool.Add("idle2");
            if (clips.ContainsKey("eat")) pool.Add("eat");
            if (clips.ContainsKey("sit")) pool.Add("sit");
            if (clips.ContainsKey("sit_read")) pool.Add("sit_read");
            if (forceIncludeWalk || WalkEnabled)
            {
                if (clips.ContainsKey("walk")) pool.Add("walk");
                if (clips.ContainsKey("walk_left")) pool.Add("walk_left");
            }
            if (pool.Count == 0) return "idle";
            if (pool.Count == 1) return pool[0];
            // 尽量挑选与当前不同的动作，让穿插变化更明显
            string pick = pool[rnd.Next(pool.Count)];
            for (int tries = 0; tries < 4 && pick == mode; tries++)
            {
                pick = pool[rnd.Next(pool.Count)];
            }
            return pick;
        }

        // ---------- 拖动与双击交互（使用屏幕坐标，杜绝乱跳） ----------
        void OnPetDown(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            dragMoved = false;
            dragStartScreen = Cursor.Position;
            formStartLoc = Location;
        }

        void OnPetMove(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            Point cur = Cursor.Position;
            int dx = cur.X - dragStartScreen.X;
            int dy = cur.Y - dragStartScreen.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 8)
            {
                dragMoved = true;
                if (ringOpen) CloseRing(); // 拖拽时自动收起扇形图标，体验更平滑
            }
            if (dragMoved)
            {
                Location = new Point(formStartLoc.X + dx, formStartLoc.Y + dy);
                // 拖动时实时同步扇形图标窗口位置，避免图标留在原地看起来"乱动"
                if (ringOpen && ringOverlay != null && !ringOverlay.IsDisposed && ringOverlay.Visible)
                {
                    ringOverlay.SyncToPet();
                }
                if (audioVizOn && audioViz != null && !audioViz.IsDisposed && audioViz.Visible)
                {
                    audioViz.SyncToPet();
                }
            }
        }

        void OnPetUp(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = false;

            if (!dragMoved)
            {
                var now = DateTime.Now;
                var pos = Cursor.Position;
                bool isDouble = (now - lastClickAt).TotalMilliseconds < 450
                    && Math.Abs(pos.X - lastClickScreen.X) < 20
                    && Math.Abs(pos.Y - lastClickScreen.Y) < 20;

                if (isDouble)
                {
                    // 消费双击事件，重置时间防止三击反弹
                    lastClickAt = DateTime.MinValue;
                    if (clickDelayTimer != null)
                    {
                        clickDelayTimer.Stop();
                        clickDelayTimer.Dispose();
                        clickDelayTimer = null;
                    }
                    if (ringOpen) CloseRing();
                    else OpenRing();
                }
                else
                {
                    lastClickAt = now;
                    lastClickScreen = pos;
                    if (clickDelayTimer != null)
                    {
                        clickDelayTimer.Stop();
                        clickDelayTimer.Dispose();
                        clickDelayTimer = null;
                    }
                    var t = new Timer { Interval = 450 };
                    t.Tick += (se, ea) =>
                    {
                        t.Stop();
                        t.Dispose();
                        if (clickDelayTimer == t) clickDelayTimer = null;
                        if (!IsDisposed && IsHandleCreated)
                        {
                            try { ShowBubble(Lines.Click(), 2500); } catch { }
                        }
                    };
                    t.Start();
                    clickDelayTimer = t;
                }
            }
        }

        // ---------- 扇形展开与收起 ----------
        public void OpenRing()
        {
            if (ringOpen) return;
            ringOpen = true;

            if (mode == "walk" || mode == "walk_left") SetMode("idle");

            if (icons.Count == 0)
            {
                ringOpen = false;
                ShowBubble("还没有图标，本宫带你先收录一个。", 3000);
                var t = new Timer { Interval = 900 };
                t.Tick += (se, ea) => { t.Stop(); t.Dispose(); AddIcon(); };
                t.Start();
                return;
            }

            if (ringOverlay == null || ringOverlay.IsDisposed)
                ringOverlay = new RingOverlayForm(this);

            ringOverlay.ShowRing();
        }

        public void CloseRing()
        {
            if (!ringOpen) return;
            ringOpen = false;
            if (ringOverlay != null && !ringOverlay.IsDisposed)
            {
                ringOverlay.HideRing();
            }
        }

        public void OnRingClosed()
        {
            ringOpen = false;
        }

        public void RebuildIcons()
        {
            if (ringOverlay != null && !ringOverlay.IsDisposed)
            {
                ringOverlay.RebuildIcons();
            }
        }

        // ---------- 图标管理 ----------
        Form dialogHost;
        Form GetDialogHost()
        {
            if (dialogHost == null || dialogHost.IsDisposed)
            {
                dialogHost = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    TopMost = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-32000, -32000),
                    Size = new Size(1, 1),
                    Opacity = 0
                };
                dialogHost.Show();
                dialogHost.Hide();
            }
            IntPtr h = dialogHost.Handle;
            SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            return dialogHost;
        }

        public void AddIcon()
        {
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "选择要收录的桌面图标";
                ofd.Filter = "程序/快捷方式/网址|*.exe;*.lnk;*.url;*.bat;*.cmd|所有文件|*.*";
                ofd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                Form host = GetDialogHost();
                // 在文件选择框弹出后的首帧立即恢复桌宠最高置顶层级，且因对话框归属独立非置顶 host，绝不遮盖桌宠
                BeginInvoke(new Action(() => EnsureTopMostZOrder()));
                if (ofd.ShowDialog(host) == DialogResult.OK)
                {
                    string name = Path.GetFileNameWithoutExtension(ofd.FileName);
                    icons.Add(new PetIcon(name, ofd.FileName));
                    SaveConfig();
                    RebuildIcons();
                    BuildMenu();
                    ShowBubble("已收录「" + name + "」，双击桌宠即可召唤。", 3000);
                }
            }
        }

        // ---------- 右键菜单 ----------
        void BuildMenu()
        {
            menu.Items.Clear();

            menu.Items.Add("控制面板", null, (s, e) => ShowPanel());

            var iconMgr = new ToolStripMenuItem("图标管理");
            iconMgr.DropDownItems.Add("添加图标…", null, (s, e) => AddIcon());
            var delMenu = new ToolStripMenuItem("删除图标…");
            foreach (var ic in icons)
            {
                var target = ic;
                delMenu.DropDownItems.Add(target.Name, null, (s, e) =>
                {
                    icons.Remove(target);
                    SaveConfig();
                    RebuildIcons();
                    BuildMenu();
                });
            }
            iconMgr.DropDownItems.Add(delMenu);
            menu.Items.Add(iconMgr);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(clockOn ? "隐藏时间" : "显示时间", null, (s, e) => ToggleClock());

            var pom = new ToolStripMenuItem("番茄钟");
            pom.DropDownItems.Add("开始 25 分钟", null, (s, e) => StartTomato(25 * 60));
            pom.DropDownItems.Add("开始 45 分钟", null, (s, e) => StartTomato(45 * 60));
            pom.DropDownItems.Add("停止番茄钟", null, (s, e) => StopTomato());
            menu.Items.Add(pom);

            var tdl = new ToolStripMenuItem("待办清单");
            tdl.DropDownItems.Add("添加待办…", null, (s, e) => AddTodo());
            tdl.DropDownItems.Add("查看待办", null, (s, e) => ShowTodos());
            var toggleMenu = new ToolStripMenuItem("切换状态…");
            for (int i = 0; i < todos.Count; i++)
            {
                var idx = i;
                var item = todos[idx];
                toggleMenu.DropDownItems.Add((item.Done ? "☑ " : "☐ ") + item.Text, null, (s, e) =>
                {
                    item.Done = !item.Done;
                    SaveConfig();
                    BuildMenu();
                });
            }
            tdl.DropDownItems.Add(toggleMenu);
            tdl.DropDownItems.Add("清空待办", null, (s, e) => { todos.Clear(); SaveConfig(); BuildMenu(); });
            menu.Items.Add(tdl);

            var wea = new ToolStripMenuItem("天气");
            wea.DropDownItems.Add("刷新天气", null, (s, e) => RefreshWeather());
            wea.DropDownItems.Add("设置城市…", null, (s, e) => SetCity());
            menu.Items.Add(wea);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(outlineOn ? "✨ 边缘描边已消除（点击显示）" : "✨ 边缘描边已显示（点击消除）", null, (s, e) => ToggleOutline());
            menu.Items.Add(audioVizOn ? "🎵 音频频谱已开（点击关闭）" : "🎵 音频频谱已关（点击开启）", null, (s, e) => ToggleAudioViz());
            menu.Items.Add(fullscreenHide ? "🖥️ 全屏隐藏已开（点击关闭）" : "🖥️ 全屏隐藏已关（点击开启）", null, (s, e) => ToggleFullscreenHide());

            var updMenu = new ToolStripMenuItem("🔄 GitHub 同步更新");
            updMenu.DropDownItems.Add(autoUpdateOn ? "☑ 自动同步更新已开启（点击关闭）" : "☐ 自动同步更新已关闭（点击开启）", null, (s, e) => ToggleAutoUpdate());
            updMenu.DropDownItems.Add("⚡ 立即检查 GitHub 更新", null, (s, e) => CheckGitHubUpdateAsync(true));
            updMenu.DropDownItems.Add("🔗 设置 GitHub 更新直链…", null, (s, e) => ConfigureGitHubUpdateUrl());
            menu.Items.Add(updMenu);

            menu.Items.Add("开机自启", null, (s, e) => ToggleAutoStart());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出桌宠", null, (s, e) => { SaveConfig(); Application.Exit(); });

            foreach (Control c in Controls)
                c.ContextMenuStrip = menu;
            ContextMenuStrip = menu;
        }

        void ToggleClock()
        {
            clockOn = !clockOn;
            clockLabel.Visible = clockOn;
            SaveConfig();
            BuildMenu();
        }

        void ToggleOutline()
        {
            outlineOn = !outlineOn;
            SaveConfig();
            BuildMenu();
            // 切换边缘描边时重建预缩放缓存帧并立即回收旧内存
            LoadGifs();
            PlayFrame();
        }

        void ToggleAudioViz()
        {
            audioVizOn = !audioVizOn;
            SaveConfig();
            BuildMenu();
            if (audioVizOn)
            {
                if (audioViz == null || audioViz.IsDisposed) audioViz = new AudioVizForm(this);
                audioViz.SyncToPet();
                audioViz.Show();
                audioViz.Start();
            }
            else if (audioViz != null)
            {
                audioViz.Stop();
            }
        }

        void CheckFullscreen()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                // 排除桌宠自身及附属窗口
                if (fg == this.Handle ||
                    (ringOverlay != null && !ringOverlay.IsDisposed && fg == ringOverlay.Handle) ||
                    (audioViz != null && !audioViz.IsDisposed && fg == audioViz.Handle) ||
                    (cpanel != null && !cpanel.IsDisposed && fg == cpanel.Handle))
                {
                    if (isHiddenByFullscreen)
                    {
                        isHiddenByFullscreen = false;
                        RestoreAll();
                    }
                    return;
                }

                // 排除 Windows 桌面 (Progman / WorkerW) 与任务栏 (Shell_TrayWnd)，防止点击桌面时误判为全屏而隐藏桌宠
                var clsSb = new StringBuilder(64);
                if (GetClassName(fg, clsSb, clsSb.Capacity) > 0)
                {
                    string cls = clsSb.ToString();
                    if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd")
                    {
                        if (isHiddenByFullscreen)
                        {
                            isHiddenByFullscreen = false;
                            RestoreAll();
                        }
                        return;
                    }
                }

                RECT r;
                if (!GetWindowRect(fg, out r)) return;
                var screen = Screen.FromHandle(fg).Bounds;
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                // 窗口覆盖当前显示器整个屏幕才算全屏（兼容多显示器坐标）
                bool isFs = (w >= screen.Width - 5 && h >= screen.Height - 5 &&
                             r.Left <= screen.Left + 5 && r.Top <= screen.Top + 5);
                if (isFs && !isHiddenByFullscreen)
                {
                    isHiddenByFullscreen = true;
                    HideAll();
                }
                else if (!isFs && isHiddenByFullscreen)
                {
                    isHiddenByFullscreen = false;
                    RestoreAll();
                }
            }
            catch { }
        }

        IntPtr OnLowLevelKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 仅在按下按键 (WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104) 时记录打字并立即切入打字动画
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == 0x0100 || msg == 0x0104)
                {
                    lastKeyTime = DateTime.Now;
                    if (!manualMode && !isHiddenByFullscreen && !ringOpen && !dragging &&
                        mode != "typing" && mode != "typing1" && IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (!manualMode && mode != "typing" && mode != "typing1" &&
                                (clips.ContainsKey("typing") || clips.ContainsKey("typing1")))
                            {
                                walkEndTick = 0;
                                string pick = (clips.ContainsKey("typing") && clips.ContainsKey("typing1"))
                                    ? (rnd.Next(2) == 0 ? "typing" : "typing1")
                                    : (clips.ContainsKey("typing") ? "typing" : "typing1");
                                SetMode(pick);
                            }
                        }));
                    }
                }
            }
            return CallNextHookEx(kbHook, nCode, wParam, lParam);
        }

        DateTime lastIdleSwitch = DateTime.Now;
        DateTime lastSoundActiveTime = DateTime.MinValue;
        bool isSingingActive = false;
        // 唱歌穿插状态机：严格遵循 Singing(20s) -> Interlude(8s) -> Singing(20s) 循环
        enum SingPhase { Singing, Interlude }
        SingPhase singPhase = SingPhase.Singing;
        DateTime singPhaseStart = DateTime.Now;
        string interludeMode = "idle";

        public void EnsureTopMostZOrder()
        {
            if (IsDisposed || !IsHandleCreated || !Visible || isHiddenByFullscreen) return;
            // 右键菜单弹出时不抢层级，避免遮挡菜单
            if (menu != null && menu.Visible) return;

            // 若当前前台窗口是添加图标等弹出的文件对话框（归属在非置顶 dialogHost 下），强制将其保持在普通非置顶层，防止其继承置顶遮挡桌宠
            if (dialogHost != null && !dialogHost.IsDisposed && dialogHost.IsHandleCreated)
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != this.Handle &&
                    (ringOverlay == null || fg != ringOverlay.Handle) &&
                    (audioViz == null || fg != audioViz.Handle))
                {
                    if (GetWindow(fg, GW_OWNER) == dialogHost.Handle)
                    {
                        SetWindowPos(fg, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                    }
                }
            }

            // 1. 将桌宠主体推至 HWND_TOPMOST 最顶层（压住其他小窗、文件选择框及置顶应用，且不抢占焦点）
            SetWindowPos(this.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

            // 2. 将音频光环紧贴放置在桌宠主体正后方（同处 HWND_TOPMOST 层级，位于桌宠之下、其他所有窗口之上）
            if (audioVizOn && audioViz != null && !audioViz.IsDisposed && audioViz.Visible && audioViz.IsHandleCreated)
            {
                SetWindowPos(audioViz.Handle, this.Handle, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            }

            // 3. 若扇形快捷图标栏已展开，保持图标栏在桌宠上方以便点击
            if (ringOpen && ringOverlay != null && !ringOverlay.IsDisposed && ringOverlay.Visible && ringOverlay.IsHandleCreated)
            {
                SetWindowPos(ringOverlay.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            }
        }

        void AutoModeCheck()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (fullscreenHide) CheckFullscreen();
            if (isHiddenByFullscreen) return;
            EnsureTopMostZOrder();
            if (ringOpen || dragging) return;
            // 用户手动选择模式时不自动切换
            if (manualMode) return;

            // 1. 检测音频能量并维护 3.5 秒抗抖动保持窗口（防止歌曲换气/乐句间隙导致 20s-8s-20s 计时被中途重置）
            if (audioViz != null && !audioViz.IsDisposed && audioVizOn)
            {
                try
                {
                    float energy = audioViz.GetSpectrumEnergy();
                    if (energy > 0.012f)
                    {
                        lastSoundActiveTime = DateTime.Now;
                    }
                }
                catch { }
            }
            bool singing = (DateTime.Now - lastSoundActiveTime).TotalSeconds < 3.5;

            // 2. 打字优先级最高：打字期间绝对锁定打字动作，不穿插任何其他动作
            if (IsTypingActive && (clips.ContainsKey("typing") || clips.ContainsKey("typing1")))
            {
                walkEndTick = 0;
                if (mode != "typing" && mode != "typing1")
                {
                    string pick = (clips.ContainsKey("typing") && clips.ContainsKey("typing1"))
                        ? (rnd.Next(2) == 0 ? "typing" : "typing1")
                        : (clips.ContainsKey("typing") ? "typing" : "typing1");
                    SetMode(pick);
                }
                return;
            }

            // 若刚结束打字：如果当前正在播放音乐，立刻从头进入唱歌 20 秒阶段
            if (mode == "typing" || mode == "typing1")
            {
                if (singing && clips.ContainsKey("sing"))
                {
                    isSingingActive = true;
                    singPhase = SingPhase.Singing;
                    singPhaseStart = DateTime.Now;
                    walkEndTick = 0;
                    SetMode("sing");
                    return;
                }
                else
                {
                    isSingingActive = false;
                    SetMode("idle");
                    lastIdleSwitch = DateTime.Now;
                    return;
                }
            }

            // 3. 唱歌状态机：严格执行「唱歌 20 秒 -> 随机穿插动作 8 秒 -> 唱歌 20 秒」循环
            if (singing && clips.ContainsKey("sing"))
            {
                // 刚开始播放音乐进入唱歌状态：严格从 20 秒唱歌阶段起点开始计时（即便此前在走路或其他动作也立即切入唱歌）
                if (!isSingingActive)
                {
                    isSingingActive = true;
                    singPhase = SingPhase.Singing;
                    singPhaseStart = DateTime.Now;
                    walkEndTick = 0;
                    SetMode("sing");
                    return;
                }

                double phaseElapsed = (DateTime.Now - singPhaseStart).TotalSeconds;
                if (singPhase == SingPhase.Singing)
                {
                    if (phaseElapsed >= 20.0)
                    {
                        // 唱歌满 20 秒：切换到 8 秒随机穿插阶段（从 待机/站姿待机1/咀嚼/右走/左走/蹲/蹲姿看书 中随机选）
                        singPhase = SingPhase.Interlude;
                        singPhaseStart = DateTime.Now;
                        interludeMode = PickRandomPerformMode(true);
                        walkEndTick = 0; // 穿插阶段时长由 singPhaseStart 严格控制 8 秒
                        if (interludeMode == "walk" || interludeMode == "walk_left")
                        {
                            facing = (interludeMode == "walk") ? 1 : -1;
                        }
                        SetMode(interludeMode);
                    }
                    else if (mode != "sing")
                    {
                        // 20 秒唱歌阶段内严格锁定 sing 动作
                        walkEndTick = 0;
                        SetMode("sing");
                    }
                }
                else // SingPhase.Interlude 穿插阶段（严格持续 8 秒）
                {
                    if (phaseElapsed >= 8.0)
                    {
                        // 穿插满 8 秒：切回唱歌阶段，重新开始 20 秒计时
                        singPhase = SingPhase.Singing;
                        singPhaseStart = DateTime.Now;
                        walkEndTick = 0;
                        SetMode("sing");
                    }
                    else
                    {
                        // 8 秒穿插期内锁定该穿插动作，不被其他逻辑打断
                        if (interludeMode == "walk" || interludeMode == "walk_left")
                        {
                            if (mode != "walk" && mode != "walk_left")
                                SetMode(facing == 1 ? "walk" : "walk_left");
                        }
                        else if (mode != interludeMode)
                        {
                            SetMode(interludeMode);
                        }
                    }
                }
                return;
            }

            // 4. 非唱歌、非打字的普通状态
            if (isSingingActive)
            {
                // 音乐停止，退出唱歌循环回待机
                isSingingActive = false;
                singPhase = SingPhase.Singing;
                walkEndTick = 0;
                SetMode("idle");
                lastIdleSwitch = DateTime.Now;
                return;
            }

            if (mode == "walk" || mode == "walk_left")
            {
                return; // 普通散步由 StepWalk 的 walkEndTick 控制结束
            }

            if (mode == "sing")
            {
                SetMode("idle");
                lastIdleSwitch = DateTime.Now;
            }
        }

        void HideAll()
        {
            Hide();
            if (audioViz != null && !audioViz.IsDisposed)
            {
                audioViz.Stop();
                audioViz.Hide();
            }
            if (clockLabel != null) clockLabel.Hide();
            if (tomatoLabel != null) tomatoLabel.Hide();
            if (bubble != null) bubble.Hide();
            if (ringOverlay != null && !ringOverlay.IsDisposed) ringOverlay.HideRing();
        }

        void RestoreAll()
        {
            Show();
            if (audioViz != null && !audioViz.IsDisposed && audioVizOn)
            {
                audioViz.SyncToPet(); // 先同步位置，再显示
                audioViz.Show();
                audioViz.Start();
            }
            if (clockLabel != null) clockLabel.Visible = clockOn;
            if (tomatoLabel != null) tomatoLabel.Visible = (tomatoLeft > 0);
            if (ringOverlay != null && !ringOverlay.IsDisposed) ringOverlay.SyncToPet();
            EnsureTopMostZOrder();
        }

        void ToggleFullscreenHide()
        {
            fullscreenHide = !fullscreenHide;
            SaveConfig();
            BuildMenu();
            if (fullscreenHide)
            {
                fsTimer.Start();
                // 立即检测一次
                CheckFullscreen();
            }
            else
            {
                fsTimer.Stop();
                isHiddenByFullscreen = false;
                RestoreAll();
            }
        }

        void ToggleAutoStart()
        {
            try
            {
                bool turnedOn;
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    var cur = (string)k.GetValue("ZhuoChongPet");
                    turnedOn = cur == null;
                    if (turnedOn)
                        k.SetValue("ZhuoChongPet", "\"" + Application.ExecutablePath + "\"");
                    else
                        k.DeleteValue("ZhuoChongPet", false);
                }
                ShowBubble(turnedOn ? "已设为开机自启。可不想你忘记本宫呢。" : "已取消开机自启。", 3000);
            }
            catch { ShowBubble("开机自启设置失败。", 3000); }
        }

        // ---------- 图标参数调整节流提示 ----------
        DateTime lastRingTip = DateTime.MinValue;
        public void RingPreviewTip()
        {
            if (ringOpen) return;
            if ((DateTime.Now - lastRingTip).TotalMilliseconds < 3000) return;
            lastRingTip = DateTime.Now;
            ShowBubble("图标布局已更新，双击本宫展开扇形即可预览。", 3000);
        }

        // ---------- 气泡 ----------
        Timer bubbleTimer;
        string lastBubbleText;
        int lastBubbleMs;

        public void ShowBubble(string text, int ms)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowBubble(text, ms)));
                return;
            }
            lastBubbleText = text;
            lastBubbleMs = ms;
            if (bubbleTimer != null) { bubbleTimer.Stop(); bubbleTimer.Dispose(); bubbleTimer = null; }

            bubbleText.Text = text;
            int maxW = Math.Max(80, (int)(Width * BubbleScale));
            var sz = TextRenderer.MeasureText(text, bubbleText.Font, new Size(maxW - 44, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            int bw = Math.Min(maxW, sz.Width + 24);
            int bh = sz.Height + 14;

            int r = Math.Min(255, 100 + (BubbleDepth * 100) / 100);
            int g = Math.Min(255, 70 + (BubbleDepth * 80) / 100);
            int b = Math.Min(255, 150 + (BubbleDepth * 90) / 100);
            var col = Color.FromArgb(r, g, b);
            bubble.BackColor = col;
            bubbleText.BackColor = col;

            bubble.SuspendLayout();
            bubble.SetBounds(0, 0, bw, bh);
            bubbleText.SetBounds(12, 7, bw - 24, bh - 14);
            bubbleText.AutoSize = false;
            bubble.ResumeLayout();

            int x = (Width - bubble.Width) / 2;
            int y = FootY - PetH - bubble.Height - 8 + BubbleOffset;
            if (y < 2) y = 2;
            bubble.Location = new Point(x, y);
            bubble.Visible = true;
            bubble.BringToFront();
            ApplyRoundRegion(bubble, 10);

            if (ms <= 0) ms = BubbleDur * 1000;
            if (ms > 0)
            {
                bubbleTimer = new Timer { Interval = ms };
                bubbleTimer.Tick += (s, e) => { bubbleTimer.Stop(); HideBubble(); };
                bubbleTimer.Start();
            }
        }

        // 圆角矩形Region辅助
        System.Drawing.Drawing2D.GraphicsPath RoundRectPath(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        void ApplyRoundRegion(Control c, int radius)
        {
            if (c.Region != null) { c.Region.Dispose(); c.Region = null; }
            using (var path = RoundRectPath(new Rectangle(0, 0, c.Width, c.Height), radius))
            {
                c.Region = new Region(path);
            }
        }

        void HideBubble()
        {
            bubble.Visible = false;
            if (bubbleTimer != null) { bubbleTimer.Stop(); bubbleTimer.Dispose(); bubbleTimer = null; }
        }

        // ---------- 时钟 ----------
        public void UpdateClock()
        {
            int totalW = 0;
            int clockW = 0, tomatoW = 0;
            if (clockOn)
            {
                clockLabel.Text = DateTime.Now.ToString("HH:mm");
                clockW = TextRenderer.MeasureText(clockLabel.Text, clockLabel.Font).Width + 12;
                totalW += clockW;
            }
            if (tomatoLeft > 0)
            {
                tomatoLabel.Text = "⏳ " + TimeSpan.FromSeconds(tomatoLeft).ToString(@"mm\:ss");
                tomatoW = TextRenderer.MeasureText(tomatoLabel.Text, tomatoLabel.Font).Width + 12;
                totalW += tomatoW + 6;
            }
            int startX = (Width - totalW) / 2;
            int cur = startX;
            int br = ClockBrightness;
            int cr = Math.Min(255, 70 + (br * 60) / 100);
            int cg = Math.Min(255, 50 + (br * 40) / 100);
            int cb = Math.Min(255, 110 + (br * 50) / 100);
            Color clockCol = Color.FromArgb(cr, cg, cb);
            if (clockOn)
            {
                clockLabel.Visible = true;
                clockLabel.SetBounds(cur, FootY + 2, clockW, 24);
                clockLabel.BackColor = clockCol;
                ApplyRoundRegion(clockLabel, 8);
                cur += clockW + 6;
            }
            else clockLabel.Visible = false;

            if (tomatoLeft > 0)
            {
                tomatoLabel.Visible = true;
                tomatoLabel.SetBounds(cur, FootY + 2, tomatoW, 24);
                tomatoLabel.BackColor = clockCol;
                ApplyRoundRegion(tomatoLabel, 8);
            }
            else tomatoLabel.Visible = false;
        }

        // ---------- 番茄钟 ----------
        void StartTomato(int sec)
        {
            tomatoLeft = sec;
            tomatoTimer.Start();
            ShowBubble("番茄钟开始，" + (sec / 60) + " 分钟后本宫提醒你。", 3000);
        }
        void StopTomato()
        {
            tomatoLeft = 0;
            tomatoTimer.Stop();
            tomatoLabel.Visible = false;
            ShowBubble("番茄钟已停止。", 2500);
        }
        void TomatoTick()
        {
            if (tomatoLeft > 0)
            {
                tomatoLeft--;
                if (tomatoLeft == 0)
                {
                    tomatoTimer.Stop();
                    ShowBubble("⏰ 番茄钟结束！该休息了，小笨蛋。", 6000);
                }
            }
        }

        // ---------- 待办 ----------
        void AddTodo()
        {
            string t = SimpleInput("添加待办", "内容：");
            if (!string.IsNullOrWhiteSpace(t))
            {
                todos.Add(new TodoItem(t.Trim(), false));
                SaveConfig();
                BuildMenu();
                ShowBubble("已添加待办。", 2500);
            }
        }
        void ShowTodos()
        {
            if (todos.Count == 0) { ShowBubble("暂无待办，右键→待办清单→添加待办。", 3500); return; }
            var sb = new StringBuilder();
            int shown = 0;
            foreach (var t in todos)
            {
                if (shown >= 4) break;
                sb.Append(t.Done ? "☑ " : "☐ ").Append(t.Text).Append("\n");
                shown++;
            }
            if (todos.Count > 4) sb.Append("…共 " + todos.Count + " 条");
            ShowBubble(sb.ToString().TrimEnd('\n'), 6000);
        }

        // ---------- 天气（异步网络请求，杜绝界面卡顿假死） ----------
        void RefreshWeather()
        {
            ShowBubble("正在查询天气…", 2000);
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // Tls12
                    string url = "https://wttr.in/" + Uri.EscapeDataString(city) + "?format=%C+%t+%f";
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 8000;
                    req.UserAgent = "curl/7.68.0";
                    using (var resp = req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string s = reader.ReadToEnd().Trim();
                        BeginInvoke(new Action(() =>
                        {
                            if (s.Length > 0)
                                ShowBubble(city + "：" + s, 6000);
                            else
                                ShowBubble("天气获取失败。", 3000);
                        }));
                    }
                }
                catch
                {
                    BeginInvoke(new Action(() =>
                    {
                        ShowBubble("天气服务连接失败。", 3000);
                    }));
                }
            });
        }

        void SetCity()
        {
            string c = SimpleInput("设置城市", "城市名（拼音或英文）：", city);
            if (!string.IsNullOrWhiteSpace(c))
            {
                city = c.Trim().ToLowerInvariant();
                SaveConfig();
                RefreshWeather();
            }
        }

        // ---------- GitHub 直链多设备自动同步更新 ----------
        void LoadUpdateUrlFromFileIfEmpty()
        {
            try
            {
                string[] candidates = new string[]
                {
                    Path.Combine(baseDir, "update_url.txt"),
                    Path.Combine(baseDir, "..", "update_url.txt")
                };
                foreach (string p in candidates)
                {
                    if (File.Exists(p))
                    {
                        string[] lines = File.ReadAllLines(p, Encoding.UTF8);
                        foreach (string line in lines)
                        {
                            string t = line.Trim();
                            if (t.Length > 0 && !t.StartsWith("#") && (t.StartsWith("http://") || t.StartsWith("https://")))
                            {
                                updateUrl = NormalizeGitHubRawUrl(t);
                                return;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        static string NormalizeGitHubRawUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            string u = url.Trim();
            // 若用户粘贴的是 GitHub 普通网页链接 (https://github.com/user/repo/blob/branch/path)，自动转换为 raw 直链
            if (u.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) &&
                u.IndexOf("/blob/", StringComparison.OrdinalIgnoreCase) > 0)
            {
                u = "https://raw.githubusercontent.com/" + u.Substring("https://github.com/".Length);
                int blobIdx = u.IndexOf("/blob/", StringComparison.OrdinalIgnoreCase);
                if (blobIdx > 0)
                {
                    u = u.Substring(0, blobIdx) + "/" + u.Substring(blobIdx + "/blob/".Length);
                }
            }
            return u;
        }

        void ToggleAutoUpdate()
        {
            autoUpdateOn = !autoUpdateOn;
            SaveConfig();
            BuildMenu();
            if (autoUpdateOn)
            {
                updateTimer.Start();
                ShowBubble("已开启 GitHub 自动同步更新。", 2500);
                if (!string.IsNullOrWhiteSpace(updateUrl))
                    CheckGitHubUpdateAsync(false);
            }
            else
            {
                updateTimer.Stop();
                ShowBubble("已关闭自动同步更新。", 2500);
            }
        }

        void ConfigureGitHubUpdateUrl()
        {
            string tip = "支持 PetApp.cs 或 .exe 的 GitHub Raw/Blob 直链：";
            string input = SimpleInput("设置 GitHub 更新直链", tip, updateUrl);
            if (input != null)
            {
                updateUrl = NormalizeGitHubRawUrl(input);
                SaveConfig();
                try
                {
                    File.WriteAllText(Path.Combine(baseDir, "update_url.txt"), updateUrl + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(updateUrl))
                {
                    CheckGitHubUpdateAsync(true);
                }
                else
                {
                    ShowBubble("已清空 GitHub 更新直链。", 2500);
                }
            }
        }

        void CleanupOldUpdateBackup()
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                Thread.Sleep(2000);
                try
                {
                    string bak = Application.ExecutablePath + ".bak";
                    if (File.Exists(bak)) File.Delete(bak);
                }
                catch { }
            });
        }

        static string ComputeBytesSha256(byte[] data)
        {
            using (var sha = new SHA256Managed())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        static string ComputeNormalizedCsHash(string csText)
        {
            if (csText == null) return "";
            string norm = csText.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            return ComputeBytesSha256(Encoding.UTF8.GetBytes(norm));
        }

        string GetLocalCurrentCsHash()
        {
            try
            {
                string[] hashFiles = new string[]
                {
                    Path.Combine(baseDir, "current_version.sha256"),
                    Path.Combine(cfgDir, "current_version.sha256")
                };
                foreach (string hf in hashFiles)
                {
                    if (File.Exists(hf))
                    {
                        string h = File.ReadAllText(hf, Encoding.UTF8).Trim().ToLowerInvariant();
                        if (h.Length == 64) return h;
                    }
                }
                string[] csFiles = new string[]
                {
                    Path.Combine(baseDir, "PetApp.cs"),
                    Path.Combine(baseDir, "..", "PetApp.cs")
                };
                foreach (string cf in csFiles)
                {
                    if (File.Exists(cf))
                    {
                        string text = File.ReadAllText(cf, Encoding.UTF8);
                        return ComputeNormalizedCsHash(text);
                    }
                }
            }
            catch { }
            return "";
        }

        void SaveLocalCurrentCsHash(string hash)
        {
            try { File.WriteAllText(Path.Combine(baseDir, "current_version.sha256"), hash, Encoding.UTF8); } catch { }
            try { File.WriteAllText(Path.Combine(cfgDir, "current_version.sha256"), hash, Encoding.UTF8); } catch { }
        }

        static byte[] DownloadWithGitHubFallback(string rawUrl)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls;
            string sep = rawUrl.Contains("?") ? "&" : "?";
            string bustUrl = rawUrl + sep + "t=" + DateTime.UtcNow.Ticks;

            // 依次尝试：1. GitHub 官方直连  2. ghfast 镜像加速  3. ghproxy 镜像加速（解决部分设备直连 GitHub 超时问题）
            List<string> tryUrls = new List<string>();
            tryUrls.Add(bustUrl);
            if (rawUrl.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
                rawUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                tryUrls.Add("https://ghfast.top/" + bustUrl);
                tryUrls.Add("https://mirror.ghproxy.com/" + bustUrl);
            }

            Exception lastEx = null;
            foreach (string u in tryUrls)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(u);
                    req.Timeout = 12000;
                    req.ReadWriteTimeout = 15000;
                    req.UserAgent = "ZhuoChong-AutoUpdater/1.0";
                    req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                    using (var resp = req.GetResponse())
                    using (var stream = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        byte[] buf = new byte[8192];
                        int read;
                        while ((read = stream.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, read);
                        }
                        byte[] data = ms.ToArray();
                        if (data.Length > 128) return data;
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }
            }
            if (lastEx != null) throw lastEx;
            throw new Exception("下载内容为空");
        }

        bool CompileCsToNewExe(string csText, string outExePath, out string errMsg)
        {
            errMsg = "";
            string tempCs = Path.Combine(baseDir, "update_temp.cs");
            try
            {
                string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
                string cscPath = Path.Combine(runtimeDir, "csc.exe");
                if (!File.Exists(cscPath))
                {
                    errMsg = "未找到系统 .NET 编译器 csc.exe";
                    return false;
                }

                // 写入带 UTF-8 BOM 的源码文件，确保中文字符串常量不乱码
                File.WriteAllText(tempCs, csText, new UTF8Encoding(true));
                if (File.Exists(outExePath)) File.Delete(outExePath);

                var args = new StringBuilder();
                args.Append("/nologo /target:winexe /optimize ");
                args.Append("/out:\"").Append(outExePath).Append("\" ");
                args.Append("/r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll /r:System.Net.dll ");

                string[] naudioCandidates = new string[]
                {
                    Path.Combine(baseDir, "NAudio.dll"),
                    Path.Combine(baseDir, "naudio110", "lib", "net35", "NAudio.dll"),
                    Path.Combine(baseDir, "..", "naudio110", "lib", "net35", "NAudio.dll")
                };
                foreach (string np in naudioCandidates)
                {
                    if (File.Exists(np))
                    {
                        args.Append("/r:\"").Append(Path.GetFullPath(np)).Append("\" ");
                        break;
                    }
                }
                args.Append("\"").Append(tempCs).Append("\"");

                var psi = new ProcessStartInfo(cscPath, args.ToString())
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = baseDir
                };
                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(30000);
                    if (proc.ExitCode != 0 || !File.Exists(outExePath))
                    {
                        errMsg = "编译失败: " + stdout + " " + stderr;
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                errMsg = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(tempCs)) File.Delete(tempCs); } catch { }
            }
        }

        void ApplyNewExeAndRestart(string newExePath)
        {
            string curExe = Application.ExecutablePath;
            string bakExe = curExe + ".bak";
            try
            {
                if (File.Exists(bakExe)) File.Delete(bakExe);
            }
            catch { }

            // Windows 允许直接将正在运行的自身 .exe 重命名为 .bak，再将新 .exe 移入原路径并直接拉起
            File.Move(curExe, bakExe);
            File.Move(newExePath, curExe);

            SaveConfig();
            Process.Start(new ProcessStartInfo(curExe, "--updated") { WorkingDirectory = baseDir });
            Application.Exit();
        }

        void CheckGitHubUpdateAsync(bool manualNotify)
        {
            if (isCheckingUpdate)
            {
                if (manualNotify) ShowBubble("正在后台检查更新中，请稍候…", 2000);
                return;
            }
            if (string.IsNullOrWhiteSpace(updateUrl))
            {
                if (manualNotify)
                {
                    ShowBubble("尚未配置 GitHub 更新直链，请先设置。", 3000);
                    ConfigureGitHubUpdateUrl();
                }
                return;
            }

            isCheckingUpdate = true;
            string targetUrl = NormalizeGitHubRawUrl(updateUrl);
            if (manualNotify) ShowBubble("正在连接 GitHub 检查更新…", 2500);

            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    byte[] rawBytes = DownloadWithGitHubFallback(targetUrl);
                    string cleanPath = targetUrl.Split('?')[0].ToLowerInvariant();
                    string newExePath = Path.Combine(baseDir, "update_new.exe");

                    if (cleanPath.EndsWith(".cs"))
                    {
                        string csText = Encoding.UTF8.GetString(rawBytes).TrimStart('\uFEFF');
                        if (!csText.Contains("namespace ZhuChong") || !csText.Contains("class PetForm"))
                        {
                            throw new Exception("下载的不是有效的 PetApp.cs 源码文件");
                        }
                        string remoteHash = ComputeNormalizedCsHash(csText);
                        string localHash = GetLocalCurrentCsHash();
                        if (!string.IsNullOrEmpty(localHash) && string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
                        {
                            if (manualNotify)
                            {
                                BeginInvoke(new Action(() => ShowBubble("✅ 当前已是 GitHub 最新版本！", 3000)));
                            }
                            return;
                        }

                        BeginInvoke(new Action(() => ShowBubble("✨ 发现 GitHub 新版本源码，正在后台编译更新…", 4000)));
                        string err;
                        if (!CompileCsToNewExe(csText, newExePath, out err))
                        {
                            throw new Exception(err);
                        }
                        // 同步更新本地源码文件与版本哈希记录
                        try
                        {
                            string localCs = Path.Combine(baseDir, "PetApp.cs");
                            if (File.Exists(localCs)) File.WriteAllText(localCs, csText, new UTF8Encoding(true));
                        }
                        catch { }
                        SaveLocalCurrentCsHash(remoteHash);

                        BeginInvoke(new Action(() => ApplyNewExeAndRestart(newExePath)));
                    }
                    else
                    {
                        // 二进制 .exe 更新模式：校验 PE 头 "MZ" 与体积
                        if (rawBytes.Length < 40960 || rawBytes[0] != 0x4D || rawBytes[1] != 0x5A)
                        {
                            throw new Exception("下载的文件不是有效的 Windows EXE 程序");
                        }
                        string remoteHash = ComputeBytesSha256(rawBytes);
                        string localHash = ComputeBytesSha256(File.ReadAllBytes(Application.ExecutablePath));
                        if (string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
                        {
                            if (manualNotify)
                            {
                                BeginInvoke(new Action(() => ShowBubble("✅ 当前已是 GitHub 最新版本！", 3000)));
                            }
                            return;
                        }

                        File.WriteAllBytes(newExePath, rawBytes);
                        BeginInvoke(new Action(() =>
                        {
                            ShowBubble("✨ 已下载 GitHub 新版本，正在重启应用更新…", 2500);
                            ApplyNewExeAndRestart(newExePath);
                        }));
                    }
                }
                catch (Exception ex)
                {
                    if (manualNotify)
                    {
                        BeginInvoke(new Action(() => ShowBubble("更新检查失败：" + ex.Message, 4000)));
                    }
                }
                finally
                {
                    isCheckingUpdate = false;
                }
            });
        }

        // ---------- 简单输入框 ----------
        string SimpleInput(string title, string prompt, string def = "")
        {
            var f = new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(300, 110),
                MaximizeBox = false, MinimizeBox = false
            };
            var lbl = new Label { Text = prompt, Location = new Point(12, 12), AutoSize = true };
            var txt = new TextBox { Location = new Point(12, 36), Width = 276, Text = def };
            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(110, 70), Width = 80 };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(200, 70), Width = 80 };
            f.Controls.Add(lbl); f.Controls.Add(txt); f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.StartPosition = FormStartPosition.CenterScreen;
            BeginInvoke(new Action(() => EnsureTopMostZOrder()));
            return f.ShowDialog(GetDialogHost()) == DialogResult.OK ? txt.Text : null;
        }

        // ---------- 窗体销毁与资源释放 ----------
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SaveConfig();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (petBox != null) petBox.Image = null;
                if (animTimer != null) { animTimer.Dispose(); animTimer = null; }
                if (walkTimer != null) { walkTimer.Dispose(); walkTimer = null; }
                if (clockTimer != null) { clockTimer.Dispose(); clockTimer = null; }
                if (idleTimer != null) { idleTimer.Dispose(); idleTimer = null; }
                if (tomatoTimer != null) { tomatoTimer.Dispose(); tomatoTimer = null; }
                if (fsTimer != null) { fsTimer.Dispose(); fsTimer = null; }
                if (autoTimer != null) { autoTimer.Dispose(); autoTimer = null; }
                if (updateTimer != null) { updateTimer.Dispose(); updateTimer = null; }
                if (bubbleTimer != null) { bubbleTimer.Dispose(); bubbleTimer = null; }
                if (clickDelayTimer != null) { clickDelayTimer.Dispose(); clickDelayTimer = null; }
                if (kbHook != IntPtr.Zero) { UnhookWindowsHookEx(kbHook); kbHook = IntPtr.Zero; }
                if (dialogHost != null && !dialogHost.IsDisposed) { dialogHost.Dispose(); dialogHost = null; }
                if (ringOverlay != null && !ringOverlay.IsDisposed) { ringOverlay.Dispose(); ringOverlay = null; }
                if (audioViz != null && !audioViz.IsDisposed) { audioViz.Dispose(); audioViz = null; }
                foreach (var c in clips.Values)
                {
                    foreach (var f in c.Frames) f.Dispose();
                    c.Frames.Clear();
                }
                clips.Clear();
            }
            base.Dispose(disposing);
        }

        // ---------- 控制面板公开接口 ----------
        public bool IsClockOn { get { return clockOn; } }
        public void ToggleClockPub() { ToggleClock(); }
        public string CurrentMode { get { return mode; } }
        public void SetAction(string m)
        {
            // 手动切换动作时重置随机动作计时器，避免刚切完立刻被随机动作覆盖
            if (idleTimer != null)
            {
                idleTimer.Stop();
                idleTimer.Start();
            }
            lastIdleSwitch = DateTime.Now;
            if (m == "walk")
            {
                if (WalkEnabled)
                {
                    facing = rnd.Next(2) == 0 ? -1 : 1;
                    SetMode(facing == 1 ? "walk" : "walk_left");
                }
                else SetMode("idle");
            }
            else SetMode(m);
        }
        public void RedrawBubble()
        {
            if (bubble != null && bubble.Visible && lastBubbleText != null)
                ShowBubble(lastBubbleText, lastBubbleMs);
        }
        public void SetPetScale(double s)
        {
            if (s < 0.4) s = 0.4;
            if (s > 2.0) s = 2.0;
            if (Math.Abs(s - PetScale) < 0.01) return;
            double oldScale = PetScale;
            PetScale = s;
            int oldFootScreen = Location.Y + (int)(400 * oldScale);
            int nw = Nw, nh = Nh;
            int y = oldFootScreen - FootY;
            SetBounds(Location.X + (Width - nw) / 2, y, nw, nh);
            LoadGifs();
            PlayFrame();
            if (petBox != null) petBox.Location = new Point((nw - petBox.Width) / 2, FootY - petBox.Height);
            clockLabel.Location = new Point(nw / 2, FootY + 2);
            tomatoLabel.Location = new Point(nw / 2 - 30, FootY + 2);

            if (ringOpen && ringOverlay != null && !ringOverlay.IsDisposed)
            {
                ringOverlay.ShowRing();
            }
            if (audioVizOn && audioViz != null && !audioViz.IsDisposed && audioViz.Visible)
            {
                audioViz.SyncToPet();
            }
        }

        ControlPanelForm cpanel;
        public void ShowPanel()
        {
            if (cpanel == null || cpanel.IsDisposed) cpanel = new ControlPanelForm(this);
            if (!cpanel.Visible) cpanel.Show();
            cpanel.Activate();
        }
    }

    // ---------- 控制面板 ----------
    class ControlPanelForm : Form
    {
        PetForm pet;
        RadioButton rbIdle, rbSit, rbRead, rbWalk;
        CheckBox chkWalk, chkClock;
        TrackBar tbScale, tbSpeed, tbBScale, tbBDepth, tbBOffset, tbBDur,
                 tbIcon, tbRing, tbGap, tbSpan, tbCx, tbCy,
                 tbAudOut, tbAudIn, tbAudSpd, tbClockBr;
        ListBox lbLines;
        TextBox txtLine;
        Label lbScaleV, lbSpeedV, lbBScaleV, lbBDepthV, lbBOffsetV, lbBDurV,
              lbIconV, lbRingV, lbGapV, lbSpanV, lbCxV, lbCyV,
              lbAudOutV, lbAudInV, lbAudSpdV, lbClockBrV;

        public ControlPanelForm(PetForm owner)
        {
            pet = owner;
            Text = "桌宠控制面板";
            ClientSize = new Size(470, 800);
            MinimumSize = new Size(430, 420);
            AutoScroll = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 9);
            BackColor = Color.FromArgb(35, 24, 60);
            ForeColor = Color.White;

            int gx = 14, gw = 440;
            int rowY = 26;
            var baseY = 24;

            // —— 桌宠大小 ——
            var gScale = AddGroup("桌宠大小", gx, baseY, gw, 60);
            AddRow(gScale, "整体大小", out tbScale, out lbScaleV, rowY, 40, 200, (int)(pet.PetScale * 100));
            baseY += 78;

            // —— 动作 ——
            var gAct = AddGroup("动作", gx, baseY, gw, 130);
            rbIdle = AddRadio(gAct, "站姿", 26, 24);
            rbSit = AddRadio(gAct, "蹲姿", 172, 24);
            rbRead = AddRadio(gAct, "看书", 318, 24);
            rbWalk = AddRadio(gAct, "走路", 26, 50);
            chkWalk = AddCheck(gAct, "随机走路", 172, 50);
            AddRow(gAct, "走路速度", out tbSpeed, out lbSpeedV, 82, 2, 14, pet.WalkSpeed);
            baseY += 150;

            // —— 对话框 ——
            var gBub = AddGroup("对话框", gx, baseY, gw, 176);
            AddRow(gBub, "宽度", out tbBScale, out lbBScaleV, 24, 50, 200, (int)(pet.BubbleScale * 100));
            AddRow(gBub, "深浅", out tbBDepth, out lbBDepthV, 62, 0, 100, pet.BubbleDepth);
            AddRow(gBub, "偏移", out tbBOffset, out lbBOffsetV, 100, -60, 60, pet.BubbleOffset);
            AddRow(gBub, "时长", out tbBDur, out lbBDurV, 138, 1, 10, pet.BubbleDur);
            baseY += 196;

            // —— 音频频谱 ——
            var gAud = AddGroup("音频频谱", gx, baseY, gw, 176);
            AddRow(gAud, "外层振幅", out tbAudOut, out lbAudOutV, 24, 50, 200, pet.AudioOutAmp);
            AddRow(gAud, "内层振幅", out tbAudIn, out lbAudInV, 62, 50, 200, pet.AudioInAmp);
            AddRow(gAud, "响应速度", out tbAudSpd, out lbAudSpdV, 100, 50, 200, pet.AudioSpeed);
            baseY += 196;

            // —— 时间框 ——
            var gClock = AddGroup("时间框", gx, baseY, gw, 60);
            AddRow(gClock, "颜色明度", out tbClockBr, out lbClockBrV, 24, 0, 200, pet.ClockBrightness);
            baseY += 80;

            // —— 快捷图标 ——
            var gIcon = AddGroup("快捷图标", gx, baseY, gw, 252);
            AddRow(gIcon, "大小", out tbIcon, out lbIconV, 24, 28, 72, pet.IconSize);
            AddRow(gIcon, "半径", out tbRing, out lbRingV, 62, 100, 400, pet.RingBase);
            AddRow(gIcon, "层距", out tbGap, out lbGapV, 100, 30, 150, pet.RingGap);
            AddRow(gIcon, "图标间距", out tbSpan, out lbSpanV, 138, 30, 120, pet.RingSpan);
            AddRow(gIcon, "圆心X", out tbCx, out lbCxV, 176, -200, 200, pet.RingCxAdj);
            AddRow(gIcon, "圆心Y", out tbCy, out lbCyV, 214, -120, 120, pet.RingCyAdj);
            baseY += 272;

            // —— 通用 ——
            var gGen = AddGroup("通用", gx, baseY, gw, 64);
            chkClock = AddCheck(gGen, "显示时间", 18, 26);
            baseY += 84;

            // —— 附加语录 ——
            var gLine = AddGroup("附加语录（对话框随机说）", gx, baseY, gw, 152);
            lbLines = new ListBox { Location = new Point(18, 24), Size = new Size(gw - 36, 74), BackColor = Color.FromArgb(50, 32, 86), ForeColor = Color.White };
            gLine.Controls.Add(lbLines);
            txtLine = new TextBox { Location = new Point(18, 104), Size = new Size(gw - 130, 24), BackColor = Color.White };
            gLine.Controls.Add(txtLine);
            var bAdd = new Button { Text = "添加", Location = new Point(gw - 108, 103), Size = new Size(44, 24) };
            var bDel = new Button { Text = "删除", Location = new Point(gw - 60, 103), Size = new Size(44, 24) };
            gLine.Controls.Add(bAdd); gLine.Controls.Add(bDel);
            baseY += 172;

            var bSave = new Button { Text = "保存设置", Location = new Point(gx, baseY), Size = new Size(120, 28) };
            var bClose = new Button { Text = "关闭", Location = new Point(gx + 130, baseY), Size = new Size(80, 28) };
            Controls.Add(bSave); Controls.Add(bClose);

            // 初始值（根据当前实际动作选中对应单选框，避免打开控制面板时把当前动作强行重置为站姿）
            string curM = pet.CurrentMode;
            if (curM == "sit") rbSit.Checked = true;
            else if (curM == "sit_read") rbRead.Checked = true;
            else if (curM == "walk" || curM == "walk_left") rbWalk.Checked = true;
            else rbIdle.Checked = true;
            chkWalk.Checked = pet.WalkEnabled;
            chkClock.Checked = pet.IsClockOn;
            foreach (var ln in pet.ExtraLines) lbLines.Items.Add(ln);

            // 事件绑定
            rbIdle.CheckedChanged += (se, ea) => { if (rbIdle.Checked) pet.SetAction("idle"); };
            rbSit.CheckedChanged += (se, ea) => { if (rbSit.Checked) pet.SetAction("sit"); };
            rbRead.CheckedChanged += (se, ea) => { if (rbRead.Checked) pet.SetAction("sit_read"); };
            rbWalk.CheckedChanged += (se, ea) => { if (rbWalk.Checked) pet.SetAction("walk"); };
            chkWalk.CheckedChanged += (se, ea) => { pet.WalkEnabled = chkWalk.Checked; pet.SaveConfig(); };
            chkClock.CheckedChanged += (se, ea) => pet.ToggleClockPub();

            tbScale.ValueChanged += (se, ea) => { pet.SetPetScale(tbScale.Value / 100.0); lbScaleV.Text = tbScale.Value.ToString() + "%"; };
            tbSpeed.ValueChanged += (se, ea) => { pet.WalkSpeed = tbSpeed.Value; lbSpeedV.Text = tbSpeed.Value.ToString(); };
            tbBScale.ValueChanged += (se, ea) => { pet.BubbleScale = tbBScale.Value / 100.0; lbBScaleV.Text = tbBScale.Value.ToString() + "%"; pet.RedrawBubble(); };
            tbBDepth.ValueChanged += (se, ea) => { pet.BubbleDepth = tbBDepth.Value; lbBDepthV.Text = tbBDepth.Value.ToString(); pet.RedrawBubble(); };
            tbBOffset.ValueChanged += (se, ea) => { pet.BubbleOffset = tbBOffset.Value; lbBOffsetV.Text = tbBOffset.Value.ToString(); pet.RedrawBubble(); };
            tbBDur.ValueChanged += (se, ea) => { pet.BubbleDur = tbBDur.Value; lbBDurV.Text = tbBDur.Value.ToString() + "s"; pet.RedrawBubble(); };
            tbIcon.ValueChanged += (se, ea) => { pet.IconSize = tbIcon.Value; lbIconV.Text = tbIcon.Value.ToString(); pet.RebuildIcons(); pet.RingPreviewTip(); };
            tbRing.ValueChanged += (se, ea) => { pet.RingBase = tbRing.Value; lbRingV.Text = tbRing.Value.ToString(); pet.RebuildIcons(); pet.RingPreviewTip(); };
            tbGap.ValueChanged += (se, ea) => { pet.RingGap = tbGap.Value; lbGapV.Text = tbGap.Value.ToString(); pet.RebuildIcons(); pet.RingPreviewTip(); };
            tbSpan.ValueChanged += (se, ea) => { pet.RingSpan = tbSpan.Value; lbSpanV.Text = tbSpan.Value.ToString() + "°"; pet.RebuildIcons(); pet.RingPreviewTip(); };
            tbCx.ValueChanged += (se, ea) => { pet.RingCxAdj = tbCx.Value; lbCxV.Text = tbCx.Value.ToString(); pet.RebuildIcons(); pet.RingPreviewTip(); };
            tbCy.ValueChanged += (se, ea) => { pet.RingCyAdj = tbCy.Value; lbCyV.Text = tbCy.Value.ToString(); pet.RebuildIcons(); pet.RingPreviewTip(); };
            tbAudOut.ValueChanged += (se, ea) => { pet.AudioOutAmp = tbAudOut.Value; lbAudOutV.Text = tbAudOut.Value.ToString() + "%"; };
            tbAudIn.ValueChanged += (se, ea) => { pet.AudioInAmp = tbAudIn.Value; lbAudInV.Text = tbAudIn.Value.ToString() + "%"; };
            tbAudSpd.ValueChanged += (se, ea) => { pet.AudioSpeed = tbAudSpd.Value; lbAudSpdV.Text = tbAudSpd.Value.ToString() + "%"; };
            tbClockBr.ValueChanged += (se, ea) => { pet.ClockBrightness = tbClockBr.Value; lbClockBrV.Text = tbClockBr.Value.ToString() + "%"; pet.UpdateClock(); };
            // 鼠标松开滑块时写盘，避免拖动过程频繁 I/O
            tbScale.MouseUp += (se, ea) => pet.SaveConfig();
            tbSpeed.MouseUp += (se, ea) => pet.SaveConfig();
            tbBScale.MouseUp += (se, ea) => pet.SaveConfig();
            tbBDepth.MouseUp += (se, ea) => pet.SaveConfig();
            tbBOffset.MouseUp += (se, ea) => pet.SaveConfig();
            tbBDur.MouseUp += (se, ea) => pet.SaveConfig();
            tbIcon.MouseUp += (se, ea) => pet.SaveConfig();
            tbRing.MouseUp += (se, ea) => pet.SaveConfig();
            tbGap.MouseUp += (se, ea) => pet.SaveConfig();
            tbSpan.MouseUp += (se, ea) => pet.SaveConfig();
            tbCx.MouseUp += (se, ea) => pet.SaveConfig();
            tbCy.MouseUp += (se, ea) => pet.SaveConfig();
            tbAudOut.MouseUp += (se, ea) => pet.SaveConfig();
            tbAudIn.MouseUp += (se, ea) => pet.SaveConfig();
            tbAudSpd.MouseUp += (se, ea) => pet.SaveConfig();
            tbClockBr.MouseUp += (se, ea) => pet.SaveConfig();

            bAdd.Click += (se, ea) =>
            {
                string t = txtLine.Text.Trim();
                if (t.Length > 0)
                {
                    pet.ExtraLines.Add(t);
                    lbLines.Items.Add(t);
                    txtLine.Clear();
                    pet.SaveConfig();
                }
            };
            bDel.Click += (se, ea) =>
            {
                if (lbLines.SelectedIndex >= 0 && lbLines.SelectedIndex < pet.ExtraLines.Count)
                {
                    pet.ExtraLines.RemoveAt(lbLines.SelectedIndex);
                    lbLines.Items.RemoveAt(lbLines.SelectedIndex);
                    pet.SaveConfig();
                }
            };
            bSave.Click += (se, ea) => { pet.SaveConfig(); MessageBox.Show("设置已保存。", "桌宠控制面板"); };
            bClose.Click += (se, ea) => Close();
        }

        GroupBox AddGroup(string t, int x, int y, int w, int h)
        {
            var g = new GroupBox
            {
                Text = t,
                Location = new Point(x, y),
                Size = new Size(w, h),
                ForeColor = Color.White,
                Font = Font
            };
            Controls.Add(g);
            return g;
        }
        Label AddLbl(Control parent, string t, int x, int y)
        {
            var l = new Label { Text = t, Location = new Point(x, y), AutoSize = true, ForeColor = Color.White };
            parent.Controls.Add(l);
            return l;
        }
        RadioButton AddRadio(Control parent, string t, int x, int y)
        {
            var r = new RadioButton { Text = t, Location = new Point(x, y), AutoSize = true, ForeColor = Color.White };
            parent.Controls.Add(r);
            return r;
        }
        CheckBox AddCheck(Control parent, string t, int x, int y)
        {
            var c = new CheckBox { Text = t, Location = new Point(x, y), AutoSize = true, ForeColor = Color.White };
            parent.Controls.Add(c);
            return c;
        }
        void AddRow(Control parent, string label, out TrackBar tb, out Label lbV, int y, int min, int max, int val)
        {
            if (val < min) val = min;
            if (val > max) val = max;
            AddLbl(parent, label, 18, y + 2);
            tb = new TrackBar
            {
                Location = new Point(80, y),
                Size = new Size(270, 28),
                Minimum = min, Maximum = max, Value = val,
                TickStyle = TickStyle.None,
                BackColor = Color.FromArgb(35, 24, 60)
            };
            lbV = new Label
            {
                Text = val.ToString(),
                Location = new Point(360, y + 2),
                AutoSize = true,
                ForeColor = Color.FromArgb(255, 200, 120)
            };
            parent.Controls.Add(tb); parent.Controls.Add(lbV);
        }
    }

    static class Lines
    {
        static readonly string[] Greet = {
            "哦呀？找本宫有何事呀？",
            "今天也很努力呢，值得表扬。",
            "无聊得很，陪本宫说说话吧。",
            "想我了吗？假话也得当真哦。",
            "哼哼，本宫的皮毛金贵得很。",
            "这一路可别让本宫无聊，否则……",
            "沉迷工作的时候，记得抬头看看本宫。",
            "稻妻的雷雨再大，也淋不到这儿。"
        };
        static readonly string[] ClickLines = {
            "嗯？摸够了吗？",
            "手在做什么呢，小坏蛋。",
            "哎呀，真是不稳重的孩子。",
            "再摸就要收费了哦？",
            "轻点，本宫的皮毛金贵得很。"
        };
        static readonly Random Rnd = new Random();
        public static string Greeting(List<string> extra = null)
        {
            if (extra != null && extra.Count > 0 && Rnd.NextDouble() < 0.4)
            {
                return extra[Rnd.Next(extra.Count)];
            }
            return Greet[Rnd.Next(Greet.Length)];
        }
        public static string Click() { return ClickLines[Rnd.Next(ClickLines.Length)]; }
    }
}