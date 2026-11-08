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

        AppSettings _settings = new AppSettings();
        AppSettings _draft;
        bool _syncUi;

        public bool NoConnect { get; set; }
        public int DemoState { get; set; }
        public bool SelfTestReconnect { get; set; }

        public MainWindow()
        {
            InitializeComponent();

            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string verText = "v" + ver.Major + "." + ver.Minor;
            VersionText.Text = verText;

            _settings = AppSettings.Load(out var loadNote);
            ApplyRuntimeSettings(_settings);
            Log(verText + " 启动 设置 idle=" + _settings.IdleSeconds + " poll=" + _settings.PollMs
                + " bind=" + _settings.DeviceMode + " opacity=" + _settings.Opacity + " scale=" + _settings.Scale);

            Loaded += async (s, e) =>
            {
                Hint(IdleHint());
                if (!string.IsNullOrEmpty(loadNote)) ShowSettingsHintSafe(loadNote);
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

            PreviewKeyDown += (s, e) =>
            {
                if (SettingsLayer.Visibility != Visibility.Visible) return;
                if (e.Key == Key.Escape) { CloseSettings(); e.Handled = true; }
            };
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

            if (DemoState == 9) OpenSettings();
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
            ConnSub.Text = _settings.DeviceMode == "mac" && !string.IsNullOrEmpty(_settings.TargetMac)
                ? "· 锁定 " + _settings.TargetMac
                : "· 扫描 JOMOO_SMART";
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
            ConnSub.Text = IdleText();
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
            ConnSub.Text = _settings.IdleEnabled ? "· 空闲 " + _settings.IdleSeconds + " s 已断开" : "· 已断开 · 不自动断开";
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
            if (_settings.DeviceMode == "mac")
            {
                var mac = AppSettings.ParseMac(_settings.TargetMac);
                if (mac != 0) { ble.BindMac = true; ble.BindAddress = mac; }
            }
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

                int rest = _settings.PollMs - (int)sw.ElapsedMilliseconds;
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
                ConnSub.Text = IdleText();
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
            if (!_settings.IdleEnabled) return -1;
            double idle = (DateTime.Now - _lastAction).TotalSeconds;
            return Math.Max(0, _settings.IdleSeconds - (int)idle);
        }

        string IdleText()
        {
            if (!_settings.IdleEnabled) return "· 不自动断开";
            return "· 空闲 " + IdleLeft() + " s";
        }

        string IdleHint()
        {
            if (!_settings.IdleEnabled) return "启动后自动连接，连接后不自动断开";
            return "启动后自动连接，空闲 " + _settings.IdleSeconds + " 秒自动断开";
        }

        void OnTick()
        {
            if (!_connected) return;
            int left = IdleLeft();
            if (left < 0) { ConnSub.Text = "· 不自动断开"; return; }
            ConnSub.Text = "· 空闲 " + left + " s";
            if (left == 0) _ = DisconnectAsync("空闲 " + _settings.IdleSeconds + " 秒，已自动断开");
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

        // ------------------------------------------------------------ 界面设置

        void ApplyRuntimeSettings(AppSettings s)
        {
            Backdrop.Opacity = s.Opacity;
            ApplyScale(s.Scale);
            LogDirText.Text = "%APPDATA%\\JomooRemote\\logs";
        }

        /// <summary>整体缩放：同步窗口宽高并夹回工作区；放不下返回 false</summary>
        bool ApplyScale(double scale)
        {
            var wa = SystemParameters.WorkArea;
            double w = 1240 * scale, h = 800 * scale;
            if (w > wa.Width + 0.5 || h > wa.Height + 0.5) return false;

            ScaleHost.LayoutTransform = new ScaleTransform(scale, scale);
            Width = w;
            Height = h;

            double left = Left, top = Top;
            if (double.IsNaN(left) || double.IsNaN(top)) return true;
            if (left + w > wa.Right) left = wa.Right - w;
            if (top + h > wa.Bottom) top = wa.Bottom - h;
            if (left < wa.Left) left = wa.Left;
            if (top < wa.Top) top = wa.Top;
            Left = left;
            Top = top;
            return true;
        }

        void OpenSettings()
        {
            _draft = _settings.Clone();
            SettingsHint.Text = "";
            DeviceSheet.Visibility = Visibility.Collapsed;
            SyncSettingsUi();
            SettingsLayer.Visibility = Visibility.Visible;
            Log("打开设置面板");
        }

        void CloseSettings()
        {
            SettingsLayer.Visibility = Visibility.Collapsed;
            DeviceSheet.Visibility = Visibility.Collapsed;
            _draft = null;
        }

        void SyncSettingsUi()
        {
            if (_draft == null) return;
            _syncUi = true;
            try
            {
                IdleSlider.Value = _draft.IdleSeconds;
                IdleVal.Text = _draft.IdleEnabled ? _draft.IdleSeconds + " s" : "不自动断开";
                PollSlider.Value = _draft.PollMs;
                PollVal.Text = _draft.PollMs + " ms";
                OpacitySlider.Value = Math.Round(_draft.Opacity * 100);
                OpacityVal.Text = Math.Round(_draft.Opacity * 100) + "%";

                var cells = new[] { ScaleCell0, ScaleCell1, ScaleCell2, ScaleCell3 };
                var texts = new[] { ScaleText0, ScaleText1, ScaleText2, ScaleText3 };
                int idx = _draft.ScaleIndex;
                for (int i = 0; i < cells.Length; i++) SetSeg(cells[i], texts[i], i == idx);

                bool mac = _draft.DeviceMode == "mac";
                SetSeg(BindAutoCell, BindAutoText, !mac);
                SetSeg(BindMacCell, BindMacText, mac);
                BindDesc.Text = mac ? "按 MAC 锁定设备，扫不到时回落自动并写日志"
                                    : "自动扫描首个 JOMOO_SMART 设备";
                TargetBox.Opacity = mac ? 1.0 : 0.45;
                TargetText.Text = string.IsNullOrEmpty(_draft.TargetMac) ? "未指定 · 点此选择设备" : _draft.TargetMac;
                TargetText.Foreground = string.IsNullOrEmpty(_draft.TargetMac) ? GrayText : TextNormal;

                LogTrack.Background = _draft.Logging ? Accent(0x22) : Neutral(0x22);
                LogTrack.BorderBrush = _draft.Logging ? Accent(0x3D) : Neutral(0x1F);
                LogKnob.Fill = _draft.Logging ? TextAccent : GrayIcon;
                LogKnob.HorizontalAlignment = _draft.Logging ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                LogKnob.Margin = _draft.Logging ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);
            }
            finally { _syncUi = false; }
        }

        static void SetSeg(System.Windows.Controls.Border cell, System.Windows.Controls.TextBlock text, bool on)
        {
            cell.Background = on
                ? new SolidColorBrush(Color.FromArgb(0x17, 0x57, 0xC8, 0xFF))
                : new SolidColorBrush(Colors.Transparent);
            cell.BorderBrush = on
                ? new SolidColorBrush(Color.FromArgb(0x3D, 0x57, 0xC8, 0xFF))
                : new SolidColorBrush(Colors.Transparent);
            text.Foreground = on
                ? new SolidColorBrush(Color.FromRgb(0x8F, 0xDC, 0xFA))
                : new SolidColorBrush(Color.FromRgb(0x8F, 0xA6, 0xB8));
        }

        void IdleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_draft == null || _syncUi) return;
            int v = (int)Math.Round(e.NewValue);
            _draft.IdleSeconds = v;
            IdleVal.Text = v >= AppSettings.IdleNever ? "不自动断开" : v + " s";
            SettingsHint.Text = v < 10 ? "频繁断开会增加重连等待" : "";
        }

        void PollChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_draft == null || _syncUi) return;
            int v = (int)Math.Round(e.NewValue);
            _draft.PollMs = v;
            PollVal.Text = v + " ms";
            SettingsHint.Text = v < 400 ? "低于 400 ms 时设备回帧可能来不及，易丢帧" : "";
        }

        void OpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_draft == null || _syncUi) return;
            _draft.Opacity = Math.Round(e.NewValue) / 100.0;
            OpacityVal.Text = Math.Round(e.NewValue) + "%";
            SettingsHint.Text = e.NewValue < 85 ? "再降低会让背景与桌面混色，影响可读性" : "";
        }

        void ScaleClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender) || _draft == null) return;
            var f = sender as FrameworkElement;
            if (f == null || string.IsNullOrEmpty(f.Uid)) return;
            int idx = int.Parse(f.Uid);
            if (idx < 0 || idx >= AppSettings.Scales.Length) return;
            _draft.Scale = AppSettings.Scales[idx];
            SyncSettingsUi();
            SettingsHint.Text = "";
        }

        void BindAutoClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender) || _draft == null) return;
            _draft.DeviceMode = "auto";
            SyncSettingsUi();
            SettingsHint.Text = "";
        }

        void BindMacClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender) || _draft == null) return;
            _draft.DeviceMode = "mac";
            SyncSettingsUi();
            SettingsHint.Text = string.IsNullOrEmpty(_draft.TargetMac) ? "点下方选择要锁定的设备" : "";
        }

        async void TargetBoxClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender) || _draft == null) return;
            DeviceSheet.Visibility = Visibility.Visible;
            DevicesList.Children.Clear();
            ScanState.Text = "扫描中… 最长 6 秒";

            System.Collections.Generic.List<(string Name, string Mac, ulong Address, short Rssi)> hits;
            try { hits = await JomooBle.ScanAllAsync(6000); }
            catch (Exception ex)
            {
                ScanState.Text = "扫描异常：" + ex.Message;
                return;
            }

            DevicesList.Children.Clear();
            if (hits.Count == 0)
            {
                ScanState.Text = "未扫描到 JOMOO_SMART 设备 · 确认马桶通电并在范围内";
                return;
            }

            ScanState.Text = "扫描到 " + hits.Count + " 台设备，点击选择";
            foreach (var h in hits)
            {
                var row = new System.Windows.Controls.Border
                {
                    Height = 44,
                    CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(0, 0, 0, 8),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Background = Neutral(0x0C),
                    BorderBrush = Neutral(0x14),
                    Tag = h.Mac
                };
                var g = new System.Windows.Controls.Grid { Margin = new Thickness(12, 0, 12, 0) };
                g.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = h.Name, FontSize = 12, Foreground = TextNormal,
                    VerticalAlignment = VerticalAlignment.Center
                });
                g.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = h.Mac + " · " + h.Rssi + " dBm", FontSize = 11, Foreground = GrayText,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right
                });
                row.Child = g;
                row.PreviewMouseLeftButtonDown += (s, ev) => { _downOn = row; };
                row.MouseLeftButtonUp += DeviceItemClick;
                DevicesList.Children.Add(row);
            }
        }

        void DeviceItemClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender) || _draft == null) return;
            var f = sender as FrameworkElement;
            _draft.TargetMac = (f != null && f.Tag is string) ? (string)f.Tag : "";
            _draft.DeviceMode = "mac";
            DeviceSheet.Visibility = Visibility.Collapsed;
            SyncSettingsUi();
            SettingsHint.Text = "已选定 " + _draft.TargetMac + "，保存后生效";
        }

        void DeviceSheetCancelClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            DeviceSheet.Visibility = Visibility.Collapsed;
        }

        void LogToggleClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender) || _draft == null) return;
            _draft.Logging = !_draft.Logging;
            SyncSettingsUi();
            SettingsHint.Text = _draft.Logging ? "" : "关闭后不再写入本地日志";
        }

        void OpenLogDirClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            try
            {
                System.IO.Directory.CreateDirectory(AppSettings.LogDir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppSettings.LogDir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SettingsHint.Text = "打开目录失败：" + ex.Message;
            }
        }

        void ResetSettingsClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            _draft = new AppSettings();
            _draft.Clamp();
            SyncSettingsUi();
            SettingsHint.Text = "已填入默认值，点「保存」后生效";
        }

        void SaveSettingsClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender) || _draft == null) return;

            var s = _draft.Clone();
            s.Clamp();

            if (s.DeviceMode == "mac" && AppSettings.ParseMac(s.TargetMac) == 0)
            {
                SettingsHint.Text = "「指定」需要先选择一个设备，或切回「自动」";
                return;
            }

            if (!ApplyScale(s.Scale))
            {
                ApplyScale(_settings.Scale);
                _draft.Scale = _settings.Scale;
                SyncSettingsUi();
                SettingsHint.Text = "当前缩放超出屏幕工作区，已回退到上一档";
                return;
            }

            var oldBind = _settings.DeviceMode + "|" + _settings.TargetMac;
            _settings = s;
            _settings.Save();
            Backdrop.Opacity = s.Opacity;
            if (_connected) ConnSub.Text = IdleText();
            Hint("设置已保存 · " + (s.IdleEnabled ? "空闲 " + s.IdleSeconds + " s 自动断开" : "不自动断开")
                 + " · 轮询 " + s.PollMs + " ms");
            Log("设置已保存 idle=" + s.IdleSeconds + " poll=" + s.PollMs + " bind=" + s.DeviceMode
                + " mac=" + s.TargetMac + " opacity=" + s.Opacity + " scale=" + s.Scale + " logging=" + s.Logging);
            CloseSettings();

            if (oldBind != s.DeviceMode + "|" + s.TargetMac && _connected)
                _ = DisconnectAsync("目标设备设置已变更，已断开 · 点按钮按新设置重连");
        }

        void CancelSettingsClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            CloseSettings();
        }

        void SettingsCloseClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            CloseSettings();
        }

        void SettingsMaskClick(object sender, MouseButtonEventArgs e)
        {
            CloseSettings();
        }

        void GearClick(object sender, MouseButtonEventArgs e)
        {
            if (!Arm(sender)) return;
            OpenSettings();
        }

        void ShowSettingsHintSafe(string text)
        {
            if (SettingsHint != null) SettingsHint.Text = text;
        }

        // ------------------------------------------------------------ 杂项

        void Log(string msg)
        {
            if (!_settings.Logging) return;
            try
            {
                System.IO.Directory.CreateDirectory(AppSettings.LogDir);
                System.IO.File.AppendAllText(AppSettings.LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n");
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
            if (e.OriginalSource is DependencyObject src && (IsInside(src, CloseButton) || IsInside(src, GearButton))) return;
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
