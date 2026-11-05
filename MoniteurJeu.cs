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

    Dashboard form; SettingsForm settings;
    public bool Admin { get { return admin; } }
    public bool AutoStartOn { get { return autoStart.Checked; } }
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

        var menu = new ContextMenuStrip { Renderer = new DarkMenu(), ShowImageMargin = false, ShowCheckMargin = true, Font = new Font("Segoe UI", 9.5f), Padding = new Padding(2, 6, 2, 6) };
        menu.HandleCreated += delegate { Theme.SmallCorners(menu.Handle); };
        menu.Items.Add("Tableau de bord", null, delegate { ShowForm(); });
        menu.Items.Add("Historique", null, delegate { OpenHistory(); });
        menu.Items.Add("Vider la RAM", null, delegate { CleanRam(); });
        menu.Items.Add("Réglages", null, delegate { ShowSettings(); });
        autoStart = new ToolStripMenuItem("Démarrer avec Windows", null, delegate { ToggleAutoStart(); });
        autoStart.Checked = RunHidden("schtasks", "/Query /TN MoniteurJeu") == 0;
        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, delegate { Quit(); });
        foreach (ToolStripItem it in menu.Items) { it.ForeColor = Theme.Text; if (it is ToolStripMenuItem) it.Padding = new Padding(4, 5, 12, 5); }
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

    public string CfgFile { get { return Path.Combine(dir, "reglages.ini"); } }
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
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Theme.FillRound(g, new RectangleF(0, 0, 15.5f, 15.5f), 4, game ? Theme.Red : v >= 85 ? Color.FromArgb(234, 140, 0) : Color.FromArgb(37, 99, 235));
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
        form = new Dashboard(this);
        form.FormClosed += delegate { form.Dispose(); form = null; Native.TrimMemory(); };
        form.Show();
        Tick();
    }

    public void OpenHistory() { History.Open(HistDir); }

    public void CleanRam()
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

    public void ShowSettings()
    {
        if (settings != null) { settings.Activate(); return; }
        settings = new SettingsForm(this);
        settings.FormClosed += delegate { settings.Dispose(); settings = null; Native.TrimMemory(); };
        settings.Show();
    }

    public void ApplyCfg(Reglages r) { r.Save(CfgFile); Cfg = r; games = Reglages.List(Cfg.Jeux); }

    public void ToggleAutoStart()
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

        string html = Page.Replace("/*D*/", data.ToString()).Replace("<!--S-->", rows.Length > 0 ? rows.ToString() : "<tr><td colspan=8 class=empty>Aucune session pour l'instant</td></tr>");
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

    const string Page = @"<!doctype html><html lang=fr><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>Historique · MoniteurJeu</title>
