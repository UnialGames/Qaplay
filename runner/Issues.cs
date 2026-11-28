using System;
using System.Collections.Generic;
using System.IO;

namespace Qaplay.Runner
{
    /// <summary>
    /// 버그를 올리는 이슈 관리 도구 하나(전략). 모듈이 더한다(Jira 모듈의 jira, 뒤에 Notion).
    /// 버그 리포트는 늘 이 PC의 폴더(qaplay-bugs)에 있고, 도구를 이으면 거기에도 올라간다. 무엇을 쓸지는 설정의 issues.type이다.
    /// </summary>
    interface IIssueTracker
    {
        /// <summary>설정 파일의 issues.type에 적는 이름.</summary>
        string Type { get; }
        /// <summary>사람에게 보여 주는 이름.</summary>
        string Name { get; }
        /// <summary>설정의 issues 부분이 이 도구에 맞는지. 모자라면 FormatException.</summary>
        void Check(Dictionary<string, object> config);
        /// <summary>그 도구와 이야기하는 것. 이 PC에 로그인 정보가 없으면 UsageException(무엇을 하면 되는지와 함께).</summary>
        IIssueConnection Open(Dictionary<string, object> config);
    }

    /// <summary>요구사항의 연동 모듈 공통 인터페이스: 등록, 첨부 업로드, 상태 조회. 그리고 다시 났을 때 알리기.</summary>
    interface IIssueConnection
    {
        /// <summary>등록: 버그 하나를 이슈로 만든다.</summary>
        IssueRef Create(BugDraft bug);
        /// <summary>첨부 업로드: 화면, 로그, 재현 스텝.</summary>
        void Attach(string key, string file);
        /// <summary>상태 조회.</summary>
        IssueState State(string key);
        /// <summary>다시 났다고 덧글을 단다. reopen이면 닫힌 이슈를 다시 연다(회귀). 다시 열지 못했으면(워크플로에 길이 없다) false.</summary>
        bool Seen(string key, string note, bool reopen);
    }

    sealed class IssueRef
    {
        public string key, url;
    }

    sealed class IssueState
    {
        /// <summary>도구에 보이는 상태의 이름(할 일, 진행 중, 완료 등).</summary>
        public string name;
        /// <summary>끝난 상태인지(고쳤거나 닫았다).</summary>
        public bool done;
    }

    /// <summary>이슈로 올릴 버그의 내용. 도구는 이것을 자기 형식으로 적는다.</summary>
    sealed class BugDraft
    {
        public string id, title, kind, severity;
        /// <summary>설명과 정보(종류, 횟수, 빌드, 기기 등) 줄들.</summary>
        public readonly List<string> lines = new List<string>();
        /// <summary>재현 스텝(규칙 파일). 없으면 null.</summary>
        public string steps;
    }

    /// <summary>쓸 수 있는 이슈 관리 도구들. 설치된 모듈이 더한다. 하나도 없으면 버그는 이 PC의 폴더에만 있다.</summary>
    sealed class IssueTrackers
    {
        readonly List<IIssueTracker> m_All;
        readonly string m_ModulesFolder;

        public IssueTrackers(params IIssueTracker[] trackers) : this(null, trackers)
        {
        }

        IssueTrackers(string modulesFolder, IEnumerable<IIssueTracker> trackers)
        {
            m_ModulesFolder = modulesFolder;
            m_All = new List<IIssueTracker>(trackers);
        }

        public static IssueTrackers Installed(Modules modules) => new IssueTrackers(modules.Folder, modules.Create<IIssueTracker>());

        public IReadOnlyList<IIssueTracker> All => m_All;

        public IIssueTracker Get(string type) =>
            m_All.Find(tracker => tracker.Type == type)
            ?? throw new UsageException("the issue tracker \"" + type + "\" is not installed"
                                        + (m_ModulesFolder == null ? "" : ": its module goes in " + m_ModulesFolder + " (see qaplay modules)"));
    }

