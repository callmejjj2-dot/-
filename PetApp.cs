// 八重神子桌宠 - C# WinForms 单进程原生版（100% 直接使用原始精抠 GIF 帧）
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
            bool createdNew;
            using (var mutex = new Mutex(true, "ZhuoChongPet_Mutex", out createdNew))
            {
                if (!createdNew) return; // 单实例运行
                Application.ThreadException += (s, e) =>
                {
                    try { MessageBox.Show("出错了：" + e.Exception.Message, "桌宠错误", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    try { MessageBox.Show("严重错误：" + ((Exception)e.ExceptionObject).Message, "桌宠崩溃", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
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
        public List<int> Delays = new List<int>();
        public Image Raw;
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

    // 独立透明扇形快捷图标层
    class RingOverlayForm : Form
    {
        PetForm pet;
        List<Button> iconBtns = new List<Button>();
        Timer ringTimer;
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

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;
                return cp;
            }
        }

        public void ShowRing()
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
            RebuildIcons();

            if (!Visible) Show();
            BringToFront();
            if (pet != null && !pet.IsDisposed) pet.EnsureTopMostZOrder();

            ringTimer.Stop();
            ringTimer.Start();
        }

        public void HideRing()
        {
            ringTimer.Stop();
            if (Visible) Hide();
            pet.OnRingClosed();
        }

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
                if (b.Region != null) b.Region.Dispose();
                b.Dispose();
            }
            iconBtns.Clear();

            int n = pet.Icons.Count;
            if (n == 0) return;

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
                double ang = layerCount == 1 ? 0 : angMin + (angMax - angMin) * idxInLayer / (layerCount - 1);

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

            using (var path = new GraphicsPath())
            {
                path.AddEllipse(1, 1, s - 2, s - 2);
                b.Region = new Region(path);
            }

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
                if (dragging) return;
                var now = DateTime.Now;
                if ((now - lastIconClick).TotalMilliseconds < 450)
                {
                    lastIconClick = DateTime.MinValue;
                    try { Process.Start(ic.Path); HideRing(); }
                    catch { pet.ShowBubble("打不开这个图标。", 2500); }
                }
                else lastIconClick = now;
            };

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
            b.MouseMove += (se, ea) => HandleIconDragMove();
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
                        var tmp = pet.Icons[srcIdx];
                        pet.Icons[srcIdx] = pet.Icons[targetIdx];
                        pet.Icons[targetIdx] = tmp;
                        pet.SaveConfig();
                        BeginInvoke(new Action(() => RebuildIcons()));
                    }
                    else
                    {
                        for (int i = 0; i < iconBtns.Count; i++)
                            iconBtns[i].BackColor = Color.FromArgb(40, 24, 66);
                        ringTimer.Start();
                    }
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
                    if (b.Region != null) b.Region.Dispose();
                    b.Dispose();
                }
                iconBtns.Clear();
            }
            base.Dispose(disposing);
        }
    }

    // 音频频谱光环
    class AudioVizForm : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WinPoint { public int x, y; public WinPoint(int x, int y) { this.x = x; this.y = y; } }
        [StructLayout(LayoutKind.Sequential)]
        struct WinSize { public int cx, cy; public WinSize(int cx, int cy) { this.cx = cx; this.cy = cy; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref WinPoint pptDst,
            ref WinSize psize, IntPtr hdcSrc, ref WinPoint pptSrc, uint crKey,
            [In] ref BLENDFUNCTION pblend, uint dwFlags);
        [DllImport("user32.dll", ExactSpelling = true)] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll", ExactSpelling = true)] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll", ExactSpelling = true)] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)] static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

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
                side = Math.Max(owner.PetBox.Width, owner.PetBox.Height) + 150;
            Size = new Size(side, side);

            for (int i = 0; i < FFT_SIZE; i++)
                window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FFT_SIZE - 1))));

            vizTimer = new Timer { Interval = 50 };
            vizTimer.Tick += (s, e) => RenderLayeredFrame();
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000;
                return cp;
            }
        }

        void Fft()
        {
            lock (bufLock)
            {
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
                    float tr = fftReal[i]; fftReal[i] = fftReal[j]; fftReal[j] = tr;
                    float ti = fftImag[i]; fftImag[i] = fftImag[j]; fftImag[j] = ti;
                }
                int m = FFT_SIZE >> 1;
                while (m >= 1 && j >= m) { j -= m; m >>= 1; }
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
                    float wr = 1f, wi = 0f;
                    for (int k = 0; k < half; k++)
                    {
                        int evenIdx = i + k;
                        int oddIdx = evenIdx + half;
                        float ur = fftReal[evenIdx], ui = fftImag[evenIdx];
                        float vr = fftReal[oddIdx] * wr - fftImag[oddIdx] * wi;
                        float vi = fftReal[oddIdx] * wi + fftImag[oddIdx] * wr;
                        fftReal[evenIdx] = ur + vr; fftImag[evenIdx] = ui + vi;
                        fftReal[oddIdx] = ur - vr;  fftImag[oddIdx] = ui - vi;
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
            if (Owner != null) Owner = null;
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
                    if (capture != null) { try { capture.Dispose(); } catch { } capture = null; }
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
                try { capture.DataAvailable -= OnDataAvailable; capture.StopRecording(); } catch { }
                try { capture.Dispose(); } catch { }
                capture = null;
            }
            if (Visible) Hide();
        }

        public float GetSpectrumEnergy()
        {
            try
            {
                if (lastAudioTick == 0 || unchecked(Environment.TickCount - lastAudioTick) > 600) return 0f;
                float sum = 0f;
                lock (bufLock)
                {
                    for (int i = 0; i < BIN_COUNT; i++) sum += smoothBins[i];
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
            if (Width != side || Height != side) Size = new Size(side, side);
            var screenPt = pet.PointToScreen(new Point(pb.Left + pb.Width / 2, pb.Top + pb.Height / 2));
            Location = new Point(screenPt.X - side / 2 + pet.AudioVizCx, screenPt.Y - side / 2 + pet.AudioVizCy);
            if (Visible && IsHandleCreated) UpdateZOrderBehindPet();
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
            double u = 1.0 - (d / halfWidth);
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
            float sum = 0f, maxV = 0f;
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

                float tilt = 1.0f + i * 0.048f;
                float norm = (mag / 46.0f) * tilt;
                float newValue = (float)Math.Log10(1.0 + norm * 9.5);
                if (newValue < 0f) newValue = 0f;
                if (newValue > 1.05f) newValue = 1.05f;

                float spd = pet != null ? pet.AudioSpeed / 100f : 1f;
                if (newValue > spectrum[i])
                {
                    float attack = Math.Min(0.98f, 0.90f * spd);
                    spectrum[i] = spectrum[i] * (1f - attack) + newValue * attack;
                }
                else
                {
                    float release = Math.Min(0.5f, 0.22f * spd);
                    spectrum[i] = spectrum[i] * (1f - release) + newValue * release;
                }
            }

            for (int i = 0; i < BIN_COUNT; i++)
            {
                float s = 0f, wSum = 0f;
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

            int[] ringOffsets = new int[] { -10, -26, -44 };
            int[] ringAlphas = new int[] { 20, 13, 7 };
            for (int rIdx = 0; rIdx < ringOffsets.Length; rIdx++)
            {
                float rr = baseR + ringOffsets[rIdx];
                if (rr > 10f)
                {
                    using (var ringPen = new Pen(Color.FromArgb(ringAlphas[rIdx], 70, 105, 245), 2.5f))
                        g.DrawEllipse(ringPen, cx - rr, cy - rr, rr * 2f, rr * 2f);
                }
            }

            float outAmpRatio = (pet != null ? pet.AudioOutAmp : 100) / 100f;
            float inAmpRatio = (pet != null ? pet.AudioInAmp : 100) / 100f;
            float outAmp = 56.1f * scaleRatio * outAmpRatio;
            float inAmp = 37.4f * scaleRatio * inAmpRatio;
            PointF[] outPts = new PointF[SEGS];
            PointF[] inPts = new PointF[SEGS];
            float[] lobeEnv = new float[SEGS];

            double[] centers = new double[] { 0.042, 0.132, 0.208, 0.285, 0.542, 0.628, 0.765 };
            double[] widths  = new double[] { 0.053, 0.041, 0.043, 0.039, 0.053, 0.043, 0.135 };
            float[] amps     = new float[] {
                Math.Min(1.08f, bass * 1.10f), Math.Min(0.68f, lowMid * 0.56f),
                Math.Min(0.72f, mid * 0.60f), Math.Min(0.55f, highMid * 0.45f),
                Math.Min(1.05f, bass * 1.05f), Math.Min(0.64f, lowMid * 0.52f),
                Math.Min(0.38f, treble * 0.36f)
            };
            bool[] isSharp   = new bool[] { true, false, false, false, true, false, false };

            for (int i = 0; i < SEGS; i++)
            {
                double t = (double)i / SEGS;
                double ang = -Math.PI * 0.5 + (Math.PI * 2.0 * i) / SEGS;

                float vo = 0.012f, vi = 0.012f;
                for (int k = 0; k < centers.Length; k++)
                {
                    vo += amps[k] * LobeValue(t, centers[k], widths[k], isSharp[k], false);
                    vi += amps[k] * LobeValue(t, centers[k], widths[k], isSharp[k], true);
                }

                int binIdx = (int)((t < 0.5 ? t * 2.0 : (1.0 - t) * 2.0) * (BIN_COUNT - 1));
                if (binIdx < 0) binIdx = 0;
                if (binIdx >= BIN_COUNT) binIdx = BIN_COUNT - 1;
                float fine = smoothBins[binIdx] * 0.075f * (float)Math.Sin(t * Math.PI * 14.0 + phase);
                vo = Math.Max(0.01f, Math.Min(1.10f, vo + fine));
                vi = Math.Max(0.01f, Math.Min(1.06f, vi + fine * 0.8f));

                lobeEnv[i] = vo;
                float rOut = baseR + vo * outAmp;
                float rIn = baseR - vi * inAmp;
                float cosA = (float)Math.Cos(ang), sinA = (float)Math.Sin(ang);
                outPts[i] = new PointF(cx + rOut * cosA, cy + rOut * sinA);
                inPts[i]  = new PointF(cx + rIn * cosA,  cy + rIn * sinA);
            }

            PointF[] smoothOut = new PointF[SEGS];
            PointF[] smoothIn  = new PointF[SEGS];
            for (int i = 0; i < SEGS; i++)
            {
                float sx = 0f, sy = 0f, sw = 0f, ix = 0f, iy = 0f, iw = 0f;
                for (int k = -4; k <= 4; k++)
                {
                    int idx = (i + k + SEGS) % SEGS;
                    float wt = (k == 0) ? 0.20f : (Math.Abs(k) == 1 ? 0.18f : (Math.Abs(k) == 2 ? 0.12f : (Math.Abs(k) == 3 ? 0.06f : 0.03f)));
                    sx += outPts[idx].X * wt; sy += outPts[idx].Y * wt; sw += wt;
                    ix += inPts[idx].X * wt;  iy += inPts[idx].Y * wt;  iw += wt;
                }
                smoothOut[i] = new PointF(sx / sw, sy / sw);
                smoothIn[i]  = new PointF(ix / iw, iy / iw);
            }
            outPts = smoothOut;
            inPts  = smoothIn;

            using (var bandPath = new GraphicsPath(FillMode.Alternate))
            {
                bandPath.AddClosedCurve(outPts, 0.05f);
                bandPath.AddClosedCurve(inPts, 0.05f);
                using (var fillBrush = new SolidBrush(Color.FromArgb(54, 65, 82, 235)))
                    g.FillPath(fillBrush, bandPath);
            }

            for (int i = 0; i < SEGS; i += 3)
            {
                float v = lobeEnv[i];
                if (v > 0.025f)
                {
                    double t = (double)i / SEGS;
                    bool isLeftBand = (t >= 0.66 && t <= 0.94);
                    int alpha = Math.Min(210, (int)(58 + v * 165));
                    Color ribColor = isLeftBand ? Color.FromArgb(alpha, 85, 112, 252) : Color.FromArgb(alpha, 95, 142, 255);
                    using (var ribPen = new Pen(ribColor, 1.15f))
                        g.DrawLine(ribPen, inPts[i], outPts[i]);
                }
            }

            int[] glowWidths = new int[] { 11, 7, 4 };
            int[] glowAlphasOut = new int[] { 20, 42, 80 };
            int[] glowAlphasIn = new int[] { 15, 32, 62 };
            for (int gIdx = 0; gIdx < glowWidths.Length; gIdx++)
            {
                using (var penOut = new Pen(Color.FromArgb(glowAlphasOut[gIdx], 82, 92, 255), glowWidths[gIdx]))
                using (var penIn = new Pen(Color.FromArgb(glowAlphasIn[gIdx], 82, 92, 255), Math.Max(2, glowWidths[gIdx] - 2)))
                {
                    penOut.LineJoin = LineJoin.Round; penIn.LineJoin = LineJoin.Round;
                    g.DrawClosedCurve(penOut, outPts, 0.05f, FillMode.Winding);
                    g.DrawClosedCurve(penIn, inPts, 0.05f, FillMode.Winding);
                }
            }

            using (var midOutPen = new Pen(Color.FromArgb(215, 100, 125, 255), 2.6f))
            using (var midInPen = new Pen(Color.FromArgb(185, 100, 125, 255), 2.0f))
            using (var coreOutPen = new Pen(Color.FromArgb(245, 165, 185, 255), 1.1f))
            using (var coreInPen = new Pen(Color.FromArgb(215, 165, 185, 255), 1.0f))
            {
                midOutPen.LineJoin = LineJoin.Round; midInPen.LineJoin = LineJoin.Round;
                coreOutPen.LineJoin = LineJoin.Round; coreInPen.LineJoin = LineJoin.Round;
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
                DrawSpectrumToBitmap(g, Width, Height);

            IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;
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
                blend.BlendOp = 0; blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255; blend.AlphaFormat = 1;

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
                if (vizTimer != null) { vizTimer.Dispose(); vizTimer = null; }
                if (frameBmp != null) { frameBmp.Dispose(); frameBmp = null; }
            }
            base.Dispose(disposing);
        }
    }

    // 桌宠主体窗体
    class PetForm : Form
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint GW_OWNER = 4;
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        const int WH_KEYBOARD_LL = 13;
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string gifDir, voiceDir, cfgDir;

        bool clockOn = true, outlineOn = true, audioVizOn = true;
        bool fullscreenHide = false;
        bool isHiddenByFullscreen = false;
        Timer fsTimer;
        bool autoUpdateOn = true;
        string updateUrl = "";
        Timer updateTimer;
        bool isCheckingUpdate = false;
        Timer autoTimer;
        IntPtr kbHook = IntPtr.Zero;
        LowLevelKeyboardProc kbProc;
        DateTime lastKeyTime = DateTime.MinValue;
        bool manualMode = false;
        AudioVizForm audioViz;
        public AudioVizForm AudioViz { get { return audioViz; } }
        string city = "changzhou";
        List<PetIcon> icons = new List<PetIcon>();
        List<TodoItem> todos = new List<TodoItem>();
        string cfgPath;

        public List<PetIcon> Icons { get { return icons; } }

        public double BubbleScale = 1.0;
        public int BubbleDepth = 60;
        public int BubbleOffset = 0;
        public int BubbleDur = 4;
        public int IconSize = 48;
        public int RingBase = 150;
        public int RingGap = 100;
        public int RingSpan = 70;
        public int RingCyAdj = 0;
        public int RingCxAdj = 0;
        public int AudioVizCx = 0;
        public int AudioVizCy = 0;
        public int AudioVizSize = 100;
        public int AudioOutAmp = 100;
        public int AudioInAmp = 100;
        public int AudioSpeed = 100;