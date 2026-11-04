// MoniteurJeu : moniteur PC + mode jeu automatique, dans la zone de notification.
// Conçu pour coûter presque rien : appels Windows directs, 1 mesure toutes les 2 s (5 s sur batterie),
// GPU NVIDIA, FPS et overlay seulement en jeu ou fenêtre ouverte (sinon on réveillerait le GPU d'un portable Optimus).
// C# 5 (compilateur intégré à Windows) : pas de $"", ?., => sur les membres.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var m = new Mutex(true, "MoniteurJeu_unique", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.Run(new App());
        }
    }
}

static class Native
{
    [DllImport("kernel32")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMSTAT { public uint len, load; public ulong totPhys, availPhys, totPage, availPage, totVirt, availVirt, availExt; }
    [DllImport("kernel32")] public static extern bool GlobalMemoryStatusEx(ref MEMSTAT m);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("shell32")] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("powrprof")] public static extern uint PowerGetActualOverlayScheme(out Guid g);
    [DllImport("powrprof")] public static extern uint PowerSetActiveOverlayScheme(Guid g);
    [DllImport("kernel32")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32")] public static extern bool SetPriorityClass(IntPtr h, uint cls);
    [DllImport("kernel32")] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32")] public static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);
    [DllImport("kernel32")] public static extern IntPtr GetCurrentProcess();

    [DllImport("ntdll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll")] public static extern int NtResumeProcess(IntPtr h);
    [DllImport("ntdll")] public static extern int NtSetSystemInformation(int cls, ref int info, int len);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct TOKPRIV { public int count; public long luid; public int attr; }
    [DllImport("advapi32")] public static extern bool OpenProcessToken(IntPtr p, int access, out IntPtr tok);
    [DllImport("advapi32", CharSet = CharSet.Unicode)] public static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
    [DllImport("advapi32")] public static extern bool AdjustTokenPrivileges(IntPtr tok, bool all, ref TOKPRIV p, int len, IntPtr prev, IntPtr ret);

    [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr dev);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint t);
    [StructLayout(LayoutKind.Sequential)] public struct NvUtil { public uint gpu, mem; }
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out NvUtil u);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr dev, out ulong r);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void EventCb(IntPtr rec);
    [DllImport("advapi32", CharSet = CharSet.Unicode)] public static extern int StartTraceW(out ulong h, string name, IntPtr props);
    [DllImport("advapi32", CharSet = CharSet.Unicode)] public static extern int ControlTraceW(ulong h, string name, IntPtr props, int code);
    [DllImport("advapi32")] public static extern int EnableTraceEx2(ulong h, ref Guid prov, int code, byte level, ulong any, ulong all, int timeout, IntPtr prm);
    [DllImport("advapi32")] public static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32")] public static extern int ProcessTrace(ulong[] h, int n, IntPtr start, IntPtr end);
    [DllImport("advapi32")] public static extern int CloseTrace(ulong h);

    public static string ExePath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(520); int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public static void TrimMemory() { SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1)); }

    // Vide la mémoire "en veille" (cache) pour laisser de la RAM libre au jeu. Admin requis.
    public static void PurgeStandby()
    {
        IntPtr tok;
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out tok)) return; // ADJUST_PRIVILEGES | QUERY
        var p = new TOKPRIV { count = 1, attr = 2 }; // SE_PRIVILEGE_ENABLED
        if (LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out p.luid)) AdjustTokenPrivileges(tok, false, ref p, 0, IntPtr.Zero, IntPtr.Zero);
        CloseHandle(tok);
        int cmd = 4; // MemoryPurgeStandbyList
        NtSetSystemInformation(80, ref cmd, 4); // SystemMemoryListInformation
    }
}

// Compteur FPS sans injection : compte les "Present" vus par le noyau graphique (ETW DxgKrnl), comme PresentMon.
// Marche pour DirectX, OpenGL et Vulkan, invisible pour les anticheats. Admin requis. Session ETW ouverte seulement en jeu.
sealed class Fps
{
    const string Name = "MoniteurJeu_FPS";
    static Guid DxgKrnl = new Guid("802ec45a-1e99-4b83-9920-87c98277ba9d");
    IntPtr props, logfile, name; ulong session, trace; Thread th;
    readonly Native.EventCb cb; // gardé en champ pour que le GC ne le libère pas
    int pid, n184, n215; // Present_Info et PresentHistoryDetailed : selon l'API, l'un ou l'autre suit chaque image
    readonly Stopwatch sw = new Stopwatch();

    public Fps() { cb = OnEvent; }

    void InitProps()
    {
        Marshal.Copy(new byte[1024], 0, props, 1024);
        Marshal.WriteInt32(props, 0, 1024);      // Wnode.BufferSize
        Marshal.WriteInt32(props, 40, 1);        // ClientContext = QPC
        Marshal.WriteInt32(props, 44, 0x20000);  // WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(props, 48, 64);       // BufferSize (Ko)
        Marshal.WriteInt32(props, 64, 0x110);    // REAL_TIME_MODE | USE_MS_FLUSH_TIMER
        Marshal.WriteInt32(props, 68, 250);      // FlushTimer (ms)
        Marshal.WriteInt32(props, 116, 120);     // LoggerNameOffset
    }

