using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace JomooRemote
{
    /// <summary>按需连接：每次操作连一次、发完就断，手机小程序随时可用</summary>
    public sealed class JomooBle : IDisposable
    {
        const string NamePrefix = "JOMOO_SMART";
        static readonly Guid SvcUuid = Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb");
        static readonly Guid ChrUuid = Guid.Parse("0000ffe1-0000-1000-8000-00805f9b34fb");

        public static string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JomooRemote", "config.json");

        public ulong Address;
        public string DevName = "";
        public short Rssi;
        /// <summary>设备码（广播厂商数据里的 jmZtmxm），未识别为 0</summary>
        public int DeviceCode;

        BluetoothLEDevice _dev;
        GattCharacteristic _chr;
        GattDeviceService _svc;
        IReadOnlyList<GattDeviceService> _allSvc;
        readonly List<byte[]> _frames = new();
        readonly object _lock = new();
        public Action<string> Log = _ => { };

        public int Count { get { lock (_lock) return _frames.Count; } }

        public List<byte[]> Frames
        {
            get { lock (_lock) return new List<byte[]>(_frames); }
        }

        public static ulong LoadAddress()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return 0;
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                if (doc.RootElement.TryGetProperty("address", out var a))
                    return ulong.TryParse(a.GetString(), out var v) ? v : 0;
            }
            catch { }
            return 0;
        }

        public static void SaveAddress(ulong addr)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath, "{\"address\":\"" + addr + "\"}");
            }
            catch { }
        }

        /// <summary>广播名形如 JOMOO_SMART_231839，末尾 6 位就是设备码（jmZtmxm）</summary>
        static int CodeFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int p = name.LastIndexOf('_');
            if (p < 0 || p + 1 >= name.Length) return 0;
            var tail = name.Substring(p + 1);
            if (tail.Length < 5 || tail.Length > 7) return 0;
            int v;
            return int.TryParse(tail, out v) ? v : 0;
        }

        /// <summary>从厂商广播数据里取设备码（jmZtmxm）：优先按已知型号表反查三字节组合，退化为固定偏移读取</summary>
        static int ReadDeviceCode(BluetoothLEAdvertisement adv)
        {
            foreach (var md in adv.ManufacturerData)
            {
                if (md.CompanyId != 0xC1A4) continue;
                var buf = md.Data;
                if (buf == null || buf.Length < 4) continue;

                var bytes = new byte[buf.Length];
                using (var dr = DataReader.FromBuffer(buf)) dr.ReadBytes(bytes);

                for (int i = 0; i + 2 < bytes.Length; i++)
                {
                    int c = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
                    if (ModelCatalog.TryGet(c, out _, out _)) return c;
                }
                if (bytes.Length >= 10)
                    return (bytes[7] << 16) | (bytes[8] << 8) | bytes[9];
            }
            return 0;
        }

        async Task<ulong> ScanAsync(int ms)
        {
            ulong found = 0;
            string name = "";
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            watcher.Received += (s, e) =>
            {
                var n = e.Advertisement.LocalName;
                if (found == 0 && !string.IsNullOrEmpty(n) && n.StartsWith(NamePrefix))
                {
                    found = e.BluetoothAddress;
                    name = n;
                    Rssi = (short)e.RawSignalStrengthInDBm;
                    DeviceCode = CodeFromName(n);
                    if (DeviceCode == 0) { try { DeviceCode = ReadDeviceCode(e.Advertisement); } catch { DeviceCode = 0; } }
                    if (DeviceCode == 0)
                    {
                        try
                        {
                            var sb = new System.Text.StringBuilder("广播原始数据:");
                            foreach (var sec in e.Advertisement.DataSections)
                            {
                                var b = new byte[sec.Data.Length];
                                using (var dr2 = DataReader.FromBuffer(sec.Data)) dr2.ReadBytes(b);
                                sb.Append(" [type=0x").Append(sec.DataType.ToString("X2")).Append(" len=").Append(b.Length).Append(" ->");
                                foreach (var x in b) sb.Append(" ").Append(x.ToString("X2"));
                                sb.Append("]");
                            }
                            sb.Append(" ; 厂商段数=").Append(e.Advertisement.ManufacturerData.Count);
                            Log(sb.ToString());
                        }
                        catch { }
                    }
                }
            };
            watcher.Start();
            var t0 = Environment.TickCount64;
            while (Environment.TickCount64 - t0 < ms && found == 0)
                await Task.Delay(100);
            watcher.Stop();
            DevName = name;
            return found;
        }

        /// <summary>扫描接入：先按广播名找，找不到再按上次地址直连；每轮 8 秒，最多 3 轮</summary>
        public async Task<bool> ConnectAsync()
        {
            for (int round = 0; round < 3; round++)
            {
                ulong addr = await ScanAsync(8000);
                if (addr != 0)
                {
                    if (await OpenAsync(addr)) return true;
                }
                else if (Address != 0)
                {
                    Log("没扫到广播，按上次地址直连…");
                    if (await OpenAsync(Address)) return true;
                }

                if (round < 2)
                {
                    Log("第 " + (round + 1) + " 轮没连上，稍后重试");
                    await Task.Delay(1500);
                }
            }
            return false;
        }

        async Task<bool> OpenAsync(ulong addr)
        {
            try
            {
                Address = addr;
                if (string.IsNullOrEmpty(DevName)) DevName = "JOMOO_SMART_" + addr.ToString("X12");
                SaveAddress(addr);

                _dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
                if (_dev == null) return false;

                var svcRes = await _dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                _allSvc = svcRes.Services;

                GattDeviceService svc = null;
                foreach (var s in _allSvc)
                    if (s.Uuid == SvcUuid) { svc = s; break; }
                if (svc == null) return false;
                _svc = svc;

                var chrRes = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                foreach (var c in chrRes.Characteristics)
                    if (c.Uuid == ChrUuid) { _chr = c; break; }
                if (_chr == null) return false;

                _chr.ValueChanged += OnValue;
                var st = await _chr.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (st != GattCommunicationStatus.Success) return false;
                await Task.Delay(600);   // 实测必须留这段，否则设备不认第一帧
                return true;
            }
            catch (Exception ex)
            {
                Log("打开连接异常：" + ex.Message);
                return false;
            }
        }


        void OnValue(GattCharacteristic sender, GattValueChangedEventArgs e)
        {
            try
            {
                var buf = new byte[e.CharacteristicValue.Length];
                DataReader.FromBuffer(e.CharacteristicValue).ReadBytes(buf);
                lock (_lock) _frames.Add(buf);
            }
            catch { }
        }

        public async Task<bool> WriteAsync(byte[] frame)
        {
            if (_chr == null) return false;
            var w = new DataWriter();
            w.WriteBytes(frame);
            var st = await _chr.WriteValueAsync(w.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
            return st == GattCommunicationStatus.Success;
        }

        /// <summary>等一帧新数据（序号 &gt; afterIndex）</summary>
        public async Task<byte[]> WaitFrameAsync(int timeoutMs, int afterIndex)
        {
            var t0 = Environment.TickCount64;
            while (Environment.TickCount64 - t0 < timeoutMs)
            {
                lock (_lock)
                {
                    if (_frames.Count > afterIndex) return _frames[afterIndex];
                }
                await Task.Delay(40);
            }
            return null;
        }

        /// <summary>彻底释放：取消通知订阅 + 关掉所有 GATT 服务/设备。不这样做 Windows 会继续占着链路，马桶一直不恢复广播</summary>
        public async Task CloseAsync()
        {
            try { if (_chr != null) _chr.ValueChanged -= OnValue; } catch { }

            try
            {
                if (_chr != null)
                    await _chr.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { }

            try { _chr = null; } catch { }
            try { _svc?.Dispose(); } catch { }
            try
            {
                if (_allSvc != null)
                    foreach (var s in _allSvc)
                    {
                        try { if (!ReferenceEquals(s, _svc)) s.Dispose(); } catch { }
                    }
            }
            catch { }

            _svc = null;
            _allSvc = null;
            try { _dev?.Dispose(); } catch { }
            _dev = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public void Dispose()
        {
            try { CloseAsync().Wait(1200); } catch { }
        }
    }
}
