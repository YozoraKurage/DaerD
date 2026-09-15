using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using Debug = UnityEngine.Debug;

// DaerD のテストデーモン受け口。テストプロジェクト側（名前付きボリューム）にだけ
// インストールされ、DaerD 本体（配布パッケージ）には決して入らない。
//
// 動く仕組み: 常駐エディタ（GUI モードでも batchmode でも）の中で EditorApplication.update に乗り、
// <project>/TestDaemon/request.json を見つけたら running.json に「主張」してから
// AssetDatabase.Refresh() する。/workspace の変更で再コンパイル + ドメインリロードが
// 起きても running.json はファイルなので生き残り、新しいドメインの静的コンストラクタが
// 続きから実行する。テストは TestRunnerApi で走らせ、結果は summarize-results.js が
// 読める最小の NUnit3 風 XML に落とす。
//
// テスト以外の依頼（request.json の op）:
//  - exec:    static string Method(string) を 1 本呼ぶ（exec-method.sh）
//  - console: コンソールの内容を返す（unity-do.sh console）
//  - compile: 再コンパイルの結果とエラー本文を返す（unity-do.sh compile）
//  - snippet: C# の断片を Unity 同梱の Roslyn で外部コンパイルし、DLL をこのドメインへ
//             読み込んで実行する。ドメインリロードは起きない（unity-do.sh run）
//
// 慣性の保険が 2 つ:
//  - コールド実行（-runTests）中は完全に沈黙する（二重実行の防止）。
//  - TestDaemon/enabled が無ければ何もしない（受け口が入っていても無害）。
namespace Yozolab.DaerDTestDaemon
{
    [InitializeOnLoad]
    static class DaerDTestDaemon
    {
        static readonly string Dir =
            Path.Combine(Path.GetDirectoryName(Application.dataPath), "TestDaemon");

        static string RequestPath => Path.Combine(Dir, "request.json");
        static string RunningPath => Path.Combine(Dir, "running.json");
        static string ResultXmlPath => Path.Combine(Dir, "result.xml");
        static string ExecResultPath => Path.Combine(Dir, "exec-result.txt");
        // 直近のコンパイルのメッセージ。リロードで static は消えるが、これは残る。
        static string CompileLogPath => Path.Combine(Dir, "compile.txt");
        static string DonePath => Path.Combine(Dir, "done");
        static string AlivePath => Path.Combine(Dir, "alive");
        static string QuitPath => Path.Combine(Dir, "quit");

        static readonly bool Inert;
        static double s_nextTick;
        static double s_nextBeat;
        static double s_compilingSince;
        static bool s_started;

        // Stall watchdog (see CheckStall). Statics are fine: a reload that ends a stall also
        // resets them, and running.json carries the request across it.
        const double SettleSeconds = 1.0;
        const double StallSeconds = 30.0;
        static double s_refreshedAt;
        static double s_startedAt;
        static bool s_testSeen;
        static bool s_stallUnlocked;

        // Compile-gate watchdog (see CheckGateStall).
        const double GateStallSeconds = 90.0;
        const double GateGiveUpSeconds = 60.0;
        static double s_gateSince;
        static double s_gateEscalatedAt;

        static string TracePath => Path.Combine(Dir, "trace.log");

        static DaerDTestDaemon()
        {
            var args = Environment.GetCommandLineArgs();
            // コールドの -runTests と共存しない。デーモンが生きている間にコールドは
            // 走らせない運用だが、逆(コールド中に受け口が動く)はここで確実に殺す。
            Inert = Array.IndexOf(args, "-runTests") >= 0 || !File.Exists(Path.Combine(Dir, "enabled"));
            if (Inert) return;
            // 非フォーカスのエディタは既定でループが間引かれ、EditMode ランナーは
            // 1 tick ずつしか進まない — 全件 1305 件が実行 9 秒 + 待ち 160 秒になる
            // (実測)。テスト専用プロジェクトなので常時フルスロットルで良い。
            if (EditorPrefs.GetInt("InteractionMode", 0) != 1)
                EditorPrefs.SetInt("InteractionMode", 1);
            Trace("domain loaded" + (File.Exists(RunningPath) ? "; resuming running.json" : ""));
            CompilationPipeline.compilationStarted += _ => { s_compileMessages.Clear(); };
            CompilationPipeline.assemblyCompilationFinished += (asm, messages) =>
            {
                foreach (var m in messages)
                    if (m.type == CompilerMessageType.Error || m.type == CompilerMessageType.Warning)
                        s_compileMessages.Add(m);
            };
            CompilationPipeline.compilationFinished += _ => WriteCompileLog();
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now < s_nextTick) return;
            s_nextTick = now + 0.5;

