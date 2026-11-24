using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;

namespace Qaplay.Editor
{
    /// <summary>
    /// 빌드를 시작할 때, QA 코드가 출시 빌드에 섞여 들어가지 않는지 확인한다.
    /// 이 패키지의 어셈블리 중 출시 빌드에 들어가도 되는 것은 게임 코드가 참조하는 Qaplay.Api뿐이다.
    /// 나머지는 asmdef의 defineConstraints("QAPLAY || UNITY_EDITOR")로 빠지는데,
    /// 그 조건을 빼먹은 어셈블리가 생기면 빌드를 실패시킨다.
    /// </summary>
    public sealed class ReleaseBuildCheck : IPreprocessBuildWithReport
    {
        const string PackagePath = "Packages/com.qaplay.core/";
        const string AlwaysIncluded = "Qaplay.Api";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            List<string> leaks = FindLeaks();
            if (leaks.Count > 0)
                throw new BuildFailedException(
                    "[qaplay] QA-only assemblies would be included in a build without the QAPLAY symbol: "
                    + string.Join(", ", leaks)
                    + ". Add \"QAPLAY || UNITY_EDITOR\" to defineConstraints in their asmdef.");
        }

        /// <summary>QAPLAY 심벌 없이 플레이어 빌드에 들어가는 이 패키지의 어셈블리 중 Qaplay.Api가 아닌 것.</summary>
        public static List<string> FindLeaks()
        {
            var leaks = new List<string>();
            foreach (Assembly assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                if (assembly.name == AlwaysIncluded) continue;
                if (System.Array.IndexOf(assembly.defines, "QAPLAY") >= 0) continue; // QA 빌드
                if (System.Array.Exists(assembly.sourceFiles, file => file.Replace('\\', '/').StartsWith(PackagePath)))
                    leaks.Add(assembly.name);
            }
            return leaks;
        }
    }
}
