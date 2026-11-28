using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Qaplay.Editor
{
    /// <summary>qaplay의 에디터 화면이 함께 쓰는 것: 화면의 말(언어), 글과 버튼의 모양, 프로젝트 설정 파일.</summary>
    static class QaplayUi
    {
        const string LanguageKey = "qaplay.language";

        /// <summary>화면의 언어: auto(PC의 언어), en, ko. 이 PC의 이 사용자에게만 적용된다.</summary>
        public static string Language
        {
            get => EditorPrefs.GetString(LanguageKey, "auto");
            set => EditorPrefs.SetString(LanguageKey, value);
        }

        public static bool Korean => Language == "ko" || Language == "auto" && Application.systemLanguage == SystemLanguage.Korean;

        public static string L(string english, string korean) => Korean ? korean : english;

        public static string ProjectFolder => Directory.GetParent(Application.dataPath).FullName;

        /// <summary>프로젝트 설정 파일. 러너가 읽고, 저장소에 넣는다.</summary>
        public static string SettingsFile => Path.Combine(ProjectFolder, "qaplay.json");

        /// <summary>프로젝트 안의 경로는 프로젝트 기준으로 적는다(설정 파일을 다른 PC로 옮겨도 맞게). 밖이면 전체 경로다.</summary>
        public static string Relative(string path)
        {
            string relative = Path.GetRelativePath(ProjectFolder, path);
            return (relative.StartsWith("..") || Path.IsPathRooted(relative) ? path : relative).Replace('\\', '/');
        }

        // ---- 설정 파일 ----

        /// <summary>설정 파일의 내용. 없으면 빈 것. 읽을 수 없으면 null이고 error에 까닭이 담긴다.</summary>
        public static Dictionary<string, object> ReadSettings(out string error)
        {
            error = null;
            if (!File.Exists(SettingsFile)) return new Dictionary<string, object>();
            try
            {
                if (Json.Parse(File.ReadAllText(SettingsFile)) is Dictionary<string, object> json) return json;
                error = "it is not a JSON object";
            }
            catch (Exception e) when (e is FormatException || e is IOException)
            {
                error = e.Message;
            }
            return null;
        }

        /// <summary>
        /// 설정 파일의 일부를 바꾼다. 바꾸기 직전에 파일을 다시 읽으므로, 러너가 그 사이에 적은 것(시트 등)을 덮어쓰지 않는다.
        /// 파일을 읽을 수 없으면 아무것도 쓰지 않고 false.
        /// </summary>
        public static bool ChangeSettings(Action<Dictionary<string, object>> change)
        {
            Dictionary<string, object> json = ReadSettings(out _);
            if (json == null) return false;
            change(json);
            File.WriteAllText(SettingsFile, Json.Write(json, true), new System.Text.UTF8Encoding(false));
            return true;
        }

        /// <summary>그 이름의 부분(객체). 없으면 만들어 넣는다.</summary>
        public static Dictionary<string, object> Section(Dictionary<string, object> json, string name)
        {
            if (json.TryGetValue(name, out object value) && value is Dictionary<string, object> section) return section;
            var made = new Dictionary<string, object>();
            json[name] = made;
            return made;
        }

        // ---- 그리기 ----

        // 긴 글은 창의 폭에 맞춰 여러 줄로 꺾인다
        public static void Hint(string text) => GUILayout.Label(text, EditorStyles.wordWrappedMiniLabel);

        public static void Title(string text)
        {
            EditorGUILayout.Space(10);
            GUILayout.Label(text, EditorStyles.boldLabel);
        }

        // 한 줄: 설명과, 그 페이지를 여는 버튼
        public static void Step(string text, string button, string url)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(text, EditorStyles.wordWrappedLabel);
                if (GUILayout.Button(button, GUILayout.Width(150))) Application.OpenURL(url);
            }
        }

        public static void Copyable(string text)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel(text, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button(L("Copy", "복사"), GUILayout.Width(60))) EditorGUIUtility.systemCopyBuffer = text;
            }
        }

        /// <summary>러너를 부르는 동안 진행 막대를 보여 준다.</summary>
        public static QaplayRunner.Result Call(string busy, params string[] arguments)
        {
            EditorUtility.DisplayProgressBar("qaplay", busy, 0.5f);
            try
            {
                return QaplayRunner.Run(ProjectFolder, arguments);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