            try
            {
                if (now >= s_nextBeat)
                {
                    s_nextBeat = now + 2.0;
                    File.WriteAllText(AlivePath, DateTime.UtcNow.ToString("o"));
                }

                if (File.Exists(QuitPath))
                {
                    File.Delete(QuitPath);
                    EditorApplication.Exit(0);
                    return;
                }

                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    // バッチの常駐エディタは、コンパイル済みアセンブリを当てるドメイン
                    // リロードを自発的に始めないことがある(実測: ソース変更後の初回依頼が
                    // isCompiling のまま固まり、クライアントがタイムアウトする)。依頼を
                    // 抱えたままこのゲートに 10 秒居座ったら、リロードを明示的に頼む。
                    // isCompiling に限定しない — isUpdating で固まる個体も実測で 2 例あり、
                    // そちらは促されないまま永久に待っていた。
                    if (File.Exists(RunningPath))
                    {
                        if (s_gateSince == 0)
                        {
                            s_gateSince = now;
                            Trace("gate: waiting on compile/update");
                        }
                        if (s_compilingSince == 0) s_compilingSince = now;
                        else if (now - s_compilingSince > 10)
                        {
                            s_compilingSince = 0;
                            Trace("gate: nudge (RequestScriptReload)");
                            EditorUtility.RequestScriptReload();
                        }
                        CheckGateStall(now);
                    }
                    return;
                }
                if (s_gateSince != 0)
                {
                    Trace("gate: cleared after " + (now - s_gateSince).ToString("0") + " s");
                    s_gateSince = 0;
                    s_gateEscalatedAt = 0;
                }
                s_compilingSince = 0;

                if (File.Exists(RunningPath))
                {
                    // リロード後にコンパイルが失敗していたら、テストは 1 件も走れない。
                    // コールド経路の終了コード 3 と同じ意味で返す。
                    if (EditorUtility.scriptCompilationFailed)
                    {
                        File.WriteAllText(ExecResultPath, CompileReport());
                        Finish(3, "compile errors — see exec-result.txt / daemon.log");
                        return;
                    }
                    if (!s_started)
                    {
                        // Refresh only queues the compile for changed scripts; give it a moment
                        // to show up as isCompiling before a run locks reloads behind it.
                        if (now - s_refreshedAt < SettleSeconds) return;
                        Start(File.ReadAllText(RunningPath));
                        s_startedAt = now;
                        return;
                    }
                    CheckStall(now);
                    return;
                }

                if (File.Exists(RequestPath))
                {
                    // 主張してから Refresh。リロードで自分が死んでも running.json が残り、
                    // 次のドメインが続きをやる。
                    File.Delete(DonePath);
                    File.Delete(ResultXmlPath);
                    File.Move(RequestPath, RunningPath);
                    Trace("claim: " + File.ReadAllText(RunningPath).Trim());
                    AssetDatabase.Refresh();
                    s_refreshedAt = now;
                    Trace("refresh done");
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[DaerDTestDaemon] " + e);
                try { Finish(3, e.Message); } catch { /* 客側のタイムアウトに任せる */ }
            }
        }

