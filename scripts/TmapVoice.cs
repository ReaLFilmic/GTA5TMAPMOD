// TmapVoice.cs  -  GTA V 길안내 음성 스크립트 (Script Hook V .NET v3용, 싱글플레이 전용)
// scripts 폴더에 이 파일과 TmapVoice 폴더, TmapVoice.ini 를 같이 넣으면 게임 시작 때 자동 컴파일됩니다.
// v2: 회전 판정을 GTA의 dir 번호 하나에 맡기지 않고, 경로 위의 점들을 직접 읽어 '꺾임 각도'를 계산해서 정한다.
//     (경로를 못 읽는 환경이면 예전처럼 dir 방식으로 자동 전환. 로그에 mode=ROUTE / mode=DIR 로 표시됨)
// ※ 게임 안에서 아직 테스트되지 않았습니다. TmapVoice.log 를 보고 보정하세요.

using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

public class TmapVoice : Script
{
    // ---------- 설정 (TmapVoice.ini 에서 읽어옴) ----------
    string baseDir;
    string voiceFolder = "tmap_plain";
    float prepareDist = 250f;   // 이 거리 안에 들어오면 "prepare_*" (미리 안내)
    float turnDist = 70f;       // 이 거리 안에 들어오면 "turn_*" (지금 안내)
    float arriveDist = 40f;     // 목적지 도착 판정 거리
    float distScale = 1f;       // GTA가 주는 거리 값 -> 미터 변환 배율 (값이 미터의 10배면 0.1)
    bool debug = true;
    bool trace = true;          // 보정용 상세 기록 (INI의 Trace=0 으로 끔)
    Keys testKey = Keys.F9;
    Keys debugKey = Keys.F10;
    Keys dumpKey = Keys.F8;     // 지금 읽은 경로 점/각도를 로그에 덤프

    // ---------- 경로 직접 읽기 설정 ([Route]) ----------
    bool useRoute = true;
    float routeStep = 12f;          // 경로 위 점을 몇 m 간격으로 읽을지
    float routeLookahead = 320f;    // 앞으로 몇 m까지 읽을지 (최소 prepareDist+60 으로 보정됨)
    float cornerWindow = 30f;       // 꺾임 각도를 잴 때 앞뒤로 보는 거리(m)
    float minTurnAngle = 35f;       // 이 각도 이상이면 '꺾임 후보'
    float strongTurnAngle = 60f;    // 이 각도 이상이면 dir 없이 확정, 그 미만(후보~이 값)은 dir 이 같은 방향일 때만 확정
    float uTurnAngle = 150f;        // 이 각도 이상이면 유턴
    float minAheadDist = 25f;       // 내 앞 이 거리 안의 꺾임은 무시(이미 도는 중)
    int routeFlag1 = -1;            // GET_POS_ALONG_GPS_TYPE_ROUTE 2번째 인자: -1=자동탐색, 0/1=고정
    int routeFlag3 = -1;            // 4번째 인자: -1=자동탐색, 0/1/2=고정
    float routeScale = 1f;          // 거리 인자 배율 (점 간격이 이상하면 조정)
    string routeHashText = "";      // 비워두면 이름으로 찾음. 안 되면 0x... 해시를 직접 적음

    // GENERATE_DIRECTIONS_TO_COORD 의 direction 값 -> 의미. -1 이면 사용 안 함. (INI에서 보정)
    // 기본값은 커뮤니티 문서(RAGE:MP 위키)의 표를 따랐고, 3/6(Keep)은 문서에서도 '미확인'이라 꺼 둠.
    int codeStraight = 7, codeLeft = 4, codeRight = 5;
    int codeKeepLeft = -1, codeKeepRight = -1, codeUTurn = -1, codeRecalc = 1;

    // ---------- 상태 ----------
    readonly Random rng = new Random();
    SoundPlayer soundPlayer;
    DateTime playingUntil = DateTime.MinValue;
    DateTime nextRecalcOk = DateTime.MinValue;
    DateTime lastValidTime = DateTime.MinValue;
    bool wasWaypoint = false;
    int lastDir = -999;
    float lastDist = 0f;
    bool prepared = false, announced = false;
    string logPath;