    public bool Start(int gamePid)
    {
        pid = gamePid; n184 = n215 = 0;
        props = Marshal.AllocHGlobal(1024); InitProps();
        int rc = Native.StartTraceW(out session, Name, props);
        if (rc == 183) { Native.ControlTraceW(0, Name, props, 1); InitProps(); rc = Native.StartTraceW(out session, Name, props); } // reste d'un plantage
        if (rc != 0) { Marshal.FreeHGlobal(props); props = IntPtr.Zero; return false; }
        Native.EnableTraceEx2(session, ref DxgKrnl, 1, 4, 0x8000000, 0, 0, IntPtr.Zero); // mot-clé Present
        logfile = Marshal.AllocHGlobal(448); Marshal.Copy(new byte[448], 0, logfile, 448);
        name = Marshal.StringToHGlobalUni(Name);
        Marshal.WriteIntPtr(logfile, 8, name);                                    // LoggerName
        Marshal.WriteInt32(logfile, 28, 0x10000100);                              // REAL_TIME | EVENT_RECORD
        Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(cb)); // EventRecordCallback
        trace = Native.OpenTraceW(logfile);
        if (trace == ulong.MaxValue) { Stop(); return false; }
        th = new Thread(delegate() { Native.ProcessTrace(new[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        th.Start();
        sw.Restart();
        return true;
    }

    void OnEvent(IntPtr r)
    {
        if (Marshal.ReadInt32(r, 12) != pid) return; // EVENT_HEADER.ProcessId
        short id = Marshal.ReadInt16(r, 40);          // EventDescriptor.Id
        if (id == 184) Interlocked.Increment(ref n184); else if (id == 215) Interlocked.Increment(ref n215);
    }

    public float Take()
    {
        double s = sw.Elapsed.TotalSeconds; sw.Restart();
        int a = Interlocked.Exchange(ref n184, 0), b = Interlocked.Exchange(ref n215, 0);
        return s > 0.2 ? (float)(Math.Max(a, b) / s) : -1;
    }

    public void Stop()
    {
        if (props != IntPtr.Zero) Native.ControlTraceW(session, null, props, 1);
        if (trace != 0 && trace != ulong.MaxValue) Native.CloseTrace(trace);
        if (th != null) th.Join(2000);
        if (props != IntPtr.Zero) Marshal.FreeHGlobal(props);
        if (logfile != IntPtr.Zero) Marshal.FreeHGlobal(logfile);
        if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
        props = logfile = name = IntPtr.Zero; trace = 0; th = null;
    }
}

// Réglages : reglages.ini à côté de l'exe, modifiables depuis la fenêtre Réglages.
sealed class Reglages
{
    [Category("1. Alertes"), DisplayName("Température CPU max (°C)")] public int AlerteCpuC { get; set; }
    [Category("1. Alertes"), DisplayName("Température GPU max (°C)")] public int AlerteGpuC { get; set; }
    [Category("1. Alertes"), DisplayName("RAM max (%)")] public int AlerteRam { get; set; }
    [Category("2. Mode jeu"), DisplayName("Jeux"), Description("Noms (ou morceaux de nom) de programmes, séparés par ;. Les jeux en plein écran exclusif sont détectés tout seuls.")]
    public string Jeux { get; set; }
    [Category("2. Mode jeu"), DisplayName("Applis mises en pause"), Description("Programmes gelés pendant le jeu puis relancés, séparés par ; (ex. OneDrive;Steam). Attention : une appli en pause ne répond plus (ne pas mettre Discord si tu parles en vocal).")]
    public string AppsEnPause { get; set; }
    [Category("2. Mode jeu"), DisplayName("Services arrêtés"), Description("Services Windows arrêtés pendant le jeu puis relancés, séparés par ;. Admin requis.")]
    public string Services { get; set; }
    [Category("2. Mode jeu"), DisplayName("Vider la RAM en veille"), Description("Libère le cache mémoire au lancement du jeu. Admin requis.")]
    public bool ViderRam { get; set; }
    [Category("2. Mode jeu"), DisplayName("Forcer la carte NVIDIA"), Description("Enregistre le jeu en « Hautes performances » dans Windows (pris en compte au prochain lancement du jeu).")]
    public bool ForcerNvidia { get; set; }
    [Category("2. Mode jeu"), DisplayName("Overlay en jeu"), Description("Petit cadre FPS / CPU / GPU en haut à gauche (jeux en fenêtré ou plein écran fenêtré).")]
    public bool Overlay { get; set; }

    public Reglages()
    {
        AlerteCpuC = 95; AlerteGpuC = 87; AlerteRam = 92;
        Jeux = "GTAProcess;GTA5;javaw"; AppsEnPause = "OneDrive"; Services = "SysMain;WSearch;DiagTrack";
        ViderRam = ForcerNvidia = Overlay = true;
    }

    public static Reglages Load(string file)
    {
        var r = new Reglages();
        try
        {
            if (File.Exists(file))
                foreach (var l in File.ReadAllLines(file))
                {
                    int i = l.IndexOf('=');
                    var p = i > 0 ? typeof(Reglages).GetProperty(l.Substring(0, i).Trim()) : null;
                    if (p != null) try { p.SetValue(r, Convert.ChangeType(l.Substring(i + 1).Trim(), p.PropertyType, CultureInfo.InvariantCulture), null); } catch { }
                }
            else r.Save(file);
        }
        catch { }
        return r;
    }

    public void Save(string file)
    {
        var sb = new StringBuilder();
        foreach (var p in typeof(Reglages).GetProperties()) sb.Append(p.Name).Append('=').Append(Convert.ToString(p.GetValue(this, null), CultureInfo.InvariantCulture)).Append("\r\n");
        File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
    }

    public static string[] List(string s)
    {
        var l = new List<string>();
        foreach (var x in (s ?? "").Split(';')) { var t = x.Trim().ToLowerInvariant(); if (t.EndsWith(".exe")) t = t.Substring(0, t.Length - 4); if (t.Length > 0) l.Add(t); }
        return l.ToArray();
    }
}

sealed class App : ApplicationContext
{
    const int Interval = 2000, IntervalBattery = 5000, IntervalFast = 1000, N = 300;
    static readonly Guid BestPerf = new Guid("ded574b5-45a0-4f42-8737-46345c09c238"); // mode d'alimentation "Performances optimales"

    readonly string dir = AppDomain.CurrentDomain.BaseDirectory;
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Font iconFont = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly ToolStripMenuItem autoStart;
    readonly int myPid = Process.GetCurrentProcess().Id;
    public Reglages Cfg;
    string[] games;

    // Mesures (-1 = inconnu)
    long pIdle, pKernel, pUser;
    public float Cpu, Ram, CpuT = -1, Gpu = -1, GpuT = -1, FpsVal = -1;
    public bool CpuThrottle, GpuThrottle, Battery;
    public double RamUsedGo, RamTotGo;
    public readonly float[] HCpu = new float[N], HGpu = new float[N], HCpuT = new float[N], HGpuT = new float[N];
    public int Head;
    int tick;
    PerformanceCounter[] zones; PerformanceCounter perfLimit;
    IntPtr nvDev; int nvState; // 0 pas essayé, 1 ok, -1 absent

    // Historique par minute
    DateTime lastWrite = DateTime.Now;
    float sCpu, mCpu, mCpuT = -1, sRam, sGpu, mGpuT = -1; int cnt, cntGpu;
    readonly Dictionary<string, DateTime> lastAlert = new Dictionary<string, DateTime>();

    // Mode jeu
    IntPtr gameH; public string GameName; Guid oldOverlay; bool overlaySet;
    readonly List<string> stopped = new List<string>();
    readonly List<IntPtr> paused = new List<IntPtr>();
    readonly object svcLock = new object();
    readonly bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    Fps fps; Overlay overlay;
    DateTime sStart; float sFps, sCpuTMax, sGpuTMax; int sFpsN, sThrottle, sAlerts;

    StatsForm form; Form settings;
    int iconVal = -2; bool iconGame; IntPtr iconHandle;

    public App()
    {
        for (int i = 0; i < N; i++) { HCpu[i] = HGpu[i] = HCpuT[i] = HGpuT[i] = -1; }
        Native.GetSystemTimes(out pIdle, out pKernel, out pUser);
        try
        {
            var names = new PerformanceCounterCategory("Thermal Zone Information").GetInstanceNames();
            zones = new PerformanceCounter[names.Length];
            for (int i = 0; i < names.Length; i++) zones[i] = new PerformanceCounter("Thermal Zone Information", "High Precision Temperature", names[i], true);
        }
        catch { zones = null; }
        LoadCfg();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Afficher les statistiques", null, delegate { ShowForm(); });
        menu.Items.Add("Historique (graphiques)", null, delegate { History.Open(HistDir); });
        menu.Items.Add("Vider la RAM", null, delegate { CleanRam(); });
        menu.Items.Add("Réglages", null, delegate { ShowSettings(); });
        autoStart = new ToolStripMenuItem("Démarrer avec Windows", null, delegate { ToggleAutoStart(); });
        autoStart.Checked = RunHidden("schtasks", "/Query /TN MoniteurJeu") == 0;
        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, delegate { Quit(); });
        tray.ContextMenuStrip = menu;
        tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowForm(); };
        tray.Visible = true;

        // Quoi qu'il arrive, on rend la main : applis dégelées, services relancés, mode d'alimentation remis.
        SystemEvents.SessionEnding += delegate { Cleanup(); };
        Application.ThreadException += delegate { Cleanup(); };
        timer.Interval = Interval;
        timer.Tick += delegate { Tick(); };
        timer.Start();
        Tick();
        Native.TrimMemory();
    }

    string CfgFile { get { return Path.Combine(dir, "reglages.ini"); } }
    public string HistDir { get { return Path.Combine(dir, "historique"); } }

    void LoadCfg()
    {
        bool first = !File.Exists(CfgFile);
        Cfg = Reglages.Load(CfgFile);
        string old = Path.Combine(dir, "jeux.txt"); // ancienne liste de jeux : reprise une fois puis supprimée
        if (first && File.Exists(old))
            try
            {
                var l = new List<string>();
                foreach (var s in File.ReadAllLines(old)) { var t = s.Trim(); if (t.Length > 0 && !t.StartsWith("#")) l.Add(t); }
                Cfg.Jeux = string.Join(";", l); Cfg.Save(CfgFile); File.Delete(old);
            }
            catch { }
        games = Reglages.List(Cfg.Jeux);
    }

    void Tick()
    {
        tick++;
        bool inGame = gameH != IntPtr.Zero, visible = form != null;
        bool deep = visible || inGame; // mesures complètes seulement si quelqu'un regarde ou en jeu
        Battery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        timer.Interval = deep ? IntervalFast : Battery ? IntervalBattery : Interval;

        long idle, kernel, user;
        Native.GetSystemTimes(out idle, out kernel, out user);
        long total = (kernel - pKernel) + (user - pUser), busy = total - (idle - pIdle);
        if (total > 0) Cpu = 100f * busy / total;
        pIdle = idle; pKernel = kernel; pUser = user;

        var m = new Native.MEMSTAT(); m.len = (uint)Marshal.SizeOf(m);
        if (Native.GlobalMemoryStatusEx(ref m))
        {
            Ram = m.load;
            RamTotGo = m.totPhys / 1073741824.0; RamUsedGo = (m.totPhys - m.availPhys) / 1073741824.0;
        }

        if (zones != null && (deep || tick % 5 == 1))
        {
            float best = -1;
            try { foreach (var z in zones) best = Math.Max(best, z.NextValue() / 10f - 273.15f); } catch { }
            CpuT = best > 0 ? best : -1;
        }

        if (deep) ReadDeep(); else { Gpu = GpuT = -1; CpuThrottle = GpuThrottle = false; }
        FpsVal = fps != null ? fps.Take() : -1;

        CheckGame();
        if (gameH != IntPtr.Zero) // stats de la session de jeu
        {
            if (FpsVal >= 0) { sFps += FpsVal; sFpsN++; }
            sCpuTMax = Math.Max(sCpuTMax, CpuT); sGpuTMax = Math.Max(sGpuTMax, GpuT);
            if (CpuThrottle || GpuThrottle) sThrottle++;
        }

        HCpu[Head] = Cpu; HGpu[Head] = Gpu; HCpuT[Head] = CpuT; HGpuT[Head] = GpuT;
        Head = (Head + 1) % N;

        sCpu += Cpu; mCpu = Math.Max(mCpu, Cpu); mCpuT = Math.Max(mCpuT, CpuT); sRam += Ram; cnt++;
        if (Gpu >= 0) { sGpu += Gpu; cntGpu++; mGpuT = Math.Max(mGpuT, GpuT); }
        if ((DateTime.Now - lastWrite).TotalSeconds >= 60) WriteHistory();

        if (CpuT >= Cfg.AlerteCpuC) Alert("cpu", "Processeur très chaud : " + (int)CpuT + " °C");
        if (GpuT >= Cfg.AlerteGpuC) Alert("gpu", "Carte graphique très chaude : " + (int)GpuT + " °C");
        if (Ram >= Cfg.AlerteRam) Alert("ram", "Mémoire presque pleine : " + (int)Ram + " %");
        if (CpuThrottle) Alert("bcpu", "Le processeur est bridé (chaleur ou alimentation) : il ralentit pour se protéger.");
        if (GpuThrottle) Alert("bgpu", "La carte graphique est bridée (chaleur ou alimentation) : il ralentit pour se protéger.");

        UpdateIcon();
        if (visible) form.Invalidate();
        if (overlay != null) overlay.Invalidate();
    }

    void ReadDeep()
    {
        if (nvState == 0)
        {
            try { nvState = Native.nvmlInit_v2() == 0 && Native.nvmlDeviceGetHandleByIndex_v2(0, out nvDev) == 0 ? 1 : -1; }
            catch { nvState = -1; } // pas de carte NVIDIA
            try { perfLimit = new PerformanceCounter("Processor Information", "% Performance Limit", "_Total", true); } catch { }
        }
        if (perfLimit != null) try { CpuThrottle = perfLimit.NextValue() < 70; } catch { } // shortcut: seuil fixe 70 %, à affiner si fausses alertes
        if (nvState != 1) return;
        uint t; Native.NvUtil u; ulong why = 0;
        GpuT = Native.nvmlDeviceGetTemperature(nvDev, 0, out t) == 0 ? (float)t : -1;
        Gpu = Native.nvmlDeviceGetUtilizationRates(nvDev, out u) == 0 ? (float)u.gpu : -1;
        try { Native.nvmlDeviceGetCurrentClocksThrottleReasons(nvDev, out why); } catch { }
        GpuThrottle = (why & 0xE8) != 0; // HW slowdown | SW thermal | HW thermal | HW power brake
    }

    void WriteHistory()
    {
        try
        {
            Directory.CreateDirectory(HistDir);
            string f = Path.Combine(HistDir, DateTime.Now.ToString("yyyy-MM") + ".csv");
            bool header = !File.Exists(f);
            using (var w = new StreamWriter(f, true, Encoding.UTF8))
            {
                if (header) w.WriteLine("heure;cpu_moy_%;cpu_max_%;cpu_temp_max;ram_moy_%;gpu_moy_%;gpu_temp_max;jeu");
                w.WriteLine(string.Join(";", lastWrite.ToString("yyyy-MM-dd HH:mm"),
                    (int)(sCpu / cnt), (int)mCpu, mCpuT < 0 ? "" : ((int)mCpuT).ToString(), (int)(sRam / cnt),
                    cntGpu == 0 ? "" : ((int)(sGpu / cntGpu)).ToString(), mGpuT < 0 ? "" : ((int)mGpuT).ToString(), GameName ?? ""));
            }
        }
        catch { }
        lastWrite = DateTime.Now; sCpu = mCpu = sRam = sGpu = 0; mCpuT = mGpuT = -1; cnt = cntGpu = 0;
    }

    void Alert(string kind, string text)
    {
        DateTime last;
        if (lastAlert.TryGetValue(kind, out last) && (DateTime.Now - last).TotalMinutes < 10) return;
        lastAlert[kind] = DateTime.Now;
        if (gameH != IntPtr.Zero) sAlerts++;
        tray.ShowBalloonTip(5000, "MoniteurJeu", text, ToolTipIcon.Warning);
    }

    // ---------- Mode jeu ----------
    void CheckGame()
    {
        if (gameH != IntPtr.Zero)
        {
            uint code;
            if (!Native.GetExitCodeProcess(gameH, out code) || code != 259) EndGame(); // 259 = encore en cours
            return;
        }
        uint pid;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
        if (pid == 0 || pid == myPid) return;
        string path = Native.ExePath(pid);
        if (path == null) return;
        string low = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        bool match = false;
        foreach (var g in games) if (low.Contains(g)) { match = true; break; }
        int state;
        if (!match && Native.SHQueryUserNotificationState(out state) == 0 && state == 3) match = true; // plein écran Direct3D
        if (match) StartGame(pid, path);
    }

    void StartGame(uint pid, string path)
    {
        // SYNCHRONIZE | QUERY_LIMITED | SET_INFORMATION ; sans SET_INFORMATION si le jeu est en admin et pas nous
        gameH = Native.OpenProcess(0x100000 | 0x1000 | 0x0200, false, pid);
        if (gameH == IntPtr.Zero) gameH = Native.OpenProcess(0x100000 | 0x1000, false, pid);
        if (gameH == IntPtr.Zero) return;
        GameName = Path.GetFileName(path);
        sStart = DateTime.Now; sFps = 0; sFpsN = sThrottle = sAlerts = 0; sCpuTMax = sGpuTMax = -1;
        Native.SetPriorityClass(gameH, 0x80); // HIGH_PRIORITY_CLASS

        // N'a d'effet que sur le plan "Utilisation normale" ; sur "Haute performance" c'est déjà au max.
        if (Native.PowerGetActualOverlayScheme(out oldOverlay) == 0 && oldOverlay != BestPerf)
            overlaySet = Native.PowerSetActiveOverlayScheme(BestPerf) == 0;

        if (Cfg.ForcerNvidia)
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                    if (k.GetValue(path) == null) k.SetValue(path, "GpuPreference=2;");
            }
            catch { }

        foreach (var name in Reglages.List(Cfg.AppsEnPause))
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id != pid)
                {
                    IntPtr h = Native.OpenProcess(0x0800, false, (uint)p.Id); // PROCESS_SUSPEND_RESUME
                    if (h != IntPtr.Zero) { if (Native.NtSuspendProcess(h) == 0) paused.Add(h); else Native.CloseHandle(h); }
                }
                p.Dispose();
            }

        if (admin)
        {
            fps = new Fps();
            if (!fps.Start((int)pid)) fps = null;
            bool purge = Cfg.ViderRam; string[] svc = Reglages.List(Cfg.Services);
            ThreadPool.QueueUserWorkItem(delegate { if (purge) Native.PurgeStandby(); StopServices(svc); });
        }
        if (Cfg.Overlay) { overlay = new Overlay(this); overlay.Show(); }

        tray.ShowBalloonTip(3000, "Mode jeu activé", GameName + " : priorité haute" + (paused.Count > 0 ? ", " + paused.Count + " appli(s) en pause" : "")
            + (admin ? ", services en pause" + (Cfg.ViderRam ? ", RAM vidée" : "") : ""), ToolTipIcon.Info);
    }

    void EndGame()
    {
        Native.CloseHandle(gameH); gameH = IntPtr.Zero;
        string name = GameName; GameName = null;
        Cleanup();

        var d = DateTime.Now - sStart;
        string fpsTxt = sFpsN > 0 ? ((int)(sFps / sFpsN)).ToString() : "";
        string sum = (int)d.TotalMinutes + " min" + (fpsTxt != "" ? ", " + fpsTxt + " FPS moy." : "")
            + (sCpuTMax > 0 ? ", CPU max " + (int)sCpuTMax + "°C" : "") + (sGpuTMax > 0 ? ", GPU max " + (int)sGpuTMax + "°C" : "")
            + (sThrottle > 0 ? ", bridé " + sThrottle + " s" : "") + (sAlerts > 0 ? ", " + sAlerts + " alerte(s)" : "");
        tray.ShowBalloonTip(6000, name + " fermé", sum, ToolTipIcon.Info);
        try
        {
            Directory.CreateDirectory(HistDir);
            string f = Path.Combine(HistDir, "sessions.csv");
            bool header = !File.Exists(f);
            using (var w = new StreamWriter(f, true, Encoding.UTF8))
            {
                if (header) w.WriteLine("debut;duree_min;jeu;fps_moy;cpu_temp_max;gpu_temp_max;bride_s;alertes");
                w.WriteLine(string.Join(";", sStart.ToString("yyyy-MM-dd HH:mm"), (int)d.TotalMinutes, name, fpsTxt,
                    sCpuTMax > 0 ? ((int)sCpuTMax).ToString() : "", sGpuTMax > 0 ? ((int)sGpuTMax).ToString() : "", sThrottle, sAlerts));
            }
        }
        catch { }
        Native.TrimMemory();
    }

    // Remet tout comme avant le jeu (aussi appelé en quittant, à la fermeture de session ou sur erreur).
    void Cleanup()
    {
        if (fps != null) { fps.Stop(); fps = null; FpsVal = -1; }
        if (overlay != null) { overlay.Close(); overlay.Dispose(); overlay = null; }
        foreach (var h in paused) { Native.NtResumeProcess(h); Native.CloseHandle(h); }
        paused.Clear();
        if (overlaySet) { Native.PowerSetActiveOverlayScheme(oldOverlay); overlaySet = false; }
        ThreadPool.QueueUserWorkItem(delegate { RestartServices(); });
    }

    void StopServices(string[] names)
    {
        lock (svcLock)
            foreach (var n in names)
                try
                {
                    using (var sc = new ServiceController(n))
                    {
                        if (sc.Status != ServiceControllerStatus.Running || !sc.CanStop) continue;
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                        stopped.Add(n);
                    }
                }
                catch { }
    }

    void RestartServices()
    {
        lock (svcLock)
        {
            foreach (var n in stopped)
                try { using (var sc = new ServiceController(n)) sc.Start(); } catch { }
            stopped.Clear();
        }
    }

    // ---------- Interface ----------
    void UpdateIcon()
    {
        bool game = gameH != IntPtr.Zero;
        int v = (int)Math.Round(game && GpuT >= 0 ? GpuT : Cpu);
        if (v > 99) v = 99;

        string tip = "CPU " + (int)Cpu + "%" + (CpuT >= 0 ? " " + (int)CpuT + "°C" : "") + " | RAM " + (int)Ram + "%"
            + (Gpu >= 0 ? "\nGPU " + (int)Gpu + "% " + (int)GpuT + "°C" : "") + (FpsVal >= 0 ? " " + (int)FpsVal + "fps" : "")
            + (game ? "\nJeu : " + GameName : "");
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

        if (v == iconVal && game == iconGame) return; // redessine seulement si le chiffre change
        iconVal = v; iconGame = game;
        using (var bmp = new Bitmap(16, 16))
        {
            using (var g = Graphics.FromImage(bmp))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.Clear(game ? Color.FromArgb(190, 30, 30) : v >= 85 ? Color.FromArgb(200, 120, 0) : Color.FromArgb(25, 25, 25));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                g.DrawString(v.ToString(), iconFont, Brushes.White, new RectangleF(-2, 0, 20, 16), sf);
            }
            IntPtr h = bmp.GetHicon();
            tray.Icon = Icon.FromHandle(h);
            if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
            iconHandle = h;
        }
    }

    void ShowForm()
    {
        if (form != null) { form.Activate(); return; }
        form = new StatsForm(this);
        form.FormClosed += delegate { form.Dispose(); form = null; Native.TrimMemory(); };
        form.Show();
        Tick();
    }

    void CleanRam()
    {
        if (!admin) { tray.ShowBalloonTip(4000, "MoniteurJeu", "Lance MoniteurJeu en administrateur pour vider la RAM.", ToolTipIcon.Warning); return; }
        ThreadPool.QueueUserWorkItem(delegate
        {
            var m = new Native.MEMSTAT(); m.len = (uint)Marshal.SizeOf(m);
            Native.GlobalMemoryStatusEx(ref m); ulong before = m.availPhys;
            Native.PurgeStandby();
            Native.GlobalMemoryStatusEx(ref m);
            double freed = ((double)m.availPhys - before) / 1073741824.0;
            tray.ShowBalloonTip(3000, "RAM vidée", (freed > 0 ? freed : 0).ToString("0.0") + " Go libérés", ToolTipIcon.Info);
        });
    }

    void ShowSettings()
    {
        if (settings != null) { settings.Activate(); return; }
        var edit = Reglages.Load(CfgFile);
        settings = new Form { Text = "MoniteurJeu - Réglages", ClientSize = new Size(460, 420), StartPosition = FormStartPosition.CenterScreen, MinimizeBox = false, MaximizeBox = false };
        var grid = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = edit, ToolbarVisible = false, PropertySort = PropertySort.Categorized };
        var ok = new Button { Text = "Enregistrer", Dock = DockStyle.Bottom, Height = 32 };
        ok.Click += delegate { edit.Save(CfgFile); Cfg = edit; games = Reglages.List(Cfg.Jeux); settings.Close(); };
        settings.Controls.Add(grid); settings.Controls.Add(ok);
        settings.FormClosed += delegate { settings.Dispose(); settings = null; Native.TrimMemory(); };
        settings.Show();
    }

    void ToggleAutoStart()
    {
        if (autoStart.Checked)
        {
            RunHidden("schtasks", "/Delete /TN MoniteurJeu /F");
        }
        else
        {
            // Tâche planifiée (et pas la clé Run) : démarre en admin sans fenêtre UAC, aussi sur batterie.
            string user = System.Security.SecurityElement.Escape(System.Security.Principal.WindowsIdentity.GetCurrent().Name);
            string exe = System.Security.SecurityElement.Escape(Application.ExecutablePath);
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                + "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>"
                + "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
                + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
                + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>"
                + "<Actions Context=\"Author\"><Exec><Command>" + exe + "</Command></Exec></Actions></Task>";
            string tmp = Path.Combine(Path.GetTempPath(), "MoniteurJeu_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            RunHidden("schtasks", "/Create /TN MoniteurJeu /XML \"" + tmp + "\" /F");
            File.Delete(tmp);
        }
        autoStart.Checked = RunHidden("schtasks", "/Query /TN MoniteurJeu") == 0;
        if (!autoStart.Checked && !admin)
            tray.ShowBalloonTip(4000, "MoniteurJeu", "Lance MoniteurJeu en administrateur pour activer le démarrage automatique.", ToolTipIcon.Warning);
    }

    static int RunHidden(string file, string args)
    {
        try
        {
            using (var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                p.StandardOutput.ReadToEnd(); p.WaitForExit();
                return p.ExitCode;
            }
        }
        catch { return -1; }
    }

    void Quit()
    {
        timer.Stop();
        if (gameH != IntPtr.Zero) { Native.CloseHandle(gameH); gameH = IntPtr.Zero; }
        Cleanup();
        RestartServices(); // synchrone : les services sont relancés avant de quitter
        if (cnt > 0) WriteHistory();
        tray.Visible = false;
        ExitThread();
    }
}