    /// <summary>
    /// 이 PC의 버그 리포트와 이슈 관리 도구를 맞춘다(qaplay bugs sync, 그리고 run과 bugs pull이 끝날 때).
    /// 올리지 않은 버그는 등록하고 첨부를 올린다. 다시 난 버그는 덧글을 달고, 도구에서 닫혀 있었으면 다시 연다.
    /// 도구에서 끝난 이슈는 이 PC에서 resolved가 된다(회귀 규칙으로 확인하면 verified).
    /// 실패하면 이 PC의 리포트는 그대로 두고 멈춘다. 다음에 맞출 때 남은 것부터 다시 보낸다.
    /// </summary>
    static class IssueSync
    {
        static readonly string[] Attachments = { "screenshot.png", "log.txt", "steps.yaml" };

        /// <summary>설정에 이어진 도구가 없으면 null.</summary>
        public static IIssueConnection Open(ProjectSettings settings, IssueTrackers trackers, out IIssueTracker tracker)
        {
            tracker = null;
            Dictionary<string, object> config = settings.Issues;
            if (config == null) return null;
            tracker = trackers.Get(config.GetValueOrDefault("type") as string ?? "");
            try
            {
                tracker.Check(config);
            }
            catch (FormatException e)
            {
                throw new UsageException(settings.File + ": issues: " + e.Message);
            }
            return tracker.Open(config);
        }

        /// <summary>맞추고, 한 일을 줄마다 say로 알린다. 다 맞췄으면 true. 도중에 실패하면 그 까닭을 알리고 false.</summary>
        public static bool Run(BugStore store, IIssueTracker tracker, IIssueConnection connection, Action<string> say)
        {
            foreach (Dictionary<string, object> bug in store.All())
            {
                string id = (string)bug["id"];
                try
                {
                    Sync(store, tracker, connection, bug, say);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Net.Http.HttpRequestException || e is UsageException)
                {
                    say("bug " + id + " was not sent to " + tracker.Name + ": " + e.Message + " (it is kept here; qaplay bugs sync sends it again)");
                    return false;
                }
            }
            return true;
        }