    // ---------- 경로 읽기 상태 ----------
    class Man
    {
        public int kind;        // 1=좌회전 2=우회전 3=유턴
        public float angle;     // 꺾임 각도(+ 왼쪽, - 오른쪽)
        public float dist;      // 내 차에서 그 모퉁이까지 경로 거리(m)
        public Vector3 pos;     // 모퉁이 위치
        public bool prepared, announced;
        public int missed;      // 연속으로 못 찾은 횟수 (잠깐 깜빡여도 안내 상태 유지용)
    }
    Man curMan = null;
    bool routeOk = false;
    Hash routeHash;
    bool routeHashFound = false;
    int rp1 = 1, rp3 = 0;
    DateTime nextProbe = DateTime.MinValue;
    int routeFailCount = 0;
    readonly List<Vector3> rpts = new List<Vector3>();
    readonly List<float> rcum = new List<float>();
    int probeAttempts = 0;
    int lastGood1 = -1, lastGood3 = -1;
    bool dumpRequested = false;

    public TmapVoice()
    {
        baseDir = FindBase();
        logPath = Path.Combine(baseDir, "TmapVoice.log");
        LoadIni(Path.Combine(baseDir, "TmapVoice.ini"));
        InitRouteNative();
        Interval = 250;
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Log("TmapVoice loaded. voice=" + voiceFolder);
    }

    // scripts 폴더 위치를 여러 후보에서 찾는다. (TmapVoice 폴더가 실제로 있는 곳을 고름)
    string FindBase()
    {
        List<string> roots = new List<string>();
        try { roots.Add(Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)); } catch { }
        try { roots.Add(AppDomain.CurrentDomain.BaseDirectory); } catch { }
        try { roots.Add(Directory.GetCurrentDirectory()); } catch { }