// Petit cadre en jeu : transparent aux clics, ne prend jamais le focus, redessiné 1x/s.
sealed class Overlay : Form
{
    readonly App app;
    readonly Font f = new Font("Consolas", 10f, FontStyle.Bold);

    public Overlay(App a)
    {
        app = a;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; Location = new Point(Screen.PrimaryScreen.Bounds.Left + 8, Screen.PrimaryScreen.Bounds.Top + 8);
        ClientSize = new Size(140, 54); BackColor = Color.Black; Opacity = 0.75; DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000; return cp; } // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawString("FPS " + (app.FpsVal >= 0 ? ((int)app.FpsVal).ToString() : "-"), f, Brushes.White, 4, 2);
        g.DrawString("CPU " + (int)app.Cpu + "% " + (app.CpuT >= 0 ? (int)app.CpuT + "°" : ""), f, app.CpuThrottle ? Brushes.OrangeRed : Brushes.LightSkyBlue, 4, 18);
        g.DrawString("GPU " + (app.Gpu >= 0 ? (int)app.Gpu + "% " + (int)app.GpuT + "°" : "-"), f, app.GpuThrottle ? Brushes.OrangeRed : Brushes.LightGreen, 4, 34);
    }

    protected override void Dispose(bool disposing) { if (disposing) f.Dispose(); base.Dispose(disposing); }
}