        /// <summary>
        /// The other stall: a request that never gets past the compile gate. Seen 2026-09-11 with
        /// an exec request: after the Refresh that brought a new script in, nothing was compiled,
        /// reloaded or run for ten minutes, and the 10-second reload nudge changed nothing. The
        /// cause is not known — trace.log is here to catch it next time — so this escalates once
        /// (hand back any reload lock, ask for the compile outright, import synchronously) and then
        /// gives up with code 5 rather than hold the client to its timeout.
        /// </summary>
        static void CheckGateStall(double now)
        {
            if (s_gateEscalatedAt == 0)
            {
                if (now - s_gateSince < GateStallSeconds) return;
                s_gateEscalatedAt = now;
                Trace("gate: stuck " + GateStallSeconds + " s; escalating");
                Debug.LogWarning("[DaerDTestDaemon] stuck in the compile gate for " + GateStallSeconds
                                 + " s; unlocking reloads, requesting compilation, refreshing synchronously");
                if (s_locked)
                {
                    s_locked = false;
                    EditorApplication.UnlockReloadAssemblies();
                }
                CompilationPipeline.RequestScriptCompilation();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return;
            }
            if (now - s_gateEscalatedAt < GateGiveUpSeconds) return;
            Trace("gate: still stuck after escalating; giving up");
            s_gateSince = 0;
            s_gateEscalatedAt = 0;
            Finish(5, "stuck in the compile gate (isCompiling/isUpdating) — see TestDaemon/trace.log");
        }

        /// <summary>
        /// One timestamped line per state change, appended across restarts: daemon.log is
        /// recreated each time the daemon starts, and a stalled daemon is fixed by restarting it,
        /// so without this the evidence goes with the fix. Starts over past ~1 MB.
        /// </summary>
        static void Trace(string what)
        {
            try
            {
                var info = new FileInfo(TracePath);
                if (info.Exists && info.Length > 1000000) info.Delete();
                File.AppendAllText(TracePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + what
                    + " [compiling=" + EditorApplication.isCompiling
                    + " updating=" + EditorApplication.isUpdating
                    + " locked=" + s_locked + "]\n");
            }
            catch { /* tracing must never take the daemon down */ }
        }

        /// <summary>
        /// A run that has not reached its first test within <see cref="StallSeconds"/> is waiting
        /// on something that will not come. Measured 2026-09-11: a Refresh that found changed
        /// scripts had not started the compile by the next tick, so Start locked reloads first and
        /// the test runner then waited on the compile + reload that lock was holding back — no log
        /// line for 20 minutes, while the heartbeat kept the client waiting. First hand the lock
        /// back and refresh: a pending reload lands in a new domain, which finds running.json and
        /// starts over; or the waiting run proceeds and finishes. If neither happens within one
        /// more window, give up with code 5 rather than hold the client to its timeout.
        /// </summary>
        static void CheckStall(double now)
        {
            if (s_testSeen || now - s_startedAt < StallSeconds) return;
            if (!s_stallUnlocked)
            {
                s_stallUnlocked = true;
                s_startedAt = now;
                Trace("stall: no test started; releasing the reload lock");
                Debug.LogWarning("[DaerDTestDaemon] no test started in " + StallSeconds
                                 + " s; releasing the reload lock and refreshing");
                if (s_locked)
                {
                    s_locked = false;
                    EditorApplication.UnlockReloadAssemblies();
                }
                AssetDatabase.Refresh();
                return;
            }
            Trace("stall: still no test; giving up");
            Finish(5, "stalled: no test started, even with reloads unlocked — see daemon.log");
        }

        static void Start(string requestJson)
        {
            s_started = true;
            s_testSeen = false;
            s_stallUnlocked = false;
            Trace("start: " + (requestJson ?? "").Trim());
            var request = JsonUtility.FromJson<Request>(
                string.IsNullOrWhiteSpace(requestJson) ? "{}" : requestJson) ?? new Request();

            // テスト以外の依頼: static メソッドをリフレクションで実行して文字列を返す
            // （exec-method.sh 参照）。テストと違い同期で終わるので、ここで完結する。
            if (!string.IsNullOrEmpty(request.exec))
            {
                RunExec(request.exec, request.execArg);
                return;
            }
            switch (request.op)
            {
                case "console": RunConsole(request.arg); return;
                case "compile": RunCompile(request.arg); return;
                case "snippet": RunSnippet(request.arg); return;
            }

            var filter = new Filter { testMode = TestMode.EditMode };
            if (!string.IsNullOrEmpty(request.filter))
                filter.groupNames = new[] { request.filter };
            if (!string.IsNullOrEmpty(request.category))
                filter.categoryNames = new[] { request.category };

            // エクスポート系テストは生成 .cs を書いて ImportAsset する。常駐エディタで
            // 素通しにすると 1 回ごとに再コンパイル + ドメインリロード(~10s)が走り、
            // 全件で 2 分超が消える(実測)。-runTests のコールドは実行後まで遅延して
            // いるので、同じ意味論にするためリロードを実行の間だけ施錠する。
            EditorApplication.LockReloadAssemblies();
            s_locked = true;
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(new Callbacks());
            api.Execute(new ExecutionSettings(filter));
        }

