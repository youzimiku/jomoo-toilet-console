using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace JomooRemote
{
    public partial class MainWindow : Window
    {
        const int IdleSeconds = 30;
        const int PollMs = 500;

        static readonly Brush TextNormal = new SolidColorBrush(Color.FromRgb(0xDC, 0xE9, 0xF3));
        static readonly Brush TextAccent = new SolidColorBrush(Color.FromRgb(0x57, 0xC8, 0xFF));
        static readonly Brush TextGreen = new SolidColorBrush(Color.FromRgb(0x86, 0xD8, 0xB2));
        static readonly Brush TextRed = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        static readonly Brush GrayIcon = new SolidColorBrush(Color.FromRgb(0x7C, 0x93, 0xA6));
        static readonly Brush GrayText = new SolidColorBrush(Color.FromRgb(0x6F, 0x86, 0x98));
        static readonly Brush DisabledText = new SolidColorBrush(Color.FromRgb(0x6B, 0x7C, 0x8A));
        static readonly Brush GreenText = new SolidColorBrush(Color.FromRgb(0x3E, 0xE0, 0x8F));
        static readonly Brush YellowText = new SolidColorBrush(Color.FromRgb(0xFE, 0xBC, 0x2E));

        JomooBle _ble;
        State _state;
        bool _connected;
        bool _busy;
        bool _closing;
        DateTime _lastAction = DateTime.Now;
        CancellationTokenSource _cts;
        readonly SemaphoreSlim _io = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);
        readonly DispatcherTimer _tick;
        int _lastRtt = -1;
        bool _loggedFirstFrame;
        FrameworkElement _downOn;
        bool _lastCover, _lastRing;
        int _coverDir, _ringDir;

        public bool NoConnect { get; set; }
        public int DemoState { get; set; }
        public bool SelfTestReconnect { get; set; }

        public MainWindow()
        {
            InitializeComponent();

            Loaded += async (s, e) =>
            {
                if (NoConnect) { ApplyDemo(); return; }
                await EnsureConnectedAsync(90000);
                if (SelfTestReconnect) await RunReconnectSelfTest();
            };

            Closed += (s, e) =>
            {
                _closing = true;
                try { _cts?.Cancel(); } catch { }
                try { _ble?.Dispose(); } catch { }
            };

            _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
            _tick.Tick += (s, e) => OnTick();
            _tick.Start();
        }

        async Task RunReconnectSelfTest()
        {
            if (!_connected) { Log("selftest: 初始连接失败，终止"); Close(); return; }

            Log("selftest: 等待空闲 30 秒自动断开");
            var t0 = Stopwatch.StartNew();
            while (_connected && t0.ElapsedMilliseconds < 45000) await Task.Delay(500);

            Log("selftest: 断开状态=" + !_connected + "，开始重连");
            bool ok = await EnsureConnectedAsync(60000);
            Log("selftest: 重连结果=" + ok);

            await Dispatcher.InvokeAsync(() => Close());
        }

        void ApplyDemo()
        {
            DevAddr.Text = "A4:C1:38:C6:24:9E";
            ApplyDeviceIdentity(DemoState == 8 ? 999999 : 231839);

            if (DemoState == 3)
            {
                _connected = false;
                SetOffline("空闲 30 秒，已自动断开");
                LastCmd.Text = "—";
                LastRtt.Text = "往返 —";
                LastTime.Text = "—";
                Hint("点任意按钮会自动连接后执行");
                return;
            }

            if (DemoState == 4)
            {
                _connected = false;
                SetConnecting();
                LastCmd.Text = "—";
                LastRtt.Text = "往返 —";
                LastTime.Text = "—";
                return;
            }

            _connected = true;
            _lastAction = DateTime.Now;
            SetOnline("JOMOO_SMART_231839");
            DevSig.Text = "-58 dBm";
            _lastRtt = 12;

            var st = new State();
            st.WorkState = 2;
            st.SeatTempRaw = 0;
            st.WaterFull = 1;

            if (DemoState == 5)
            {
                st.Faults.Add("进水传感器故障");
                st.Faults.Add("水箱传感器故障");
            }

            if (DemoState == 1)
            {
                st.Cover = 1; st.Ring = 1; st.SeatTempRaw = 5; st.NightLight = 1;
                LastCmd.Text = "开盖 0x33";
                LastTime.Text = DateTime.Now.ToString("HH:mm:ss");
                Hint("盖板已开启，座圈已开启");
            }
            else if (DemoState == 6)
            {
                // 正在关盖：设备行程中盖板位仍为 1，方向由行程前稳定状态推
                _lastCover = true; _coverDir = -1;
                st.Cover = 1; st.CoverRunning = 1;
                LastCmd.Text = "关盖 0x33";
                LastTime.Text = DateTime.Now.ToString("HH:mm:ss");
                Hint("正在关盖");
            }
            else if (DemoState == 7)
            {
                _lastRing = true; _ringDir = -1;
                st.Ring = 1; st.RingRunning = 1;
                LastCmd.Text = "关圈 0x34";
                LastTime.Text = DateTime.Now.ToString("HH:mm:ss");
                Hint("正在关圈");
            }
            else if (DemoState == 2)
            {
                st.WaterFull = 0;
                LastCmd.Text = "小冲 0x35";
                LastTime.Text = DateTime.Now.ToString("HH:mm:ss");
                Hint("冲水已完成，正在补水，大冲、小冲和魔力泡暂时不可用");
            }
            else
            {
                LastCmd.Text = "查询状态 0x11";
                LastTime.Text = DateTime.Now.ToString("HH:mm:ss");
                Hint("状态每 0.5 秒自动刷新");
            }

            ApplyState(st);
        }

        // ------------------------------------------------------------ 连接与会话

        /// <summary>把广播里读到的设备码翻成型号名，填进设备卡和左上角胶囊</summary>
        void ApplyDeviceIdentity(int code)
        {
            if (ModelCatalog.TryGet(code, out var model, out var series))
            {
                DevModel.Text = model;
                var shortName = model.Split('-')[0];
                TagModel.Text = shortName + " · " + series;
                Title = "九牧 " + shortName + " 智控中心";
                Log("识别型号 " + model + "（设备码 " + code + "）");
                return;
            }

            if (code > 0)
            {
                DevModel.Text = "未知机型 · " + code;
                TagModel.Text = "未登记机型";
                Title = "九牧智控中心";
                Log("设备码 " + code + " 不在型号表里，型号未识别");
                return;
            }

            DevModel.Text = "未识别";
            TagModel.Text = "型号待识别";
            Log("广播里没有设备码，型号未识别");
        }

        void SetConnecting()
        {
            ConnBar.Background = Accent(0x1A);
            ConnBar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFE, 0xBC, 0x2E));
            ConnDot.Fill = new SolidColorBrush(Color.FromRgb(0xFE, 0xBC, 0x2E));
            ConnText.Text = "正在连接…";
            ConnText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xDF, 0xA8));
            ConnSub.Text = "· 扫描 JOMOO_SMART";
            ConnSub.Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xA9, 0x6A));
            ConnAction.Visibility = Visibility.Collapsed;
            ReadoutCard.Opacity = 0.45;
            StatusBig.Text = "连接中";
            RunChip.Visibility = Visibility.Collapsed;
            if (_state == null) StatusSub.Text = "等待设备响应";
            Hint("正在扫描 JOMOO_SMART…");
        }

        void SetOnline(string name)
        {
            ConnBar.Background = new SolidColorBrush(Color.FromArgb(0x0F, 0x3E, 0xE0, 0x8F));
            ConnBar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x3E, 0xE0, 0x8F));
            ConnDot.Fill = new SolidColorBrush(Color.FromRgb(0x3E, 0xE0, 0x8F));
            ConnText.Text = "已连接";
            ConnText.Foreground = new SolidColorBrush(Color.FromRgb(0xBE, 0xEB, 0xD4));
            ConnSub.Text = "· 空闲 " + IdleLeft() + " s";
            ConnSub.Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xB7, 0x9B));
            ConnAction.Visibility = Visibility.Visible;
            ConnActionText.Text = "断开";
            ReadoutCard.Opacity = 1.0;
            Hint("已连接 " + (string.IsNullOrEmpty(name) ? "JOMOO" : name));
        }

        void SetOffline(string hint)
        {
            ConnBar.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xF8, 0x71, 0x71));
            ConnBar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xF8, 0x71, 0x71));
            ConnDot.Fill = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            ConnText.Text = "未连接";
            ConnText.Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xB8, 0xB8));
            ConnSub.Text = "· 空闲 " + IdleSeconds + " s 已断开";
            ConnSub.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x8F, 0x8F));
            ConnAction.Visibility = Visibility.Visible;
            ConnActionText.Text = "重新连接";

            ReadoutCard.Opacity = 0.45;
            DevSig.Text = "—";
            DevRtt.Text = "—";
            StatusBig.Text = "已断开";
            RunChip.Visibility = Visibility.Collapsed;

            bool cov = _state != null && _state.Cover != 0;
            bool ring = _state != null && _state.Ring != 0;
            bool sit = _state != null && _state.OnSeat != 0;
            if (_state != null)
                StatusSub.Text = "盖板" + (cov ? "开启" : "闭合") + " · 座圈" + (ring ? "开启" : "闭合")
                               + " · " + (sit ? "有人着座" : "未着座");
            else
                StatusSub.Text = "等待连接 · 点任意按钮自动连";

            Hint(hint);
        }

        async Task<bool> ConnectOnce()
        {
            if (_connected) return true;
            SetConnecting();

            var ble = new JomooBle { Address = JomooBle.LoadAddress() };
            ble.Log = Log;
            _ble = ble;

            bool ok = false;
            try { ok = await ble.ConnectAsync(); }
            catch (Exception ex) { SetOffline("连接异常：" + ex.Message); return false; }

            if (!ok)
            {
                try { ble.Dispose(); } catch { }
                if (ReferenceEquals(_ble, ble)) _ble = null;
                SetOffline("没找到设备 · 确认马桶通电、在范围内，手机小程序已退出");
                Log("扫描失败，未找到设备");
                return false;
            }

            _connected = true;
            _lastAction = DateTime.Now;
            SetOnline(ble.DevName);
            Log("已连接 " + ble.DevName + " rssi=" + ble.Rssi);

            DevAddr.Text = Mac(ble.Address);
            DevSig.Text = ble.Rssi != 0 ? ble.Rssi + " dBm" : "—";
            ApplyDeviceIdentity(ble.DeviceCode);

            _cts = new CancellationTokenSource();
            _ = PollLoop(_cts.Token);
            return true;
        }

        /// <summary>连接（失败自动重试；设备断开后广播约 3 秒恢复，靠重试覆盖）</summary>
        async Task<bool> EnsureConnectedAsync(int maxWaitMs = 60000)
        {
            if (_connected) return true;

            var t0 = Stopwatch.StartNew();
            while (!_connected && t0.ElapsedMilliseconds < maxWaitMs && !_closing)
            {
                bool ok;
                await _connectGate.WaitAsync();
                try
                {
                    ok = _connected || await ConnectOnce();
                }
                finally { _connectGate.Release(); }

                if (ok) return true;

                int left = maxWaitMs - (int)t0.ElapsedMilliseconds;
                if (left <= 6000) break;

                SetConnecting();
                Hint("没连上 · 断开后广播约 3 秒恢复，正在重试…");
                try { await Task.Delay(6000); } catch { return _connected; }
            }

            if (!_connected) SetOffline("仍未连上 · 确认马桶通电、在范围内，点「重新连接」再试");
            return _connected;
        }

        async Task DisconnectAsync(string hint)
        {
            if (!_connected) return;
            _connected = false;

            try { _cts?.Cancel(); } catch { }

            var ble = _ble;
            _ble = null;
            if (ble != null)
            {
                await Task.Delay(150);          // 让轮询循环先退出，别再往这条链路上写
                try
                {
                    await ble.CloseAsync();     // 取消通知 + 关服务 + 关设备，链路立刻释放
                    Log("已释放 GATT 会话");
                }
                catch (Exception ex) { Log("释放异常：" + ex.Message); }
            }

            SetOffline(hint);
            Log("已断开：" + hint);
        }

        // ------------------------------------------------------------ 0.5 秒轮询

        async Task PollLoop(CancellationToken ct)
        {
            int fails = 0;

            while (!ct.IsCancellationRequested && _connected && !_closing)
            {
                var sw = Stopwatch.StartNew();
                int idx = _ble != null ? _ble.Count : 0;

                bool written = false;
                try
                {
                    await _io.WaitAsync(ct);
                    try { written = await _ble.WriteAsync(Protocol.Build(Protocol.CMD_QUERY, 0)); }
                    finally { _io.Release(); }
                }
                catch (OperationCanceledException) { return; }
                catch { written = false; }

                if (!written)
                {
                    if (++fails >= 3)
                    {
                        await Dispatcher.InvokeAsync(async () => await DisconnectAsync("写入失败 · 连接可能已断开，点按钮会自动重连"));
                        return;
                    }
                }
                else
                {
                    fails = 0;
                    try
                    {
                        var f = await _ble.WaitFrameAsync(200, idx);
                        if (f != null)
                        {
                            var st = Protocol.Parse(f);
                            if (st != null)
                            {
                                _lastRtt = (int)sw.ElapsedMilliseconds;
                                if (!_loggedFirstFrame)
                                {
                                    _loggedFirstFrame = true;
                                    Log("首帧 rtt=" + _lastRtt + "ms 工作态=" + st.WorkState + " 盖=" + st.Cover + " 圈=" + st.Ring + " 座温=" + st.SeatTempRaw);
                                }
                                await Dispatcher.InvokeAsync(() => ApplyState(st));
                            }
                        }
                    }
                    catch { }
                }

                int rest = PollMs - (int)sw.ElapsedMilliseconds;
                if (rest > 0)
                {
                    try { await Task.Delay(rest, ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        // ------------------------------------------------------------ 状态刷新

        void ApplyState(State s)
        {
            _state = s;

            if (_connected)
            {
                ReadoutCard.Opacity = 1.0;
                ConnSub.Text = "· 空闲 " + IdleLeft() + " s";
            }

            bool cov = s.Cover != 0, ring = s.Ring != 0;
            bool covRun = s.CoverRunning != 0, ringRun = s.RingRunning != 0;
            bool night = s.NightLight != 0;
            bool sit = s.OnSeat != 0;
            bool hasFault = s.Faults.Count > 0;

            // 行程方向：设备行程中仍保持原状态位，用行程开始前的稳定状态推方向
            if (covRun && _coverDir == 0) _coverDir = _lastCover ? -1 : 1;
            if (!covRun) { _coverDir = 0; _lastCover = cov; }
            if (ringRun && _ringDir == 0) _ringDir = _lastRing ? -1 : 1;
            if (!ringRun) { _ringDir = 0; _lastRing = ring; }

            bool flushing = s.FlushLarge || s.FlushSmall;
            bool full = s.WaterFull != 0;
            bool refilling = !flushing && !full;          // d[13] bit7=0 且不在冲水中 → 补水/蓄水中

            ValCover.Text = covRun ? (_coverDir < 0 ? "关闭中" : "开启中") : (cov ? "开启" : "闭合");
            ValCover.Foreground = covRun ? TextAccent : TextNormal;
            ValRing.Text = ringRun ? (_ringDir < 0 ? "关闭中" : "开启中") : (ring ? "开启" : "闭合");
            ValRing.Foreground = ringRun ? TextAccent : TextNormal;

            ValTemp.Text = s.SeatTempText;
            ValNight.Text = night ? "开启" : "关闭";
            ValSit.Text = sit ? "有人" : "无人";

            ApplyFaults(s);

            // 水箱：冲水中 → 排空中；bit7=0 → 蓄水中；bit7=1 → 蓄满
            if (flushing)
            {
                WaterStrip.Background = Accent(0x1A);
                WaterStrip.BorderBrush = Accent(0x3D);
                WaterDot.Fill = TextAccent;
                WaterVal.Text = "冲水中 · 水箱排空";
                WaterVal.Foreground = AccentText;
            }
            else if (refilling)
            {
                WaterStrip.Background = Accent(0x1A);
                WaterStrip.BorderBrush = Accent(0x3D);
                WaterDot.Fill = TextAccent;
                WaterVal.Text = "蓄水中 · 水箱未补满";
                WaterVal.Foreground = AccentText;
            }
            else
            {
                WaterStrip.Background = Neutral(0x0C);
                WaterStrip.BorderBrush = Neutral(0x14);
                WaterDot.Fill = GreenText;
                WaterVal.Text = "已蓄满";
                WaterVal.Foreground = GreenText;
            }

            bool trans = false;
            bool refillStatus = false;
            if (hasFault) StatusBig.Text = "故障";
            else if (covRun) { StatusBig.Text = _coverDir < 0 ? "正在关盖" : "正在开盖"; trans = true; }
            else if (ringRun) { StatusBig.Text = _ringDir < 0 ? "正在关圈" : "正在开圈"; trans = true; }
            else if (s.FlushLarge) { StatusBig.Text = "大冲执行中"; trans = true; }
            else if (s.FlushSmall) { StatusBig.Text = "小冲执行中"; trans = true; }
            else if (refilling) { StatusBig.Text = "蓄水中"; trans = true; refillStatus = true; }
            else if (s.Bubbling != 0) { StatusBig.Text = "发泡中"; trans = true; }
            else if (s.WorkState == 1) StatusBig.Text = "休眠";
            else if (s.WorkState == 16) StatusBig.Text = "自清洁中";
            else if (s.WorkState == 17) StatusBig.Text = "预湿润中";
            else if (s.WorkState == 31) StatusBig.Text = "自检未就绪";
            else if (sit) StatusBig.Text = "使用中";
            else StatusBig.Text = "待机";

            RunChip.Visibility = trans ? Visibility.Visible : Visibility.Collapsed;
            RunChipText.Text = refillStatus ? "补水进行" : "进行中";

            StatusSub.Text = "盖板" + (covRun ? (_coverDir < 0 ? "关闭中" : "开启中") : (cov ? "开启" : "闭合"))
                           + " · 座圈" + (ringRun ? (_ringDir < 0 ? "关闭中" : "开启中") : (ring ? "开启" : "闭合"))
                           + " · " + (sit ? "有人着座" : "未着座");

            // 三个状态色按钮：开=蓝、合/关=灰
            SetTile(BtnCover, cov);
            CoverIconRect.Stroke = cov ? TextAccent : GrayIcon;
            CoverIconBand.Fill = cov ? TextAccent : GrayIcon;
            CoverLabel.Text = cov ? "关盖" : "开盖";
            CoverState.Text = covRun ? (_coverDir < 0 ? "关闭中…" : "开启中…") : "当前 · " + (cov ? "开启" : "闭合");
            CoverState.Foreground = cov ? AccentText : GrayText;

            SetTile(BtnRing, ring);
            RingIcon.Stroke = ring ? TextAccent : GrayIcon;
            RingIcon.Fill = ring
                ? new LinearGradientBrush(Color.FromRgb(0x57, 0xC8, 0xFF), Color.FromArgb(0x00, 0x57, 0xC8, 0xFF),
                                          new Point(0, 0), new Point(0, 1))
                : Neutral(0x00);
            RingLabel.Text = ring ? "关圈" : "开圈";
            RingState.Text = ringRun ? (_ringDir < 0 ? "关闭中…" : "开启中…") : "当前 · " + (ring ? "开启" : "闭合");
            RingState.Foreground = ring ? AccentText : GrayText;

            SetTile(BtnNight, night);
            NightIconBar.Fill = night ? TextAccent : GrayIcon;
            NightIconShade.Fill = night ? new SolidColorBrush(Color.FromArgb(0x55, 0x57, 0xC8, 0xFF)) : Neutral(0x22);
            NightState.Text = "当前 · " + (night ? "开启" : "关闭");
            NightState.Foreground = night ? AccentText : GrayText;

            // 冲水中、蓄水中都禁用冲水，蓄满自动恢复
            bool flushOff = flushing || refilling;
            BtnBig.Opacity = flushOff ? 0.35 : 1.0;
            BtnSmall.Opacity = flushOff ? 0.35 : 1.0;
            BtnBubble.Opacity = flushOff ? 0.35 : 1.0;
            BtnBig.IsHitTestVisible = !flushOff;
            BtnSmall.IsHitTestVisible = !flushOff;
            BtnBubble.IsHitTestVisible = !flushOff;
            BigLabel.Foreground = flushOff ? DisabledText : TextNormal;
            SmallLabel.Foreground = flushOff ? DisabledText : TextNormal;
            BubbleLabel.Foreground = flushOff ? DisabledText : TextNormal;
            BigState.Text = flushOff ? (flushing ? "冲水中不可用" : "蓄水中不可用") : "满水强冲";
            SmallState.Text = flushOff ? (flushing ? "冲水中不可用" : "蓄水中不可用") : "半水轻冲";
            BubbleState.Text = flushOff ? (flushing ? "冲水中不可用" : "蓄水中不可用")
                                        : (s.Bubbling != 0 ? "发泡中" : "泡沫隔臭");

            SelectTemp(s.SeatTempRaw);

            DevRtt.Text = _lastRtt > 0 ? _lastRtt + " ms" : "—";
            LastRtt.Text = _lastRtt > 0 ? "往返 " + _lastRtt + " ms" : "往返 —";
        }

        static void SetTile(System.Windows.Controls.Border b, bool on)
        {
            b.Tag = on ? "on" : "off";   // 由样式触发器上色，避免局部值压掉 hover
        }

        void ApplyFaults(State s)
        {
            FaultList.Children.Clear();

            if (s.Faults.Count == 0)
            {
                FaultVal.Text = "正常";
                FaultVal.Foreground = GreenText;
                FaultCell.Tag = "ok";
                if (FaultPopup.IsOpen) FaultPopup.IsOpen = false;
                return;
            }

            foreach (var f in s.Faults)
            {
                FaultList.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = f,
                    FontSize = 12,
                    Foreground = TextRed,
                    Margin = new Thickness(0, 2, 0, 2),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            FaultVal.Text = "异常";
            FaultVal.Foreground = YellowText;
            FaultCell.Tag = "bad";
        }

        void FaultEnter(object sender, MouseEventArgs e)
        {
            if (_state == null || _state.Faults.Count == 0) return;
            FaultPopup.IsOpen = true;
        }

        void FaultLeave(object sender, MouseEventArgs e)
        {
            FaultPopup.IsOpen = false;
        }

        void SelectTemp(int raw)
        {
            var tiles = new[] { Temp0, Temp1, Temp2 };
            var keys = new[] { 0, 3, 5 };
            var labels = new[] { TempTitle0, TempTitle1, TempTitle2 };

            for (int i = 0; i < 3; i++)
            {
                bool on = raw == keys[i];
                SetTile(tiles[i], on);
                labels[i].Foreground = on ? AccentText : TextNormal;
            }
        }

        static Brush Accent(byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, 0x57, 0xC8, 0xFF));
        static Brush Neutral(byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        static readonly Brush AccentText = new SolidColorBrush(Color.FromRgb(0xBF, 0xE6, 0xFB));

        int IdleLeft()
        {
            double idle = (DateTime.Now - _lastAction).TotalSeconds;
            return Math.Max(0, IdleSeconds - (int)idle);
        }

        void OnTick()
        {
            if (!_connected) return;
            int left = IdleLeft();
            ConnSub.Text = "· 空闲 " + left + " s";
            if (left == 0) _ = DisconnectAsync("空闲 30 秒，已自动断开");
        }

        // ------------------------------------------------------------ 动作

        async Task DoAction(string label, int cmd, params int[] vals)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                _lastAction = DateTime.Now;
                if (!_connected && !await EnsureConnectedAsync(45000)) return;
                _lastAction = DateTime.Now;

                var frame = Protocol.Build(cmd, vals);

                await _io.WaitAsync();
                bool ok;
                try { ok = await _ble.WriteAsync(frame); }
                finally { _io.Release(); }

                if (!ok) { Hint("写入失败，稍后再试"); return; }

                LastCmd.Text = label + " 0x" + cmd.ToString("X2");
                LastTime.Text = DateTime.Now.ToString("HH:mm:ss");
                Hint("已发送 " + label);
                _lastAction = DateTime.Now;

                await Task.Delay(300);   // 等设备推回状态帧
            }
            catch (Exception ex)
            {
                Hint("异常：" + ex.Message);
            }
            finally
            {
                _busy = false;
                _lastAction = DateTime.Now;
            }
        }

        // 按下 + 抬起都落在同一个按钮上才执行：拖窗、误触都不会触发

        void TileDown(object sender, MouseButtonEventArgs e) => _downOn = sender as FrameworkElement;

        bool Arm(object sender)
        {
            var f = sender as FrameworkElement;
            bool ok = f != null && _downOn == f && f.IsMouseOver;
            _downOn = null;
            return ok;
        }

        void CoverClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            bool open = _state != null && _state.Cover != 0;
            _ = DoAction(open ? "关盖" : "开盖", Protocol.CMD_COVER, open ? 0 : 1);
        }

        void RingClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            bool open = _state != null && _state.Ring != 0;
            _ = DoAction(open ? "关圈" : "开圈", Protocol.CMD_RING, open ? 0 : 1);
        }

        void BigClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            _ = DoAction("大冲", Protocol.CMD_FLUSH, 2);
        }

        void SmallClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            _ = DoAction("小冲", Protocol.CMD_FLUSH, 1);
        }

        void BubbleClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            _ = DoAction("魔力泡", Protocol.CMD_BUBBLE, 1);
        }

        void SleepClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            _ = DoAction("休眠", Protocol.CMD_HIBERNATE, 1);
        }

        void NightClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            bool on = _state != null && _state.NightLight != 0;
            _ = DoAction(on ? "夜灯关" : "夜灯开", Protocol.CMD_NIGHT_LIGHT, 0, on ? 0 : 1);
        }

        void TempClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            var tag = (sender as FrameworkElement)?.Uid;
            if (string.IsNullOrEmpty(tag)) return;
            int v = int.Parse(tag);
            _ = DoAction("座温 " + (v == 0 ? "常温" : v == 3 ? "中温" : "高温"), Protocol.CMD_SEAT_TEMP, v);
        }

        async void ConnActionClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            if (_connected) { await DisconnectAsync("手动断开 · 点「重新连接」可再次连上"); return; }
            await EnsureConnectedAsync(60000);
        }

        // ------------------------------------------------------------ 杂项

        static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jomoo_app.log");

        static void Log(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        void Hint(string text)
        {
            HintText.Text = text;
        }

        static string Mac(ulong a) =>
            string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (40 - 8 * i)) & 0xFF).ToString("X2")));

        void DragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src && IsInside(src, CloseButton)) return;
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        static bool IsInside(DependencyObject node, DependencyObject root)
        {
            while (node != null)
            {
                if (node == root) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        void CloseClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            Close();
        }
    }
}