// Page d'historique : générée à la demande à partir des CSV (aucun coût tant qu'on ne l'ouvre pas).
static class History
{
    public static void Open(string dir)
    {
        Directory.CreateDirectory(dir);
        var data = new StringBuilder("[");
        var since = DateTime.Now.AddDays(-8);
        var files = Directory.GetFiles(dir, "????-??.csv"); Array.Sort(files);
        foreach (var f in files)
            foreach (var l in ReadLines(f))
            {
                var c = l.Split(';'); DateTime t;
                if (c.Length < 7 || !DateTime.TryParseExact(c[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) || t < since) continue;
                // [minute, cpu, cpu°C, ram, gpu, gpu°C, en jeu]
                data.Append('[').Append((long)(t - new DateTime(1970, 1, 1)).TotalMinutes).Append(',').Append(V(c[1])).Append(',').Append(V(c[3])).Append(',')
                    .Append(V(c[4])).Append(',').Append(V(c[5])).Append(',').Append(V(c[6])).Append(',').Append(c.Length > 7 && c[7].Length > 0 ? 1 : 0).Append("],");
            }
        data.Append(']');

        var rows = new StringBuilder();
        var sess = ReadLines(Path.Combine(dir, "sessions.csv"));
        for (int i = sess.Count - 1, k = 0; i >= 0 && k < 50; i--, k++)
        {
            rows.Append("<tr>");
            foreach (var c in sess[i].Split(';')) rows.Append("<td>").Append(System.Security.SecurityElement.Escape(c)).Append("</td>");
            rows.Append("</tr>");
        }

        string html = Page.Replace("/*D*/", data.ToString()).Replace("<!--S-->", rows.Length > 0 ? rows.ToString() : "<tr><td colspan=8>Aucune session pour l'instant</td></tr>");
        string file = Path.Combine(dir, "rapport.html");
        File.WriteAllText(file, html, Encoding.UTF8);
        Process.Start(file);
    }

    static string V(string s) { int v; return int.TryParse(s, out v) ? v.ToString() : "null"; }

    static List<string> ReadLines(string f)
    {
        var l = new List<string>();
        try
        {
            if (File.Exists(f))
                using (var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
                {
                    r.ReadLine(); // en-tête
                    string s; while ((s = r.ReadLine()) != null) if (s.Length > 0) l.Add(s);
                }
        }
        catch { }
        return l;
    }

    const string Page = @"<!doctype html><html lang=fr><meta charset=utf-8><title>Historique MoniteurJeu</title>
<style>body{background:#18181c;color:#ddd;font:14px 'Segoe UI',sans-serif;margin:20px;max-width:1100px}h1{font-size:20px}h2{font-size:15px;margin:26px 0 6px}
canvas{width:100%;height:240px;background:#202026;border-radius:6px;display:block}.k{color:#aaa;font-size:12px;margin-bottom:6px}.k b{display:inline-block;width:10px;height:10px;margin:0 4px 0 12px}
table{border-collapse:collapse;font-size:13px}td,th{padding:4px 10px;border-bottom:1px solid #333;text-align:right}th{color:#aaa;font-weight:600}</style>
<h1>Historique MoniteurJeu</h1>
<h2>Dernières 24 heures (par minute)</h2><div class=k id=k1></div><canvas id=c1></canvas>
<h2>7 derniers jours (moyenne %, max °C, temps de jeu en heures ×10)</h2><div class=k id=k2></div><canvas id=c2></canvas>
<h2>Sessions de jeu</h2><table><tr><th>Début<th>Durée (min)<th>Jeu<th>FPS moy.<th>CPU max °C<th>GPU max °C<th>Bridé (s)<th>Alertes</tr><!--S--></table>
<script>
const D=/*D*/;
const C=['#50a0ff','#5ad278','#ffaa3c','#f05050','#999','#c080ff'];
function key(id,names){document.getElementById(id).innerHTML=names.map((n,i)=>'<b style=background:'+C[i]+'></b>'+n).join('')}
function draw(id,xs,ys,label,gap){const c=document.getElementById(id),r=devicePixelRatio||1,w=c.clientWidth,h=c.clientHeight;c.width=w*r;c.height=h*r;
 const g=c.getContext('2d');g.scale(r,r);g.font='11px Segoe UI';const L=30,B=18,x0=xs[0],x1=xs[xs.length-1];
 const X=v=>L+(w-L-10)*(x1>x0?(v-x0)/(x1-x0):.5),Y=v=>h-B-(h-B-8)*Math.min(v,100)/100;
 g.strokeStyle='#333';g.fillStyle='#888';for(let v=0;v<=100;v+=25){g.beginPath();g.moveTo(L,Y(v));g.lineTo(w-10,Y(v));g.stroke();g.fillText(v,4,Y(v)+4)}
 let last=-1e9;xs.forEach((x,i)=>{const t=label(x,i);if(t&&X(x)-last>40){g.fillText(t,X(x)-10,h-4);last=X(x)}});
 ys.forEach((s,j)=>{g.strokeStyle=C[j];g.lineWidth=1.5;g.beginPath();let on=false;
  s.forEach((v,i)=>{if(v==null||(i&&xs[i]-xs[i-1]>gap)){on=false;if(v==null)return}if(on)g.lineTo(X(xs[i]),Y(v));else{g.moveTo(X(xs[i]),Y(v));on=true}});g.stroke()})}
const now=D.length?D[D.length-1][0]:0,d1=D.filter(r=>r[0]>now-1440);
const hh=m=>{const d=new Date(m*60000);return d.getUTCMinutes()<2?d.getUTCHours()+'h':''};
key('k1',['CPU %','GPU %','CPU °C','GPU °C','RAM %']);
if(d1.length)draw('c1',d1.map(r=>r[0]),[1,4,2,5,3].map(k=>d1.map(r=>r[k])),hh,5);
const days={};D.forEach(r=>{const k=Math.floor(r[0]/1440);(days[k]=days[k]||[]).push(r)});
const ks=Object.keys(days).map(Number).sort((a,b)=>a-b).slice(-7);
const avg=(a,k)=>{const v=a.map(r=>r[k]).filter(x=>x!=null);return v.length?v.reduce((s,x)=>s+x,0)/v.length:null};
const max=(a,k)=>{const v=a.map(r=>r[k]).filter(x=>x!=null);return v.length?Math.max(...v):null};
key('k2',['CPU % moy.','GPU % moy.','CPU °C max','GPU °C max','RAM % moy.','Jeu (h ×10)']);
const J=['dim','lun','mar','mer','jeu','ven','sam'];
if(ks.length)draw('c2',ks,[ks.map(k=>avg(days[k],1)),ks.map(k=>avg(days[k],4)),ks.map(k=>max(days[k],2)),ks.map(k=>max(days[k],5)),ks.map(k=>avg(days[k],3)),
 ks.map(k=>days[k].filter(r=>r[6]).length/6)],k=>{const d=new Date(k*86400000);return J[d.getUTCDay()]+' '+d.getUTCDate()},1);
</script>";
}

sealed class StatsForm : Form
{
    readonly App app;
    readonly Font big = new Font("Segoe UI", 11f), small = new Font("Segoe UI", 8.5f);
    static readonly Color Bg = Color.FromArgb(24, 24, 28), Grid = Color.FromArgb(55, 55, 62);
    static readonly Color CCpu = Color.FromArgb(80, 160, 255), CGpu = Color.FromArgb(90, 210, 120),
        CCpuT = Color.FromArgb(255, 170, 60), CGpuT = Color.FromArgb(240, 80, 80);

    public StatsForm(App a)
    {
        app = a;
        Text = "MoniteurJeu";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(380, 290);
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
        BackColor = Bg; ForeColor = Color.White;
        DoubleBuffered = true; TopMost = true; ShowInTaskbar = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int y = 10;
        Line(g, ref y, CCpu, "CPU", (int)app.Cpu + " %" + (app.CpuT >= 0 ? "   " + (int)app.CpuT + " °C" : "") + (app.CpuThrottle ? "   BRIDÉ" : ""));
        Line(g, ref y, Color.Silver, "RAM", (int)app.Ram + " %   (" + app.RamUsedGo.ToString("0.0") + " / " + app.RamTotGo.ToString("0.0") + " Go)" + (app.Battery ? "   batterie" : ""));
        Line(g, ref y, CGpu, "GPU", (app.Gpu >= 0 ? (int)app.Gpu + " %   " + (int)app.GpuT + " °C" : "non disponible") + (app.GpuThrottle ? "   BRIDÉ" : ""));
        Line(g, ref y, app.GameName != null ? CGpuT : Color.Gray, "Jeu", app.GameName != null ? app.GameName + (app.FpsVal >= 0 ? "   " + (int)app.FpsVal + " FPS" : "") : "mode jeu en attente");

        var r = new Rectangle(10, y + 6, ClientSize.Width - 20, ClientSize.Height - y - 34);
        using (var p = new Pen(Grid))
        {
            g.DrawRectangle(p, r);
            for (int i = 1; i < 4; i++) g.DrawLine(p, r.Left, r.Top + r.Height * i / 4, r.Right, r.Top + r.Height * i / 4);
        }
        Series(g, r, app.HCpu, CCpu); Series(g, r, app.HGpu, CGpu); Series(g, r, app.HCpuT, CCpuT); Series(g, r, app.HGpuT, CGpuT);

        int x = 10, ly = ClientSize.Height - 20;
        x = Legend(g, x, ly, CCpu, "CPU %"); x = Legend(g, x, ly, CGpu, "GPU %");
        x = Legend(g, x, ly, CCpuT, "CPU °C"); Legend(g, x, ly, CGpuT, "GPU °C");
    }

    void Line(Graphics g, ref int y, Color c, string label, string value)
    {
        using (var b = new SolidBrush(c)) g.DrawString(label, big, b, 10, y);
        g.DrawString(value, big, Brushes.White, 60, y);
        y += 24;
    }

    int Legend(Graphics g, int x, int y, Color c, string t)
    {
        using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y + 4, 10, 10);
        g.DrawString(t, small, Brushes.Silver, x + 13, y);
        return x + 20 + (int)g.MeasureString(t, small).Width;
    }

    // Courbe des 300 dernières mesures, échelle 0-100 (% ou °C)
    void Series(Graphics g, Rectangle r, float[] h, Color c)
    {
        int n = h.Length;
        using (var p = new Pen(c, 1.5f))
        {
            PointF prev = PointF.Empty; bool has = false;
            for (int i = 0; i < n; i++)
            {
                float v = h[(app.Head + i) % n];
                if (v < 0) { has = false; continue; }
                var pt = new PointF(r.Left + (float)r.Width * i / (n - 1), r.Bottom - r.Height * Math.Min(v, 100) / 100f);
                if (has) g.DrawLine(p, prev, pt);
                prev = pt; has = true;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { big.Dispose(); small.Dispose(); }
        base.Dispose(disposing);
    }
}
