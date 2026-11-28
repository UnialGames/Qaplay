using System;
using System.Collections.Generic;
using System.IO;
using Qaplay.Agent;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Qaplay.Editor
{
    /// <summary>
    /// QA 빌드에 게임 안의 에이전트가 읽는 설정(테스터가 버그를 제보하는 방법)을 넣는다. 빌드하는 동안만 Assets/QaplayBuild/Resources에
    /// qaplay-agent.json을 두고, 빌드가 끝나면 지운다. 에디터의 Play는 qaplay.json을 바로 읽으므로 이것이 필요 없다.
    /// QA 빌드인지는 Player 설정의 QAPLAY 심벌과, 빌드 스크립트가 넘긴 심벌(BuildPlayerOptions.extraScriptingDefines, CI가 흔히 쓴다)로 안다.
    /// </summary>
    public sealed class AgentSettingsBuild : BuildPlayerProcessor, IPostprocessBuildWithReport
    {
        const string Folder = "Assets/QaplayBuild";

        public override int callbackOrder => 1;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            Remove();
            BuildPlayerOptions options = context.BuildPlayerOptions;
            NamedBuildTarget target = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(options.target));
            bool qa = QaplaySettingsProvider.QaBuild(target) || options.extraScriptingDefines != null && Array.IndexOf(options.extraScriptingDefines, "QAPLAY") >= 0;
            if (!qa) return; // 출시 빌드에는 에이전트가 없다

            Dictionary<string, object> settings = QaplayUi.ReadSettings(out string error)
                                                  ?? throw new BuildFailedException("[qaplay] " + QaplayUi.SettingsFile + " cannot be read: " + error);
            // 틀린 값은 빌드할 때 알린다. 테스터가 기기에서 제보하지 못하는 것을 늦게 알게 되지 않게
            try
            {
                BugTrigger.Parse(settings);
            }
            catch (FormatException e)
            {
                throw new BuildFailedException("[qaplay] " + QaplayUi.SettingsFile + ": " + e.Message);
            }
            var agent = new Dictionary<string, object>();
            if (settings.TryGetValue("bugReport", out object bugReport)) agent["bugReport"] = bugReport;
            string file = Folder + "/Resources/" + BugTrigger.ResourceName + ".json";
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, Json.Write(agent, true));
            AssetDatabase.ImportAsset(file);
        }

        public void OnPostprocessBuild(BuildReport report) => Remove();

        // 지난 빌드가 도중에 실패해 남은 것도 지운다
        static void Remove()
        {
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
        }
    }
}
