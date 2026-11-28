using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Qaplay.Runner
{
    /// <summary>
    /// 모듈이 더하는 명령 하나: qaplay {Group} {Name} ... (예: Google 모듈의 qaplay tc key).
    /// 코어는 그 묶음(tc)의 하위 명령 가운데 자기가 모르는 것을 모듈에게 넘긴다.
    /// </summary>
    interface IModuleCommand
    {
        string Group { get; }
        string Name { get; }
        /// <summary>그 묶음의 사용법에 덧붙일 줄.</summary>
        string Usage { get; }
        int Run(Args args);
    }

    /// <summary>모듈의 자체 검사. qaplay selftest가 설치된 모듈마다 부른다. 실패하면 InvalidOperationException.</summary>
    interface IModuleSelfTest
    {
        void Run();
    }

    /// <summary>
    /// 선택 설치 모듈. 러너 옆 modules 폴더의 폴더 하나가 모듈 하나다: modules/xlsx/qaplay.xlsx.dll과 그것이 쓰는 라이브러리들.
    /// 코어(qaplay)는 외부 라이브러리를 쓰지 않는다. 연동(Excel, Google Sheets, Jira)은 모듈이 코어의 확장 지점
    /// (ISheetProvider, IIssueTracker, IModuleCommand)을 구현해 더한다. 모듈을 빼면 그 연동만 없어지고 나머지는 그대로 돈다.
    /// </summary>
    // ponytail: 모듈은 이 저장소의 것만 있어 코어가 InternalsVisibleTo로 안을 보여 준다. 다른 사람이 모듈을 만들게 되면
    // 확장 지점을 public 계약 어셈블리로 떼어 낸다
    sealed class Modules
    {
        public sealed class Module
        {
            public string name, folder, file;
            public Assembly assembly;
            /// <summary>읽지 못한 까닭. 읽었으면 null.</summary>
            public string problem;
        }

        // 모듈이 쓰는 라이브러리는 그 모듈의 폴더에서 찾는다(모듈마다 따로). 코어와 .NET은 하나만 둔다: 같은 타입이어야 확장 지점이 맞는다
        sealed class ModuleContext : AssemblyLoadContext
        {
            static readonly string Core = typeof(Modules).Assembly.GetName().Name;
            readonly AssemblyDependencyResolver m_Resolver;

            public ModuleContext(string file) : base(Path.GetFileNameWithoutExtension(file))
            {
                m_Resolver = new AssemblyDependencyResolver(file);
            }

            protected override Assembly Load(AssemblyName name)
            {
                if (name.Name == Core) return null;
                string path = m_Resolver.ResolveAssemblyToPath(name);
                return path == null ? null : LoadFromAssemblyPath(path);
            }

            protected override IntPtr LoadUnmanagedDll(string name)
            {
                string path = m_Resolver.ResolveUnmanagedDllToPath(name);
                return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
            }
        }

        readonly List<Module> m_All = new List<Module>();

        /// <summary>모듈을 찾는 폴더. 없을 수도 있다(모듈 없이 코어만).</summary>
        public string Folder { get; }
        public IReadOnlyList<Module> All => m_All;

        public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "modules");

        /// <summary>모듈 없이 코어만.</summary>
        public static readonly Modules None = new Modules(null);

        Modules(string folder)
        {
            Folder = folder;
        }

        /// <summary>폴더의 모듈들을 읽는다. 읽지 못한 모듈은 까닭(problem)과 함께 남고 나머지는 쓴다.</summary>
        public static Modules Load(string folder = null)
        {
            var modules = new Modules(folder ?? DefaultFolder);
            if (!Directory.Exists(modules.Folder)) return modules;
            var folders = new List<string>(Directory.GetDirectories(modules.Folder));
            folders.Sort(StringComparer.Ordinal);
            foreach (string place in folders)
            {
                string name = Path.GetFileName(place);
                var module = new Module { name = name, folder = place, file = Path.Combine(place, "qaplay." + name + ".dll") };
                modules.m_All.Add(module);
                if (!File.Exists(module.file))
                {
                    module.problem = "no " + Path.GetFileName(module.file) + " in " + place;
                    continue;
                }
                try
                {
                    module.assembly = new ModuleContext(module.file).LoadFromAssemblyPath(module.file);
                }
                catch (Exception e) when (e is BadImageFormatException || e is FileLoadException || e is IOException)
                {
                    module.problem = "cannot load " + module.file + ": " + e.Message;
                }
            }
            return modules;
        }

        /// <summary>읽은 모듈들이 구현한 T를 하나씩 만든다(매개변수 없는 생성자). 만들지 못한 모듈은 problem에 까닭이 남는다.</summary>
        public List<T> Create<T>() where T : class
        {
            var made = new List<T>();
            foreach (Module module in m_All)
            {
                if (module.assembly == null) continue;
                Type[] types;
                try
                {
                    types = module.assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    // 모듈이 쓰는 라이브러리가 빠졌거나, 다른 버전의 코어에 맞춰 만든 모듈
                    module.problem = "cannot load " + module.file + ": " + (e.LoaderExceptions.Length > 0 ? e.LoaderExceptions[0]?.Message : e.Message);
                    module.assembly = null;
                    continue;
                }
                foreach (Type type in types)
                    if (typeof(T).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface && type.GetConstructor(Type.EmptyTypes) != null)
                        made.Add((T)Activator.CreateInstance(type));
            }
            return made;
        }

        /// <summary>qaplay modules: 읽은 모듈과 그 모듈이 더하는 것(시트의 종류, 명령)을 보여 준다. 읽지 못한 모듈이 있으면 종료 코드 1.</summary>
        public static int Command(Args args, Modules modules, SheetProviders sheets)
        {
            var list = new List<object>();
            var types = new List<string>();
            foreach (ISheetProvider provider in sheets.All) types.Add(provider.Type);
            List<ISheetProvider> fromModules = modules.Create<ISheetProvider>();
            List<IModuleCommand> commands = modules.Create<IModuleCommand>();
            List<IIssueTracker> trackers = modules.Create<IIssueTracker>();
            bool broken = false;
            foreach (Module module in modules.All)
            {
                var provides = new List<string>();
                foreach (ISheetProvider provider in fromModules)
                    if (provider.GetType().Assembly == module.assembly) provides.Add("sheet " + provider.Type);
                foreach (IIssueTracker tracker in trackers)
                    if (tracker.GetType().Assembly == module.assembly) provides.Add("issues " + tracker.Type);
                foreach (IModuleCommand command in commands)
                    if (command.GetType().Assembly == module.assembly) provides.Add("command " + command.Group + " " + command.Name);
                broken |= module.problem != null;
                Console.WriteLine(module.name + ": " + (module.problem ?? string.Join(", ", provides)));
                list.Add(new Dictionary<string, object> { ["name"] = module.name, ["file"] = module.file, ["provides"] = provides, ["problem"] = module.problem });
            }
            Console.WriteLine(modules.All.Count == 0
                ? "no modules in " + modules.Folder + " (the core alone: CSV sheets)"
                : modules.All.Count + " module(s) in " + modules.Folder);
            Console.WriteLine("sheet types: " + string.Join(", ", types));
            List<string> issueTypes = trackers.ConvertAll(tracker => tracker.Type);
            Console.WriteLine("issue trackers: " + (issueTypes.Count == 0 ? "(none: bugs are kept on this PC only)" : string.Join(", ", issueTypes)));
            if (args.Json) Program.PrintJson(new Dictionary<string, object> { ["folder"] = modules.Folder, ["modules"] = list, ["sheets"] = types, ["issues"] = issueTypes });
            return broken ? 1 : 0;
        }
    }
}
