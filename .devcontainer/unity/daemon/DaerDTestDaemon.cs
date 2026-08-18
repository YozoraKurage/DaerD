using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

// DaerD のテストデーモン受け口。テストプロジェクト側（名前付きボリューム）にだけ
// インストールされ、DaerD 本体（配布パッケージ）には決して入らない。
//
// 動く仕組み: 常駐 batchmode エディタの中で EditorApplication.update に乗り、
// <project>/TestDaemon/request.json を見つけたら running.json に「主張」してから
// AssetDatabase.Refresh() する。/workspace の変更で再コンパイル + ドメインリロードが
// 起きても running.json はファイルなので生き残り、新しいドメインの静的コンストラクタが
// 続きから実行する。テストは TestRunnerApi で走らせ、結果は summarize-results.js が
// 読める最小の NUnit3 風 XML に落とす。
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
        static string DonePath => Path.Combine(Dir, "done");
        static string AlivePath => Path.Combine(Dir, "alive");
        static string QuitPath => Path.Combine(Dir, "quit");

        static readonly bool Inert;
        static double s_nextTick;
        static double s_nextBeat;
        static bool s_started;

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

                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

                if (File.Exists(RunningPath))
                {
                    // リロード後にコンパイルが失敗していたら、テストは 1 件も走れない。
                    // コールド経路の終了コード 3 と同じ意味で返す。
                    if (EditorUtility.scriptCompilationFailed)
                    {
                        Finish(3, "compile errors — see daemon.log");
                        return;
                    }
                    if (!s_started) Start(File.ReadAllText(RunningPath));
                    return;
                }

                if (File.Exists(RequestPath))
                {
                    // 主張してから Refresh。リロードで自分が死んでも running.json が残り、
                    // 次のドメインが続きをやる。
                    File.Delete(DonePath);
                    File.Delete(ResultXmlPath);
                    File.Move(RequestPath, RunningPath);
                    AssetDatabase.Refresh();
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[DaerDTestDaemon] " + e);
                try { Finish(3, e.Message); } catch { /* 客側のタイムアウトに任せる */ }
            }
        }

        static void Start(string requestJson)
        {
            s_started = true;
            var request = JsonUtility.FromJson<Request>(
                string.IsNullOrWhiteSpace(requestJson) ? "{}" : requestJson) ?? new Request();

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

        static bool s_locked;

        static void Finish(int code, string note)
        {
            s_started = false;
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
        }

        class Callbacks : ICallbacks
        {
            readonly List<ITestResultAdaptor> _failures = new List<ITestResultAdaptor>();

            public void RunStarted(ITestAdaptor tests) { }
            public void TestStarted(ITestAdaptor test) => DaerDTestDaemon.Beat();

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