        static void Sync(BugStore store, IIssueTracker tracker, IIssueConnection connection, Dictionary<string, object> bug, Action<string> say)
        {
            string id = (string)bug["id"];
            string folder = store.FolderOf(id);
            var issue = bug.GetValueOrDefault("issue") as Dictionary<string, object>;
            int count = Convert.ToInt32(bug["count"]);
            if (issue == null)
            {
                IssueRef made = connection.Create(Draft(bug, folder));
                // 만들자마자 적어 둔다: 첨부를 올리다 끊겨도 다음에 이슈를 또 만들지 않는다
                issue = new Dictionary<string, object> { ["type"] = tracker.Type, ["key"] = made.key, ["url"] = made.url, ["count"] = count, ["attached"] = new List<object>() };
                bug["issue"] = issue;
                bug.Remove("reopenPending");
                store.Save(bug);
                say("bug " + id + " -> " + made.key + "  " + made.url);
            }

            // 첨부: 올리지 않은 것만. 화면이나 로그가 새로 바뀌었으면(다시 났을 때) 그것도 올린다
            var attached = issue.GetValueOrDefault("attached") as List<object> ?? new List<object>();
            issue["attached"] = attached;
            foreach (string name in Attachments)
            {
                string file = Path.Combine(folder, name);
                if (!File.Exists(file)) continue;
                string stamp = name + "@" + File.GetLastWriteTimeUtc(file).Ticks;
                if (attached.Contains(stamp)) continue;
                connection.Attach((string)issue["key"], file);
                attached.RemoveAll(entry => ((string)entry).StartsWith(name + "@", StringComparison.Ordinal));
                attached.Add(stamp);
                store.Save(bug);
            }

            IssueState state = connection.State((string)issue["key"]);
            int sent = Convert.ToInt32(issue.GetValueOrDefault("count") ?? 0);
            bool reopen = true.Equals(bug.GetValueOrDefault("reopenPending"));
            if (count > sent || reopen)
            {
                // 이 PC에서 다시 났다: 도구에 알린다. 도구에서 끝난 이슈면 다시 연다(회귀)
                bool reopenThere = state.done;
                string note = count > sent
                    ? "seen again by qaplay: " + count + " time(s) in all, last at " + bug["lastSeen"] + Where(bug)
                    : "the regression rule fails again (qaplay bugs verify)";
                bool reopened = connection.Seen((string)issue["key"], note, reopenThere);
                issue["count"] = count;
                bug.Remove("reopenPending");
                if (reopenThere && !reopened)
                    say("bug " + id + " (" + issue["key"] + ") happened again, but its workflow has no way back from " + state.name
                        + ": a comment was added; reopen it in " + tracker.Name + " by hand");
                else if (reopenThere)
                {
                    state = connection.State((string)issue["key"]);
                    say("bug " + id + " (" + issue["key"] + ") happened again: reopened in " + tracker.Name + " as a regression");
                }
                else say("bug " + id + " (" + issue["key"] + ") happened again: noted in " + tracker.Name);
            }
            else if (state.done && "open".Equals(bug["status"]))
            {
                // 도구에서 고쳤다고 했다. 회귀 규칙이 있으면 qaplay bugs verify로 확인한다
                bug["status"] = "resolved";
                say("bug " + id + " (" + issue["key"] + ") is " + state.name + " in " + tracker.Name + ": marked resolved here"
                    + (bug.GetValueOrDefault("rule") is string ? "; check it with qaplay bugs verify " + id : ""));
            }
            else if (!state.done && ("resolved".Equals(bug["status"]) || "verified".Equals(bug["status"])))
            {
                // 사람이 도구에서 다시 열었다
                bug["status"] = "open";
                say("bug " + id + " (" + issue["key"] + ") is " + state.name + " again in " + tracker.Name + ": open here too");
            }
            issue["status"] = state.name;
            issue["syncedAt"] = BugStore.Now();
            store.Save(bug);
        }

        static string Where(Dictionary<string, object> bug)
        {
            string app = bug.GetValueOrDefault("app") is Dictionary<string, object> a ? a.GetValueOrDefault("id") + " " + a.GetValueOrDefault("version") : null;
            string platform = bug.GetValueOrDefault("platform") as string;
            return app == null && platform == null ? "" : " (" + string.Join(", ", new List<string> { app, platform }.FindAll(s => s != null)) + ")";
        }

        /// <summary>도구에 올릴 내용: 제목, 설명, 정보 줄들, 재현 스텝.</summary>
        public static BugDraft Draft(Dictionary<string, object> bug, string folder)
        {
            var draft = new BugDraft
            {
                id = (string)bug["id"], title = (string)bug["title"], kind = bug.GetValueOrDefault("kind") as string, severity = bug.GetValueOrDefault("severity") as string,
            };
            if (bug.GetValueOrDefault("description") is string description && description.Trim().Length > 0) draft.lines.Add(description.Trim());
            draft.lines.Add("qaplay bug " + draft.id + ": " + draft.kind + ", seen " + bug["count"] + " time(s), first " + bug["firstSeen"] + ", last " + bug["lastSeen"]);
            foreach (string key in new[] { "app", "unity", "platform", "device", "screen" })
                if (bug.GetValueOrDefault(key) is object value && value != null)
                    draft.lines.Add(key + ": " + (value is string text ? text : Json.Write(value)));
            if (true.Equals(bug.GetValueOrDefault("regression"))) draft.lines.Add("regression: it came back after it was fixed");
            string steps = Path.Combine(folder, "steps.yaml");
            if (File.Exists(steps)) draft.steps = File.ReadAllText(steps).Replace("\r\n", "\n").TrimEnd();
            return draft;
        }
    }
}