        /// <summary>「Type の完全名.メソッド名」を全アセンブリから探し、
        /// static string Method(string) として呼ぶ。internal でよい — リフレクションは
        /// 可視性を見ないので、DaerD 側の受け口を public にしないで済む。</summary>
        static void RunExec(string target, string arg)
        {
            try
            {
                int dot = target.LastIndexOf('.');
                if (dot <= 0) { Finish(1, "exec target must be Full.Type.Name.Method: " + target); return; }
                var typeName = target.Substring(0, dot);
                var methodName = target.Substring(dot + 1);
                Type type = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(typeName);
                    if (type != null) break;
                }
                if (type == null) { Finish(1, "type not found: " + typeName); return; }
                var method = type.GetMethod(methodName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);
                if (method == null)
                {
                    Finish(1, "no static " + methodName + "(string) on " + typeName);
                    return;
                }
                var result = method.Invoke(null, new object[] { arg });
                File.WriteAllText(ExecResultPath, result as string ?? "");
                Finish(0, "");
            }
            catch (Exception e)
            {
                var root = e.GetBaseException();
                File.WriteAllText(ExecResultPath, root.ToString());
                Finish(1, root.Message);
            }
        }

        // ---- compile -----------------------------------------------------------

        static readonly List<CompilerMessage> s_compileMessages = new List<CompilerMessage>();

        static void WriteCompileLog()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var m in s_compileMessages) if (m.type == CompilerMessageType.Error) sb.AppendLine(m.message);
                foreach (var m in s_compileMessages) if (m.type == CompilerMessageType.Warning) sb.AppendLine(m.message);
                File.WriteAllText(CompileLogPath, sb.ToString());
            }
            catch { /* 記録は失っても依頼は続ける */ }
        }

        static string CompileReport()
        {
            var log = File.Exists(CompileLogPath) ? File.ReadAllText(CompileLogPath) : "";
            int errors = 0, warnings = 0;
            foreach (var line in log.Split('\n'))
            {
                if (line.Contains(": error ")) errors++;
                else if (line.Contains(": warning ")) warnings++;
            }
            // 起動時のコンパイル（このドメインが生まれる前）で失敗していると、イベントで
            // 拾った記録が無い。そのときはコンソールに残っている行から拾う。
            if (errors == 0 && EditorUtility.scriptCompilationFailed)
            {
                try
                {
                    var sb = new StringBuilder();
                    foreach (var line in ReadConsole(true, false, false, false).lines)
                        if (line.Contains(": error CS")) { sb.AppendLine(line.Substring(3)); errors++; }
                    if (errors > 0) log = sb.ToString();
                }
                catch { /* コンソールが読めなければ件数だけ返す */ }
            }
            var head = EditorUtility.scriptCompilationFailed
                ? "compile: FAILED (" + errors + " errors, " + warnings + " warnings)"
                : "compile: ok (" + warnings + " warnings)";
            return head + "\n" + log;
        }

        /// <summary>依頼を受けた時点で Refresh とコンパイル待ちは済んでいる（Tick のゲート）。
        /// ここに来たなら成功しているので状態を返すだけ。"force" は全アセンブリを
        /// コンパイルし直す — リロードが起きて自分は死ぬが、running.json が残るので
        /// 次のドメインが（force を外した依頼として）結果を返す。</summary>
        static void RunCompile(string arg)
        {
            if (arg == "force")
            {
                File.WriteAllText(RunningPath, "{\"op\":\"compile\",\"arg\":\"\"}");
                s_started = false;
                Trace("compile: forcing a full recompile");
                CompilationPipeline.RequestScriptCompilation();
                return;
            }
            File.WriteAllText(ExecResultPath, CompileReport());
            Finish(EditorUtility.scriptCompilationFailed ? 3 : 0, "");
        }

        // ---- console -----------------------------------------------------------

        // UnityEditor.ConsoleWindow.Mode のビット（2022.3）。公開されていないので写す。
        const int ModeError = (1 << 0) | (1 << 1) | (1 << 4) | (1 << 6) | (1 << 8) | (1 << 11)
                            | (1 << 17) | (1 << 20) | (1 << 21) | (1 << 22);
        const int ModeWarning = (1 << 7) | (1 << 9) | (1 << 12);
        // ConsoleFlags: 表示レベルのトグルが切れていると GetEntryInternal が飛ばすので、
        // 読む前に全部点ける。
        const int FlagCollapse = 1 << 0, FlagLog = 1 << 7, FlagWarning = 1 << 8, FlagError = 1 << 9;

        struct ConsoleRead
        {
            public int errors, warnings, logs;
            public List<string> lines;   // "E  message  (file:line)" の形。古い順
        }

        /// <summary>UnityEditor.LogEntries（非公開）をリフレクションで読む。表示レベルの
        /// トグルが切れていると飛ばされるので、読む前に全部点ける。</summary>
        static ConsoleRead ReadConsole(bool wantE, bool wantW, bool wantL, bool full)
        {
            var editor = typeof(EditorWindow).Assembly;
            var le = editor.GetType("UnityEditor.LogEntries");
            var entryType = editor.GetType("UnityEditor.LogEntry");
            if (le == null || entryType == null)
                throw new InvalidOperationException("UnityEditor.LogEntries not found in this Unity");
            var any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var setFlag = le.GetMethod("SetConsoleFlag", any);
            if (setFlag != null)
            {
                setFlag.Invoke(null, new object[] { FlagLog, true });
                setFlag.Invoke(null, new object[] { FlagWarning, true });
                setFlag.Invoke(null, new object[] { FlagError, true });
                setFlag.Invoke(null, new object[] { FlagCollapse, false });
            }
            var result = new ConsoleRead { lines = new List<string>() };
            var counts = le.GetMethod("GetCountsByType", any);
            if (counts != null)
            {
                var box = new object[] { 0, 0, 0 };
                counts.Invoke(null, box);
                result.errors = (int)box[0]; result.warnings = (int)box[1]; result.logs = (int)box[2];
            }
            var fMessage = entryType.GetField("message");
            var fFile = entryType.GetField("file");
            var fLine = entryType.GetField("line");
            var fMode = entryType.GetField("mode");
            int total = (int)le.GetMethod("StartGettingEntries", any).Invoke(null, null);
            try
            {
                var get = le.GetMethod("GetEntryInternal", any);
                var entry = Activator.CreateInstance(entryType);
                for (int i = 0; i < total; i++)
                {
                    if (!(bool)get.Invoke(null, new[] { (object)i, entry })) continue;
                    int mode = (int)fMode.GetValue(entry);
                    char kind = (mode & ModeError) != 0 ? 'E' : (mode & ModeWarning) != 0 ? 'W' : 'L';
                    if (kind == 'E' && !wantE || kind == 'W' && !wantW || kind == 'L' && !wantL) continue;
                    var msg = (fMessage.GetValue(entry) as string) ?? "";
                    if (!full)
                    {
                        int nl = msg.IndexOf('\n');
                        if (nl >= 0) msg = msg.Substring(0, nl);
                    }
                    var file = fFile.GetValue(entry) as string;
                    var where = string.IsNullOrEmpty(file) ? "" : "  (" + file + ":" + fLine.GetValue(entry) + ")";
                    result.lines.Add(kind + "  " + msg + where);
                }
            }
            finally
            {
                le.GetMethod("EndGettingEntries", any).Invoke(null, null);
            }
            return result;
        }

        static void ClearConsole()
        {
            var le = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
            le?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null)?.Invoke(null, null);
        }

        /// <summary>arg はトークンの並び: error / warning / log（省略時は全部）、
        /// limit=N（既定 50、末尾から）、full（スタックまで出す）、clear（読んでから消す）。</summary>
        static void RunConsole(string arg)
        {
            try
            {
                bool wantE = false, wantW = false, wantL = false, full = false, clear = false;
                int limit = 50;
                foreach (var t in (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (t == "error") wantE = true;
                    else if (t == "warning") wantW = true;
                    else if (t == "log") wantL = true;
                    else if (t == "full") full = true;
                    else if (t == "clear") clear = true;
                    else if (t.StartsWith("limit=")) int.TryParse(t.Substring(6), out limit);
                }
                if (!wantE && !wantW && !wantL) wantE = wantW = wantL = true;

                var read = ReadConsole(wantE, wantW, wantL, full);
                if (clear) ClearConsole();

                var lines = read.lines;
                var sb = new StringBuilder();
                sb.Append("console: ").Append(read.errors).Append(" errors, ").Append(read.warnings).Append(" warnings, ")
                  .Append(read.logs).Append(" logs");
                if (lines.Count > limit) sb.Append("  (showing last ").Append(limit).Append(" of ").Append(lines.Count).Append(')');
                if (clear) sb.Append("  [cleared]");
                sb.Append('\n');
                for (int i = Math.Max(0, lines.Count - limit); i < lines.Count; i++) sb.AppendLine(lines[i]);
                File.WriteAllText(ExecResultPath, sb.ToString());
                Finish(0, "");
            }
            catch (Exception e)
            {
                File.WriteAllText(ExecResultPath, e.GetBaseException().ToString());
                Finish(1, e.GetBaseException().Message);
            }
        }

        // ---- snippet -----------------------------------------------------------

        /// <summary>arg は断片ファイルのパス。中身はメソッド本体（先頭の using 行は外へ
        /// 出す）。Unity 同梱の csc.dll で、このドメインに読み込まれている全アセンブリを
        /// 参照にしてコンパイルし、出来た DLL をバイト列で読み込んで Run() を呼ぶ。
        /// 実行中の Debug.Log は結果に取り込む。</summary>
        static void RunSnippet(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    File.WriteAllText(ExecResultPath, "snippet file not found: " + path);
                    Finish(1, "snippet file not found");
                    return;
                }
                var contents = EditorApplication.applicationContentsPath;
                var dotnet = Path.Combine(contents, "NetCoreRuntime", Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet");
                var csc = Path.Combine(contents, "DotNetSdkRoslyn", "csc.dll");
                if (!File.Exists(dotnet) || !File.Exists(csc))
                {
                    File.WriteAllText(ExecResultPath, "Roslyn not found under " + contents);
                    Finish(1, "csc missing");
                    return;
                }

                var body = new List<string>();
                var usings = new List<string>();
                bool inHeader = true;
                foreach (var raw in File.ReadAllText(path).Replace("\r\n", "\n").Split('\n'))
                {
                    var line = raw.TrimEnd();
                    if (inHeader && line.TrimStart().StartsWith("using ") && line.TrimEnd().EndsWith(";"))
                    {
                        usings.Add(line.Trim());
                        continue;
                    }
                    if (line.Trim().Length > 0) inHeader = false;
                    body.Add(raw);
                }

                var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                var work = Path.Combine(Dir, "snippet");
                Directory.CreateDirectory(work);
                foreach (var old in Directory.GetFiles(work))
                    if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(old)).TotalHours > 1) File.Delete(old);
                var src = Path.Combine(work, id + ".cs");
                // アセンブリ名は固定（ファイル名から決まる）。バイト列からの Load は同名でも
                // 別個体として読めるので毎回ぶつからず、DaerD 側が InternalsVisibleTo で
                // この名前を挙げれば internal も触れる。
                var dll = Path.Combine(work, "DaerDSnippet.dll");
                var rsp = Path.Combine(work, id + ".rsp");

                var sb = new StringBuilder();
                sb.AppendLine("using System; using System.Collections; using System.Collections.Generic; using System.IO;");
                sb.AppendLine("using System.Linq; using System.Reflection; using System.Text;");
                sb.AppendLine("using UnityEngine; using UnityEditor; using UnityEngine.SceneManagement; using UnityEditor.SceneManagement;");
                sb.AppendLine("using Object = UnityEngine.Object;");
                foreach (var u in usings) sb.AppendLine(u);
                sb.AppendLine("public static class __DaerDSnippet { public static object Run() {");
                sb.AppendLine("#line " + (usings.Count + 1) + " \"snippet\"");
                foreach (var l in body) sb.AppendLine(l);
                sb.AppendLine("#line default");
                sb.AppendLine("return null; } }");
                File.WriteAllText(src, sb.ToString());

                // Managed/ 直下の UnityEngine.dll / UnityEditor.dll は全型を含む旧式の
                // 一枚岩で、Editor が実際に読むのは Managed/UnityEngine/ 側のモジュール +
                // 転送用ファサード。両方を参照に渡すと CS0433（型が二重）になるので、
                // 直下のものは外す。
                var legacyDir = Path.GetFullPath(Path.Combine(contents, "Managed"));
                var seen = new HashSet<string>();
                var args = new StringBuilder();
                args.AppendLine("-nologo -target:library -nostdlib+ -langversion:9.0 -define:UNITY_EDITOR -deterministic-");
                args.AppendLine("-nowarn:0162,1701,1702,1705,0219,0168");
                args.AppendLine("-out:\"" + dll + "\"");
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.IsDynamic) continue;
                    string loc;
                    try { loc = a.Location; } catch { continue; }
                    if (string.IsNullOrEmpty(loc) || !File.Exists(loc)) continue;
                    if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(loc)), legacyDir, StringComparison.Ordinal)) continue;
                    if (!seen.Add(a.GetName().Name)) continue;
                    args.AppendLine("-r:\"" + loc + "\"");
                }
                args.AppendLine("\"" + src + "\"");
                File.WriteAllText(rsp, args.ToString());

                var psi = new ProcessStartInfo(dotnet, "\"" + csc + "\" @\"" + rsp + "\"")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                string output;
                int code;
                using (var p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } code = -1; output += "\ncsc timed out"; }
                    else code = p.ExitCode;
                }
                if (code != 0 || !File.Exists(dll))
                {
                    File.WriteAllText(ExecResultPath, "snippet compile failed:\n" + output.Trim());
                    Finish(3, "snippet compile failed");
                    return;
                }

                var asm = System.Reflection.Assembly.Load(File.ReadAllBytes(dll));
                var run = asm.GetType("__DaerDSnippet").GetMethod("Run", BindingFlags.Static | BindingFlags.Public);
                var logs = new StringBuilder();
                Application.LogCallback capture = (msg, stack, type) =>
                {
                    logs.Append('[').Append(type).Append("] ").AppendLine(msg);
                    if (type == LogType.Exception || type == LogType.Error) logs.AppendLine(stack);
                };
                Application.logMessageReceived += capture;
                object result;
                var sw = Stopwatch.StartNew();
                try { result = run.Invoke(null, null); }
                finally { Application.logMessageReceived -= capture; }
                sw.Stop();
                var text = logs.ToString() + "=> " + Render(result) + "\n(" + sw.ElapsedMilliseconds + " ms)\n";
                File.WriteAllText(ExecResultPath, text);
                Finish(0, "");
            }
            catch (Exception e)
            {
                var root = e.GetBaseException();
                File.WriteAllText(ExecResultPath, root.ToString());
                Finish(1, root.Message);
            }
        }

        static string Render(object result)
        {
            if (result == null) return "null";
            if (result is string s) return s;
            if (result is System.Collections.IEnumerable list && !(result is UnityEngine.Object))
            {
                var parts = new List<string>();
                foreach (var item in list) parts.Add(item == null ? "null" : item.ToString());
                return "[" + string.Join(", ", parts) + "]  (" + parts.Count + " items)";
            }
            return result.ToString();
        }

        static bool s_locked;

        static void Finish(int code, string note)
        {
            s_started = false;
            s_testSeen = false;
            s_stallUnlocked = false;
            Trace("finish: " + code + (string.IsNullOrEmpty(note) ? "" : " " + note));
            // 施錠したまま死なない。施錠は Start だけ、返却は Finish だけの 1:1。
            // ロック保持中はドメインリロードが起きないので、この対応関係は
            // static でも壊れない。
            if (s_locked)
            {
                s_locked = false;
                try { EditorApplication.UnlockReloadAssemblies(); } catch { }
            }
            if (File.Exists(RunningPath)) File.Delete(RunningPath);
            File.WriteAllText(DonePath, code + "\n" + (note ?? ""));
        }

        static DateTime s_lastBeat;

        /// <summary>長いテスト実行の間、update は回らない。コールバックからも鼓動を
        /// 打っておくと status が「忙しい」を生存として見せられる（クライアントの死活
        /// 判定は PID のみ — 鼓動は人間向けの情報）。</summary>
        internal static void Beat()
        {
            var now = DateTime.UtcNow;
            if ((now - s_lastBeat).TotalSeconds < 2) return;
            s_lastBeat = now;
            try { File.WriteAllText(AlivePath, now.ToString("o")); } catch { }
        }

        [Serializable]
        class Request
        {
            public string filter;
            public string category;
            public string exec;      // 空でなければテストではなくメソッド実行の依頼
            public string execArg;
            public string op;        // console / compile / snippet（空ならテスト）
            public string arg;       // op ごとの引数（console: 種別と件数、snippet: 断片ファイルのパス）
        }

        class Callbacks : ICallbacks
        {
            readonly List<ITestResultAdaptor> _failures = new List<ITestResultAdaptor>();

            public void RunStarted(ITestAdaptor tests) { }
            public void TestStarted(ITestAdaptor test)
            {
                if (!s_testSeen) Trace("first test started");
                s_testSeen = true;
                DaerDTestDaemon.Beat();
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                DaerDTestDaemon.Beat();
                if (!result.Test.IsSuite && result.TestStatus == TestStatus.Failed)
                    _failures.Add(result);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                try
                {
                    File.WriteAllText(ResultXmlPath, ToXml(result, _failures));
                    Finish(result.FailCount > 0 ? 1 : 0, "");
                }
                catch (Exception e)
                {
                    Debug.LogError("[DaerDTestDaemon] " + e);
                    Finish(3, e.Message);
                }
            }

            // summarize-results.js が読むのは <test-run> の属性と、result="Failed" の
            // <test-case> チャンクの <message>/<stack-trace> だけ。その形だけを書く。
            static string ToXml(ITestResultAdaptor run, List<ITestResultAdaptor> failures)
            {
                int total = run.PassCount + run.FailCount + run.SkipCount + run.InconclusiveCount;
                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
                sb.Append("<test-run")
                  .Append(" total=\"").Append(total).Append('"')
                  .Append(" passed=\"").Append(run.PassCount).Append('"')
                  .Append(" failed=\"").Append(run.FailCount).Append('"')
                  .Append(" skipped=\"").Append(run.SkipCount).Append('"')
                  .Append(" inconclusive=\"").Append(run.InconclusiveCount).Append('"')
                  .Append(" duration=\"").Append(run.Duration.ToString("0.###")).Append('"')
                  .Append(">\n");
                foreach (var f in failures)
                {
                    sb.Append("<test-case fullname=\"").Append(Escape(f.FullName))
                      .Append("\" result=\"Failed\">\n");
                    sb.Append("<failure><message>").Append(Escape(f.Message ?? ""))
                      .Append("</message>\n");
                    sb.Append("<stack-trace>").Append(Escape(f.StackTrace ?? ""))
                      .Append("</stack-trace></failure>\n");
                    sb.Append("</test-case>\n");
                }
                sb.Append("</test-run>\n");
                return sb.ToString();
            }

            static string Escape(string s) => s
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }
}
