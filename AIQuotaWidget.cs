// AI 额度桌面小组件 —— Codex + Antigravity + 可自定义添加其他 AI 应用（Claude Code / Gemini CLI / API 余额 / 手动记录）
// 编译: build.cmd （使用 Windows 自带的 .NET Framework csc，无需安装任何东西）
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;
using WPath = System.Windows.Shapes.Path;

namespace AIQuota
{
    // ======================= 数据模型 =======================
    public class Limit
    {
        public string Key;
        public string Label;
        public string Pool;
        public double WindowMinutes;
        public double BudgetUsd;
        public double Remaining;
        public DateTime? ResetAt;
        public bool WasReset;
        public bool IsEstimated;
        public string Detail;
    }

    public class UsageStats
    {
        public double[] Daily = new double[31];   // [30] = 今天
        public double TodayTok, WeekTok, MonthTok;
        public double TodayCost, WeekCost, MonthCost;
        public string MainModel;
        public double MainShare = -1;
        public double CacheRate = -1;
    }

    public class CodexData : UsageStats
    {
        public string Error;
        public string Plan;
        public DateTime? Updated;
        public List<Limit> Limits = new List<Limit>();
    }

    public class AgData : UsageStats
    {
        public string Error;
        public string Plan;
        public DateTime? Updated;
        public List<Limit> Limits = new List<Limit>();
    }

    public class Config
    {
        public int refreshSeconds = 30;
        public bool topmost = true;
        public bool autoCollapse = true;
        public bool showCapsLock = true;
        public double? left;
        public double? top;
        public string codexHome = "";
        public string agBrainDir = "";
        public bool showCodex = true;
        public bool showAntigravity = true;
        // 每百万 token 美元价格: [输入, 缓存输入, 输出]，按模型名前缀匹配，default 为兜底
        public Dictionary<string, List<double>> prices;
        // 其他 AI 应用（Claude Code / Gemini CLI / 余额接口 / 手动记录）
        public List<ProviderCfg> providers = new List<ProviderCfg>();
    }

    // 一个额度窗口（如 5 小时限额、每周限额、每日请求数）
    public class ProviderSlot
    {
        public string key = "";
        public string label = "";
        public double windowMinutes = 300;   // 0 = 无固定周期
        public double budget = 0;            // 100% 对应的量（按 unit 计）
        public string unit = "usd";          // usd | req | tok
        public double remaining = 100;
        public string calibratedAt = "";
        public string resetAt = "";
    }

    public class ProviderCfg
    {
        public string id = "";
        public string name = "";
        public string type = "manual";       // claude-code | gemini-cli | api | manual
        public bool enabled = true;
        public string icon = "";             // 1~3 个字符，或 claude / gemini
        public string color = "#FFFFFF";
        public string plan = "";
        public string dataDir = "";          // 日志类：自定义数据目录（多个用 ; 分隔）
        public string url = "";              // 接口类
        public string method = "GET";
        public string apiKey = "";           // 可写 env:变量名 从环境变量读取
        public string headers = "";          // 每行 Key: Value，可用 {apiKey} 占位
        public string body = "";
        public string remainingPath = "";    // 如 balance_infos.0.total_balance
        public string totalPath = "";
        public string usedPath = "";
        public string unitSymbol = "";
        public double fullAmount = 0;        // 无总额字段时的满额基准（自动记录最高余额）
        public int pollSeconds = 300;
        public List<ProviderSlot> slots = new List<ProviderSlot>();
    }

    public class ProviderData : UsageStats
    {
        public string Id, Error, Plan, Unit;
        public DateTime? Updated;
        public List<Limit> Limits = new List<Limit>();
        public bool HasUsage, HasBalance;
        public double Balance, BalanceTotal, BalancePct = -1;
        public double NewFullAmount;
        public double? RingPct;
    }

    static class Native
    {
        public const int VK_CAPITAL = 0x14;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint WM_CLOSE = 0x0010;

        [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public static bool IsCapsLockOn() { return (GetKeyState(VK_CAPITAL) & 0x0001) != 0; }

        public static void ToggleCapsLock()
        {
            keybd_event(VK_CAPITAL, 0x45, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(VK_CAPITAL, 0x45, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public static void CloseExistingWidget()
        {
            try
            {
                IntPtr hwnd = FindWindow(null, "AI 额度");
                if (hwnd == IntPtr.Zero) return;
                var sb = new StringBuilder(128);
                GetClassName(hwnd, sb, sb.Capacity);
                if (sb.ToString().StartsWith("HwndWrapper[", StringComparison.Ordinal))
                    SendMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }
    }

    // ======================= 工具 =======================
    static class J
    {
        public static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };

        public static object G(object o, string key)
        {
            var d = o as IDictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }
        public static double D(object o, double def)
        {
            if (o == null) return def;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return def; }
        }
        public static string S(object o)
        {
            return o == null ? null : Convert.ToString(o, CultureInfo.InvariantCulture);
        }
        public static DateTime? Time(object o)
        {
            if (o == null) return null;
            if (o is string)
            {
                var s = (string)o;
                double num;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out num)) return FromUnix(num);
                DateTimeOffset dto;
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto)) return dto.LocalDateTime;
                return null;
            }
            return FromUnix(D(o, 0));
        }
        static DateTime? FromUnix(double v)
        {
            if (v <= 0) return null;
            if (v > 1e11) v /= 1000.0; // 毫秒
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(v).ToLocalTime();
        }
        public static double Clamp(double v) { return Math.Max(0, Math.Min(100, v)); }

        public static string Pretty(string json)
        {
            var sb = new StringBuilder(); int ind = 0; bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (inStr)
                {
                    sb.Append(ch);
                    if (ch == '\\') { i++; if (i < json.Length) sb.Append(json[i]); }
                    else if (ch == '"') inStr = false;
                    continue;
                }
                switch (ch)
                {
                    case '"': inStr = true; sb.Append(ch); break;
                    case '{': case '[': ind++; sb.Append(ch).Append('\n').Append(new string(' ', ind * 2)); break;
                    case '}': case ']': ind--; sb.Append('\n').Append(new string(' ', ind * 2)).Append(ch); break;
                    case ',': sb.Append(ch).Append('\n').Append(new string(' ', ind * 2)); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }
    }

    static class Fmt
    {
        public static string Tok(double v)
        {
            if (v >= 1e8) { double y = v / 1e8; return y.ToString(y >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + "亿"; }
            if (v >= 1e4) { double w = v / 1e4; return w.ToString(w >= 1000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "万"; }
            return v.ToString("0", CultureInfo.InvariantCulture);
        }
        public static string Money(double v) { return "$" + v.ToString(v >= 1000 ? "0" : "0.00", CultureInfo.InvariantCulture); }
        public static string Reset(Limit l)
        {
            if (l.WasReset) return "已重置";
            if (!l.ResetAt.HasValue) return "";
            var t = l.ResetAt.Value;
            if (t.Date == DateTime.Today) return t.ToString("HH:mm") + " 重置";
            return t.ToString("M月d日 HH:mm") + " 重置";
        }
        public static string Ago(DateTime? t)
        {
            if (!t.HasValue) return "";
            var s = DateTime.Now - t.Value;
            if (s.TotalMinutes < 1) return "刚刚 更新";
            if (s.TotalMinutes < 60) return (int)s.TotalMinutes + " 分钟前 更新";
            if (s.TotalHours < 24) return (int)s.TotalHours + " 小时前 更新";
            return t.Value.ToString("M月d日 HH:mm") + " 更新";
        }
        public static string WindowLabel(double minutes)
        {
            if (Math.Abs(minutes - 300) < 1) return "5 小时限额";
            if (Math.Abs(minutes - 10080) < 1) return "每周限额";
            if (Math.Abs(minutes - 1440) < 1) return "每日限额";
            if (minutes >= 1440 && minutes % 1440 == 0) return (minutes / 1440) + " 天限额";
            if (minutes >= 60) return (minutes / 60).ToString("0.#", CultureInfo.InvariantCulture) + " 小时限额";
            if (minutes > 0) return minutes + " 分钟限额";
            return "限额";
        }
    }

    // ======================= Codex 日志读取 =======================
    static class CodexReader
    {
        class Usage { public DateTime Time; public string Model; public double Input, Cached, Output; }
        class FileCache
        {
            public long Len; public DateTime Mtime;
            public List<Usage> Usages = new List<Usage>();
            public DateTime RateTime = DateTime.MinValue; public object Rate;
        }
        static readonly Dictionary<string, FileCache> cache = new Dictionary<string, FileCache>(StringComparer.OrdinalIgnoreCase);

        public static string Home(Config cfg)
        {
            string home = cfg.codexHome;
            if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrEmpty(home)) home = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return home;
        }

        public static CodexData Load(Config cfg)
        {
            var data = new CodexData();
            string home = Home(cfg);
            var cutoff = DateTime.Now.AddDays(-32);
            var files = new List<FileInfo>();
            foreach (var sub in new[] { "sessions", "archived_sessions" })
            {
                var dir = IOPath.Combine(home, sub);
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
                        if (f.LastWriteTime >= cutoff) files.Add(f);
                }
                catch { }
            }
            if (files.Count == 0) { data.Error = "未找到 Codex 会话记录\n先在 Codex 里发一条消息即可"; return data; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                seen.Add(f.FullName);
                FileCache c;
                if (cache.TryGetValue(f.FullName, out c) && c.Len == f.Length && c.Mtime == f.LastWriteTimeUtc) continue;
                try { c = Parse(f.FullName); c.Len = f.Length; c.Mtime = f.LastWriteTimeUtc; cache[f.FullName] = c; }
                catch { }
            }
            foreach (var k in cache.Keys.ToList()) if (!seen.Contains(k)) cache.Remove(k);

            // 额度：取最新的一次 rate_limits 快照
            FileCache latest = null;
            foreach (var c in cache.Values) if (c.Rate != null && (latest == null || c.RateTime > latest.RateTime)) latest = c;
            if (latest != null)
            {
                data.Updated = latest.RateTime;
                var a = MakeLimit(J.G(latest.Rate, "primary"), latest.RateTime);
                var b = MakeLimit(J.G(latest.Rate, "secondary"), latest.RateTime);
                if (a != null) data.Limits.Add(a);
                if (b != null) data.Limits.Add(b);
                var plan = J.S(J.G(latest.Rate, "plan_type"));
                if (!string.IsNullOrEmpty(plan)) data.Plan = char.ToUpperInvariant(plan[0]) + plan.Substring(1);
            }
            if (data.Limits.Count == 0) data.Error = "暂无 Codex 额度记录\n先在 Codex 里发一条消息即可";

            // 本机用量
            var today = DateTime.Today;
            var modelTok = new Dictionary<string, double>();
            double in7 = 0, ca7 = 0;
            foreach (var c in cache.Values)
                foreach (var u in c.Usages)
                {
                    int ago = (int)(today - u.Time.Date).TotalDays;
                    if (ago < 0) ago = 0;
                    if (ago > 30) continue;
                    double tok = u.Input + u.Output;
                    double cost = Cost(u, cfg);
                    data.Daily[30 - ago] += tok;
                    data.MonthTok += tok; data.MonthCost += cost;
                    if (ago < 7)
                    {
                        data.WeekTok += tok; data.WeekCost += cost;
                        in7 += u.Input; ca7 += u.Cached;
                        string m = string.IsNullOrEmpty(u.Model) ? "unknown" : u.Model;
                        double cur; modelTok.TryGetValue(m, out cur); modelTok[m] = cur + tok;
                    }
                    if (ago == 0) { data.TodayTok += tok; data.TodayCost += cost; }
                }
            if (in7 > 0) data.CacheRate = ca7 / in7 * 100;
            if (modelTok.Count > 0 && data.WeekTok > 0)
            {
                var top = modelTok.OrderByDescending(kv => kv.Value).First();
                data.MainModel = top.Key; data.MainShare = top.Value / data.WeekTok * 100;
            }
            return data;
        }

        static double Cost(Usage u, Config cfg)
        {
            List<double> p = null; int best = -1;
            if (cfg.prices != null)
            {
                foreach (var kv in cfg.prices)
                {
                    if (kv.Value == null || kv.Value.Count < 3) continue;
                    if (kv.Key == "default") { if (best < 0) p = kv.Value; continue; }
                    if (u.Model != null && u.Model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) && kv.Key.Length > best)
                    { best = kv.Key.Length; p = kv.Value; }
                }
            }
            if (p == null) p = new List<double> { 1.25, 0.125, 10 };
            double fresh = Math.Max(0, u.Input - u.Cached);
            return (fresh * p[0] + u.Cached * p[1] + u.Output * p[2]) / 1e6;
        }

        static Limit MakeLimit(object w, DateTime snap)
        {
            if (w == null) return null;
            object used = J.G(w, "used_percent");
            if (used == null) return null;
            var l = new Limit();
            l.Label = Fmt.WindowLabel(J.D(J.G(w, "window_minutes"), 0));
            l.Remaining = J.Clamp(100 - J.D(used, 0));
            var ra = J.Time(J.G(w, "resets_at"));
            object ri = J.G(w, "resets_in_seconds");
            if (ra.HasValue) l.ResetAt = ra;
            else if (ri != null) l.ResetAt = snap.AddSeconds(J.D(ri, 0));
            if (l.ResetAt.HasValue && l.ResetAt.Value < DateTime.Now) { l.Remaining = 100; l.WasReset = true; }
            return l;
        }