<style>
:root{--bg:#0e1016;--card:#161922;--line:#272c39;--field:#1c202b;--hover:#232837;--text:#e9ecf2;--muted:#8a90a2}
*{box-sizing:border-box}body{background:var(--bg);color:var(--text);font:14px 'Segoe UI',system-ui,sans-serif;margin:0;padding:32px 24px}
main{max-width:1120px;margin:auto}header{margin-bottom:24px}h1{font-family:'Segoe UI Semibold','Segoe UI',sans-serif;font-size:28px;margin:0}header p{margin:4px 0 0;color:var(--muted)}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:12px;margin-bottom:16px}
.stat,.card{background:var(--card);border:1px solid var(--line);border-radius:14px}
.stat{padding:14px 16px}.stat span{color:var(--muted);font-size:12px}.stat b{display:block;font-family:'Segoe UI Semibold','Segoe UI',sans-serif;font-size:26px;margin-top:4px}
.card{padding:18px 20px;margin-bottom:16px}.head{display:flex;align-items:center;gap:12px;flex-wrap:wrap;margin-bottom:12px}
h2{font-family:'Segoe UI Semibold','Segoe UI',sans-serif;font-size:16px;margin:0}.k{color:var(--muted);font-size:12px;flex:1}
.k i,.tip i{display:inline-block;width:8px;height:8px;border-radius:50%;margin:0 6px 0 14px}.tip i{margin-left:0}
.seg{background:var(--field);border-radius:9px;padding:3px;display:flex}.seg button{background:none;border:0;color:var(--muted);font:600 12px 'Segoe UI',sans-serif;padding:6px 14px;border-radius:7px;cursor:pointer}
.seg button.on{background:var(--hover);color:var(--text)}.seg button:hover{color:var(--text)}
.wrap{position:relative}canvas{width:100%;height:260px;display:block}
.tip{position:absolute;top:8px;pointer-events:none;background:#0e1016ee;border:1px solid var(--line);border-radius:10px;padding:8px 12px;font-size:12px;white-space:nowrap;display:none;line-height:1.7}
table{width:100%;border-collapse:collapse;font-size:13px}th{color:var(--muted);font-weight:600;text-align:left;padding:8px 10px;border-bottom:1px solid var(--line);white-space:nowrap}
td{padding:9px 10px;border-bottom:1px solid var(--line)}tbody tr:last-child td{border:0}tbody tr:hover{background:var(--hover)}td:nth-child(3){font-weight:600}
.empty{color:var(--muted);padding:32px;text-align:center}
</style>
<main><header><h1>Historique</h1><p>MoniteurJeu · généré le <span id=gen></span></p></header>
<div class=stats id=stats></div>
<section class=card><div class=head><h2>Dernières 24 heures</h2><div class=k id=c1k></div><div class=seg id=c1s></div></div><div class=wrap><canvas id=c1></canvas><div class=tip id=c1t></div></div></section>
<section class=card><div class=head><h2>7 derniers jours</h2><div class=k id=c2k></div><div class=seg id=c2s></div></div><div class=wrap><canvas id=c2></canvas><div class=tip id=c2t></div></div></section>
<section class=card><div class=head><h2>Sessions de jeu</h2></div><div style=overflow-x:auto><table><thead><tr><th>Début<th>Durée (min)<th>Jeu<th>FPS moy.<th>CPU max °C<th>GPU max °C<th>Bridé (s)<th>Alertes</thead><tbody><!--S--></tbody></table></div></section></main>
<script>
const D=/*D*/;
const $=id=>document.getElementById(id),pad=n=>String(n).padStart(2,'0');
const avg=(a,k)=>{const v=a.map(r=>r[k]).filter(x=>x!=null);return v.length?v.reduce((s,x)=>s+x,0)/v.length:null};
const max=(a,k)=>{const v=a.map(r=>r[k]).filter(x=>x!=null);return v.length?Math.max(...v):null};
const fmt=(v,u)=>v==null?'—':Math.round(v)+u;
$('gen').textContent=new Date().toLocaleString('fr-FR',{dateStyle:'long',timeStyle:'short'});

const now=D.length?D[D.length-1][0]:0,d1=D.filter(r=>r[0]>now-1440),play=D.filter(r=>r[6]).length;
$('stats').innerHTML=[['CPU moyen · 24 h',fmt(avg(d1,1),' %'),'#4f8cff'],['GPU moyen · 24 h',fmt(avg(d1,4),' %'),'#34d399'],['RAM moyenne · 24 h',fmt(avg(d1,3),' %'),'#a78bfa'],
 ['CPU max · 24 h',fmt(max(d1,2),' °C'),'#fbbf24'],['GPU max · 24 h',fmt(max(d1,5),' °C'),'#f43f5e'],['Temps de jeu · 7 j',Math.floor(play/60)+' h '+pad(play%60),'#e9ecf2']]
 .map(s=>'<div class=stat><span>'+s[0]+'</span><b style=color:'+s[2]+'>'+s[1]+'</b></div>').join('');

// Courbes en aire avec dégradé ; un écart > gap entre deux points coupe la courbe.
function chart(cv,xs,series,o){
 const r=devicePixelRatio||1,w=cv.clientWidth,h=cv.clientHeight;cv.width=w*r;cv.height=h*r;
 const g=cv.getContext('2d');g.setTransform(r,0,0,r,0,0);g.font='12px Segoe UI';g.fillStyle='#8a90a2';
 if(!xs.length){g.textAlign='center';g.fillText('Pas encore de données',w/2,h/2);return null}
 const L=46,R=12,T=10,B=26,x0=xs[0],x1=xs[xs.length-1],M=o.max;
 const X=v=>L+(w-L-R)*(x1>x0?(v-x0)/(x1-x0):.5),Y=v=>h-B-(h-B-T)*Math.min(v,M)/M;
 g.strokeStyle='#272c39';g.setLineDash([3,4]);
 for(let i=0;i<=4;i++){const v=M*i/4;g.beginPath();g.moveTo(L,Y(v));g.lineTo(w-R,Y(v));g.stroke();g.fillText(Math.round(v*10)/10+o.unit,2,Y(v)+4)}
 g.setLineDash([]);
 let last=-1e9;xs.forEach(x=>{const t=o.label(x);if(t&&X(x)-last>56){g.fillText(t,X(x)-12,h-6);last=X(x)}});
 series.forEach(s=>{let seg=[];
  const path=()=>{g.beginPath();seg.forEach((p,i)=>i?g.lineTo(p[0],p[1]):g.moveTo(p[0],p[1]))};
  const flush=()=>{if(seg.length==1){g.fillStyle=s.c;g.beginPath();g.arc(seg[0][0],seg[0][1],3,0,7);g.fill()}
   if(seg.length>1){const gr=g.createLinearGradient(0,T,0,h-B);gr.addColorStop(0,s.c+'55');gr.addColorStop(1,s.c+'00');
    path();g.lineTo(seg[seg.length-1][0],h-B);g.lineTo(seg[0][0],h-B);g.closePath();g.fillStyle=gr;g.fill();
    path();g.strokeStyle=s.c;g.lineWidth=2;g.lineJoin='round';g.stroke()}seg=[]};
  s.v.forEach((v,i)=>{if(v==null||(i&&xs[i]-xs[i-1]>o.gap))flush();if(v!=null)seg.push([X(xs[i]),Y(v)])});flush()});
 if(o.hover!=null){const x=X(xs[o.hover]);g.strokeStyle='#8a90a2';g.beginPath();g.moveTo(x,T);g.lineTo(x,h-B);g.stroke();
  series.forEach(s=>{const v=s.v[o.hover];if(v!=null){g.fillStyle=s.c;g.strokeStyle='#161922';g.lineWidth=2;g.beginPath();g.arc(x,Y(v),5,0,7);g.fill();g.stroke()}})}
 return X}

// Carte de graphique : onglets, légende, info-bulle au survol.
function panel(id,xs,sets,label,tipLabel,gap){
 const cv=$(id),tip=$(id+'t'),names=Object.keys(sets);let cur=names[0],hov=null,X=null;
 $(id+'s').innerHTML=names.map(n=>'<button>'+n+'</button>').join('');
 const btns=[...$(id+'s').children];
 const draw=()=>{const s=sets[cur];btns.forEach(b=>b.className=b.textContent==cur?'on':'');
  $(id+'k').innerHTML=s.series.map(z=>'<i style=background:'+z.c+'></i>'+z.n).join('');
  X=chart(cv,xs,s.series,{gap:gap,label:label,max:s.max,unit:s.unit,hover:hov})};
 btns.forEach(b=>b.onclick=()=>{cur=b.textContent;draw()});
 cv.onmousemove=e=>{if(!X)return;let bi=0,bd=1e9;xs.forEach((x,i)=>{const d=Math.abs(X(x)-e.offsetX);if(d<bd){bd=d;bi=i}});
  const s=sets[cur];if(bi!==hov){hov=bi;draw()}
  tip.innerHTML='<div style=color:var(--muted)>'+tipLabel(xs[bi])+'</div>'+s.series.map(z=>'<div><i style=background:'+z.c+'></i>'+z.n+' <b>'+(z.v[bi]==null?'—':Math.round(z.v[bi]*10)/10+' '+s.unit)+'</b></div>').join('');
  tip.style.display='block';const tx=X(xs[bi]);tip.style.left=(tx+tip.offsetWidth+20>cv.clientWidth?tx-tip.offsetWidth-12:tx+12)+'px'};
 cv.onmouseleave=()=>{hov=null;tip.style.display='none';draw()};
 addEventListener('resize',draw);draw()}

const t1=d1.map(r=>r[0]),c1=k=>d1.map(r=>r[k]),hm=m=>{const d=new Date(m*60000);return pad(d.getUTCHours())+':'+pad(d.getUTCMinutes())};
panel('c1',t1,{'Charge':{max:100,unit:'%',series:[{n:'CPU',c:'#4f8cff',v:c1(1)},{n:'GPU',c:'#34d399',v:c1(4)},{n:'RAM',c:'#a78bfa',v:c1(3)}]},
 'Températures':{max:100,unit:'°C',series:[{n:'CPU',c:'#fbbf24',v:c1(2)},{n:'GPU',c:'#f43f5e',v:c1(5)}]}},
 m=>{const d=new Date(m*60000);return d.getUTCMinutes()<2?d.getUTCHours()+'h':''},hm,5);

const days={};D.forEach(r=>{const k=Math.floor(r[0]/1440);(days[k]=days[k]||[]).push(r)});
const ks=Object.keys(days).map(Number).sort((a,b)=>a-b).slice(-7),J=['dim.','lun.','mar.','mer.','jeu.','ven.','sam.'];
const dl=k=>{const d=new Date(k*86400000);return J[d.getUTCDay()]+' '+d.getUTCDate()},hours=ks.map(k=>days[k].filter(r=>r[6]).length/60);
panel('c2',ks,{'Charge moyenne':{max:100,unit:'%',series:[{n:'CPU',c:'#4f8cff',v:ks.map(k=>avg(days[k],1))},{n:'GPU',c:'#34d399',v:ks.map(k=>avg(days[k],4))},{n:'RAM',c:'#a78bfa',v:ks.map(k=>avg(days[k],3))}]},
 'Températures max':{max:100,unit:'°C',series:[{n:'CPU',c:'#fbbf24',v:ks.map(k=>max(days[k],2))},{n:'GPU',c:'#f43f5e',v:ks.map(k=>max(days[k],5))}]},
 'Temps de jeu':{max:Math.max(1,Math.ceil(Math.max(0,...hours))),unit:'h',series:[{n:'Jeu',c:'#e9ecf2',v:hours}]}},dl,dl,1);
</script>";
}
