using System;
using System.Collections.Generic;
using System.Linq;

namespace JomooRemote
{
    /// <summary>九牧 SQ9650 BLE 协议（源码逆向 + 真机实测确认）</summary>
    public static class Protocol
    {
        public const int CMD_QUERY = 17;        // 0x11 查询全部状态
        public const int CMD_COVER = 51;        // 0x33 盖板    1=开 0=合
        public const int CMD_RING = 52;         // 0x34 座圈    1=开 0=合
        public const int CMD_FLUSH = 53;        // 0x35 冲水    2=大冲 1=小冲
        public const int CMD_STOP = 50;         // 0x32 停止当前清洗
        public const int CMD_NOZZLE = 55;       // 0x37 喷嘴清洁
        public const int CMD_SEAT_TEMP = 60;    // 0x3C 座温档位 0/3/5
        public const int CMD_NIGHT_LIGHT = 65;  // 0x41 夜灯
        public const int CMD_BUBBLE = 70;       // 0x46 魔力泡
        public const int CMD_HIBERNATE = 23;    // 0x17 休眠

        public static readonly Dictionary<int, string> WorkState = new()
        {
            [1] = "休眠中",
            [2] = "待机",
            [16] = "自清洁中",
            [17] = "预湿润中",
            [31] = "自检中",
        };

        public static readonly Dictionary<int, string> SeatTemp = new()
        {
            [0] = "常温",
            [3] = "中温",
            [5] = "高温",
        };

        /// <summary>FC CK 00 00 00 00 CMD [VAL...] FC，CK = sum(bytes[2..len-2]) &amp; 0xFF</summary>
        public static byte[] Build(int cmd, params int[] vals)
        {
            var b = new List<byte> { 0xFC, 0x00, 0x00, 0x00, 0x00, 0x00, (byte)cmd };
            foreach (var v in vals) b.Add((byte)v);
            b.Add(0xFC);
            int sum = 0;
            for (int i = 2; i < b.Count - 1; i++) sum += b[i];
            b[1] = (byte)(sum & 0xFF);
            return b.ToArray();
        }

        public static string Hex(byte[] d) => BitConverter.ToString(d).Replace("-", " ");

        static int Bit(byte v, int n) => (v >> n) & 1;

        /// <summary>解析 0x30 状态帧，字段位置严格照源码 dealWith30</summary>
        public static State Parse(byte[] d)
        {
            if (d == null || d.Length < 25 || d[0] != 0xFC || d[d.Length - 1] != 0xFC) return null;
            if (d[6] != 0x30) return null;

            int ck = 0;
            for (int i = 2; i < d.Length - 1; i++) ck += d[i];
            if ((ck & 0xFF) != d[1]) return null;   // 校验不过就不认

            var s = new State();
            s.WorkState = d[7] & 0x1F;
            s.AutoMode = Bit(d[7], 7);
            s.WaterSpray = Bit(d[7], 6);

            for (int n = 0; n <= 6; n++)
                if (Bit(d[8], n) != 0) s.Faults.Add(new[] { "进水传感器故障", "水箱传感器故障", "出水传感器故障", "进水超时", "进水超温", "出水超温", "流量异常" }[n]);
            for (int n = 0; n <= 7; n++)
                if (n != 6 && Bit(d[9], n) != 0)   // bit6 亮度传感器随环境光跳动，不算故障
                    s.Faults.Add(new[] { "暖风传感器故障", "座圈传感器故障", "环温传感器故障", "", "暖风超温", "座圈超温", "", "电压检测故障" }[n]);
            for (int n = 5; n <= 7; n++)
                if (Bit(d[10], n) != 0) s.Faults.Add(new[] { "", "", "", "", "", "齿轮箱异常", "冲刷模块故障", "魔力泡模块故障" }[n]);

            s.SmartPowerSave = Bit(d[11], 0);
            s.FootSensor = Bit(d[11], 3);
            s.Cover = Bit(d[11], 4);
            s.Ring = Bit(d[11], 5);
            s.OnSeat = Bit(d[11], 7);

            s.FlushLarge = (d[12] & 3) == 2;
            s.FlushSmall = (d[12] & 3) == 1;
            s.AutoTemp = Bit(d[12], 2);
            s.UV = Bit(d[12], 5);

            s.PreWetting = Bit(d[13], 2);      // 实测常置 1，是功能开关，不作运行态（见 0x94 实测帧）
            s.WaterFull = Bit(d[13], 7);       // 1=水箱已蓄满，0=正在补水/蓄水中
            s.Reflush = Bit(d[13], 7);
            s.AutoCloseFlush = Bit(d[13], 4);
            s.AutoSmallFlush = Bit(d[13], 3);
            s.CoverRunning = Bit(d[13], 1);
            s.RingRunning = Bit(d[13], 0);

            s.AutoCover = Bit(d[15], 7);
            s.NightLight = Bit(d[16], 6);
            s.LightSensor = Bit(d[16], 7);
            s.AutoFlush = Bit(d[17], 7);
            s.SeatTempRaw = (d[17] >> 3) & 7;
            s.OpenLoopBubble = Bit(d[20], 3);
            s.AutoBubble = Bit(d[20], 4);
            s.Bubbling = Bit(d[20], 5);
            s.FlipDistance = (d[21] >> 4) & 15;

            s.Raw = d;
            return s;
        }
    }

    public class State
    {
        public int WorkState;
        public int AutoMode, WaterSpray;
        public int SmartPowerSave, FootSensor, Cover, Ring, OnSeat;
        public bool FlushLarge, FlushSmall;
        public int AutoTemp, UV;
        public int PreWetting, Reflush, AutoCloseFlush, AutoSmallFlush, CoverRunning, RingRunning;
        public int WaterFull;
        public int AutoCover, NightLight, LightSensor, AutoFlush, SeatTempRaw;
        public int OpenLoopBubble, AutoBubble, Bubbling, FlipDistance;
        public List<string> Faults = new();
        public byte[] Raw;

        public string SeatTempText =>
            Protocol.SeatTemp.TryGetValue(SeatTempRaw, out var t) ? t : SeatTempRaw.ToString();
        public string WorkStateText =>
            Protocol.WorkState.TryGetValue(WorkState, out var t) ? t : "状态 " + WorkState;
    }
}