        static FileCache Parse(string path)
        {
            var c = new FileCache();
            string model = null;
            double pIn = 0, pCa = 0, pOut = 0, pTot = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    bool isTc = line.IndexOf("\"token_count\"", StringComparison.Ordinal) >= 0;
                    bool isCtx = !isTc && line.IndexOf("\"turn_context\"", StringComparison.Ordinal) >= 0;
                    if (!isTc && !isCtx) continue;
                    object o;
                    try { o = J.Js.DeserializeObject(line); } catch { continue; }
                    var payload = J.G(o, "payload");
                    if (isCtx)
                    {
                        var m = J.S(J.G(payload, "model"));
                        if (!string.IsNullOrEmpty(m)) model = m;
                        continue;
                    }
                    if (J.S(J.G(payload, "type")) != "token_count") continue;
                    DateTime t = J.Time(J.G(o, "timestamp")) ?? File.GetLastWriteTime(path);
                    var tot = J.G(J.G(payload, "info"), "total_token_usage");
                    if (tot != null)
                    {
                        double ti = J.D(J.G(tot, "input_tokens"), 0), tc = J.D(J.G(tot, "cached_input_tokens"), 0), to = J.D(J.G(tot, "output_tokens"), 0);
                        double tt = J.D(J.G(tot, "total_tokens"), ti + to);
                        if (tt > pTot)
                            c.Usages.Add(new Usage { Time = t, Model = model, Input = Math.Max(0, ti - pIn), Cached = Math.Max(0, tc - pCa), Output = Math.Max(0, to - pOut) });
                        else if (tt < pTot)
                            c.Usages.Add(new Usage { Time = t, Model = model, Input = ti, Cached = tc, Output = to });
                        pIn = ti; pCa = tc; pOut = to; pTot = tt;
                    }
                    var rl = J.G(payload, "rate_limits");
                    if (rl != null && (J.G(rl, "primary") != null || J.G(rl, "secondary") != null) && t >= c.RateTime)
                    { c.RateTime = t; c.Rate = rl; }
                }
            }
            return c;
        }
    }

    // ======================= Antigravity 实时日志 + 校准扣减 =======================
    static class AgReader
    {
        public class StepUsage
        {
            public DateTime Time;
            public string Model;
            public string Pool; // "gemini" or "claude"
            public double FreshIn, Cached, Output, CostUsd;
            public bool HitLimit;
        }

        class TranscriptCache
        {
            public long Len;
            public DateTime Mtime;
            public List<StepUsage> Steps = new List<StepUsage>();
        }

        public class CalibItem
        {
            public string key;
            public string label;
            public string pool;
            public double windowMinutes;
            public double budgetUsd;
            public double remaining;
            public string calibratedAt;
            public string resetAt;
        }

        static readonly Dictionary<string, TranscriptCache> tcache = new Dictionary<string, TranscriptCache>(StringComparer.OrdinalIgnoreCase);

        public static List<string> BrainDirs(Config cfg)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(cfg.agBrainDir)) { list.Add(cfg.agBrainDir); return list; }
            string gem = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
            foreach (var prod in new[] { "antigravity", "antigravity-ide", "antigravity-cli" })
            {
                string p = IOPath.Combine(gem, prod, "brain");
                if (Directory.Exists(p)) list.Add(p);
            }
            return list;
        }

        static double DefaultBudget(string pool, double winMin)
        {
            bool week = winMin >= 1440;
            if (pool == "claude") return week ? 11.6 : 5.8;
            return week ? 30.0 : 10.0;
        }

        static double StepCost(string model, double freshIn, double cached, double output)
        {
            string m = (model ?? "").ToLowerInvariant();
            double pIn = 1.25, pCa = 0.125, pOut = 10.0;
            if (m.Contains("flash")) { pIn = 0.30; pCa = 0.03; pOut = 2.50; }
            else if (m.StartsWith("claude-opus")) { pIn = 5.0; pCa = 0.50; pOut = 25.0; }
            else if (m.StartsWith("claude")) { pIn = 3.0; pCa = 0.30; pOut = 15.0; }
            else if (m.StartsWith("gpt")) { pIn = 0.50; pCa = 0.05; pOut = 2.0; }
            return (freshIn * pIn + cached * pCa + output * pOut) / 1e6;
        }

        public static List<StepUsage> LoadSteps(Config cfg)
        {
            var all = new List<StepUsage>();
            var roots = BrainDirs(cfg);
            if (roots.Count == 0) return all;
            var cutoff = DateTime.Now.AddDays(-32);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(root); } catch { continue; }
                foreach (var dir in dirs)
                {
                    try
                    {
                        string tf = IOPath.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
                        if (!File.Exists(tf)) continue;
                        var fi = new FileInfo(tf);
                        if (fi.LastWriteTime < cutoff) continue;
                        seen.Add(fi.FullName);
                        TranscriptCache tc;
                        if (!tcache.TryGetValue(fi.FullName, out tc) || tc.Len != fi.Length || tc.Mtime != fi.LastWriteTimeUtc)
                        {
                            tc = ParseTranscript(fi.FullName);
                            tc.Len = fi.Length;
                            tc.Mtime = fi.LastWriteTimeUtc;
                            tcache[fi.FullName] = tc;
                        }
                        all.AddRange(tc.Steps);
                    }
                    catch { }
                }
            }
            foreach (var k in tcache.Keys.ToList()) if (!seen.Contains(k)) tcache.Remove(k);
            return all;
        }

        static readonly Regex QuotaErrRe = new Regex(@"RESOURCE_EXHAUSTED|exhausted your capacity|hit your 5-hour limit|quota exceeded", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static TranscriptCache ParseTranscript(string path)
        {
            var tc = new TranscriptCache();
            DateTime fallback = File.GetLastWriteTime(path);
            string lastPool = "gemini";
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    bool hasTok = line.IndexOf("\"input_tokens\"", StringComparison.Ordinal) >= 0 ||
                                  line.IndexOf("\"output_tokens\"", StringComparison.Ordinal) >= 0;
                    bool isErr = !hasTok && line.IndexOf("\"ERROR_MESSAGE\"", StringComparison.Ordinal) >= 0;
                    if (!hasTok && !isErr) continue;
                    object o;
                    try { o = J.Js.DeserializeObject(line); } catch { continue; }
                    string tp = J.S(J.G(o, "type"));
                    DateTime t = J.Time(J.G(o, "created_at")) ?? fallback;
                    if (tp == "ERROR_MESSAGE")
                    {
                        string msg = (J.S(J.G(o, "error")) ?? "") + " " + (J.S(J.G(o, "content")) ?? "");
                        if (QuotaErrRe.IsMatch(msg))
                        {
                            string pool = lastPool;
                            if (msg.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("opus", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("sonnet", StringComparison.OrdinalIgnoreCase) >= 0) pool = "claude";
                            else if (msg.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0) pool = "gemini";
                            tc.Steps.Add(new StepUsage { Time = t, Model = pool, Pool = pool, HitLimit = true });
                        }
                        continue;
                    }
                    if (tp != "PLANNER_RESPONSE") continue;
                    double fi = J.D(J.G(o, "input_tokens"), 0);
                    double ca = J.D(J.G(o, "cache_read_tokens"), 0);
                    double ou = J.D(J.G(o, "output_tokens"), 0);
                    string model = J.S(J.G(o, "model")) ?? "gemini";
                    string ml = model.ToLowerInvariant();
                    lastPool = (ml.StartsWith("claude") || ml.StartsWith("gpt")) ? "claude" : "gemini";
                    if (fi <= 0 && ca <= 0 && ou <= 0) continue;
                    tc.Steps.Add(new StepUsage
                    {
                        Time = t, Model = model, Pool = lastPool,
                        FreshIn = fi, Cached = ca, Output = ou,
                        CostUsd = StepCost(model, fi, ca, ou)
                    });
                }
            }
            return tc;
        }

        public static List<CalibItem> DefaultSlots()
        {
            return new List<CalibItem>
            {
                new CalibItem { key = "gemini_5h", label = "5 小时限额 · Gemini", pool = "gemini", windowMinutes = 300, budgetUsd = 10.0, remaining = 100, calibratedAt = "", resetAt = "" },
                new CalibItem { key = "gemini_week", label = "每周限额 · Gemini", pool = "gemini", windowMinutes = 10080, budgetUsd = 30.0, remaining = 100, calibratedAt = "", resetAt = "" },
                new CalibItem { key = "claude_5h", label = "5 小时限额 · Claude and GPT", pool = "claude", windowMinutes = 300, budgetUsd = 5.8, remaining = 100, calibratedAt = "", resetAt = "" },
                new CalibItem { key = "claude_week", label = "每周限额 · Claude and GPT", pool = "claude", windowMinutes = 10080, budgetUsd = 11.6, remaining = 100, calibratedAt = "", resetAt = "" }
            };
        }

        public static List<CalibItem> ReadCalibItems(string file, out string plan, out DateTime? fileUpdated)
        {
            plan = ""; fileUpdated = null;
            var defaults = DefaultSlots();
            if (!File.Exists(file)) return defaults;
            object o;
            try { o = J.Js.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)); }
            catch { return defaults; }
            fileUpdated = J.Time(J.G(o, "updated")) ?? File.GetLastWriteTime(file);
            plan = J.S(J.G(o, "plan")) ?? "";
            var rawItems = J.G(o, "items") as IEnumerable;
            if (rawItems == null) return defaults;

            var parsed = new List<CalibItem>();
            foreach (var it in rawItems)
            {
                string label = J.S(J.G(it, "label")) ?? "";
                string pool = J.S(J.G(it, "pool"));
                if (string.IsNullOrEmpty(pool)) pool = (label.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("GPT", StringComparison.OrdinalIgnoreCase) >= 0) ? "claude" : "gemini";
                double win = J.D(J.G(it, "windowMinutes"), label.Contains("周") ? 10080 : 300);
                string key = J.S(J.G(it, "key"));
                if (string.IsNullOrEmpty(key)) key = pool + (win >= 1440 ? "_week" : "_5h");
                double bud = J.D(J.G(it, "budgetUsd"), DefaultBudget(pool, win));
                if (bud <= 0.1) bud = DefaultBudget(pool, win);
                string calAt = J.S(J.G(it, "calibratedAt"));
                if (string.IsNullOrEmpty(calAt) && fileUpdated.HasValue) calAt = fileUpdated.Value.ToString("yyyy-MM-ddTHH:mm:sszzz");
                parsed.Add(new CalibItem
                {
                    key = key, label = label, pool = pool, windowMinutes = win,
                    budgetUsd = bud, remaining = J.Clamp(J.D(J.G(it, "remaining"), 100)),
                    calibratedAt = calAt ?? "", resetAt = J.S(J.G(it, "resetAt")) ?? ""
                });
            }
            return parsed.Count > 0 ? parsed : defaults;
        }

        public static AgData Load(Config cfg, string file)
        {
            var d = new AgData();
            string plan; DateTime? fileUpd;
            var slots = ReadCalibItems(file, out plan, out fileUpd);
            d.Plan = plan;
            d.Updated = fileUpd;

            var steps = LoadSteps(cfg);
            var now = DateTime.Now;
            var today = DateTime.Today;
            var modelTok = new Dictionary<string, double>();
            double totPrompt7 = 0, ca7 = 0;

            foreach (var u in steps)
            {
                if (!d.Updated.HasValue || u.Time > d.Updated.Value) d.Updated = u.Time;
                if (u.HitLimit) continue;
                int ago = (int)(today - u.Time.Date).TotalDays;
                if (ago < 0) ago = 0;
                if (ago > 30) continue;
                double tok = u.FreshIn + u.Cached + u.Output;
                d.Daily[30 - ago] += tok;
                d.MonthTok += tok; d.MonthCost += u.CostUsd;
                if (ago < 7)
                {
                    d.WeekTok += tok; d.WeekCost += u.CostUsd;
                    totPrompt7 += u.FreshIn + u.Cached; ca7 += u.Cached;
                    double cur; modelTok.TryGetValue(u.Model, out cur); modelTok[u.Model] = cur + tok;
                }
                if (ago == 0) { d.TodayTok += tok; d.TodayCost += u.CostUsd; }
            }
            if (totPrompt7 > 0) d.CacheRate = ca7 / totPrompt7 * 100.0;
            if (modelTok.Count > 0 && d.WeekTok > 0)
            {
                var top = modelTok.OrderByDescending(kv => kv.Value).First();
                d.MainModel = top.Key; d.MainShare = top.Value / d.WeekTok * 100.0;
            }

            foreach (var s in slots)
            {
                DateTime? calAt = J.Time(s.calibratedAt);
                DateTime? rst = J.Time(s.resetAt);
                bool activeCalib = calAt.HasValue && (!rst.HasValue ? (now - calAt.Value).TotalMinutes < s.windowMinutes : rst.Value > now);

                var lim = new Limit
                {
                    Key = s.key, Label = s.label, Pool = s.pool,
                    WindowMinutes = s.windowMinutes, BudgetUsd = s.budgetUsd
                };

                if (activeCalib)
                {
                    double deltaCost = 0;
                    bool hitZero = false;
                    DateTime? firstStep = null;
                    foreach (var u in steps)
                        if (u.Pool == s.pool && u.Time > calAt.Value)
                        {
                            if (u.HitLimit && s.windowMinutes < 1440) hitZero = true;
                            deltaCost += u.CostUsd;
                            if (!u.HitLimit && (!firstStep.HasValue || u.Time < firstStep.Value)) firstStep = u.Time;
                        }
                    double drop = (deltaCost / s.budgetUsd) * 100.0;
                    lim.Remaining = hitZero ? 0 : J.Clamp(s.remaining - drop);
                    lim.ResetAt = rst ?? (firstStep.HasValue ? (DateTime?)firstStep.Value.AddMinutes(s.windowMinutes) : null);
                    lim.IsEstimated = !hitZero && deltaCost > 0.002;
                }
                else
                {
                    // 已过重置时间或尚未校准：基于最近 5h / 7d 滑动窗口自动实时估算
                    DateTime winStart = now.AddMinutes(-s.windowMinutes);
                    if (rst.HasValue && rst.Value > winStart && rst.Value <= now) winStart = rst.Value;
                    double winCost = 0;
                    bool hitZero = false;
                    DateTime? earliest = null;
                    foreach (var u in steps)
                        if (u.Pool == s.pool && u.Time >= winStart)
                        {
                            if (u.HitLimit && s.windowMinutes < 1440 && (now - u.Time).TotalMinutes < s.windowMinutes) hitZero = true;
                            winCost += u.CostUsd;
                            if (!u.HitLimit && (!earliest.HasValue || u.Time < earliest.Value)) earliest = u.Time;
                        }
                    lim.Remaining = hitZero ? 0 : J.Clamp(100.0 - (winCost / s.budgetUsd) * 100.0);
                    if ((winCost > 0.002 || hitZero) && earliest.HasValue)
                    {
                        lim.IsEstimated = !hitZero;
                        lim.ResetAt = earliest.Value.AddMinutes(s.windowMinutes);
                    }
                    else if (rst.HasValue && rst.Value <= now)
                    {
                        lim.WasReset = true;
                    }
                }
                d.Limits.Add(lim);
            }
            return d;
        }

        public static DateTime? ParseResetInput(string text, DateTime now, DateTime? existing)
        {
            if (string.IsNullOrWhiteSpace(text))
                return (existing.HasValue && existing.Value > now) ? existing : null;
            string s = text.Trim();
            // 支持 "6d 6h" / "4h 47m" / "6天6小时" / "45m"
            double totalMin = 0; bool matched = false;
            foreach (Match m in Regex.Matches(s, @"(\d+(?:\.\d+)?)\s*(d|day|days|天|h|hr|hrs|hour|hours|小时|时|m|min|mins|分|分钟)", RegexOptions.IgnoreCase))
            {
                double v = J.D(m.Groups[1].Value, 0);
                string u = m.Groups[2].Value.ToLowerInvariant();
                if (u.StartsWith("d") || u == "天") totalMin += v * 1440;
                else if (u.StartsWith("h") || u.Contains("时")) totalMin += v * 60;
                else totalMin += v;
                matched = true;
            }
            if (matched && totalMin > 0) return now.AddMinutes(totalMin);

            // 支持 "15:30"
            var hm = Regex.Match(s, @"^(\d{1,2}):(\d{2})$");
            if (hm.Success)
            {
                int hh = int.Parse(hm.Groups[1].Value), mm = int.Parse(hm.Groups[2].Value);
                if (hh >= 0 && hh < 24 && mm >= 0 && mm < 60)
                {
                    var dt = now.Date.AddHours(hh).AddMinutes(mm);
                    if (dt <= now) dt = dt.AddDays(1);
                    return dt;
                }
            }
            return J.Time(s);
        }

        public static string FormatRemainingSpan(DateTime? resetAt, DateTime now)
        {
            if (!resetAt.HasValue || resetAt.Value <= now) return "";
            var sp = resetAt.Value - now;
            if (sp.TotalDays >= 1) return ((int)sp.TotalDays) + "d " + sp.Hours + "h";
            if (sp.TotalHours >= 1) return ((int)sp.TotalHours) + "h " + sp.Minutes + "m";
            return Math.Max(1, (int)Math.Round(sp.TotalMinutes)) + "m";
        }

        public static void SaveCalibration(Config cfg, string file, Dictionary<string, double> newPcts, Dictionary<string, string> resetInputs)
        {
            string plan; DateTime? upd;
            var slots = ReadCalibItems(file, out plan, out upd);
            var steps = LoadSteps(cfg);
            var now = DateTime.Now;
            string nowIso = now.ToString("yyyy-MM-ddTHH:mm:sszzz");

            foreach (var s in slots)
            {
                DateTime? prevCal = J.Time(s.calibratedAt);
                DateTime? prevRst = J.Time(s.resetAt);
                bool sameWindow = prevCal.HasValue && (!prevRst.HasValue ? (now - prevCal.Value).TotalMinutes < s.windowMinutes : prevRst.Value > now);

                double nr;
                if (newPcts.TryGetValue(s.key, out nr))
                {
                    nr = J.Clamp(nr);
                    // 自适应学习该窗口 100% 对应的美元算力预算
                    if (sameWindow && s.remaining - nr >= 3.0)
                    {
                        double deltaCost = 0;
                        foreach (var u in steps)
                            if (u.Pool == s.pool && u.Time > prevCal.Value && u.Time <= now) deltaCost += u.CostUsd;
                        if (deltaCost >= 0.05)
                        {
                            double implied = deltaCost / ((s.remaining - nr) / 100.0);
                            s.budgetUsd = Math.Max(1.5, Math.Min(500.0, Math.Round(s.budgetUsd * 0.4 + implied * 0.6, 2)));
                        }
                    }
                    s.remaining = nr;
                    s.calibratedAt = nowIso;
                }
                string ri;
                if (resetInputs.TryGetValue(s.key, out ri))
                {
                    var dt = ParseResetInput(ri, now, prevRst);
                    s.resetAt = dt.HasValue ? dt.Value.ToString("yyyy-MM-ddTHH:mm:sszzz") : "";
                }
            }

            var root = new Dictionary<string, object>
            {
                { "updated", nowIso },
                { "plan", plan ?? "" },
                { "items", slots }
            };
            File.WriteAllText(file, J.Pretty(J.Js.Serialize(root)), new UTF8Encoding(false));
        }
    }

    // ======================= 其他 AI 应用（可扩展） =======================
    static class ProviderReader
    {
        public class Step
        {
            public DateTime Time;
            public string Model, Key;
            public double FreshIn, Cached, Output, Cost;
            public bool HitLimit, HitLong;   // HitLong: 命中的是 ≥1 天的长周期额度
            public DateTime? HitReset;
        }

        public class ApiResult
        {
            public string Sig, Error, Raw;
            public DateTime At;
            public double Rem = double.NaN, Total = double.NaN;
        }

        class FileCache { public long Len; public DateTime Mtime; public List<Step> Steps = new List<Step>(); }

        static readonly object sync = new object();
        static readonly Dictionary<string, FileCache> fcache = new Dictionary<string, FileCache>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, ApiResult> acache = new Dictionary<string, ApiResult>();

        public static readonly string[] PresetKeys = { "claude-code", "gemini-cli", "deepseek", "openrouter", "kimi", "siliconflow", "api", "manual" };

        public static string PresetName(string k)
        {
            switch (k)
            {
                case "claude-code": return "Claude Code";
                case "gemini-cli": return "Gemini CLI";
                case "deepseek": return "DeepSeek 余额";
                case "openrouter": return "OpenRouter 余额";
                case "kimi": return "Kimi 余额";
                case "siliconflow": return "硅基流动 余额";
                case "api": return "自定义余额接口";
                default: return "手动记录（任意应用）";
            }
        }

        public static bool IsLogType(string t) { return t == "claude-code" || t == "gemini-cli"; }

        public static string TypeDesc(string t)
        {
            switch (t)
            {
                case "claude-code": return "Claude Code 本机日志";
                case "gemini-cli": return "Gemini CLI 本机日志";
                case "api": return "余额接口";
                default: return "手动记录";
            }
        }

        public static string DefaultUnit(string t) { return t == "gemini-cli" ? "req" : "usd"; }

        public static ProviderCfg Preset(string k)
        {
            var p = new ProviderCfg { id = k + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) };
            switch (k)
            {
                case "claude-code":
                    p.name = "Claude Code"; p.type = "claude-code"; p.icon = "claude"; p.color = "#D97757";
                    p.slots = SlotsFromText("5 小时限额 | 5h | 8 | usd\n每周限额 | 7d | 50 | usd", "usd", null); break;
                case "gemini-cli":
                    p.name = "Gemini CLI"; p.type = "gemini-cli"; p.icon = "gemini"; p.color = "#4F8DF7";
                    p.slots = SlotsFromText("每日请求 | 24h | 1000 | req", "req", null); break;
                case "deepseek":
                    p.name = "DeepSeek"; p.type = "api"; p.icon = "deepseek"; p.color = "#4D6BFE"; p.unitSymbol = "¥";
                    p.url = "https://api.deepseek.com/user/balance"; p.remainingPath = "balance_infos.0.total_balance"; break;
                case "openrouter":
                    p.name = "OpenRouter"; p.type = "api"; p.icon = "OR"; p.color = "#A78BFA"; p.unitSymbol = "$";
                    p.url = "https://openrouter.ai/api/v1/credits"; p.totalPath = "data.total_credits"; p.usedPath = "data.total_usage"; break;
                case "kimi":
                    p.name = "Kimi"; p.type = "api"; p.icon = "K"; p.color = "#1783FF"; p.unitSymbol = "¥";
                    p.url = "https://api.moonshot.cn/v1/users/me/balance"; p.remainingPath = "data.available_balance"; break;
                case "siliconflow":
                    p.name = "硅基流动"; p.type = "api"; p.icon = "SF"; p.color = "#8B5CF6"; p.unitSymbol = "¥";
                    p.url = "https://api.siliconflow.cn/v1/user/info"; p.remainingPath = "data.totalBalance"; break;
                case "api":
                    p.name = "自定义接口"; p.type = "api"; p.icon = "API"; p.color = "#2EE59D"; break;
                default:
                    p.name = "自定义应用"; p.type = "manual"; p.icon = "AI"; p.color = "#FFB547";
                    p.slots = SlotsFromText("5 小时限额 | 5h\n每周限额 | 7d", "usd", null); break;
            }
            return p;
        }

        // ---------- 额度窗口文本 ----------
        public static double ParseWindow(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return Math.Max(0, v);
            double tot = 0;
            foreach (Match m in Regex.Matches(s, @"(\d+(?:\.\d+)?)\s*(w|周|d|天|h|小时|时|m|分钟|分)", RegexOptions.IgnoreCase))
            {
                double n = J.D(m.Groups[1].Value, 0);
                string u = m.Groups[2].Value.ToLowerInvariant();
                if (u == "w" || u == "周") tot += n * 10080;
                else if (u == "d" || u == "天") tot += n * 1440;
                else if (u == "h" || u.Contains("时")) tot += n * 60;
                else tot += n;
            }
            return tot;
        }

        public static string WindowText(double min)
        {
            if (min <= 0) return "";
            if (min % 1440 == 0) return (min / 1440).ToString("0", CultureInfo.InvariantCulture) + "d";
            if (min % 60 == 0) return (min / 60).ToString("0", CultureInfo.InvariantCulture) + "h";
            return min.ToString("0.#", CultureInfo.InvariantCulture) + "m";
        }

        static string NormUnit(string u, string def)
        {
            u = (u ?? "").Trim().ToLowerInvariant();
            if (u == "") return def;
            if (u.StartsWith("req") || u == "次" || u.Contains("请求")) return "req";
            if (u.StartsWith("tok") || u.Contains("token")) return "tok";
            return "usd";
        }

        public static List<ProviderSlot> SlotsFromText(string text, string defUnit, List<ProviderSlot> old)
        {
            var list = new List<ProviderSlot>();
            if (text == null) return list;
            var used = new HashSet<ProviderSlot>();
            int idx = 0;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split('|').Select(x => x.Trim()).ToArray();
                if (parts[0].Length == 0) continue;
                var s = new ProviderSlot
                {
                    label = parts[0],
                    windowMinutes = parts.Length > 1 ? ParseWindow(parts[1]) : 0,
                    budget = parts.Length > 2 ? J.D(parts[2], 0) : 0,
                    unit = NormUnit(parts.Length > 3 ? parts[3] : "", defUnit)
                };
                var prev = old == null ? null : old.FirstOrDefault(o => o != null && o.label == s.label && !used.Contains(o));
                if (prev != null)
                {
                    used.Add(prev);
                    s.key = prev.key; s.remaining = prev.remaining; s.calibratedAt = prev.calibratedAt; s.resetAt = prev.resetAt;
                    if (s.budget <= 0) s.budget = prev.budget;
                }
                if (string.IsNullOrEmpty(s.key)) s.key = "s" + idx + "_" + Guid.NewGuid().ToString("N").Substring(0, 4);
                idx++;
                list.Add(s);
            }
            return list;
        }

        public static string SlotsToText(List<ProviderSlot> slots, bool withBudget)
        {
            var sb = new StringBuilder();
            foreach (var s in slots ?? new List<ProviderSlot>())
            {
                sb.Append(s.label).Append(" | ").Append(WindowText(s.windowMinutes));
                if (withBudget) sb.Append(" | ").Append(s.budget.ToString("0.##", CultureInfo.InvariantCulture)).Append(" | ").Append(s.unit);
                sb.Append("\r\n");
            }
            return sb.ToString().TrimEnd();
        }

        // ---------- 本机日志 ----------
        static string Home() { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }

        public static List<string> LogRoots(ProviderCfg p)
        {
            var list = new List<string>();
            var cands = new List<string>();
            if (!string.IsNullOrWhiteSpace(p.dataDir))
                foreach (var d in p.dataDir.Split(';')) cands.Add(Environment.ExpandEnvironmentVariables(d.Trim()));
            else if (p.type == "claude-code")
            {
                var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (!string.IsNullOrEmpty(env))
                    foreach (var e in env.Split(',', ';')) if (e.Trim().Length > 0) cands.Add(IOPath.Combine(e.Trim(), "projects"));
                cands.Add(IOPath.Combine(Home(), ".claude", "projects"));
                cands.Add(IOPath.Combine(Home(), ".config", "claude", "projects"));
            }
            else if (p.type == "gemini-cli")
                cands.Add(IOPath.Combine(Home(), ".gemini", "tmp"));
            foreach (var c in cands)
                try { if (c.Length > 0 && Directory.Exists(c) && !list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Add(c); } catch { }
            return list;
        }

        public static string LogFilter(string type) { return type == "gemini-cli" ? "session-*.json" : "*.jsonl"; }

        public static List<Step> LoadSteps(ProviderCfg p)
        {
            var all = new List<Step>();
            var cutoff = DateTime.Now.AddDays(-32);
            string filter = LogFilter(p.type);
            lock (sync)
            {
                foreach (var root in LogRoots(p))
                {
                    List<FileInfo> files;
                    try { files = new DirectoryInfo(root).EnumerateFiles(filter, SearchOption.AllDirectories).Where(f => f.LastWriteTime >= cutoff).ToList(); }
                    catch { continue; }
                    foreach (var f in files)
                    {
                        string ck = p.type + "|" + f.FullName;
                        FileCache c;
                        if (!fcache.TryGetValue(ck, out c) || c.Len != f.Length || c.Mtime != f.LastWriteTimeUtc)
                        {
                            try
                            {
                                c = new FileCache { Steps = p.type == "gemini-cli" ? ParseGemini(f.FullName) : ParseClaude(f.FullName) };
                                c.Len = f.Length; c.Mtime = f.LastWriteTimeUtc;
                                fcache[ck] = c;
                            }
                            catch { continue; }
                        }
                        all.AddRange(c.Steps);
                    }
                }
            }
            // 去重（Claude Code 恢复会话时会把历史消息复制进新文件）
            var seen = new HashSet<string>();
            var res = new List<Step>();
            foreach (var s in all) if (s.Key == null || seen.Add(s.Key)) res.Add(s);
            return res;
        }

        static readonly Regex ClaudeLimitRe = new Regex(@"usage limit reached|limit reached|hit your limit|out of extra usage", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ClaudeTsRe = new Regex(@"limit reached\|(\d{9,13})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ResetClockRe = new Regex(@"resets?\s+(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex GemQuotaRe = new Regex(@"RESOURCE_EXHAUSTED|quota exceeded|exceeded your current quota|daily quota|reached your daily", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static string ContentText(object c)
        {
            if (c == null) return "";
            if (c is string) return (string)c;
            var sb = new StringBuilder();
            var arr = c as IEnumerable;
            if (arr != null)
                foreach (var it in arr)
                {
                    var tx = J.S(J.G(it, "text"));
                    if (tx != null) sb.Append(tx).Append(' ');
                }
            return sb.ToString();
        }

        static DateTime? ClockAfter(DateTime t, Match m)
        {
            int h = int.Parse(m.Groups[1].Value);
            int mi = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            string ap = m.Groups[3].Success ? m.Groups[3].Value.ToLowerInvariant() : "";
            if (ap == "pm" && h < 12) h += 12;
            if (ap == "am" && h == 12) h = 0;
            if (h > 23 || mi > 59) return null;
            var dt = t.Date.AddHours(h).AddMinutes(mi);
            if (dt <= t) dt = dt.AddDays(1);
            return dt;
        }

        static double ClaudeCost(string model, double fresh, double cw, double cr, double ou)
        {
            string m = (model ?? "").ToLowerInvariant();
            double pIn = 3, pOut = 15;
            if (m.Contains("opus"))
            {
                bool legacy = m.Contains("opus-4-1") || m.Contains("opus-4-0") || m.Contains("3-opus") || Regex.IsMatch(m, @"opus-4-\d{8}");
                pIn = legacy ? 15 : 5; pOut = legacy ? 75 : 25;
            }
            else if (m.Contains("haiku")) { pIn = 1; pOut = 5; }
            return (fresh * pIn + cw * pIn * 1.25 + cr * pIn * 0.1 + ou * pOut) / 1e6;
        }

        static double GeminiCost(string model, double fresh, double cached, double ou)
        {
            string m = (model ?? "").ToLowerInvariant();
            double pIn = 1.25, pCa = 0.125, pOut = 10;
            if (m.Contains("flash-lite")) { pIn = 0.10; pCa = 0.01; pOut = 0.40; }
            else if (m.Contains("flash")) { pIn = 0.30; pCa = 0.03; pOut = 2.50; }
            else if (m.Contains("gemini-3")) { pIn = 2.0; pCa = 0.20; pOut = 12.0; }
            return (fresh * pIn + cached * pCa + ou * pOut) / 1e6;
        }

        static List<Step> ParseClaude(string path)
        {
            var list = new List<Step>();
            DateTime fallback = File.GetLastWriteTime(path);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) continue;
                    bool hasUsage = line.IndexOf("\"output_tokens\"", StringComparison.Ordinal) >= 0;
                    bool maybeLimit = line.IndexOf("limit", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hasUsage && !maybeLimit) continue;
                    object o;
                    try { o = J.Js.DeserializeObject(line); } catch { continue; }
                    if (J.S(J.G(o, "type")) != "assistant") continue;
                    var msg = J.G(o, "message");
                    DateTime t = J.Time(J.G(o, "timestamp")) ?? fallback;
                    string model = J.S(J.G(msg, "model")) ?? "";
                    object ie = J.G(o, "isApiErrorMessage");
                    if (model == "<synthetic>" || (ie is bool && (bool)ie))
                    {
                        string text = ContentText(J.G(msg, "content"));
                        if (ClaudeLimitRe.IsMatch(text))
                        {
                            var st = new Step { Time = t, Model = "claude", HitLimit = true, HitLong = text.IndexOf("week", StringComparison.OrdinalIgnoreCase) >= 0 };
                            var m1 = ClaudeTsRe.Match(text);
                            if (m1.Success) st.HitReset = J.Time(m1.Groups[1].Value);
                            else { var m2 = ResetClockRe.Match(text); if (m2.Success) st.HitReset = ClockAfter(t, m2); }
                            list.Add(st);
                        }
                        continue;
                    }
                    var u = J.G(msg, "usage");
                    if (u == null) continue;
                    double fi = J.D(J.G(u, "input_tokens"), 0), cw = J.D(J.G(u, "cache_creation_input_tokens"), 0);
                    double cr = J.D(J.G(u, "cache_read_input_tokens"), 0), ou = J.D(J.G(u, "output_tokens"), 0);
                    if (fi + cw + cr + ou <= 0) continue;
                    string id = J.S(J.G(msg, "id")), rq = J.S(J.G(o, "requestId"));
                    list.Add(new Step
                    {
                        Time = t, Model = model, Key = (id != null || rq != null) ? "cc|" + id + "|" + rq : null,
                        FreshIn = fi + cw, Cached = cr, Output = ou, Cost = ClaudeCost(model, fi, cw, cr, ou)
                    });
                }
            }
            return list;
        }

        static List<Step> ParseGemini(string path)
        {
            var list = new List<Step>();
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
            object o = J.Js.DeserializeObject(text);
            var msgs = J.G(o, "messages") as IEnumerable;
            if (msgs == null) return list;
            string sid = J.S(J.G(o, "sessionId")) ?? path;
            DateTime fallback = File.GetLastWriteTime(path);
            int n = 0;
            foreach (var m in msgs)
            {
                n++;
                string tp = J.S(J.G(m, "type")) ?? "";
                DateTime t = J.Time(J.G(m, "timestamp")) ?? fallback;
                if (tp == "gemini")
                {
                    var tk = J.G(m, "tokens");
                    double inp = J.D(J.G(tk, "input"), 0), ca = J.D(J.G(tk, "cached"), 0);
                    double ou = J.D(J.G(tk, "output"), 0) + J.D(J.G(tk, "thoughts"), 0);
                    string model = J.S(J.G(m, "model")) ?? "gemini";
                    double fresh = Math.Max(0, inp - ca);
                    list.Add(new Step
                    {
                        Time = t, Model = model, Key = "gc|" + sid + "|" + (J.S(J.G(m, "id")) ?? n.ToString()),
                        FreshIn = fresh, Cached = ca, Output = ou, Cost = GeminiCost(model, fresh, ca, ou)
                    });
                }
                else if (tp == "error" || tp == "info" || tp == "warning")
                {
                    if (GemQuotaRe.IsMatch(ContentText(J.G(m, "content"))))
                        list.Add(new Step { Time = t, Model = "gemini", HitLimit = true, HitLong = true });
                }
            }
            return list;
        }

        static double StepAmount(Step u, string unit)
        {
            if (unit == "req") return 1;
            if (unit == "tok") return u.FreshIn + u.Cached + u.Output;
            return u.Cost;
        }

        public static string AmountText(double v, string unit)
        {
            if (unit == "req") return Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + " 次";
            if (unit == "tok") return Fmt.Tok(v);
            return Fmt.Money(v);
        }

        // 与 Antigravity 相同的思路：已校准时从校准点实时扣减，否则按滑动窗口估算
        public static Limit EstimateSlot(ProviderSlot s, List<Step> steps, DateTime now)
        {
            var lim = new Limit { Key = s.key, Label = s.label, WindowMinutes = s.windowMinutes, BudgetUsd = s.budget };
            double win = s.windowMinutes > 0 ? s.windowMinutes : 300;
            bool longWin = win >= 1440;
            double budget = s.budget > 0 ? s.budget : 1;
            DateTime? calAt = J.Time(s.calibratedAt), rst = J.Time(s.resetAt);
            bool active = calAt.HasValue && (!rst.HasValue ? (now - calAt.Value).TotalMinutes < win : rst.Value > now);
            DateTime from; double baseRem;
            if (active) { from = calAt.Value; baseRem = s.remaining; }
            else
            {
                from = now.AddMinutes(-win);
                if (rst.HasValue && rst.Value > from && rst.Value <= now) from = rst.Value;
                baseRem = 100;
            }
            double used = 0; bool hit = false; DateTime? earliest = null, hitReset = null;
            foreach (var u in steps)
            {
                if (active ? u.Time <= from : u.Time < from) continue;
                if (u.HitLimit)
                {
                    if (u.HitLong == longWin && (now - u.Time).TotalMinutes < win && (!u.HitReset.HasValue || u.HitReset.Value > now))
                    { hit = true; hitReset = u.HitReset; }
                    continue;
                }
                used += StepAmount(u, s.unit);
                if (!earliest.HasValue || u.Time < earliest.Value) earliest = u.Time;
            }
            lim.Remaining = hit ? 0 : J.Clamp(baseRem - used / budget * 100.0);
            lim.IsEstimated = !hit && used > 0;
            if (hit && hitReset.HasValue) lim.ResetAt = hitReset;
            else if (active) lim.ResetAt = rst ?? (earliest.HasValue ? (DateTime?)earliest.Value.AddMinutes(win) : null);
            else if ((used > 0 || hit) && earliest.HasValue) lim.ResetAt = earliest.Value.AddMinutes(win);
            else if (rst.HasValue && rst.Value <= now) lim.WasReset = true;
            if (s.budget > 0)
                lim.Detail = active ? "校准后 +" + AmountText(used, s.unit) : AmountText(used, s.unit) + " / " + AmountText(s.budget, s.unit);
            return lim;
        }

        public static Limit ManualSlot(ProviderSlot s, DateTime now)
        {
            var lim = new Limit { Key = s.key, Label = s.label, WindowMinutes = s.windowMinutes, Remaining = J.Clamp(s.remaining) };
            DateTime? rst = J.Time(s.resetAt);
            if (rst.HasValue && rst.Value <= now)
            {
                lim.Remaining = 100;
                if (s.windowMinutes > 0)
                {
                    var next = rst.Value;
                    int guard = 0;
                    while (next <= now && guard++ < 100000) next = next.AddMinutes(s.windowMinutes);
                    lim.ResetAt = next;
                }
                else lim.WasReset = true;
            }
            else lim.ResetAt = rst;
            return lim;
        }

        // ---------- 余额接口 ----------
        public static object JsonPath(object o, string path)
        {
            if (o == null || string.IsNullOrWhiteSpace(path)) return null;
            string norm = path.Trim().Replace("[", ".").Replace("]", "");
            if (norm.StartsWith("$")) norm = norm.Substring(1);
            foreach (var seg in norm.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (o == null) return null;
                var dict = o as IDictionary<string, object>;
                if (dict != null) { object v; o = dict.TryGetValue(seg, out v) ? v : null; continue; }
                var arr = o as IList; int idx;
                if (arr != null && int.TryParse(seg, out idx)) { o = idx >= 0 && idx < arr.Count ? arr[idx] : null; continue; }
                return null;
            }
            return o;
        }

        static double Num(object o)
        {
            if (o == null || o is IDictionary<string, object> || o is IList) return double.NaN;
            double v = J.D(o, double.NaN);
            return v;
        }

        static string Trunc(string s, int n) { s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length > n ? s.Substring(0, n) + "…" : s; }

        static string ResolveKey(string k, out string missingEnv)
        {
            missingEnv = null;
            k = (k ?? "").Replace("\r", "").Replace("\n", "").Trim();
            if (k.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            {
                string name = k.Substring(4).Trim();
                k = Environment.GetEnvironmentVariable(name)
                    ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
                    ?? "";
                if (k.Trim().Length == 0) missingEnv = name;
            }
            k = k.Trim().Trim('"', '\'').Trim();
            if (k.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) k = k.Substring(14).Trim();
            return k;
        }

        static double ResolveRemaining(object o, ProviderCfg p)
        {
            double rem = Num(JsonPath(o, p.remainingPath));
            if (!double.IsNaN(rem))
            {
                // DeepSeek 多币种（CNY / USD）优先按货币符号匹配
                if (p.remainingPath != null && p.remainingPath.Equals("balance_infos.0.total_balance", StringComparison.OrdinalIgnoreCase))
                {
                    var arr = JsonPath(o, "balance_infos") as IList;
                    string sym = (p.unitSymbol ?? "").Trim();
                    string want = (sym == "¥" || sym == "￥") ? "CNY" : (sym == "$" ? "USD" : "");
                    if (arr != null && want.Length > 0)
                        foreach (var item in arr)
                            if (string.Equals(J.S(J.G(item, "currency")), want, StringComparison.OrdinalIgnoreCase))
                            {
                                double v = Num(J.G(item, "total_balance"));
                                if (!double.IsNaN(v)) return v;
                            }
                }
                return rem;
            }
            // 若路径指向数组元素（如 DeepSeek 零余额时返回 balance_infos: []），数组为空时视为 0
            if (!string.IsNullOrWhiteSpace(p.remainingPath))
            {
                string norm = p.remainingPath.Trim().Replace("[", ".").Replace("]", "");
                if (norm.StartsWith("$")) norm = norm.Substring(1);
                var segs = norm.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                object cur = o;
                for (int i = 0; i < segs.Length; i++)
                {
                    var arr = cur as IList; int idx;
                    if (arr != null && int.TryParse(segs[i], out idx))
                    {
                        if (arr.Count == 0) return 0;
                        cur = idx >= 0 && idx < arr.Count ? arr[idx] : null;
                        continue;
                    }
                    var dict = cur as IDictionary<string, object>;
                    if (dict != null) { object v; cur = dict.TryGetValue(segs[i], out v) ? v : null; continue; }
                    break;
                }
            }
            return double.NaN;
        }

        static HttpWebRequest BuildRequest(ProviderCfg p, string key, bool direct)
        {
            var req = (HttpWebRequest)WebRequest.Create(p.url.Trim());
            req.Method = string.IsNullOrWhiteSpace(p.method) ? "GET" : p.method.Trim().ToUpperInvariant();
            req.Timeout = 15000; req.ReadWriteTimeout = 15000;
            req.KeepAlive = false;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (direct) req.Proxy = null;
            req.UserAgent = "AIQuotaWidget/1.0";
            req.Accept = "application/json";
            bool hasAuth = false;
            if (!string.IsNullOrWhiteSpace(p.headers))
                foreach (var raw in p.headers.Split('\n'))
                {
                    int i = raw.IndexOf(':');
                    if (i <= 0) continue;
                    string hk = raw.Substring(0, i).Trim(), hv = raw.Substring(i + 1).Trim().Replace("{apiKey}", key);
                    if (hk.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) { hasAuth = true; req.Headers["Authorization"] = hv; }
                    else if (hk.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) req.ContentType = hv;
                    else if (hk.Equals("Accept", StringComparison.OrdinalIgnoreCase)) req.Accept = hv;
                    else if (hk.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = hv;
                    else req.Headers[hk] = hv;
                }
            if (!hasAuth && key.Length > 0) req.Headers["Authorization"] = key.IndexOf(' ') > 0 ? key : "Bearer " + key;
            if (req.Method != "GET" && !string.IsNullOrEmpty(p.body))
            {
                var bytes = Encoding.UTF8.GetBytes(p.body.Replace("{apiKey}", key));
                if (string.IsNullOrEmpty(req.ContentType)) req.ContentType = "application/json";
                using (var rs = req.GetRequestStream()) rs.Write(bytes, 0, bytes.Length);
            }
            return req;
        }

        static string ReadResponseText(HttpWebRequest req)
        {
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
        }

        public static ApiResult Fetch(ProviderCfg p)
        {
            var r = new ApiResult { At = DateTime.Now };
            try
            {
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; }
                catch { try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { } }
                string missingEnv;
                string key = ResolveKey(p.apiKey, out missingEnv);
                if (missingEnv != null) { r.Error = "未找到环境变量 " + missingEnv + "，请检查名称或直接填写 API Key"; return r; }
                string text;
                try
                {
                    text = ReadResponseText(BuildRequest(p, key, false));
                }
                catch (WebException ex)
                {
                    // 系统代理不可用或拦截国内域名时，自动尝试直连一次
                    if (ex.Response == null)
                    {
                        try { text = ReadResponseText(BuildRequest(p, key, true)); }
                        catch { throw ex; }
                    }
                    else throw;
                }
                r.Raw = text;
                object o = J.Js.DeserializeObject(text);
                double rem = ResolveRemaining(o, p), tot = Num(JsonPath(o, p.totalPath)), used = Num(JsonPath(o, p.usedPath));
                if (double.IsNaN(rem) && !double.IsNaN(tot) && !double.IsNaN(used)) rem = tot - used;
                if (double.IsNaN(tot) && !double.IsNaN(rem) && !double.IsNaN(used)) tot = rem + used;
                r.Rem = rem; r.Total = tot;
                if (double.IsNaN(rem)) r.Error = "接口返回中找不到余额字段，请检查字段路径：\n" + Trunc(text, 220);
            }
            catch (WebException ex)
            {
                string body = "";
                try { if (ex.Response != null) using (var sr = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd(); } catch { }
                var hr = ex.Response as HttpWebResponse;
                if (hr != null)
                {
                    int code = (int)hr.StatusCode;
                    string tip = (code == 401 || code == 403) ? "（请检查 API Key 是否正确且为开放平台密钥）" : "";
                    r.Error = "请求失败（HTTP " + code + "）" + tip + (body.Length > 0 ? "\n" + Trunc(body, 220) : "");
                }
                else
                {
                    string inner = ex.InnerException != null ? " / " + ex.InnerException.Message : "";
                    r.Error = "网络请求失败：" + ex.Message + inner;
                }
            }
            catch (Exception ex) { r.Error = "请求/解析失败：" + ex.Message; }
            return r;
        }

        static void ApplyApi(ProviderCfg p, ProviderData d, bool force)
        {
            d.Unit = p.unitSymbol ?? "";
            if (string.IsNullOrWhiteSpace(p.url)) { d.Error = "尚未配置接口地址，点击「编辑」填写"; return; }
            string sig = string.Join("|", new[] { p.url, p.method, p.apiKey, p.headers, p.body, p.remainingPath, p.totalPath, p.usedPath });
            ApiResult c;
            lock (sync) acache.TryGetValue(p.id, out c);
            double age = c == null ? double.MaxValue : (DateTime.Now - c.At).TotalSeconds;
            if (force || c == null || c.Sig != sig || age >= Math.Max(60, p.pollSeconds) || (c.Error != null && age >= 60))
            {
                var nc = Fetch(p);
                nc.Sig = sig;
                if (double.IsNaN(nc.Rem) && c != null && c.Sig == sig) { nc.Rem = c.Rem; nc.Total = c.Total; }
                c = nc;
                lock (sync) acache[p.id] = c;
            }
            d.Updated = c.At;
            d.Error = c.Error;
            if (double.IsNaN(c.Rem)) return;
            d.HasBalance = true;
            d.Balance = c.Rem;
            if (!double.IsNaN(c.Total) && c.Total > 0)
            {
                d.BalanceTotal = c.Total;
                d.BalancePct = J.Clamp(c.Rem / c.Total * 100.0);
            }
            else
            {
                double full = p.fullAmount;
                if (c.Rem > full + 1e-9) { full = c.Rem; d.NewFullAmount = c.Rem; }
                d.BalanceTotal = full;
                d.BalancePct = full > 0 ? J.Clamp(c.Rem / full * 100.0) : 0;
            }
        }

        public static void ComputeRing(ProviderData d)
        {
            double? r = null;
            if (d.HasBalance && d.BalancePct >= 0) r = d.BalancePct;
            if (d.Limits.Count > 0)
            {
                double mn = d.Limits.Min(l => l.Remaining);
                r = r.HasValue ? Math.Min(r.Value, mn) : mn;
            }
            d.RingPct = r;
        }

        public static ProviderData Load(ProviderCfg p, bool force)
        {
            var d = new ProviderData { Id = p.id, Plan = p.plan };
            var now = DateTime.Now;
            var slots = p.slots ?? new List<ProviderSlot>();
            if (IsLogType(p.type))
            {
                if (LogRoots(p).Count == 0)
                    d.Error = p.type == "claude-code"
                        ? "未找到 Claude Code 日志目录（~/.claude/projects）\n先用 Claude Code 对话一次，或在「编辑」中指定数据目录"
                        : "未找到 Gemini CLI 日志目录（~/.gemini/tmp）\n先用 Gemini CLI 对话一次，或在「编辑」中指定数据目录";
                var steps = LoadSteps(p);
                var today = DateTime.Today;
                var modelTok = new Dictionary<string, double>();
                double prompt7 = 0, ca7 = 0;
                foreach (var u in steps)
                {
                    if (!d.Updated.HasValue || u.Time > d.Updated.Value) d.Updated = u.Time;
                    if (u.HitLimit) continue;
                    int ago = (int)(today - u.Time.Date).TotalDays;
                    if (ago < 0) ago = 0;
                    if (ago > 30) continue;
                    double tok = u.FreshIn + u.Cached + u.Output;
                    d.Daily[30 - ago] += tok;
                    d.MonthTok += tok; d.MonthCost += u.Cost;
                    if (ago < 7)
                    {
                        d.WeekTok += tok; d.WeekCost += u.Cost;
                        prompt7 += u.FreshIn + u.Cached; ca7 += u.Cached;
                        string m = string.IsNullOrEmpty(u.Model) ? "unknown" : u.Model;
                        double cur; modelTok.TryGetValue(m, out cur); modelTok[m] = cur + tok;
                    }
                    if (ago == 0) { d.TodayTok += tok; d.TodayCost += u.Cost; }
                }
                if (prompt7 > 0) d.CacheRate = ca7 / prompt7 * 100.0;
                if (modelTok.Count > 0 && d.WeekTok > 0)
                {
                    var top = modelTok.OrderByDescending(kv => kv.Value).First();
                    d.MainModel = top.Key; d.MainShare = top.Value / d.WeekTok * 100.0;
                }
                d.HasUsage = d.MonthTok > 0;
                foreach (var s in slots) d.Limits.Add(EstimateSlot(s, steps, now));
            }
            else
            {
                if (p.type == "api") ApplyApi(p, d, force);
                foreach (var s in slots)
                {
                    d.Limits.Add(ManualSlot(s, now));
                    var ca = J.Time(s.calibratedAt);
                    if (p.type != "api" && ca.HasValue && (!d.Updated.HasValue || ca.Value > d.Updated.Value)) d.Updated = ca;
                }
            }
            ComputeRing(d);
            return d;
        }

        // 校准 / 手动更新（直接修改 p.slots，由调用方保存配置）
        public static void Calibrate(ProviderCfg p, Dictionary<string, double> pcts, Dictionary<string, string> rsts)
        {
            bool log = IsLogType(p.type);
            var steps = log ? LoadSteps(p) : new List<Step>();
            var now = DateTime.Now;
            string nowIso = now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            foreach (var s in p.slots ?? new List<ProviderSlot>())
            {
                DateTime? prevCal = J.Time(s.calibratedAt), prevRst = J.Time(s.resetAt);
                double win = s.windowMinutes > 0 ? s.windowMinutes : 300;
                bool sameWindow = prevCal.HasValue && (!prevRst.HasValue ? (now - prevCal.Value).TotalMinutes < win : prevRst.Value > now);
                double nr;
                if (pcts.TryGetValue(s.key, out nr))
                {
                    nr = J.Clamp(nr);
                    if (log && sameWindow && s.remaining - nr >= 3.0)
                    {
                        double used = 0;
                        foreach (var u in steps)
                            if (!u.HitLimit && u.Time > prevCal.Value && u.Time <= now) used += StepAmount(u, s.unit);
                        double minUsed = s.unit == "req" ? 3 : s.unit == "tok" ? 10000 : 0.05;
                        if (used >= minUsed)
                        {
                            double implied = used / ((s.remaining - nr) / 100.0);
                            double nb = s.budget > 0 ? s.budget * 0.4 + implied * 0.6 : implied;
                            s.budget = Math.Max(0.01, Math.Round(nb, 2));
                        }
                    }
                    s.remaining = nr;
                    s.calibratedAt = nowIso;
                }
                string ri;
                if (rsts.TryGetValue(s.key, out ri))
                {
                    var dt = AgReader.ParseResetInput(ri, now, prevRst);
                    s.resetAt = dt.HasValue ? dt.Value.ToString("yyyy-MM-ddTHH:mm:sszzz") : "";
                }
            }
        }
    }

    // ======================= 界面 =======================
    class RingView
    {
        public Grid Root;
        public WPath Arc;
        public Grid ArcHost;
        public TextBlock Pct;
        public Border IconBg;
    }

    class CapsView
    {
        public Grid Root;
        public Ellipse OuterRing;
        public Grid OuterGlowHost;
        public Border IconBg;
        public WPath IconPath;
        public TextBlock StateText;
        public bool Hover;
    }

    class Widget : Window
    {
        const double PanelW = 360, BarH = 108, Ear = 16, BarR = 28, CellW = 84;
        static readonly Color Green = Color.FromRgb(0x2E, 0xE5, 0x9D);
        static readonly Color Amber = Color.FromRgb(0xFF, 0xB5, 0x47);
        static readonly Brush Gray = Fr("#8C8C8C"), Dim = Fr("#5E5E5E"), Label = Fr("#E4E4E4"), PanelBg = Fr("#0A0A0A");

        const string OpenAiPath = "M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z";
        const string AgPath = "M2.2,21 C5.2,19.6 7.2,13 9,7.6 C10,4.6 10.9,3 12,3 C13.1,3 14,4.6 15,7.6 C16.8,13 18.8,19.6 21.8,21";
        const string CapsPath = "M12,2.5 L4.5,10.8 H8.8 V15.2 H15.2 V10.8 H19.5 Z M8.8,17.6 H15.2 V20.6 H8.8 Z";
        const string ClaudePath = "M12,2.5 V21.5 M2.5,12 H21.5 M5.3,5.3 L18.7,18.7 M18.7,5.3 L5.3,18.7";
        const string GeminiPath = "M12,1.5 C12.6,7.2 16.8,11.4 22.5,12 C16.8,12.6 12.6,16.8 12,22.5 C11.4,16.8 7.2,12.6 1.5,12 C7.2,11.4 11.4,7.2 12,1.5 Z";
        const string DeepSeekPath = "M23.748 4.482c-.254-.124-.364.113-.512.234-.051.039-.094.09-.137.136-.372.397-.806.657-1.373.626-.829-.046-1.537.214-2.163.848-.133-.782-.575-1.248-1.247-1.548-.352-.156-.708-.311-.955-.65-.172-.241-.219-.51-.305-.774-.055-.16-.11-.323-.293-.35-.2-.031-.278.136-.356.276-.313.572-.434 1.202-.422 1.84.027 1.436.633 2.58 1.838 3.393.137.093.172.187.129.323-.082.28-.18.552-.266.833-.055.179-.137.217-.329.14a5.526 5.526 0 0 1 -1.736-1.18c-.857-.828-1.631-1.742-2.597-2.458a11.365 11.365 0 0 0 -.689-.471c-.985-.957.13-1.743.388-1.836.27-.098.093-.432-.779-.428-.872.004-1.67.295-2.687.684a3.055 3.055 0 0 1 -.465.137 9.597 9.597 0 0 0 -2.883-.102c-1.885.21-3.39 1.102-4.497 2.623C.082 8.606-.231 10.684.152 12.85c.403 2.284 1.569 4.175 3.36 5.653 1.858 1.533 3.997 2.284 6.438 2.14 1.482-.085 3.133-.284 4.994-1.86.47.234.962.327 1.78.397.63.059 1.236-.03 1.705-.128.735-.156.684-.837.419-.961-2.155-1.004-1.682-.595-2.113-.926 1.096-1.296 2.746-2.642 3.392-7.003.05-.347.007-.565 0-.845-.004-.17.035-.237.23-.256a4.173 4.173 0 0 0 1.545-.475c1.396-.763 1.96-2.015 2.093-3.517.02-.23-.004-.467-.247-.588zM11.581 18c-2.089-1.642-3.102-2.183-3.52-2.16-.392.024-.321.471-.235.763.09.288.207.486.371.739.114.167.192.416-.113.603-.673.416-1.842-.14-1.897-.167-1.361-.802-2.5-1.86-3.301-3.307-.774-1.393-1.224-2.887-1.298-4.482-.02-.386.093-.522.477-.592a4.696 4.696 0 0 1 1.529-.039c2.132.312 3.946 1.265 5.468 2.774.868.86 1.525 1.887 2.202 2.891.72 1.066 1.494 2.082 2.48 2.914.348.292.625.514.891.677-.802.09-2.14.11-3.054-.614zm1-6.44a.306.306 0 0 1 .415-.287.302.302 0 0 1 .2.288.306.306 0 0 1 -.31.307.303.303 0 0 1 -.304-.308zm3.11 1.596c-.2.081-.399.151-.59.16a1.245 1.245 0 0 1 -.798-.254c-.274-.23-.47-.358-.552-.758a1.73 1.73 0 0 1 .016-.588c.07-.327-.008-.537-.239-.727-.187-.156-.426-.199-.688-.199a.559.559 0 0 1 -.254-.078c-.11-.054-.2-.19-.114-.358.028-.054.16-.186.192-.21.356-.202.767-.136 1.146.016.352.144.618.408 1.001.782.391.451.462.576.685.914.176.265.336.537.445.848.067.195-.019.354-.25.452z";

        readonly Config cfg;
        readonly string cfgPath, agPath;
        readonly bool snapshot;
        double BarW { get { int n = VisibleKeys().Count + (cfg.showCapsLock ? 1 : 0); return Math.Max(1, n) * CellW + 64; } }
        double WinW { get { return Math.Max(PanelW, BarW + 8); } }
        Grid barGrid;
        WPath barShape;
        StackPanel ringRow;
        readonly Dictionary<string, RingView> rings = new Dictionary<string, RingView>();
        Dictionary<string, ProviderData> provData = new Dictionary<string, ProviderData>();
        CapsView capsView;
        bool? lastCaps;
        IntPtr lastExternalFg;
        Grid panelHost;
        WPath pointer;
        Border panel;
        string selected;
        CodexData codex;
        AgData ag;
        bool busy, pendingRefresh;
        int modal;
        DispatcherTimer timer, capsTimer, watchDebounce;
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();

        static Brush Fr(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

        public Widget(Config cfg, string cfgPath, string agPath, bool snapshot)
        {
            this.cfg = cfg; this.cfgPath = cfgPath; this.agPath = agPath; this.snapshot = snapshot;
            Title = "AI 额度";
            Width = WinW;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = cfg.topmost;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            UseLayoutRounding = true;

            var root = new StackPanel();
            root.Children.Add(BuildBar());
            root.Children.Add(BuildPanelHost());
            Content = root;
            SetCaps(Native.IsCapsLockOn());

            if (snapshot) return;
            ContextMenu = BuildMenu();
            MouseEnter += (s, e) =>
            {
                IntPtr fg = Native.GetForegroundWindow();
                IntPtr me = new WindowInteropHelper(this).Handle;
                if (fg != IntPtr.Zero && fg != me) lastExternalFg = fg;
            };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Select(null); };
            Deactivated += (s, e) => { if (cfg.autoCollapse && modal == 0) Select(null); };
            Closed += (s, e) =>
            {
                if (capsTimer != null) capsTimer.Stop();
                if (timer != null) timer.Stop();
                foreach (var fw in watchers) { try { fw.Dispose(); } catch { } }
                SaveConfig();
            };

            ClampPosition(true);

            capsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            capsTimer.Tick += (s, e) =>
            {
                bool on = Native.IsCapsLockOn();
                if (lastCaps != on) SetCaps(on);
            };
            capsTimer.Start();

            watchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            watchDebounce.Tick += (s, e) => { watchDebounce.Stop(); Refresh(); };
            SetupWatchers();

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(15, cfg.refreshSeconds)) };
            timer.Tick += (s, e) => Refresh();
            timer.Start();
            Loaded += (s, e) => Refresh();
        }

        void SetupWatchers()
        {
            foreach (var old in watchers) { try { old.Dispose(); } catch { } }
            watchers.Clear();
            if (cfg.showAntigravity) foreach (var bd in AgReader.BrainDirs(cfg)) WatchDir(bd, "transcript.jsonl", true);
            if (cfg.showCodex) WatchDir(IOPath.Combine(CodexReader.Home(cfg), "sessions"), "*.jsonl", true);
            WatchDir(IOPath.GetDirectoryName(agPath), IOPath.GetFileName(agPath), false);
            foreach (var p in ActiveProviders())
                if (ProviderReader.IsLogType(p.type))
                    foreach (var r in ProviderReader.LogRoots(p)) WatchDir(r, ProviderReader.LogFilter(p.type), true);
        }

        void WatchDir(string dir, string filter, bool subdirs)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var fw = new FileSystemWatcher(dir, filter)
                {
                    IncludeSubdirectories = subdirs,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                FileSystemEventHandler onChg = (s, e) =>
                    Dispatcher.BeginInvoke(new Action(() => { watchDebounce.Stop(); watchDebounce.Start(); }));
                fw.Changed += onChg;
                fw.Created += onChg;
                fw.EnableRaisingEvents = true;
                watchers.Add(fw);
            }
            catch { }
        }

        void ClampPosition(bool initial)
        {
            var wa = SystemParameters.WorkArea;
            if (initial)
            {
                if (cfg.left.HasValue && cfg.top.HasValue && cfg.left.Value < wa.Right - 60 && cfg.top.Value < wa.Bottom - 60 && cfg.left.Value > wa.Left - WinW + 60)
                { Left = cfg.left.Value; Top = cfg.top.Value; }
                else { Left = wa.Left + (wa.Width - WinW) / 2; Top = wa.Top + 6; }
            }
            double barRight = Left + (WinW + BarW) / 2;
            if (barRight > wa.Right - 4) Left = wa.Right - 4 - (WinW + BarW) / 2;
            double barLeft = Left + (WinW - BarW) / 2;
            if (barLeft < wa.Left + 4) Left = wa.Left + 4 - (WinW - BarW) / 2;
        }

        static Geometry MakeBarGeometry(double w)
        {
            string d = string.Format(CultureInfo.InvariantCulture,
                "M0,0 H{0} Q{1},0 {1},{2} V{3} Q{1},{4} {5},{4} H{6} Q{7},{4} {7},{3} V{2} Q{7},0 0,0 Z",
                w, w - Ear, Ear, BarH - BarR, BarH, w - Ear - BarR, Ear + BarR, Ear);
            return Geometry.Parse(d);
        }

        // ---------- 顶部刘海条 ----------
        UIElement BuildBar()
        {
            barGrid = new Grid { Width = BarW, Height = BarH, HorizontalAlignment = HorizontalAlignment.Center };
            barShape = new WPath { Data = MakeBarGeometry(BarW), Fill = Brushes.Black };
            if (!snapshot) barShape.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 };
            barShape.MouseLeftButtonDown += (s, e) => Drag();
            barGrid.Children.Add(barShape);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            ringRow = row;
            var codexRing = MakeRing(MakeIcon(OpenAiPath, true, 25));
            var agRing = MakeRing(MakeIcon(AgPath, false, 24));
            rings["codex"] = codexRing;
            rings["ag"] = agRing;
            capsView = MakeCapsButton();
            codexRing.Root.MouseLeftButtonUp += (s, e) => Toggle("codex");
            agRing.Root.MouseLeftButtonUp += (s, e) => Toggle("ag");
            barGrid.Children.Add(row);
            LayoutRings();
            return barGrid;
        }

        void Toggle(string key) { Select(selected == key ? null : key); }

        List<ProviderCfg> ActiveProviders()
        {
            return (cfg.providers ?? new List<ProviderCfg>()).Where(p => p != null && p.enabled).ToList();
        }

        List<string> VisibleKeys()
        {
            var l = new List<string>();
            if (cfg.showCodex) l.Add("codex");
            if (cfg.showAntigravity) l.Add("ag");
            foreach (var p in ActiveProviders()) l.Add("p:" + p.id);
            return l;
        }

        // 根据配置重新排列悬浮条上的圆环（内置 + 自定义应用 + 大小写按钮）
        void LayoutRings()
        {
            ringRow.Children.Clear();
            foreach (var k in rings.Keys.Where(k => k.StartsWith("p:")).ToList()) rings.Remove(k);
            foreach (var p in ActiveProviders())
            {
                string key = "p:" + p.id;
                var rv = MakeRing(MakeProviderIcon(p, 24));
                rv.Root.ToolTip = p.name;
                rv.Root.MouseLeftButtonUp += (s, e) => Toggle(key);
                rings[key] = rv;
            }
            var keys = VisibleKeys();
            foreach (var k in keys) { RingView rv; if (rings.TryGetValue(k, out rv)) ringRow.Children.Add(rv.Root); }
            capsView.Root.Visibility = cfg.showCapsLock ? Visibility.Visible : Visibility.Collapsed;
            ringRow.Children.Add(capsView.Root);
            double oldW = Width;
            barGrid.Width = BarW;
            barShape.Data = MakeBarGeometry(BarW);
            Width = WinW;
            if (!double.IsNaN(oldW) && !double.IsNaN(Left) && Math.Abs(oldW - WinW) > 0.5) Left -= (WinW - oldW) / 2;
            if (selected != null && !keys.Contains(selected)) selected = null;
        }

        static Brush SafeBrush(string hex, Brush def)
        {
            try { return string.IsNullOrWhiteSpace(hex) ? def : Fr(hex.Trim()); } catch { return def; }
        }

        static FrameworkElement MakeProviderIcon(ProviderCfg p, double size)
        {
            var br = SafeBrush(p.color, Brushes.White);
            string ic = (p.icon ?? "").Trim();
            if (ic == "")
                ic = p.type == "claude-code" ? "claude" : p.type == "gemini-cli" ? "gemini" : (string.IsNullOrEmpty(p.name) ? "?" : p.name.Substring(0, 1).ToUpperInvariant());
            if (ic.Equals("claude", StringComparison.OrdinalIgnoreCase))
                return new WPath
                {
                    Data = Geometry.Parse(ClaudePath), Stretch = Stretch.Uniform, Width = size * 0.9, Height = size * 0.9, Stroke = br,
                    StrokeThickness = Math.Max(1.6, size / 8), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
            if (ic.Equals("gemini", StringComparison.OrdinalIgnoreCase))
                return new WPath { Data = Geometry.Parse(GeminiPath), Stretch = Stretch.Uniform, Width = size, Height = size, Fill = br };
            bool isDeepSeek = ic.Equals("deepseek", StringComparison.OrdinalIgnoreCase) ||
                (ic.Equals("ds", StringComparison.OrdinalIgnoreCase) && ((p.name != null && p.name.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0) || (p.url != null && p.url.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0)));
            if (isDeepSeek)
                return new WPath { Data = Geometry.Parse(DeepSeekPath), Stretch = Stretch.Uniform, Width = size * 1.12, Height = size * 1.12, Fill = br };
            if (ic.Length > 3) ic = ic.Substring(0, 3);
            return new TextBlock
            {
                Text = ic, Foreground = br, FontWeight = FontWeights.Bold,
                FontSize = ic.Length >= 3 ? size * 0.46 : ic.Length == 2 ? size * 0.6 : size * 0.8,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }

        CapsView MakeCapsButton()
        {
            var cv = new CapsView();
            var cell = new StackPanel { Width = CellW, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "点击切换大小写 (Caps Lock)" };
            var ring = new Grid { Width = 60, Height = 60, HorizontalAlignment = HorizontalAlignment.Center };
            ring.Children.Add(new Ellipse { Stroke = Fr("#2B2B2B"), StrokeThickness = 5.5 });

            cv.OuterRing = new Ellipse { Width = 60, Height = 60, Stroke = new SolidColorBrush(Amber), StrokeThickness = 5.5, Margin = new Thickness(12), Visibility = Visibility.Collapsed };
            cv.OuterGlowHost = new Grid { Margin = new Thickness(-12), Background = Fr("#01000000"), IsHitTestVisible = false };
            cv.OuterGlowHost.Children.Add(cv.OuterRing);
            ring.Children.Add(cv.OuterGlowHost);

            cv.IconPath = new WPath { Data = Geometry.Parse(CapsPath), Stretch = Stretch.Uniform, Width = 20, Height = 20, StrokeLineJoin = PenLineJoin.Round };
            cv.IconBg = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(21), Background = Fr("#141414"), Child = cv.IconPath };
            ring.Children.Add(cv.IconBg);
            cell.Children.Add(ring);

            cv.StateText = new TextBlock { Text = "小写", Foreground = Gray, FontSize = 15, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
            cell.Children.Add(cv.StateText);

            cell.MouseEnter += (s, e) => { cv.Hover = true; if (lastCaps.HasValue) SetCaps(lastCaps.Value); };
            cell.MouseLeave += (s, e) => { cv.Hover = false; if (lastCaps.HasValue) SetCaps(lastCaps.Value); };
            cell.MouseLeftButtonDown += (s, e) => e.Handled = true;
            cell.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                Native.ToggleCapsLock();
                SetCaps(Native.IsCapsLockOn());
                if (selected == null && lastExternalFg != IntPtr.Zero)
                    Native.SetForegroundWindow(lastExternalFg);
            };
            cv.Root = new Grid();
            cv.Root.Children.Add(cell);
            return cv;
        }

        public void SetCaps(bool on)
        {
            lastCaps = on;
            if (capsView == null) return;
            if (on)
            {
                capsView.OuterRing.Visibility = Visibility.Visible;
                capsView.OuterGlowHost.Effect = new DropShadowEffect { Color = Amber, BlurRadius = 15, ShadowDepth = 0, Opacity = 0.85 };
                capsView.IconBg.Background = capsView.Hover ? Fr("#FFC46B") : Fr("#FFB547");
                capsView.IconPath.Fill = Fr("#111111");
                capsView.IconPath.Stroke = Fr("#111111");
                capsView.IconPath.StrokeThickness = 1.2;
                capsView.StateText.Text = "大写";
                capsView.StateText.Foreground = Fr("#FFB547");
                capsView.StateText.FontWeight = FontWeights.Bold;
            }
            else
            {
                capsView.OuterRing.Visibility = Visibility.Collapsed;
                capsView.OuterGlowHost.Effect = null;
                capsView.IconBg.Background = capsView.Hover ? Fr("#222222") : Fr("#141414");
                capsView.IconPath.Fill = Brushes.Transparent;
                capsView.IconPath.Stroke = Fr("#8E8E8E");
                capsView.IconPath.StrokeThickness = 1.85;
                capsView.StateText.Text = "小写";
                capsView.StateText.Foreground = Gray;
                capsView.StateText.FontWeight = FontWeights.Medium;
            }
        }

        static FrameworkElement MakeIcon(string data, bool fill, double size)
        {
            WPath p;
            try { p = new WPath { Data = Geometry.Parse(data) }; }
            catch { p = new WPath { Data = new EllipseGeometry(new Point(12, 12), 9, 9) }; }
            p.Stretch = Stretch.Uniform; p.Width = size; p.Height = size;
            if (fill) p.Fill = Brushes.White;
            else
            {
                p.Stroke = Brushes.White; p.StrokeThickness = 3.4;
                p.StrokeStartLineCap = PenLineCap.Round; p.StrokeEndLineCap = PenLineCap.Round; p.StrokeLineJoin = PenLineJoin.Round;
            }
            return p;
        }

        RingView MakeRing(FrameworkElement icon)
        {
            var rv = new RingView();
            var cell = new StackPanel { Width = CellW, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var ring = new Grid { Width = 60, Height = 60, HorizontalAlignment = HorizontalAlignment.Center };
            ring.Children.Add(new Ellipse { Stroke = Fr("#2B2B2B"), StrokeThickness = 5.5 });
            rv.Arc = new WPath { StrokeThickness = 5.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Stroke = new SolidColorBrush(Green) };
            rv.Arc.Margin = new Thickness(12);
            rv.ArcHost = new Grid { Margin = new Thickness(-12), Background = Fr("#01000000"), IsHitTestVisible = false };
            rv.ArcHost.Children.Add(rv.Arc);
            ring.Children.Add(rv.ArcHost);
            rv.IconBg = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(21), Background = Fr("#141414"), Child = icon };
            ring.Children.Add(rv.IconBg);
            cell.Children.Add(ring);
            rv.Pct = new TextBlock { Text = "--", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
            cell.Children.Add(rv.Pct);
            cell.MouseLeftButtonDown += (s, e) => e.Handled = true;
            rv.Root = new Grid();
            rv.Root.Children.Add(cell);
            return rv;
        }

        static Geometry Arc(double pct, double size, double th)
        {
            double c = size / 2, r = (size - th) / 2;
            if (pct <= 0.01) return Geometry.Empty;
            if (pct >= 99.99) return new EllipseGeometry(new Point(c, c), r, r);
            double a = 2 * Math.PI * pct / 100;
            var start = new Point(c, c - r);
            var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, pct > 50, SweepDirection.Clockwise, true));
            var g = new PathGeometry(); g.Figures.Add(fig);
            return g;
        }

        static Color LevelColor(double pct)
        {
            if (pct >= 30) return Green;
            if (pct >= 10) return Color.FromRgb(0xFF, 0xB5, 0x47);
            return Color.FromRgb(0xFF, 0x5D, 0x5D);
        }

        void SetRing(RingView rv, double? pct, bool sel)
        {
            if (pct.HasValue)
            {
                rv.Pct.Text = Math.Round(pct.Value) + "%";
                rv.Arc.Data = Arc(pct.Value, 60, 5.5);
                rv.Arc.Stroke = new SolidColorBrush(LevelColor(pct.Value));
            }
            else { rv.Pct.Text = "--"; rv.Arc.Data = Geometry.Empty; }
            rv.ArcHost.Effect = sel ? new DropShadowEffect { Color = pct.HasValue ? LevelColor(pct.Value) : Green, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.9 } : null;
            rv.IconBg.Background = sel ? Fr("#222222") : Fr("#141414");
        }

        // ---------- 下拉面板 ----------
        UIElement BuildPanelHost()
        {
            panelHost = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0), Width = PanelW, HorizontalAlignment = HorizontalAlignment.Left };
            panelHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panelHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pointer = new WPath
            {
                Data = Geometry.Parse("M0,14 C7,14 10,10 12,4 C13,1.2 15,1.2 16,4 C18,10 21,14 28,14 Z"),
                Fill = PanelBg, Width = 28, Height = 14, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, -1)
            };
            panelHost.Children.Add(pointer);

            var shadow = new Border { CornerRadius = new CornerRadius(24), Background = PanelBg, Margin = new Thickness(12, 0, 12, 14) };
            if (!snapshot) shadow.Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = 0.4 };
            Grid.SetRow(shadow, 1);
            panelHost.Children.Add(shadow);

            panel = new Border { CornerRadius = new CornerRadius(24), Background = PanelBg, Padding = new Thickness(20, 18, 20, 16), Margin = new Thickness(12, 0, 12, 14) };
            panel.MouseLeftButtonDown += (s, e) => Drag();
            Grid.SetRow(panel, 1);
            panelHost.Children.Add(panel);
            return panelHost;
        }

        public void Select(string which)
        {
            selected = which;
            UpdateAll();
            if (which != null && !snapshot)
                panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        static TextBlock T(string text, double size, Brush fg, FontWeight w)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = fg, FontWeight = w, TextTrimming = TextTrimming.CharacterEllipsis };
        }

        static UIElement LeftRight(UIElement left, UIElement right, Thickness margin)
        {
            var g = new Grid { Margin = margin };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var fl = left as FrameworkElement; if (fl != null) fl.VerticalAlignment = VerticalAlignment.Center;
            var fr = right as FrameworkElement; if (fr != null) { fr.VerticalAlignment = VerticalAlignment.Center; fr.Margin = new Thickness(10, 0, 0, 0); }
            Grid.SetColumn(right, 1);
            g.Children.Add(left); g.Children.Add(right);
            return g;
        }

        static UIElement SegBar(double pct)
        {
            const int N = 42;
            int filled = (int)Math.Round(N * J.Clamp(pct) / 100.0);
            var col = LevelColor(pct);
            var fill = new LinearGradientBrush(Color.FromArgb(0xFF, (byte)Math.Min(255, col.R + 40), (byte)Math.Min(255, col.G + 20), (byte)Math.Min(255, col.B + 30)), col, 90);
            fill.Freeze();
            var empty = Fr("#2C2C2C");
            var ug = new UniformGrid { Rows = 1, Columns = N, Height = 19 };
            for (int i = 0; i < N; i++)
                ug.Children.Add(new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(1.2, 0, 1.2, 0), Background = i < filled ? fill : empty });
            return ug;
        }

        static UIElement LimitBlock(Limit l, bool last)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, last ? 2 : 16) };
            var lblTb = new TextBlock { Text = l.Label, FontSize = 13.5, Foreground = Label, TextTrimming = TextTrimming.CharacterEllipsis };
            if (string.IsNullOrEmpty(l.Detail)) { lblTb.Margin = new Thickness(0, 0, 0, 8); sp.Children.Add(lblTb); }
            else sp.Children.Add(LeftRight(lblTb, T(l.Detail, 12, Gray, FontWeights.Normal), new Thickness(0, 0, 0, 8)));
            sp.Children.Add(SegBar(l.Remaining));
            string remTxt = (l.IsEstimated ? "剩余 ≈" : "剩余 ") + Math.Round(l.Remaining) + "%";
            sp.Children.Add(LeftRight(T(remTxt, 14, Brushes.White, FontWeights.SemiBold),
                                      T(Fmt.Reset(l), 13, Gray, FontWeights.Normal), new Thickness(0, 8, 0, 0)));
            return sp;
        }

        UIElement Header(string iconData, bool fill, string title, string plan, string sub)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = MakeIcon(iconData, fill, 21); ic.VerticalAlignment = VerticalAlignment.Center;
            if (!fill) ((WPath)ic).StrokeThickness = 3;
            left.Children.Add(ic);
            var tt = T(title, 17, Brushes.White, FontWeights.Bold); tt.Margin = new Thickness(10, 0, 0, 0); tt.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(tt);
            sp.Children.Add(LeftRight(left, T(plan ?? "", 13.5, Gray, FontWeights.SemiBold), new Thickness(0)));
            if (!string.IsNullOrEmpty(sub))
                sp.Children.Add(new TextBlock { Text = sub, FontSize = 12.5, Foreground = Gray, Margin = new Thickness(31, 3, 0, 0) });
            return sp;
        }

        static UIElement Sep() { return new Border { Height = 1, Background = Fr("#242424"), Margin = new Thickness(0, 12, 0, 12) }; }

        static void AppendUsageSection(StackPanel sp, UsageStats u, string note)
        {
            if (u.MonthTok <= 0) return;
            sp.Children.Add(Sep());
            sp.Children.Add(new TextBlock { Text = "本机用量（实时）", FontSize = 13, Foreground = Gray, Margin = new Thickness(0, 0, 0, 8) });
            var g = new Grid();
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string[] caps = { "今天", "7 天", "31 天" };
            double[] toks = { u.TodayTok, u.WeekTok, u.MonthTok };
            double[] costs = { u.TodayCost, u.WeekCost, u.MonthCost };
            for (int i = 0; i < 3; i++)
            {
                var col = new StackPanel();
                col.Children.Add(T(caps[i], 12.5, Gray, FontWeights.Normal));
                col.Children.Add(new TextBlock { Text = Fmt.Tok(toks[i]), FontSize = 21, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 1, 0, 1) });
                col.Children.Add(T(Fmt.Money(costs[i]) + "*", 12.5, Gray, FontWeights.Normal));
                Grid.SetColumn(col, i); g.Children.Add(col);
            }
            sp.Children.Add(g);
            sp.Children.Add(Chart(u.Daily));
            if (u.MainModel != null)
                sp.Children.Add(LeftRight(T("主力模型", 13.5, Gray, FontWeights.Normal),
                    T(u.MainModel + " · " + Math.Round(u.MainShare) + "%", 13.5, Brushes.White, FontWeights.SemiBold), new Thickness(0, 10, 0, 0)));
            if (u.CacheRate >= 0)
                sp.Children.Add(LeftRight(T("缓存命中率", 13.5, Gray, FontWeights.Normal),
                    T(Math.Round(u.CacheRate) + "%", 13.5, Brushes.White, FontWeights.SemiBold), new Thickness(0, 9, 0, 0)));
            if (!string.IsNullOrEmpty(note))
                sp.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
        }

        UIElement BuildCodexPanel()
        {
            var sp = new StackPanel();
            var c = codex ?? new CodexData { Error = "加载中…" };
            sp.Children.Add(Header(OpenAiPath, true, "Codex 用量", c.Plan, Fmt.Ago(c.Updated)));
            if (c.Limits.Count > 0)
                for (int i = 0; i < c.Limits.Count; i++) sp.Children.Add(LimitBlock(c.Limits[i], i == c.Limits.Count - 1));
            else
                sp.Children.Add(new TextBlock { Text = c.Error, FontSize = 13, Foreground = Gray, TextWrapping = TextWrapping.Wrap });
            AppendUsageSection(sp, c, "* 按 API 价格估算，仅供参考");
            return sp;
        }

        static UIElement Chart(double[] daily)
        {
            double max = daily.Max();
            var ug = new UniformGrid { Rows = 1, Columns = daily.Length, Height = 46, Margin = new Thickness(0, 14, 0, 2) };
            for (int i = 0; i < daily.Length; i++)
            {
                bool today = i == daily.Length - 1;
                double h = (max > 0 && daily[i] > 0) ? Math.Max(5, 44 * daily[i] / max) : 3.5;
                var b = new Border
                {
                    Width = daily[i] > 0 ? 5 : 3.5, Height = h, CornerRadius = new CornerRadius(daily[i] > 0 ? 2.5 : 1.75),
                    VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center,
                    Background = today ? Fr("#E8E8E8") : (daily[i] > 0 ? Fr("#5C5C5C") : Fr("#383838")),
                    ToolTip = DateTime.Today.AddDays(i - daily.Length + 1).ToString("M月d日") + "  " + Fmt.Tok(daily[i])
                };
                ug.Children.Add(b);
            }
            return ug;
        }

        UIElement BuildAgPanel()
        {
            var sp = new StackPanel();
            var a = ag ?? new AgData { Error = "加载中…" };

            // 标题栏右侧带「校准额度」按钮
            var hdr = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = MakeIcon(AgPath, false, 21); ic.VerticalAlignment = VerticalAlignment.Center;
            ((WPath)ic).StrokeThickness = 3;
            left.Children.Add(ic);
            var tt = T("Antigravity 用量", 17, Brushes.White, FontWeights.Bold); tt.Margin = new Thickness(10, 0, 0, 0); tt.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(tt);

            var calBtn = new Border
            {
                CornerRadius = new CornerRadius(10), Background = Fr("#1E1E1E"), BorderBrush = Fr("#333333"), BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand, ToolTip = "对照 Settings → Models 快速校准基准百分比"
            };
            calBtn.Child = new TextBlock { Text = "校准额度", FontSize = 12, Foreground = Label, FontWeight = FontWeights.SemiBold };
            calBtn.MouseEnter += (s, e) => calBtn.Background = Fr("#2C2C2C");
            calBtn.MouseLeave += (s, e) => calBtn.Background = Fr("#1E1E1E");
            calBtn.MouseLeftButtonDown += (s, e) => e.Handled = true;
            calBtn.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenCalibrateDialog(); };

            hdr.Children.Add(LeftRight(left, calBtn, new Thickness(0)));
            string sub = Fmt.Ago(a.Updated);
            if (!string.IsNullOrEmpty(sub))
                hdr.Children.Add(new TextBlock { Text = sub + " · 日志实时追踪", FontSize = 12.5, Foreground = Gray, Margin = new Thickness(31, 3, 0, 0) });
            sp.Children.Add(hdr);

            if (a.Limits.Count > 0)
                for (int i = 0; i < a.Limits.Count; i++) sp.Children.Add(LimitBlock(a.Limits[i], i == a.Limits.Count - 1));
            else
                sp.Children.Add(new TextBlock { Text = a.Error, FontSize = 13, Foreground = Gray, TextWrapping = TextWrapping.Wrap });

            AppendUsageSection(sp, a, "≈ 基于本机对话日志实时扣减估算；校准两次后自动学习你的额度速率");
            return sp;
        }

        // ---------- 通用暗色对话框 ----------
        Window MakeDialog(string title, double width, Window owner, out StackPanel root)
        {
            var dlg = new Window
            {
                Title = title, Width = width, SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, Topmost = true, Owner = owner ?? this, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FontFamily = FontFamily, UseLayoutRounding = true
            };
            var card = new Border
            {
                CornerRadius = new CornerRadius(20), Background = Fr("#121212"), BorderBrush = Fr("#2E2E2E"),
                BorderThickness = new Thickness(1), Padding = new Thickness(20, 18, 20, 16), Margin = new Thickness(10)
            };
            card.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5 };
            card.MouseLeftButtonDown += (s, e) => { try { dlg.DragMove(); } catch { } };
            root = new StackPanel();
            root.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            card.Child = root;
            dlg.Content = card;
            dlg.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) dlg.Close(); };
            dlg.Loaded += (s, e) =>
            {
                var wa = SystemParameters.WorkArea;
                if (dlg.Top + dlg.ActualHeight > wa.Bottom) dlg.Top = wa.Bottom - dlg.ActualHeight;
                if (dlg.Top < wa.Top) dlg.Top = wa.Top;
                if (dlg.Left + dlg.ActualWidth > wa.Right) dlg.Left = wa.Right - dlg.ActualWidth;
                if (dlg.Left < wa.Left) dlg.Left = wa.Left;
            };
            return dlg;
        }

        void RunDialog(Window dlg)
        {
            modal++;
            try { dlg.ShowDialog(); }
            finally { modal--; }
        }

        static TextBlock Hint(string text, Thickness margin)
        {
            return new TextBlock { Text = text, FontSize = 11.5, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = margin };
        }

        static Border SmallBtn(string text, string tip, Action act)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(10), Background = Fr("#1E1E1E"), BorderBrush = Fr("#333333"), BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 0, 0)
            };
            if (!string.IsNullOrEmpty(tip)) b.ToolTip = tip;
            b.Child = new TextBlock { Text = text, FontSize = 12, Foreground = Label, FontWeight = FontWeights.SemiBold };
            b.MouseEnter += (s, e) => b.Background = Fr("#2C2C2C");
            b.MouseLeave += (s, e) => b.Background = Fr("#1E1E1E");
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (act != null) act(); };
            return b;
        }

        class SlotRow { public string Key, Label; public double Pct; public DateTime? Reset; }

        void ShowSlotDialog(string title, string desc, List<SlotRow> rows, string saveText, Action<Dictionary<string, double>, Dictionary<string, string>> onSave)
        {
            StackPanel root;
            var dlg = MakeDialog(title, 346, null, out root);
            root.Children.Add(Hint(desc, new Thickness(0, 4, 0, 14)));

            var pctBoxes = new Dictionary<string, TextBox>();
            var rstBoxes = new Dictionary<string, TextBox>();
            var now = DateTime.Now;
            foreach (var r in rows)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
                row.Children.Add(new TextBlock { Text = r.Label, FontSize = 12.5, Foreground = Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

                var tbPct = MakeInputBox(Math.Round(r.Pct).ToString("0", CultureInfo.InvariantCulture) + "%");
                tbPct.Margin = new Thickness(6, 0, 0, 0);
                Grid.SetColumn(tbPct, 1); row.Children.Add(tbPct);
                pctBoxes[r.Key] = tbPct;

                var tbRst = MakeInputBox(AgReader.FormatRemainingSpan(r.Reset, now));
                tbRst.Margin = new Thickness(6, 0, 0, 0);
                tbRst.ToolTip = "剩余重置时长（如 6d 6h 或 4h 47m），留空保持原重置时间";
                Grid.SetColumn(tbRst, 2); row.Children.Add(tbRst);
                rstBoxes[r.Key] = tbRst;
                root.Children.Add(row);
            }

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var cancelBtn = MakeDialogBtn("取消", "#222222", Brushes.White);
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            cancelBtn.MouseLeftButtonUp += (s, e) => dlg.Close();
            var saveBtn = MakeDialogBtn(saveText, "#2EE59D", Fr("#0A0A0A"));
            saveBtn.MouseLeftButtonUp += (s, e) =>
            {
                var np = new Dictionary<string, double>();
                var nr = new Dictionary<string, string>();
                foreach (var kv in pctBoxes)
                {
                    string raw = kv.Value.Text.Trim().TrimEnd('%').Trim();
                    double v;
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) np[kv.Key] = v;
                }
                foreach (var kv in rstBoxes) nr[kv.Key] = kv.Value.Text;
                dlg.Close();
                onSave(np, nr);
            };
            btns.Children.Add(cancelBtn);
            btns.Children.Add(saveBtn);
            root.Children.Add(btns);
            RunDialog(dlg);
        }

        void OpenCalibrateDialog()
        {
            string plan; DateTime? upd;
            var slots = AgReader.ReadCalibItems(agPath, out plan, out upd);
            // 按 Settings -> Models 界面顺序排列：Gemini 周、Gemini 5h、Claude 周、Claude 5h
            string[] order = { "gemini_week", "gemini_5h", "claude_week", "claude_5h" };
            var ordered = order.Select(k => slots.FirstOrDefault(x => x.key == k)).Where(x => x != null).ToList();
            foreach (var s in slots) if (!ordered.Contains(s)) ordered.Add(s);
            var rows = new List<SlotRow>();
            foreach (var s in ordered)
            {
                var curLim = ag != null ? ag.Limits.FirstOrDefault(l => l.Key == s.key) : null;
                rows.Add(new SlotRow
                {
                    Key = s.key, Label = s.label.Replace("限额 · ", " · "),
                    Pct = curLim != null ? curLim.Remaining : s.remaining,
                    Reset = curLim != null ? curLim.ResetAt : J.Time(s.resetAt)
                });
            }
            ShowSlotDialog("校准 Antigravity 额度",
                "对照 Settings → Models 填入当前百分比；之后每次对话都会根据本机日志实时自动扣减。重置时间可填如 6d 6h、4h 47m 或 15:30。",
                rows, "保存并实时追踪", (np, nr) => { AgReader.SaveCalibration(cfg, agPath, np, nr); Refresh(); });
        }

        void OpenProviderCalibrate(ProviderCfg p)
        {
            if (p.slots == null || p.slots.Count == 0) { if (OpenEditDialog(p, false, null)) AfterProvidersChanged(); return; }
            ProviderData d;
            provData.TryGetValue(p.id, out d);
            var rows = new List<SlotRow>();
            foreach (var s in p.slots)
            {
                var cur = d != null ? d.Limits.FirstOrDefault(l => l.Key == s.key) : null;
                rows.Add(new SlotRow { Key = s.key, Label = s.label, Pct = cur != null ? cur.Remaining : s.remaining, Reset = cur != null ? cur.ResetAt : J.Time(s.resetAt) });
            }
            bool log = ProviderReader.IsLogType(p.type);
            string desc = log
                ? "对照 " + p.name + " 官方用量页填入当前剩余百分比；之后根据本机日志实时自动扣减，校准两次后会自动学习额度速率。重置时间可填如 4h 47m、6d 6h 或 15:30。"
                : "填入当前剩余百分比与距离重置的时长（如 4h 47m、6d 6h 或 15:30）。到达重置时间后自动恢复 100%；设置了重置周期的会自动滚动到下一周期。";
            ShowSlotDialog((log ? "校准 " : "更新 ") + p.name + " 额度", desc, rows, log ? "保存并实时追踪" : "保存",
                (np, nr) => { ProviderReader.Calibrate(p, np, nr); SaveConfig(); Refresh(false); });
        }

        void AfterProvidersChanged()
        {
            SaveConfig();
            LayoutRings();
            ClampPosition(false);
            SetupWatchers();
            UpdateAll();
            Refresh(true);
        }

        // ---------- 管理 AI 应用 ----------
        UIElement ManageRow(FrameworkElement icon, string name, string desc, bool on, Action<bool> setOn, Action edit, Action del, Action up)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cb = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = "显示在悬浮条上" };
            cb.Checked += (s, e) => setOn(true);
            cb.Unchecked += (s, e) => setOn(false);
            g.Children.Add(cb);
            icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center;
            var iconHost = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Fr("#1E1E1E"), Child = icon, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(iconHost, 1); g.Children.Add(iconHost);
            var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            txt.Children.Add(T(name, 13, Brushes.White, FontWeights.SemiBold));
            txt.Children.Add(T(desc, 11, Gray, FontWeights.Normal));
            Grid.SetColumn(txt, 2); g.Children.Add(txt);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (up != null) btns.Children.Add(SmallBtn("↑", "上移", up));
            if (edit != null) btns.Children.Add(SmallBtn("编辑", null, edit));
            if (del != null) btns.Children.Add(SmallBtn("删除", null, del));
            Grid.SetColumn(btns, 3); g.Children.Add(btns);
            return g;
        }

        void OpenManageDialog()
        {
            if (cfg.providers == null) cfg.providers = new List<ProviderCfg>();
            StackPanel root;
            var dlg = MakeDialog("管理 AI 应用", 430, null, out root);
            root.Children.Add(Hint("勾选要显示在悬浮条上的应用。可添加 Claude Code / Gemini CLI（读取本机日志实时估算）、各家 API 余额，或手动记录任意 AI 应用（Cursor、ChatGPT、Kimi…）的额度。", new Thickness(0, 4, 0, 12)));
            var list = new StackPanel();
            root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            Action rebuild = null;
            rebuild = () =>
            {
                list.Children.Clear();
                list.Children.Add(ManageRow(MakeIcon(OpenAiPath, true, 15), "Codex", "内置 · 本机会话日志", cfg.showCodex, v => cfg.showCodex = v, null, null, null));
                var agIc = MakeIcon(AgPath, false, 15); ((WPath)agIc).StrokeThickness = 2.4;
                list.Children.Add(ManageRow(agIc, "Antigravity", "内置 · 本机对话日志", cfg.showAntigravity, v => cfg.showAntigravity = v, null, null, null));
                var provs = cfg.providers;
                for (int i = 0; i < provs.Count; i++)
                {
                    var p = provs[i];
                    int idx = i;
                    if (p == null) continue;
                    Action up = null;
                    if (idx > 0) up = () => { provs.RemoveAt(idx); provs.Insert(idx - 1, p); rebuild(); };
                    list.Children.Add(ManageRow(MakeProviderIcon(p, 15), p.name, ProviderReader.TypeDesc(p.type), p.enabled, v => p.enabled = v,
                        () => { OpenEditDialog(p, false, dlg); rebuild(); },
                        () =>
                        {
                            if (MessageBox.Show(dlg, "确定删除「" + p.name + "」？", "AI 额度", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                            { provs.Remove(p); rebuild(); }
                        }, up));
                }
            };
            rebuild();

            root.Children.Add(new TextBlock { Text = "添加应用", FontSize = 13, Foreground = Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 8) });
            var wrap = new WrapPanel();
            foreach (var k in ProviderReader.PresetKeys)
            {
                string key = k;
                var b = SmallBtn("+ " + ProviderReader.PresetName(key), null, () =>
                {
                    var p = ProviderReader.Preset(key);
                    if (OpenEditDialog(p, true, dlg)) { cfg.providers.Add(p); rebuild(); }
                });
                b.Margin = new Thickness(0, 0, 6, 6);
                wrap.Children.Add(b);
            }
            root.Children.Add(wrap);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var done = MakeDialogBtn("完成", "#2EE59D", Fr("#0A0A0A"));
            done.MouseLeftButtonUp += (s, e) => dlg.Close();
            btns.Children.Add(done);
            root.Children.Add(btns);
            RunDialog(dlg);
            AfterProvidersChanged();
        }

        static TextBox MakeField(string text, bool multi)
        {
            var tb = new TextBox
            {
                Text = text ?? "", FontSize = 12.5, Foreground = Brushes.White, CaretBrush = Brushes.White,
                Background = Fr("#1E1E1E"), BorderBrush = Fr("#343434"), BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4)
            };
            if (multi)
            {
                tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.NoWrap; tb.Height = 66;
                tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; tb.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            return tb;
        }

        static void AddRow(StackPanel root, string label, Control box, string hint)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, string.IsNullOrEmpty(hint) ? 8 : 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = Label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(box, 1); g.Children.Add(box);
            root.Children.Add(g);
            if (!string.IsNullOrEmpty(hint))
                root.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(92, 2, 0, 8) });
        }

        static TextBox AddField(StackPanel root, string label, string val, string hint, bool multi)
        {
            var tb = MakeField(val, multi);
            AddRow(root, label, tb, hint);
            return tb;
        }

        // 返回是否保存
        bool OpenEditDialog(ProviderCfg p, bool isNew, Window owner)
        {
            StackPanel root;
            var dlg = MakeDialog((isNew ? "添加 " : "编辑 ") + p.name, 470, owner, out root);
            bool log = ProviderReader.IsLogType(p.type), api = p.type == "api";
            string intro;
            if (p.type == "claude-code") intro = "读取本机 Claude Code 对话日志（~/.claude/projects），按 API 价格换算用量，并按 5 小时 / 每周窗口实时估算剩余额度；对照 claude.ai 的用量页「校准」两次后会自动学习额度速率。";
            else if (p.type == "gemini-cli") intro = "读取本机 Gemini CLI 会话记录（~/.gemini/tmp），统计 token 用量与请求次数，按每日请求上限估算剩余额度。";
            else if (api) intro = "定时调用余额查询接口（默认每 5 分钟），解析 JSON 中的余额字段。字段路径用点号分隔，数组用数字下标，如 balance_infos.0.total_balance。";
            else intro = "适用于没有本机日志或接口的应用（Cursor、ChatGPT、Kimi 网页版等）：在面板里点「更新」填入当前剩余百分比与重置时间，到期自动恢复。";
            root.Children.Add(Hint(intro, new Thickness(0, 4, 0, 14)));

            var content = new StackPanel();
            root.Children.Add(new ScrollViewer { Content = content, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var fName = AddField(content, "名称", p.name, null, false);
            var fIcon = AddField(content, "图标", p.icon, "1~3 个字符，或填 claude / gemini / deepseek 使用内置图标", false);
            var fColor = AddField(content, "颜色", p.color, "图标颜色，如 #4D6BFE", false);
            var fPlan = AddField(content, "套餐备注", p.plan, null, false);
            TextBox fDir = null, fUrl = null, fMethod = null, fHeaders = null, fBody = null, fRem = null, fTot = null, fUsed = null, fUnit = null, fFull = null, fPoll = null, fKeyPlain = null;
            PasswordBox fKey = null;
            bool keyVisible = false;
            if (log)
                fDir = AddField(content, "数据目录", p.dataDir, "留空自动检测（" + (p.type == "claude-code" ? "~/.claude/projects，支持 CLAUDE_CONFIG_DIR" : "~/.gemini/tmp") + "）；多个目录用 ; 分隔", false);
            if (api)
            {
                fUrl = AddField(content, "接口地址", p.url, null, false);
                fKey = new PasswordBox
                {
                    Password = p.apiKey ?? "", FontSize = 12.5, Foreground = Brushes.White, CaretBrush = Brushes.White,
                    Background = Fr("#1E1E1E"), BorderBrush = Fr("#343434"), BorderThickness = new Thickness(1), Padding = new Thickness(6, 4, 6, 4)
                };
                fKeyPlain = MakeField(p.apiKey ?? "", false);
                fKeyPlain.Visibility = Visibility.Collapsed;
                var keyRow = new Grid();
                keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                keyRow.Children.Add(fKey);
                keyRow.Children.Add(fKeyPlain);
                Border toggleKeyBtn = null;
                toggleKeyBtn = SmallBtn("显示", "显示或隐藏 API Key", () =>
                {
                    keyVisible = !keyVisible;
                    if (keyVisible) { fKeyPlain.Text = fKey.Password; fKey.Visibility = Visibility.Collapsed; fKeyPlain.Visibility = Visibility.Visible; }
                    else { fKey.Password = fKeyPlain.Text; fKeyPlain.Visibility = Visibility.Collapsed; fKey.Visibility = Visibility.Visible; }
                    ((TextBlock)toggleKeyBtn.Child).Text = keyVisible ? "隐藏" : "显示";
                });
                Grid.SetColumn(toggleKeyBtn, 1); keyRow.Children.Add(toggleKeyBtn);
                var kg = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                kg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                kg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                kg.Children.Add(new TextBlock { Text = "API Key", FontSize = 12.5, Foreground = Label, VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(keyRow, 1); kg.Children.Add(keyRow);
                content.Children.Add(kg);
                content.Children.Add(new TextBlock { Text = "自动以 Bearer 方式发送；也可写 env:变量名 从环境变量读取（明文保存在 config.json）", FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(92, 2, 0, 8) });
                fMethod = AddField(content, "请求方法", string.IsNullOrEmpty(p.method) ? "GET" : p.method, null, false);
                fHeaders = AddField(content, "额外请求头", p.headers, "每行一个 Key: Value，可用 {apiKey} 占位", true);
                fBody = AddField(content, "请求体", p.body, "仅 POST 等方法使用，可留空", false);
                fRem = AddField(content, "余额字段", p.remainingPath, "剩余余额的 JSON 路径", false);
                fTot = AddField(content, "总额字段", p.totalPath, "可选；有总额时按 余额/总额 计算百分比", false);
                fUsed = AddField(content, "已用字段", p.usedPath, "可选；与总额字段配合可推算余额", false);
                fUnit = AddField(content, "货币符号", p.unitSymbol, "如 ¥、$，或留空", false);
                fFull = AddField(content, "满额基准", p.fullAmount > 0 ? p.fullAmount.ToString("0.##", CultureInfo.InvariantCulture) : "", "无总额字段时，百分比 = 余额 / 基准；留空则自动记录出现过的最高余额", false);
                fPoll = AddField(content, "查询间隔(秒)", p.pollSeconds.ToString(CultureInfo.InvariantCulture), null, false);
            }
            string slotHint = log
                ? "每行一个：名称 | 窗口 | 100% 对应的量 | 单位(usd/req/tok)\n例：5 小时限额 | 5h | 8 | usd　　每日请求 | 24h | 1000 | req"
                : "每行一个：名称 | 重置周期（可留空，如 5h、7d、30d）\n例：每月快速请求 | 30d";
            var fSlots = AddField(content, api ? "手动额度(可选)" : "额度窗口", ProviderReader.SlotsToText(p.slots, log), slotHint, true);

            TextBox testOut = null;
            if (api)
            {
                testOut = new TextBox
                {
                    FontSize = 11.5, Foreground = Gray, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                    IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 84,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0),
                    Visibility = Visibility.Collapsed
                };
                root.Children.Add(testOut);
            }

            Action<ProviderCfg> apply = t =>
            {
                t.name = string.IsNullOrWhiteSpace(fName.Text) ? "未命名" : fName.Text.Trim();
                t.icon = fIcon.Text.Trim();
                t.color = fColor.Text.Trim();
                t.plan = fPlan.Text.Trim();
                if (fDir != null) t.dataDir = fDir.Text.Trim();
                if (api)
                {
                    t.url = fUrl.Text.Trim();
                    t.apiKey = (keyVisible ? fKeyPlain.Text : fKey.Password).Trim();
                    t.method = string.IsNullOrWhiteSpace(fMethod.Text) ? "GET" : fMethod.Text.Trim().ToUpperInvariant();
                    t.headers = fHeaders.Text.Replace("\r", "").Trim();
                    t.body = fBody.Text.Trim();
                    t.remainingPath = fRem.Text.Trim();
                    t.totalPath = fTot.Text.Trim();
                    t.usedPath = fUsed.Text.Trim();
                    t.unitSymbol = fUnit.Text.Trim();
                    double full = J.D(fFull.Text.Trim(), 0);
                    t.fullAmount = Math.Max(0, full);
                    t.pollSeconds = (int)Math.Max(60, J.D(fPoll.Text.Trim(), 300));
                }
                t.slots = ProviderReader.SlotsFromText(fSlots.Text.Replace("\r", ""), ProviderReader.DefaultUnit(p.type), p.slots);
            };

            bool saved = false;
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            if (api)
            {
                var testBtn = MakeDialogBtn("测试接口", "#222222", Brushes.White);
                testBtn.Margin = new Thickness(0, 0, 8, 0);
                testBtn.MouseLeftButtonUp += (s, e) =>
                {
                    var tmp = new ProviderCfg { id = p.id, type = p.type };
                    apply(tmp);
                    testOut.Visibility = Visibility.Visible;
                    testOut.Foreground = Gray;
                    testOut.Text = "正在请求…";
                    Task.Run(() =>
                    {
                        var r = ProviderReader.Fetch(tmp);
                        dlg.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (r.Error == null)
                                testOut.Text = "✓ 余额 " + tmp.unitSymbol + r.Rem.ToString("0.####", CultureInfo.InvariantCulture) +
                                    (double.IsNaN(r.Total) ? "" : "，总额 " + tmp.unitSymbol + r.Total.ToString("0.####", CultureInfo.InvariantCulture));
                            else testOut.Text = r.Error;
                            testOut.Foreground = r.Error == null ? new SolidColorBrush(Green) : Fr("#FF8A8A");
                        }));
                    });
                };
                btns.Children.Add(testBtn);
            }
            var cancelBtn = MakeDialogBtn("取消", "#222222", Brushes.White);
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            cancelBtn.MouseLeftButtonUp += (s, e) => dlg.Close();
            var saveBtn = MakeDialogBtn("保存", "#2EE59D", Fr("#0A0A0A"));
            saveBtn.MouseLeftButtonUp += (s, e) => { apply(p); saved = true; dlg.Close(); };
            btns.Children.Add(cancelBtn);
            btns.Children.Add(saveBtn);
            root.Children.Add(btns);
            RunDialog(dlg);
            return saved;
        }

        // ---------- 自定义应用面板 ----------
        static string FmtBal(double v)
        {
            return v.ToString(Math.Abs(v) >= 10000 ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);
        }

        UIElement BuildProviderPanel(ProviderCfg p)
        {
            var sp = new StackPanel();
            ProviderData d;
            if (!provData.TryGetValue(p.id, out d)) d = new ProviderData { Error = "加载中…" };
            bool log = ProviderReader.IsLogType(p.type);

            var hdr = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var icHost = new Grid { Width = 21, Height = 21, VerticalAlignment = VerticalAlignment.Center };
            var ic = MakeProviderIcon(p, 20);
            ic.HorizontalAlignment = HorizontalAlignment.Center; ic.VerticalAlignment = VerticalAlignment.Center;
            icHost.Children.Add(ic);
            left.Children.Add(icHost);
            var tt = T(p.name, 17, Brushes.White, FontWeights.Bold); tt.Margin = new Thickness(10, 0, 0, 0); tt.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(tt);
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            if (p.slots != null && p.slots.Count > 0)
                btns.Children.Add(SmallBtn(log ? "校准" : "更新", log ? "对照官方用量页校准百分比" : "手动填入当前剩余额度", () => OpenProviderCalibrate(p)));
            btns.Children.Add(SmallBtn("编辑", "修改此应用的配置", () => { if (OpenEditDialog(p, false, null)) AfterProvidersChanged(); }));
            hdr.Children.Add(LeftRight(left, btns, new Thickness(0)));
            string sub = Fmt.Ago(d.Updated);
            string desc = (string.IsNullOrEmpty(p.plan) ? "" : p.plan + " · ") + ProviderReader.TypeDesc(p.type);
            hdr.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(sub) ? desc : sub + " · " + desc, FontSize = 12.5, Foreground = Gray, Margin = new Thickness(31, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(hdr);

            if (d.HasBalance)
            {
                var bal = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
                bal.Children.Add(new TextBlock { Text = "账户余额", FontSize = 13.5, Foreground = Label, Margin = new Thickness(0, 0, 0, 2) });
                bal.Children.Add(new TextBlock { Text = (d.Unit ?? "") + FmtBal(d.Balance), FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) });
                double pct = d.BalancePct >= 0 ? d.BalancePct : 0;
                bal.Children.Add(SegBar(pct));
                string right = "";
                if (d.BalanceTotal > 0)
                    right = (!string.IsNullOrEmpty(p.totalPath) || !string.IsNullOrEmpty(p.usedPath) ? "总额 " : "基准 ") + (d.Unit ?? "") + FmtBal(d.BalanceTotal);
                bal.Children.Add(LeftRight(T("剩余 " + Math.Round(pct) + "%", 14, Brushes.White, FontWeights.SemiBold), T(right, 13, Gray, FontWeights.Normal), new Thickness(0, 8, 0, 0)));
                sp.Children.Add(bal);
            }
            if (!string.IsNullOrEmpty(d.Error))
                sp.Children.Add(new TextBlock { Text = d.Error, FontSize = 12.5, Foreground = d.HasBalance ? Fr("#FF8A8A") : Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            for (int i = 0; i < d.Limits.Count; i++) sp.Children.Add(LimitBlock(d.Limits[i], i == d.Limits.Count - 1));
            if (!d.HasBalance && d.Limits.Count == 0 && string.IsNullOrEmpty(d.Error))
                sp.Children.Add(new TextBlock { Text = "还没有设置额度窗口，点击「编辑」添加", FontSize = 13, Foreground = Gray, TextWrapping = TextWrapping.Wrap });

            if (d.HasUsage)
                AppendUsageSection(sp, d, p.type == "gemini-cli" ? "* 按 API 价格估算；额度按请求次数估算，仅供参考" : "≈ 基于本机日志按 API 价格估算；校准两次后自动学习你的额度速率");
            else if (p.type == "api")
                sp.Children.Add(new TextBlock { Text = "每 " + Math.Max(60, p.pollSeconds) + " 秒查询一次接口；右键「立即刷新」可强制查询", FontSize = 11, Foreground = Dim, Margin = new Thickness(0, d.Limits.Count > 0 ? 14 : 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            else if (!log)
                sp.Children.Add(new TextBlock { Text = "手动记录：到达重置时间后自动恢复 100%", FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });
            return sp;
        }

        static TextBox MakeInputBox(string text)
        {
            return new TextBox
            {
                Text = text, FontSize = 12.5, Foreground = Brushes.White, CaretBrush = Brushes.White,
                Background = Fr("#1E1E1E"), BorderBrush = Fr("#343434"), BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4), HorizontalContentAlignment = HorizontalAlignment.Center
            };
        }

        static Border MakeDialogBtn(string text, string bg, Brush fg)
        {
            var b = new Border { CornerRadius = new CornerRadius(9), Background = Fr(bg), Padding = new Thickness(14, 6, 14, 6), Cursor = Cursors.Hand };
            b.Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = fg, FontWeight = FontWeights.Bold };
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            return b;
        }

        static double? MinRemaining(List<Limit> ls)
        {
            if (ls == null || ls.Count == 0) return null;
            return ls.Min(l => l.Remaining);
        }

        static double? AgRingRemaining(List<Limit> ls)
        {
            if (ls == null || ls.Count == 0) return null;
            var g5 = ls.FirstOrDefault(l => l.Key == "gemini_5h") ??
                     ls.FirstOrDefault(l => l.Pool == "gemini" && l.WindowMinutes > 0 && l.WindowMinutes < 1440) ??
                     ls.FirstOrDefault(l => l.Label != null && l.Label.IndexOf("Gemini", StringComparison.OrdinalIgnoreCase) >= 0);
            if (g5 == null) return ls.Min(l => l.Remaining);
            var gw = ls.FirstOrDefault(l => l.Key == "gemini_week") ??
                     ls.FirstOrDefault(l => l.Pool == "gemini" && l.WindowMinutes >= 1440);
            if (gw != null && gw.Remaining < 0.5) return 0;
            return g5.Remaining;
        }

        double? RingPctFor(string key)
        {
            if (key == "codex") return codex == null ? null : MinRemaining(codex.Limits);
            if (key == "ag") return ag == null ? null : AgRingRemaining(ag.Limits);
            ProviderData d;
            if (key.StartsWith("p:") && provData.TryGetValue(key.Substring(2), out d)) return d.RingPct;
            return null;
        }

        public void UpdateAll()
        {
            var keys = VisibleKeys();
            foreach (var k in keys) { RingView rv; if (rings.TryGetValue(k, out rv)) SetRing(rv, RingPctFor(k), selected == k); }
            if (selected == null || !keys.Contains(selected)) { panelHost.Visibility = Visibility.Collapsed; return; }
            int cells = keys.Count + (cfg.showCapsLock ? 1 : 0);
            double barLeft = (WinW - BarW) / 2, cellsLeft = barLeft + (BarW - cells * CellW) / 2;
            double cx = cellsLeft + CellW * (keys.IndexOf(selected) + 0.5);
            double off = Math.Max(0, Math.Min(WinW - PanelW, cx - PanelW / 2));
            panelHost.Margin = new Thickness(off, 6, 0, 0);
            pointer.Margin = new Thickness(cx - off - 14, 0, 0, -1);
            if (selected == "codex") panel.Child = BuildCodexPanel();
            else if (selected == "ag") panel.Child = BuildAgPanel();
            else
            {
                string id = selected.Substring(2);
                var p = (cfg.providers ?? new List<ProviderCfg>()).FirstOrDefault(x => x != null && x.id == id);
                if (p == null) { panelHost.Visibility = Visibility.Collapsed; return; }
                panel.Child = BuildProviderPanel(p);
            }
            panelHost.Visibility = Visibility.Visible;
        }

        public void SetData(CodexData c, AgData a) { codex = c; ag = a; UpdateAll(); }

        public void SetProviderData(Dictionary<string, ProviderData> pd) { provData = pd ?? new Dictionary<string, ProviderData>(); UpdateAll(); }

        public static Dictionary<string, ProviderData> LoadProviders(List<ProviderCfg> provs, bool force)
        {
            var r = new Dictionary<string, ProviderData>();
            foreach (var p in provs)
            {
                try { r[p.id] = ProviderReader.Load(p, force); }
                catch (Exception ex) { r[p.id] = new ProviderData { Id = p.id, Error = ex.Message }; }
            }
            return r;
        }

        void ApplyProviderResults(Dictionary<string, ProviderData> pd)
        {
            bool dirty = false;
            foreach (var kv in pd)
            {
                if (kv.Value.NewFullAmount <= 0) continue;
                var p = (cfg.providers ?? new List<ProviderCfg>()).FirstOrDefault(x => x != null && x.id == kv.Key);
                if (p != null && Math.Abs(p.fullAmount - kv.Value.NewFullAmount) > 1e-9) { p.fullAmount = kv.Value.NewFullAmount; dirty = true; }
            }
            provData = pd;
            if (dirty) SaveConfig();
        }

        void Refresh() { Refresh(false); }

        void Refresh(bool force)
        {
            if (busy) { if (force) pendingRefresh = true; return; }
            busy = true;
            bool sc = cfg.showCodex, sa = cfg.showAntigravity;
            var provs = ActiveProviders();
            Task.Run(() =>
            {
                CodexData c = null; AgData a = null;
                if (sc) { try { c = CodexReader.Load(cfg); } catch (Exception ex) { c = new CodexData { Error = ex.Message }; } }
                if (sa) { try { a = AgReader.Load(cfg, agPath); } catch (Exception ex) { a = new AgData { Error = ex.Message }; } }
                var pd = LoadProviders(provs, force);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    busy = false;
                    ApplyProviderResults(pd);
                    SetData(c ?? codex, a ?? ag);
                    if (pendingRefresh) { pendingRefresh = false; Refresh(true); }
                }));
            });
        }

        // 演示数据（用于预览自定义应用）
        public static Dictionary<string, ProviderData> DemoProviders(List<ProviderCfg> provs)
        {
            var r = new Dictionary<string, ProviderData>();
            var now = DateTime.Now;
            var rnd = new Random(11);
            foreach (var p in provs)
            {
                bool log = ProviderReader.IsLogType(p.type);
                var d = new ProviderData { Id = p.id, Plan = p.plan, Updated = now.AddMinutes(-2), Unit = p.unitSymbol };
                if (p.type == "api") { d.HasBalance = true; d.Balance = 86.42; d.BalanceTotal = 120; d.BalancePct = 72; }
                int i = 0;
                foreach (var s in p.slots ?? new List<ProviderSlot>())
                {
                    var l = new Limit { Key = s.key, Label = s.label, Remaining = Math.Max(5, 82 - 31 * i), IsEstimated = log, ResetAt = now.AddMinutes(s.windowMinutes > 0 ? s.windowMinutes * 0.4 : 180) };
                    if (log && s.budget > 0) l.Detail = ProviderReader.AmountText(s.budget * (100 - l.Remaining) / 100, s.unit) + " / " + ProviderReader.AmountText(s.budget, s.unit);
                    d.Limits.Add(l);
                    i++;
                }
                if (log)
                {
                    for (int k = 0; k < 31; k++) d.Daily[k] = rnd.NextDouble() < 0.3 ? 0 : rnd.NextDouble() * 4e7;
                    d.Daily[30] = 1.2e7;
                    d.TodayTok = 1.2e7; d.WeekTok = 9.8e7; d.MonthTok = 3.6e8;
                    d.TodayCost = 6.2; d.WeekCost = 41.7; d.MonthCost = 160.3;
                    d.MainModel = p.type == "claude-code" ? "claude-sonnet-4-5" : "gemini-2.5-pro"; d.MainShare = 81; d.CacheRate = 92;
                    d.HasUsage = true;
                }
                ProviderReader.ComputeRing(d);
                r[p.id] = d;
            }
            return r;
        }

        void Drag()
        {
            try { DragMove(); } catch { }
            cfg.left = Math.Round(Left); cfg.top = Math.Round(Top);
            SaveConfig();
        }

        public void SaveConfig()
        {
            try { File.WriteAllText(cfgPath, J.Pretty(J.Js.Serialize(cfg)), new UTF8Encoding(false)); } catch { }
        }

        // ---------- 右键菜单 ----------
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static bool AutoStart
        {
            get { try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("AIQuotaWidget") != null; } catch { return false; } }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("AIQuotaWidget", Program.LaunchCommand);
                    else k.DeleteValue("AIQuotaWidget", false);
                }
            }
        }

        ContextMenu BuildMenu()
        {
            var m = new ContextMenu();
            Func<string, Action, MenuItem> add = (h, act) => { var mi = new MenuItem { Header = h }; mi.Click += (s, e) => act(); m.Items.Add(mi); return mi; };
            add("立即刷新", () => Refresh(true));
            add("管理 AI 应用（添加其他应用额度）…", OpenManageDialog);
            add("校准 Antigravity 额度…", OpenCalibrateDialog);
            add("打开 Codex 数据目录", () => { var h = CodexReader.Home(cfg); if (Directory.Exists(h)) Process.Start("explorer.exe", "\"" + h + "\""); });
            m.Items.Add(new Separator());
            MenuItem top = null, auto = null, col = null, caps = null;
            caps = add("显示大小写状态按钮", () =>
            {
                cfg.showCapsLock = !cfg.showCapsLock;
                caps.IsChecked = cfg.showCapsLock;
                LayoutRings();
                ClampPosition(false);
                UpdateAll();
                SaveConfig();
            });
            caps.IsChecked = cfg.showCapsLock;
            top = add("始终置顶", () => { cfg.topmost = !cfg.topmost; Topmost = cfg.topmost; top.IsChecked = cfg.topmost; SaveConfig(); });
            top.IsChecked = cfg.topmost;
            col = add("失去焦点时收起面板", () => { cfg.autoCollapse = !cfg.autoCollapse; col.IsChecked = cfg.autoCollapse; SaveConfig(); });
            col.IsChecked = cfg.autoCollapse;
            auto = add("开机自动启动", () => { try { AutoStart = !AutoStart; } catch { } auto.IsChecked = AutoStart; });
            auto.IsChecked = AutoStart;
            add("回到屏幕顶部居中", () => { var wa = SystemParameters.WorkArea; Left = wa.Left + (wa.Width - WinW) / 2; Top = wa.Top + 6; cfg.left = Left; cfg.top = Top; SaveConfig(); });
            add("编辑设置文件", () => { SaveConfig(); Process.Start("notepad.exe", "\"" + cfgPath + "\""); });
            m.Items.Add(new Separator());
            add("退出", Close);
            return m;
        }

        // ---------- 截图（用于预览） ----------
        public void RenderTo(string file)
        {
            var fe = (FrameworkElement)Content;
            Content = null;
            var host = new Grid { Background = Fr("#E9E9EE") };
            host.Children.Add(fe);
            host.Measure(new Size(WinW, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, WinW, host.DesiredSize.Height));
            host.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)(WinW * 2), (int)(host.DesiredSize.Height * 2), 192, 192, PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        // ---------- 演示数据 ----------
        public static void Demo(out CodexData c, out AgData a)
        {
            var now = DateTime.Now;
            c = new CodexData { Plan = "Plus", Updated = now.AddMinutes(-1) };
            c.Limits.Add(new Limit { Label = "5 小时限额", Remaining = 61, ResetAt = now.Date.AddHours(19).AddMinutes(5) });
            c.Limits.Add(new Limit { Label = "每周限额", Remaining = 75, ResetAt = now.Date.AddDays(5).AddHours(15).AddMinutes(45) });
            var rnd = new Random(7);
            for (int i = 0; i < 31; i++) c.Daily[i] = rnd.NextDouble() < 0.25 ? 0 : rnd.NextDouble() * 9e7;
            c.Daily[30] = 3.853e7;
            c.TodayTok = 3.853e7; c.WeekTok = 2.8e8; c.MonthTok = 1.31e9;
            c.TodayCost = 7.41; c.WeekCost = 65.91; c.MonthCost = 348.53;
            c.MainModel = "gpt-5-codex"; c.MainShare = 74; c.CacheRate = 97;
            a = new AgData { Updated = now.AddMinutes(-1) };
            a.Limits.Add(new Limit { Label = "5 小时限额 · Gemini", Remaining = 89, IsEstimated = true, ResetAt = now.Date.AddHours(20).AddMinutes(36) });
            a.Limits.Add(new Limit { Label = "每周限额 · Gemini", Remaining = 81, IsEstimated = true, ResetAt = now.Date.AddDays(6).AddHours(15).AddMinutes(36) });
            a.Limits.Add(new Limit { Label = "5 小时限额 · Claude and GPT", Remaining = 60, ResetAt = now.Date.AddHours(15).AddMinutes(17) });
            a.Limits.Add(new Limit { Label = "每周限额 · Claude and GPT", Remaining = 80, ResetAt = now.Date.AddDays(6).AddHours(9).AddMinutes(30) });
            for (int i = 0; i < 31; i++) a.Daily[i] = rnd.NextDouble() < 0.3 ? 0 : rnd.NextDouble() * 5e7;
            a.Daily[30] = 1.92e7;
            a.TodayTok = 1.92e7; a.WeekTok = 1.46e8; a.MonthTok = 6.2e8;
            a.TodayCost = 4.18; a.WeekCost = 28.50; a.MonthCost = 112.40;
            a.MainModel = "gemini-4-carbon-high"; a.MainShare = 64; a.CacheRate = 95;
        }
    }

    // ======================= 入口 =======================
    public static class Program
    {
        static Mutex mutex;
        public static string LaunchCommand;   // 开机启动时写入注册表的命令

        [STAThread]
        public static int Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--dir") dir = args[i + 1];
                if (args[i] == "--launch") LaunchCommand = args[i + 1];
            }
            if (LaunchCommand == null) LaunchCommand = "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"";
            string cfgPath = IOPath.Combine(dir, "config.json");
            string agPath = IOPath.Combine(dir, "antigravity_quota.json");

            string snap = null, sel = null; bool demo = false, test = false; bool? forceCaps = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--snapshot" && i + 1 < args.Length) snap = args[++i];
                else if (args[i] == "--select" && i + 1 < args.Length) sel = args[++i];
                else if (args[i] == "--demo") demo = true;
                else if (args[i] == "--test") test = true;
                else if (args[i] == "--caps-on") forceCaps = true;
                else if (args[i] == "--caps-off") forceCaps = false;
            }

            if (snap == null && !test)
            {
                bool created;
                mutex = new Mutex(true, @"Local\AIQuotaWidget_Mutex_v2", out created);
                if (!created)
                {
                    Native.CloseExistingWidget();
                    try { if (!mutex.WaitOne(2000)) return 0; }
                    catch (AbandonedMutexException) { }
                }
            }

            var cfg = LoadConfig(cfgPath);

            if (test)
            {
                var c = CodexReader.Load(cfg); var a = AgReader.Load(cfg, agPath);
                var sb = new StringBuilder();
                sb.AppendLine("Codex error=" + c.Error + " plan=" + c.Plan + " updated=" + c.Updated);
                foreach (var l in c.Limits) sb.AppendLine("  " + l.Label + " " + l.Remaining + "% " + Fmt.Reset(l));
                sb.AppendLine("  today=" + Fmt.Tok(c.TodayTok) + " week=" + Fmt.Tok(c.WeekTok) + " month=" + Fmt.Tok(c.MonthTok) + " model=" + c.MainModel + " cache=" + c.CacheRate);
                sb.AppendLine("Antigravity error=" + a.Error + " updated=" + a.Updated);
                foreach (var l in a.Limits) sb.AppendLine("  " + l.Label + " " + Math.Round(l.Remaining, 1) + "% est=" + l.IsEstimated + " " + Fmt.Reset(l));
                sb.AppendLine("  today=" + Fmt.Tok(a.TodayTok) + " ($" + a.TodayCost.ToString("0.00") + ") week=" + Fmt.Tok(a.WeekTok) + " month=" + Fmt.Tok(a.MonthTok) + " model=" + a.MainModel + " cache=" + Math.Round(a.CacheRate) + "%");
                foreach (var p in cfg.providers.Where(x => x.enabled))
                {
                    ProviderData pd;
                    try { pd = ProviderReader.Load(p, true); } catch (Exception ex) { pd = new ProviderData { Error = ex.Message }; }
                    sb.AppendLine(p.name + " [" + p.type + "] error=" + pd.Error + " updated=" + pd.Updated + " ring=" + (pd.RingPct.HasValue ? Math.Round(pd.RingPct.Value, 1).ToString() : "--"));
                    if (pd.HasBalance) sb.AppendLine("  balance=" + pd.Unit + pd.Balance + " total=" + pd.BalanceTotal + " pct=" + Math.Round(pd.BalancePct, 1));
                    foreach (var l in pd.Limits) sb.AppendLine("  " + l.Label + " " + Math.Round(l.Remaining, 1) + "% est=" + l.IsEstimated + " " + Fmt.Reset(l) + " " + l.Detail);
                    if (pd.HasUsage) sb.AppendLine("  today=" + Fmt.Tok(pd.TodayTok) + " ($" + pd.TodayCost.ToString("0.00") + ") week=" + Fmt.Tok(pd.WeekTok) + " month=" + Fmt.Tok(pd.MonthTok) + " model=" + pd.MainModel);
                }
                sb.AppendLine("CapsLock=" + Native.IsCapsLockOn());
                File.WriteAllText(IOPath.Combine(dir, "test-output.txt"), sb.ToString(), new UTF8Encoding(true));
                return 0;
            }

            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var w = new Widget(cfg, cfgPath, agPath, snap != null);
            if (!File.Exists(cfgPath)) w.SaveConfig();

            if (snap != null)
            {
                CodexData c; AgData a;
                if (demo) Widget.Demo(out c, out a);
                else { c = CodexReader.Load(cfg); a = AgReader.Load(cfg, agPath); }
                w.SetData(c, a);
                var active = cfg.providers.Where(x => x.enabled).ToList();
                w.SetProviderData(demo ? Widget.DemoProviders(active) : Widget.LoadProviders(active, true));
                if (forceCaps.HasValue) w.SetCaps(forceCaps.Value);
                w.Select(sel);
                w.RenderTo(snap);
                return 0;
            }
            if (demo)
            {
                CodexData c; AgData a; Widget.Demo(out c, out a);
                var active = cfg.providers.Where(x => x.enabled).ToList();
                var pd = Widget.DemoProviders(active);
                w.Loaded += (s, e) => w.Dispatcher.BeginInvoke(new Action(() => { w.SetProviderData(pd); w.SetData(c, a); }), DispatcherPriority.ApplicationIdle);
            }
            try { app.Run(w); }
            finally { if (mutex != null) { try { mutex.ReleaseMutex(); } catch { } } }
            return 0;
        }

        static Config LoadConfig(string path)
        {
            Config cfg = null;
            try { if (File.Exists(path)) cfg = J.Js.Deserialize<Config>(File.ReadAllText(path, Encoding.UTF8)); } catch { }
            if (cfg == null) cfg = new Config();
            if (cfg.prices == null)
                cfg.prices = new Dictionary<string, List<double>>
                {
                    { "default", new List<double> { 1.25, 0.125, 10 } },
                    { "gpt-5-mini", new List<double> { 0.25, 0.025, 2 } }
                };
            if (cfg.providers == null) cfg.providers = new List<ProviderCfg>();
            cfg.providers.RemoveAll(p => p == null);
            var ids = new HashSet<string>();
            foreach (var p in cfg.providers)
            {
                if (string.IsNullOrEmpty(p.type)) p.type = "manual";
                if (string.IsNullOrEmpty(p.id) || !ids.Add(p.id)) { p.id = p.type + "-" + Guid.NewGuid().ToString("N").Substring(0, 6); ids.Add(p.id); }
                if (p.slots == null) p.slots = new List<ProviderSlot>();
                p.slots.RemoveAll(s => s == null);
                for (int i = 0; i < p.slots.Count; i++)
                    if (string.IsNullOrEmpty(p.slots[i].key)) p.slots[i].key = "s" + i + "_" + Guid.NewGuid().ToString("N").Substring(0, 4);
                if (string.IsNullOrEmpty(p.name)) p.name = ProviderReader.PresetName(p.type);
            }
            return cfg;
        }
    }
}