        foreach (string r in roots)
        {
            if (string.IsNullOrEmpty(r)) continue;
            string a = Path.Combine(r, "scripts");
            if (Directory.Exists(Path.Combine(a, "TmapVoice"))) return a;
            if (Directory.Exists(Path.Combine(r, "TmapVoice"))) return r;
        }
        return roots.Count > 0 ? Path.Combine(roots[0], "scripts") : "scripts";
    }

    // ---------- INI 읽기 (아주 단순한 key=value 파서) ----------
    void LoadIni(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("[")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                float f; int n;
                switch (k)
                {
                    case "voice": voiceFolder = v; break;
                    case "preparedistance": if (float.TryParse(v, out f)) prepareDist = f; break;
                    case "turndistance": if (float.TryParse(v, out f)) turnDist = f; break;
                    case "arrivedistance": if (float.TryParse(v, out f)) arriveDist = f; break;
                    case "distscale": if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) distScale = f; break;
                    case "debug": debug = (v == "1"); break;
                    case "trace": trace = (v == "1"); break;
                    case "testkey": TryKey(v, ref testKey); break;
                    case "debugkey": TryKey(v, ref debugKey); break;
                    case "dumpkey": TryKey(v, ref dumpKey); break;
                    case "useroute": useRoute = (v == "1"); break;
                    case "routestep": if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) && f >= 4f) routeStep = f; break;
                    case "routelookahead": if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) routeLookahead = f; break;
                    case "cornerwindow": if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) cornerWindow = f; break;
                    case "minturnangle": if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) minTurnAngle = f; break;
                    case "strongturnangle": if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) strongTurnAngle = f; break;
                    case "uturnangle": if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) uTurnAngle = f; break;
                    case "minaheaddist": if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) minAheadDist = f; break;
                    case "routeflag1": if (int.TryParse(v, out n)) routeFlag1 = n; break;
                    case "routeflag3": if (int.TryParse(v, out n)) routeFlag3 = n; break;
                    case "routescale": if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) && f > 0f) routeScale = f; break;
                    case "routehash": routeHashText = v; break;
                    case "straight": if (int.TryParse(v, out n)) codeStraight = n; break;
                    case "left": if (int.TryParse(v, out n)) codeLeft = n; break;
                    case "right": if (int.TryParse(v, out n)) codeRight = n; break;
                    case "keepleft": if (int.TryParse(v, out n)) codeKeepLeft = n; break;
                    case "keepright": if (int.TryParse(v, out n)) codeKeepRight = n; break;
                    case "uturn": if (int.TryParse(v, out n)) codeUTurn = n; break;
                    case "recalc": if (int.TryParse(v, out n)) codeRecalc = n; break;
                }
            }
        }
        catch (Exception e) { Log("INI error: " + e.Message); }
    }

    static void TryKey(string v, ref Keys target)
    {
        try { target = (Keys)Enum.Parse(typeof(Keys), v, true); } catch { }
    }

    // ---------- 키 입력 ----------
    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == debugKey)
        {
            debug = !debug;
            GTA.UI.Screen.ShowSubtitle("TmapVoice debug: " + (debug ? "ON" : "OFF"), 1500);
        }
        else if (e.KeyCode == dumpKey)
        {
            dumpRequested = true;
            GTA.UI.Screen.ShowSubtitle("TmapVoice: 경로 덤프 요청 (다음 틱에 로그 기록)", 1500);
        }
        else if (e.KeyCode == testKey)
        {
            // 소리가 나오는지 확인용
            string testFile = Path.Combine(Path.Combine(Path.Combine(baseDir, "TmapVoice"), voiceFolder), "turn_left.wav");
            Play("turn_left", true);
            GTA.UI.Screen.ShowSubtitle("TmapVoice file=" + (File.Exists(testFile) ? "OK" : "NOT FOUND") + "  log=" + logPath, 6000);
        }
    }

    // ---------- 메인 루프 ----------
    void OnTick(object sender, EventArgs e)
    {
        Ped player = Game.Player.Character;
        bool active = Game.IsWaypointActive && player != null && player.Exists() && player.IsInVehicle();

        if (!active)
        {
            wasWaypoint = false;
            lastDir = -999;
            curMan = null;
            routeOk = false;
            return;
        }

        Vector3 wp = World.WaypointPosition;
        Vector3 pos = player.Position;
        float groundZ = World.GetGroundHeight(wp);
        float z = groundZ > 0f ? groundZ : pos.Z;

        // 새 경로가 설정된 순간 -> 출발 안내
        if (!wasWaypoint)
        {
            wasWaypoint = true;
            prepared = announced = false;
            lastDir = -999;
            curMan = null;
            routeOk = false;          // 새 경로이므로 경로 읽기를 다시 검증 (이전 경로의 점을 쓰지 않도록)
            probeAttempts = 0;
            routeFailCount = 0;
            nextProbe = DateTime.MinValue;
            Log("NEW ROUTE waypoint x=" + wp.X.ToString("0.0") + " y=" + wp.Y.ToString("0.0"));
            Play("start", true);
        }

        // 도착 판정
        float dx = wp.X - pos.X, dy = wp.Y - pos.Y;
        float distToDest = (float)Math.Sqrt(dx * dx + dy * dy);
        if (distToDest < arriveDist)
        {
            Play("finish", true);
            Function.Call(Hash.SET_WAYPOINT_OFF);
            wasWaypoint = false;
            return;
        }

        // GTA 내장 길찾기에서 '다음 안내'를 얻어옴
        OutputArgument outDir = new OutputArgument();
        OutputArgument outP5 = new OutputArgument();
        OutputArgument outDist = new OutputArgument();
        int ret = Function.Call<int>(Hash.GENERATE_DIRECTIONS_TO_COORD, wp.X, wp.Y, z, true, outDir, outP5, outDist);
        int dir = outDir.GetResult<int>();
        float dist = outDist.GetResult<float>() * distScale;

        // 보정용 상세 기록: 매 틱마다 GTA가 준 원본 값 + 내 차의 위치/방향/속도
        // (나중에 '실제로 어느 쪽으로 꺾었는지'를 위치 변화로 계산해서 dir 번호와 맞춰보기 위함)
        if (trace)
        {
            System.Globalization.CultureInfo ic = System.Globalization.CultureInfo.InvariantCulture;
            Log("T ret=" + ret + " dir=" + dir + " p5=" + outP5.GetResult<float>().ToString("0.###", ic)
                + " raw=" + outDist.GetResult<float>().ToString("0.###", ic)
                + " x=" + pos.X.ToString("0.0", ic) + " y=" + pos.Y.ToString("0.0", ic)
                + " h=" + player.Heading.ToString("0", ic) + " v=" + player.Speed.ToString("0.0", ic));
        }

        // ★ 경로 직접 읽기 방식: 경로 위 점들로 꺾임 각도를 계산해서 판정한다. (dir 은 참고용)
        //    경로를 못 읽으면 false 가 돌아오고, 아래의 예전 dir 방식으로 넘어간다.
        if (useRoute && routeHashFound)
        {
            if (RouteStep(pos, dir, dist, distToDest)) return;
        }

        // 이 함수는 매번 의미 있는 값을 주지 않는다. (로그를 보면 0/2/쓰레기 값이 섞여 나옴)
        // 회전 안내(3~9)이면서 거리가 정상 범위일 때만 '진짜 값'으로 인정한다.
        bool valid = dir >= 3 && dir <= 9 && dist > 0.5f && dist < 20000f;

        if (!valid)
        {
            if (debug)
                GTA.UI.Screen.ShowSubtitle("(대기)  last dir=" + lastDir + "  dist=" + lastDist.ToString("0") + "  toDest=" + distToDest.ToString("0"), 400);

            // 경로 재탐색 값은 거리 없이 오므로 여기서 따로 처리
            if (codeRecalc >= 0 && dir == codeRecalc && DateTime.Now >= nextRecalcOk)
            {
                Log("recalc dir=" + dir);
                Play("recomputing", false);
                nextRecalcOk = DateTime.Now.AddSeconds(10);
            }
            // 한동안 진짜 값이 없으면 안내 상태를 초기화
            if (DateTime.Now > lastValidTime.AddSeconds(4))
            {
                lastDir = -999;
                prepared = announced = false;
            }
            return;
        }
        lastValidTime = DateTime.Now;

        if (debug)
            GTA.UI.Screen.ShowSubtitle("dir=" + dir + "  dist=" + dist.ToString("0") + "  toDest=" + distToDest.ToString("0"), 400);

        // 값이 바뀔 때마다 로그 (보정용)
        if (dir != lastDir)
            Log("dir changed: " + lastDir + " -> " + dir + "  dist=" + dist.ToString("0.0") + "  ret=" + ret + "  speed=" + player.Speed.ToString("0.0"));

        // 같은 안내가 계속되는 중인지, 새 안내로 넘어갔는지 판단
        if (dir != lastDir || dist > lastDist + 40f)
        {
            prepared = announced = false;
        }
        lastDir = dir;
        lastDist = dist;

        // 경로 재탐색
        if (dir == codeRecalc && codeRecalc >= 0)
        {
            if (DateTime.Now >= nextRecalcOk)
            {
                Play("recomputing", false);
                nextRecalcOk = DateTime.Now.AddSeconds(10);
            }
            return;
        }

        // 회전 종류 -> 파일 이름
        string suffix = null;       // prepare_turn_left / turn_left 처럼 쓰는 경우
        string single = null;       // keep_left, u_turn 처럼 한 번만 말하는 경우
        if (dir == codeLeft && codeLeft >= 0) suffix = "turn_left";
        else if (dir == codeRight && codeRight >= 0) suffix = "turn_right";
        else if (dir == codeKeepLeft && codeKeepLeft >= 0) single = "keep_left";
        else if (dir == codeKeepRight && codeKeepRight >= 0) single = "keep_right";
        else if (dir == codeUTurn && codeUTurn >= 0) single = "u_turn";

        if (suffix != null)
        {
            if (!prepared && dist <= prepareDist && dist > turnDist + 20f)
            {
                // 다른 음성이 재생 중이라 건너뛰었으면 다음 샘플에서 다시 시도
                if (Play("prepare_" + suffix, false)) prepared = true;
            }
            if (!announced && dist <= turnDist)
            {
                Play(suffix, true);
                announced = prepared = true;
            }
        }
        else if (single != null)
        {
            if (!announced && dist <= turnDist + 40f)
            {
                Play(single, true);
                announced = true;
            }
        }
    }

    // =====================================================================
    //  경로 직접 읽기 방식 (v2)
    //  1) GET_POS_ALONG_GPS_TYPE_ROUTE 로 '지금 경로 위의 점'을 일정 간격으로 읽는다.
    //  2) 점들의 진행 방향이 꺾이는 각도를 직접 계산해서 좌/우/유턴을 정한다.
    //  3) GTA 가 주는 dir 번호는 '참고'로만 쓴다. (애매한 각도일 때 방향이 같으면 확정)
    // =====================================================================

    void InitRouteNative()
    {
        try
        {
            string t = routeHashText == null ? "" : routeHashText.Trim();
            if (t.Length > 0)
            {
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
                ulong h;
                if (ulong.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out h))
                {
                    routeHash = (Hash)h;
                    routeHashFound = true;
                    Log("route native: hash from ini = 0x" + h.ToString("X"));
                    return;
                }
            }
            // Hash 열거형에 이름이 없는 버전이면 여기서 예외가 나므로, 컴파일 오류 대신 안전하게 dir 방식으로 넘어간다.
            routeHash = (Hash)Enum.Parse(typeof(Hash), "GET_POS_ALONG_GPS_TYPE_ROUTE");
            routeHashFound = true;
            Log("route native: found by name");
        }
        catch (Exception e)
        {
            routeHashFound = false;
            Log("route native NOT available (" + e.Message + ") -> dir mode only");
        }
    }

    static float Dist2D(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    static string F(float v)
    {
        return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    static string KindName(int k)
    {
        return k == 1 ? "left" : (k == 2 ? "right" : (k == 3 ? "uturn" : "?"));
    }

    // GTA dir 번호 -> 1=좌 2=우 3=유턴 0=해당없음
    int KindFromDir(int dir)
    {
        if (codeLeft >= 0 && dir == codeLeft) return 1;
        if (codeRight >= 0 && dir == codeRight) return 2;
        if (codeUTurn >= 0 && dir == codeUTurn) return 3;
        return 0;
    }

    // 경로 위 'meters' 지점(차에서 경로를 따라 간 거리)의 좌표를 읽는다.
    bool GetRoutePoint(float meters, out Vector3 p)
    {
        p = Vector3.Zero;
        try
        {
            OutputArgument o = new OutputArgument();
            Function.Call(routeHash, o, rp1 != 0, meters * routeScale, rp3);
            p = o.GetResult<Vector3>();
            if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.X) || float.IsInfinity(p.Y)) return false;
            if (Math.Abs(p.X) < 0.01f && Math.Abs(p.Y) < 0.01f) return false;
            return true;
        }
        catch { return false; }
    }

    // 지금 설정(rp1, rp3)으로 읽은 점들이 '내 차 앞의 길'처럼 보이는지 검사
    bool LooksLikeRoute(Vector3 pos, bool verbose)
    {
        Vector3 a, b, c, e;
        bool ra = GetRoutePoint(10f, out a);
        bool rb = GetRoutePoint(50f, out b);
        bool rc = GetRoutePoint(100f, out c);
        bool re = GetRoutePoint(150f, out e);
        bool good = ra && rb && rc && re
            && Dist2D(a, pos) < 60f                              // 10m 지점은 내 차 근처여야 함
            && Dist2D(a, e) > 50f && Dist2D(a, e) < 190f         // 10m~150m 지점 사이 직선거리가 말이 되는 범위
            && Dist2D(a, b) > 8f && Dist2D(b, c) > 8f;           // 점들이 실제로 앞으로 나아감
        if (verbose)
        {
            string s = "route probe p1=" + rp1 + " p3=" + rp3 + " ok=" + (ra ? 1 : 0) + (rb ? 1 : 0) + (rc ? 1 : 0) + (re ? 1 : 0);
            if (ra) s += " a-car=" + F(Dist2D(a, pos));
            if (ra && re) s += " a-e=" + F(Dist2D(a, e));
            if (ra && rb) s += " a-b=" + F(Dist2D(a, b));
            if (rb && rc) s += " b-c=" + F(Dist2D(b, c));
            Log(s + " -> " + (good ? "OK" : "no"));
        }
        return good;
    }

    // 4번째/2번째 인자 조합을 돌려보며 쓸 수 있는 조합을 찾는다.
    bool ProbeRoute(Vector3 pos, bool verbose)
    {
        List<int> c1 = new List<int>();
        List<int> c3 = new List<int>();
        if (routeFlag1 >= 0) c1.Add(routeFlag1); else { c1.Add(1); c1.Add(0); }
        if (routeFlag3 >= 0) c3.Add(routeFlag3); else { c3.Add(0); c3.Add(1); c3.Add(2); }

        // 전에 성공한 조합이 있으면 그걸 먼저 시험
        if (lastGood1 >= 0 && lastGood3 >= 0)
        {
            rp1 = lastGood1; rp3 = lastGood3;
            if (LooksLikeRoute(pos, verbose)) return true;
        }
        foreach (int x in c1)
        {
            foreach (int y in c3)
            {
                rp1 = x; rp3 = y;
                if (LooksLikeRoute(pos, verbose))
                {
                    lastGood1 = x; lastGood3 = y;
                    return true;
                }
            }
        }
        return false;
    }

    // 앞쪽 경로를 일정 간격으로 읽어 rpts/rcum 에 채운다. 읽은 점 개수를 돌려줌.
    // rcum = 첫 점부터 각 점까지의 '실제 좌표 사이 거리'의 누적합 (단위 가정에 의존하지 않음)
    int ReadRoute()
    {
        rpts.Clear();
        rcum.Clear();
        float look = Math.Max(routeLookahead, prepareDist + 60f);
        int n = (int)Math.Ceiling(look / routeStep) + 1;
        Vector3 prev = Vector3.Zero;
        float acc = 0f;
        int stuck = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 p;
            if (!GetRoutePoint(i * routeStep, out p)) break;
            if (i > 0)
            {
                float seg = Dist2D(prev, p);
                if (seg < 0.5f) { stuck++; if (stuck >= 2) break; continue; }   // 경로 끝: 같은 점이 반복됨
                if (seg > routeStep * 4f) break;                                // 점이 튄 경우: 여기까지만 사용
                acc += seg;
            }
            rpts.Add(p);
            rcum.Add(acc);
            prev = p;
        }
        return rpts.Count;
    }

    int WinK()
    {
        int m = rpts.Count;
        float avg = m > 1 ? rcum[m - 1] / (m - 1) : routeStep;
        return Math.Max(1, (int)Math.Round(cornerWindow / Math.Max(avg, 1f)));
    }

    // i 번째 점에서 '들어오는 방향'과 '나가는 방향'이 이루는 각도 (+ 왼쪽, - 오른쪽, 도 단위)
    // GTA 좌표: X=동, Y=북 → 반시계 방향(+)이 좌회전
    float WinAngle(int i, int k)
    {
        float ix = rpts[i].X - rpts[i - k].X, iy = rpts[i].Y - rpts[i - k].Y;
        float ox = rpts[i + k].X - rpts[i].X, oy = rpts[i + k].Y - rpts[i].Y;
        double cross = ix * oy - iy * ox;
        double dot = ix * ox + iy * oy;
        return (float)(Math.Atan2(cross, dot) * 180.0 / Math.PI);
    }

    // 읽은 경로에서 '앞으로 처음 만나는 진짜 꺾임'을 찾는다. 없으면 null.
    Man FindManeuver(int hintKind, Vector3 pos)
    {
        int m = rpts.Count;
        int k = WinK();
        if (m < 2 * k + 2) return null;

        // 차가 경로 첫 점에서 얼마나 앞/뒤에 있는지 (첫 구간에 투영) → 거리 보정
        float off = 0f;
        float dx0 = rpts[1].X - rpts[0].X, dy0 = rpts[1].Y - rpts[0].Y;
        float l0 = (float)Math.Sqrt(dx0 * dx0 + dy0 * dy0);
        if (l0 > 0.5f)
        {
            off = ((pos.X - rpts[0].X) * dx0 + (pos.Y - rpts[0].Y) * dy0) / l0;
            off = Math.Max(-l0, Math.Min(l0, off));
        }

        int i = k;
        while (i + k < m)
        {
            float distI = rcum[i] - off;
            if (distI < minAheadDist) { i++; continue; }

            float a = WinAngle(i, k);
            if (Math.Abs(a) < minTurnAngle) { i++; continue; }

            // 꺾임 후보 발견: 각도가 가장 큰 지점(모퉁이)을 찾는다.
            int pk = i;
            float pa = a;
            int j = i + 1;
            while (j + k < m)
            {
                float aj = WinAngle(j, k);
                if (aj * pa <= 0f || Math.Abs(aj) < minTurnAngle) break;
                if (Math.Abs(aj) > Math.Abs(pa)) { pk = j; pa = aj; }
                j++;
            }

            int kind = Math.Abs(pa) >= uTurnAngle ? 3 : (pa > 0f ? 1 : 2);
            bool strong = Math.Abs(pa) >= strongTurnAngle;

            // 확정 조건: 각도가 충분히 크거나, 애매한 각도지만 GTA dir 도 같은 방향이라고 할 때
            if (strong || hintKind == kind)
            {
                Man mn = new Man();
                mn.kind = kind;
                mn.angle = pa;
                mn.dist = Math.Max(0f, rcum[pk] - off);
                mn.pos = rpts[pk];
                return mn;
            }
            i = j;  // 애매하고 dir 도 동의하지 않음 → 건너뛰고 다음 후보 탐색
        }
        return null;
    }

    // 반환: true = 경로 방식으로 처리함(이번 틱은 dir 방식을 쓰지 않음)
    //       false = 경로를 못 읽어서 예전 dir 방식으로 넘김
    bool RouteStep(Vector3 pos, int dir, float dirDist, float distToDest)
    {
        // 1) 경로 읽기가 검증됐는지 확인 (아직이면 3초 간격으로 탐색)
        if (!routeOk)
        {
            // 경로 계산이 끝나기 전 몇 초 동안은 dir 방식으로 넘기지 않고 조용히 기다린다. (잘못된 안내 방지)
            if (DateTime.Now < nextProbe) return probeAttempts >= 4;
            probeAttempts++;
            bool verbose = probeAttempts <= 3;       // 로그가 도배되지 않게 처음 3번만 자세히 기록
            if (ProbeRoute(pos, verbose))
            {
                routeOk = true;
                routeFailCount = 0;
                Log("mode=ROUTE  p1=" + rp1 + " p3=" + rp3 + " step=" + F(routeStep) + " window=" + F(cornerWindow));
            }
            else
            {
                nextProbe = DateTime.Now.AddSeconds(3);
                if (probeAttempts == 4) Log("route probe failing -> mode=DIR (조용히 계속 재시도)");
                return probeAttempts >= 4;
            }
        }

        // 2) 경로 읽기
        int n = ReadRoute();
        if (n < 3)
        {
            // 목적지 근처라 점이 거의 없을 수도 있고, 읽기가 깨졌을 수도 있다.
            routeFailCount++;
            if (routeFailCount >= 12)
            {
                routeOk = false;
                nextProbe = DateTime.Now.AddSeconds(3);
                Log("route read lost -> re-probe");
                return false;
            }
            return true;
        }
        routeFailCount = 0;

        // 3) 경로 재탐색 (dir 번호가 알려줄 때만 – 거리 값과 무관)
        if (codeRecalc >= 0 && dir == codeRecalc && DateTime.Now >= nextRecalcOk)
        {
            Log("recalc dir=" + dir);
            Play("recomputing", false);
            nextRecalcOk = DateTime.Now.AddSeconds(10);
        }

        // 4) 꺾임 찾기 (dir 은 참고 의견)
        int hintKind = (dirDist > 0.5f && dirDist < 20000f) ? KindFromDir(dir) : 0;
        Man found = FindManeuver(hintKind, pos);

        // 5) 같은 모퉁이면 안내 상태(prepared/announced)를 이어받는다
        if (found != null)
        {
            if (curMan != null && Dist2D(curMan.pos, found.pos) < 35f)
            {
                found.prepared = curMan.prepared;
                found.announced = curMan.announced;
            }
            else
            {
                string agree = hintKind == found.kind ? "dir agrees" : (hintKind == 0 ? "dir: none" : "dir DISAGREES(" + KindName(hintKind) + ")");
                Log("MAN new " + KindName(found.kind) + " angle=" + F(found.angle) + " dist=" + F(found.dist)
                    + "  | dir=" + dir + " dirDist=" + F(dirDist) + " (" + agree + ")");
            }
            curMan = found;
        }
        else if (curMan != null)
        {
            // 잠깐 못 찾은 것일 수 있으니 몇 틱은 이전 모퉁이를 유지 (직선거리로 남은 거리 추정)
            curMan.missed++;
            if (curMan.missed >= 4) curMan = null;
            else curMan.dist = Dist2D(pos, curMan.pos);
        }

        // 6) 안내 방송
        if (curMan != null)
        {
            float d = curMan.dist;
            string suffix = curMan.kind == 1 ? "turn_left" : (curMan.kind == 2 ? "turn_right" : null);
            if (suffix != null)
            {
                if (!curMan.prepared && d <= prepareDist && d > turnDist + 20f)
                {
                    if (Play("prepare_" + suffix, false)) { curMan.prepared = true; Log("say prepare_" + suffix + " d=" + F(d)); }
                }
                if (!curMan.announced && d <= turnDist)
                {
                    Play(suffix, true);
                    curMan.announced = curMan.prepared = true;
                    Log("say " + suffix + " d=" + F(d) + " angle=" + F(curMan.angle));
                }
            }
            else if (curMan.kind == 3)
            {
                if (!curMan.announced && d <= turnDist + 40f)
                {
                    Play("u_turn", true);
                    curMan.announced = true;
                    Log("say u_turn d=" + F(d) + " angle=" + F(curMan.angle));
                }
            }
        }

        // 7) 화면 자막 / 기록
        if (debug)
        {
            string ms = curMan == null ? "다음 꺾임 없음" : KindName(curMan.kind) + " " + Math.Abs(curMan.angle).ToString("0") + "도 " + curMan.dist.ToString("0") + "m";
            GTA.UI.Screen.ShowSubtitle("[경로] " + ms + "   | dir=" + dir + " dist=" + dirDist.ToString("0") + "  toDest=" + distToDest.ToString("0"), 400);
        }
        if (trace)
        {
            Log("R n=" + n + " man=" + (curMan == null ? "-" : KindName(curMan.kind) + "/" + F(curMan.angle) + "/" + F(curMan.dist)) + " hint=" + hintKind);
        }
        if (dumpRequested) DumpRoute(pos);
        return true;
    }

    // F8: 지금 읽은 경로 점과 각 점에서의 꺾임 각도를 로그에 남김 (판정이 이상할 때 원인 확인용)
    void DumpRoute(Vector3 pos)
    {
        dumpRequested = false;
        int m = rpts.Count;
        int k = WinK();
        Log("DUMP car x=" + F(pos.X) + " y=" + F(pos.Y) + " n=" + m + " k=" + k + " p1=" + rp1 + " p3=" + rp3);
        for (int i = 0; i < m; i++)
        {
            string ang = (i >= k && i + k < m) ? F(WinAngle(i, k)) : "-";
            Log("DUMP i=" + i + " x=" + F(rpts[i].X) + " y=" + F(rpts[i].Y) + " s=" + F(rcum[i]) + " ang=" + ang);
        }
    }

    // ---------- 소리 재생 ----------
    // name 이 "start" 처럼 번호가 붙은 묶음이면(start_1, start_2 ...) 그중 하나를 랜덤으로 고름.
    // 실제로 재생을 시작했으면 true, 다른 음성이 재생 중이라 건너뛰었거나 실패했으면 false
    bool Play(string name, bool interrupt)
    {
        try
        {
            if (!interrupt && DateTime.Now < playingUntil) return false;

            string dir = Path.Combine(Path.Combine(baseDir, "TmapVoice"), voiceFolder);
            string file = Pick(dir, name);
            if (file == null)
            {
                Log("missing clip: " + name);
                if (debug) GTA.UI.Screen.ShowSubtitle("TmapVoice: missing clip " + name + " in " + dir, 4000);
                return false;
            }

            // WAV 길이 계산 (44100Hz, 16bit, mono = 초당 88200바이트)
            long bytes = new FileInfo(file).Length;
            double sec = Math.Max(0.3, (bytes - 44) / 88200.0);
            playingUntil = DateTime.Now.AddSeconds(sec);

            // 재생 중에 Dispose 하면 소리가 끊기므로, 다음 재생 때 정리한다.
            if (soundPlayer != null) { try { soundPlayer.Stop(); soundPlayer.Dispose(); } catch { } }
            soundPlayer = new SoundPlayer(file);
            soundPlayer.Play(); // 비동기 재생
            Log("play " + Path.GetFileName(file));
            return true;
        }
        catch (Exception e)
        {
            Log("play error: " + e.Message);
            if (debug) GTA.UI.Screen.ShowSubtitle("TmapVoice error: " + e.Message, 5000);
            return false;
        }
    }

    string Pick(string dir, string name)
    {
        string exact = Path.Combine(dir, name + ".wav");
        if (File.Exists(exact)) return exact;
        string[] many = Directory.GetFiles(dir, name + "_*.wav");
        if (many.Length == 0) return null;
        return many[rng.Next(many.Length)];
    }

    void Log(string msg)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
        try { File.AppendAllText(logPath, line); return; }
        catch { }
        // scripts 폴더에 쓸 수 없으면(폴더 쓰기 권한 문제 등) '문서' 폴더에 대신 기록
        try
        {
            logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TmapVoice.log");
            File.AppendAllText(logPath, line);
        }
        catch { logPath = "(log write failed)"; }
    }
}
