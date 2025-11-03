#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using System.Net; // for Firebase .tgz downloads

namespace VoyagerSDK.Editor
{
    public class VoyagerIntegrationWizard : EditorWindow
    {
        // ======= UI State =======
        private int _step = 0; // 0=Dependencies, 1=Reserved, 2=Done
        private Vector2 _scroll;
        private bool _busy;
        private string _status = "Ready";
        private double _nextRepaint;

        // ======= Logo & Version =======
        private Texture2D _logo;
        private string _pkgVersion = "0.0.0";

        // ======= Install queue =======
        private Queue<InstallTask> _queue;
        private AddRequest _addReq;

        // ======= Installation kinds =======
        private enum InstallKind { Git, Registry, Tarball, Manual }

        // ======= Data model =======
        private class InstallTask
        {
            public string Label;          // Display name
            public InstallKind Kind;      // Git / Registry / Tarball / Manual
            public string Ref;            // Git: full url (?path + #tag) | Registry: name@ver | Tarball: download URL | Manual: help URL
            public bool Selected = true;
            public string Note;
            public bool NeedsGoogleRegistry;  // Google/Play packages
            public bool NeedsOpenUpmRegistry; // RevenueCat
        }

        private List<InstallTask> _packages;

        // ======= Styles =======
        private GUIStyle _titleStyle;
        private GUIStyle _h2Style;
        private GUIStyle _cardStyle;
        private GUIStyle _mutedStyle;
        private GUIStyle _tagStyle;

        // ======= Firebase constants =======
        private const string FirebaseVersion = "13.4.0";
        private const string FirebaseBase = "https://github.com/firebase/firebase-unity-sdk/releases/download/13.4.0/";
        private static string LocalCacheDir => Path.Combine(Directory.GetCurrentDirectory(), "Library/VoyagerSDK/Cache");

        [MenuItem("VoyagerSDK/Integration Wizard")]
        public static void ShowWindow()
        {
            var w = GetWindow<VoyagerIntegrationWizard>("Voyager Integration Wizard");
            w.minSize = new Vector2(840, 560);
            w.Init();
        }

        private void Init()
        {
            _logo = AssetDatabase.LoadAssetAtPath<Texture2D>("Packages/com.voyager.sdk.core/Editor/Resources/voyager_logo.png");
            _pkgVersion = TryReadPackageVersion() ?? _pkgVersion;

            // Package list (final, conflict-free)
            _packages = new List<InstallTask>
            {
                // Git UPM (correct path/tag)
                new InstallTask {
                    Label = "Adjust 5.4.4",
                    Kind = InstallKind.Git,
                    Ref = "https://github.com/adjust/unity_sdk.git?path=Assets/Adjust#v5.4.4",
                    Note = "Mobile attribution."
                },
                new InstallTask {
                    Label = "GameAnalytics 7.10.6",
                    Kind = InstallKind.Git,
                    Ref = "https://github.com/GameAnalytics/GA-SDK-UNITY.git#7.10.6",
                    Note = "Game analytics SDK."
                },
                new InstallTask {
                    Label = "EDM4U (External Dependency Manager) 1.2.186",
                    Kind = InstallKind.Git,
                    Ref = "https://github.com/googlesamples/unity-jar-resolver.git?path=upm#1.2.186",
                    Note = "Resolver for Android/iOS dependencies."
                },

                // Registry packages
                new InstallTask {
                    Label = "Play In-App Reviews 1.8.4",
                    Kind = InstallKind.Registry,
                    Ref = "com.google.play.review@1.8.4",
                    NeedsGoogleRegistry = true,
                    Note = "Google Play Core In-App Review."
                },
                new InstallTask {
                    Label = "RevenueCat 8.4.1",
                    Kind = InstallKind.Registry,
                    Ref = "com.revenuecat.purchases-unity@8.4.1",
                    NeedsOpenUpmRegistry = true,
                    Note = "IAP infrastructure via RevenueCat."
                },

                // Firebase via tarball (.tgz)
                new InstallTask {
                    Label = "Firebase Analytics 13.4.0",
                    Kind = InstallKind.Tarball,
                    Ref = FirebaseBase + "com.google.firebase.analytics-" + FirebaseVersion + ".tgz",
                    NeedsGoogleRegistry = true,
                    Note = "Installs from .tgz; Google registry needed for transitive deps."
                },
                new InstallTask {
                    Label = "Firebase Crashlytics 13.4.0",
                    Kind = InstallKind.Tarball,
                    Ref = FirebaseBase + "com.google.firebase.crashlytics-" + FirebaseVersion + ".tgz",
                    NeedsGoogleRegistry = true,
                    Note = "Installs from .tgz; Google registry needed for transitive deps."
                },
                new InstallTask {
                    Label = "Firebase Remote Config 13.4.0",
                    Kind = InstallKind.Tarball,
                    Ref = FirebaseBase + "com.google.firebase.remote-config-" + FirebaseVersion + ".tgz",
                    NeedsGoogleRegistry = true,
                    Note = "Installs from .tgz; Google registry needed for transitive deps."
                },

                // Manual steps
                new InstallTask {
                    Label = "Facebook SDK 18.0.0",
                    Kind = InstallKind.Manual,
                    Ref = "https://developers.facebook.com/docs/unity/",
                    Note = "Official UPM not provided by Meta. Use .unitypackage."
                },
                new InstallTask {
                    Label = "AppLovin MAX 8.5.0",
                    Kind = InstallKind.Manual,
                    Ref = "https://dash.applovin.com/documentation/mediation/unity/getting-started",
                    Note = "Integration Manager / .unitypackage recommended by AppLovin."
                },
            };

            BuildStyles();
        }

        private void BuildStyles()
        {
            _titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 18 };
            _h2Style = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };
            _cardStyle = new GUIStyle("HelpBox") { padding = new RectOffset(12, 12, 10, 12) };
            _mutedStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.75f,0.75f,0.75f) : new Color(0.3f,0.3f,0.3f) }
            };
            _tagStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 10,
                normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.8f,0.9f,1f) : new Color(0.15f,0.35f,0.6f) },
                alignment = TextAnchor.MiddleCenter
            };
        }

        private void OnGUI()
        {
            DrawHeader();
            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawStepper();
                GUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope())
                {
                    switch (_step)
                    {
                        case 0: DrawStep1(); break;
                        case 1: DrawStep2(); break;
                        case 2: DrawStep3(); break;
                    }
                }
            }

            DrawFooter();

            if (_busy && EditorApplication.timeSinceStartup > _nextRepaint)
            {
                _nextRepaint = EditorApplication.timeSinceStartup + 0.3f;
                Repaint();
            }
        }

        // ======= Header =======
        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (_logo != null)
                    GUILayout.Label(_logo, GUILayout.Width(64), GUILayout.Height(64));
                else
                    GUILayout.Box("VOYAGER", GUILayout.Width(64), GUILayout.Height(64));

                GUILayout.Space(12);

                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label("VoyagerSDK – Integration Wizard", _titleStyle);
                    GUILayout.Label("Step-by-step guided setup for required dependencies.", _mutedStyle);
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"Core v{_pkgVersion}", _mutedStyle, GUILayout.Height(20));
            }
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        }

        private void DrawStepper()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(200)))
            {
                DrawStepButton(0, "1. Dependencies");
                DrawStepButton(1, "2. (Reserved)");
                DrawStepButton(2, "3. Completed");
            }
        }

        private void DrawStepButton(int idx, string label)
        {
            var selected = _step == idx;
            var style = selected ? EditorStyles.miniButtonMid : EditorStyles.miniButton;
            using (new EditorGUI.DisabledScope(_busy))
            {
                if (GUILayout.Button(label, style, GUILayout.Height(28)))
                    _step = idx;
            }
        }

        // ======= Step 1 =======
        private void DrawStep1()
        {
            EditorGUILayout.LabelField("Step 1 — Install Required Packages", _h2Style);
            EditorGUILayout.HelpBox("Select the dependencies below and click “Install Selected”. The wizard will:\n• Ensure scoped registries (Google + OpenUPM)\n• Install Git packages (correct path + tag)\n• Install Registry packages\n• Download and install Firebase tarballs", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_busy))
                {
                    if (GUILayout.Button("Ensure Scoped Registries (Google + OpenUPM)", GUILayout.Height(24)))
                    {
                        var ok1 = EnsureGoogleScopedRegistry();
                        var ok2 = EnsureOpenUpmScopedRegistry();
                        _status = (ok1 && ok2) ? "Registries ensured." : "Registry update failed (see Console).";
                    }
                    if (GUILayout.Button("Select All", GUILayout.Width(100), GUILayout.Height(24)))
                        foreach (var p in _packages) p.Selected = true;
                    if (GUILayout.Button("Deselect All", GUILayout.Width(100), GUILayout.Height(24)))
                        foreach (var p in _packages) p.Selected = false;
                }
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(6);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var p in _packages)
            {
                using (new EditorGUILayout.VerticalScope(_cardStyle))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        p.Selected = EditorGUILayout.Toggle(p.Selected, GUILayout.Width(20));
                        GUILayout.Label(p.Label, EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        Tag(p.Kind.ToString().ToLowerInvariant());
                        if (p.NeedsGoogleRegistry) Tag("google-registry");
                        if (p.NeedsOpenUpmRegistry) Tag("openupm");
                    }

                    EditorGUILayout.LabelField("Ref:", p.Ref, _mutedStyle);
                    if (!string.IsNullOrEmpty(p.Note))
                        EditorGUILayout.LabelField(p.Note, _mutedStyle);
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_busy))
                {
                    if (GUILayout.Button("Install Selected", GUILayout.Width(200), GUILayout.Height(30)))
                        StartInstall();
                }
            }
        }

        private void Tag(string text)
        {
            using (new EditorGUILayout.HorizontalScope(GUILayout.Width(0))) { }
            var c = GUI.color;
            var bg = EditorGUIUtility.isProSkin ? new Color(0.15f, 0.2f, 0.28f, 1f) : new Color(0.85f, 0.9f, 1f, 1f);
            GUI.color = bg;
            GUILayout.BeginHorizontal("box", GUILayout.Height(18));
            GUI.color = c;
            GUILayout.Label(text, _tagStyle, GUILayout.Height(16));
            GUILayout.EndHorizontal();
        }

        // ======= Step 2 =======
        private void DrawStep2()
        {
            EditorGUILayout.LabelField("Step 2 — (Reserved)", _h2Style);
            EditorGUILayout.HelpBox("This page is intentionally left blank for now. Future configuration pages will appear here.", MessageType.None);
            GUILayout.FlexibleSpace();
        }

        // ======= Step 3 =======
        private void DrawStep3()
        {
            EditorGUILayout.LabelField("Step 3 — Integration Completed", _h2Style);
            EditorGUILayout.HelpBox("All selected dependencies have been processed. Check the Console for details. If any item failed, re-run the installer.", MessageType.Info);

            if (GUILayout.Button("Back to Step 1", GUILayout.Width(160), GUILayout.Height(26)))
                _step = 0;

            GUILayout.FlexibleSpace();
        }

        // ======= Footer =======
        private void DrawFooter()
        {
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_busy || _step == 0))
                {
                    if (GUILayout.Button("Back", GUILayout.Width(100))) _step = Mathf.Max(0, _step - 1);
                }
                GUILayout.FlexibleSpace();
                GUILayout.Label(_status, _mutedStyle, GUILayout.Height(20));
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_busy || _step == 2))
                {
                    if (GUILayout.Button("Next", GUILayout.Width(100))) _step = Mathf.Min(2, _step + 1);
                }
            }

            var right = new Rect(position.width - 160, position.height - 24, 160, 18);
            GUI.Label(right, $"Core v{_pkgVersion}", _mutedStyle);
        }

        // ======= Install logic =======
        private void StartInstall()
        {
            var list = _packages.Where(p => p.Selected).ToList();
            if (list.Count == 0)
            {
                _status = "No packages selected.";
                return;
            }

            // Ensure registries first if needed
            if (list.Any(p => p.NeedsGoogleRegistry)) EnsureGoogleScopedRegistry();
            if (list.Any(p => p.NeedsOpenUpmRegistry)) EnsureOpenUpmScopedRegistry();

            // Expand tarballs to local file paths by downloading
            PrepareFirebaseTarballs(list);

            _queue = new Queue<InstallTask>(list.Where(p => p.Kind != InstallKind.Manual));
            _busy = true;
            _status = "Starting installation queue…";
            InstallNext();
        }

        private void InstallNext()
        {
            if (_queue == null || _queue.Count == 0)
            {
                _busy = false;
                _status = "All done.";
                _step = 2;
                AssetDatabase.Refresh();
                return;
            }

            var t = _queue.Dequeue();

            string idToAdd;
            switch (t.Kind)
            {
                case InstallKind.Git:
                case InstallKind.Registry:
                    idToAdd = t.Ref; // full ref already
                    break;
                case InstallKind.Tarball:
                    // t.Ref becomes local file path after PrepareFirebaseTarballs()
                    idToAdd = "file:" + t.Ref.Replace("\\", "/");
                    break;
                default:
                    InstallNext();
                    return;
            }

            _status = $"Installing: {t.Label}";
            Debug.Log($"[VoyagerSDK] Installing {t.Label} → {idToAdd}");

            try
            {
                _addReq = Client.Add(idToAdd);
                EditorApplication.update += Progress;
            }
            catch (Exception e)
            {
                Debug.LogError($"[VoyagerSDK] Failed to add {t.Label}\n{e}");
                InstallNext();
            }
        }

        private void Progress()
        {
            if (_addReq == null) return;

            if (_addReq.IsCompleted)
            {
                EditorApplication.update -= Progress;

                if (_addReq.Status == StatusCode.Success)
                    Debug.Log($"[VoyagerSDK] Installed: {_addReq.Result.packageId}");
                else
                    Debug.LogError($"[VoyagerSDK] ERROR: {_addReq.Error?.message}");

                _addReq = null;
                InstallNext();
            }
        }

        private void PrepareFirebaseTarballs(List<InstallTask> list)
        {
            Directory.CreateDirectory(LocalCacheDir);

            foreach (var t in list)
            {
                if (t.Kind != InstallKind.Tarball) continue;

                try
                {
                    var fileName = Path.GetFileName(new Uri(t.Ref).AbsolutePath);
                    var localPath = Path.Combine(LocalCacheDir, fileName);

                    if (!File.Exists(localPath) || new FileInfo(localPath).Length < 10_000)
                    {
                        _status = $"Downloading {fileName}…";
                        using (var wc = new WebClient())
                        {
                            wc.DownloadFile(t.Ref, localPath);
                        }
                        Debug.Log($"[VoyagerSDK] Downloaded: {localPath}");
                    }

                    // Switch Ref to local path so InstallNext() can add it
                    t.Ref = localPath;
                }
                catch (Exception e)
                {
                    Debug.LogError($"[VoyagerSDK] Failed downloading tarball for {t.Label} from {t.Ref}\n{e}");
                }
            }
        }

        // ======= Manifest helpers (BOM-safe) =======
        private static string ReadTextNoBom(string path)
        {
            var bytes = File.ReadAllBytes(path);
            // UTF8 BOM = EF BB BF
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteUtf8NoBom(string path, string content)
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(path, content, utf8NoBom);
        }

        private bool EnsureGoogleScopedRegistry()
        {
            try
            {
                var path = Path.Combine(Directory.GetCurrentDirectory(), "Packages/manifest.json");
                if (!File.Exists(path))
                {
                    Debug.LogError("[VoyagerSDK] manifest.json not found.");
                    return false;
                }

                var json = ReadTextNoBom(path);
                json = json.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

                // If scopedRegistries missing → create one with Google
                if (!json.Contains("\"scopedRegistries\""))
                {
                    var block =
                        ",\n  \"scopedRegistries\": [\n" +
                        "    {\n" +
                        "      \"name\": \"Google\",\n" +
                        "      \"url\": \"https://packages.unity.com\",\n" +
                        "      \"scopes\": [ \"com.google\", \"com.google.firebase\", \"com.google.play\", \"com.google.external-dependency-manager\" ]\n" +
                        "    }\n" +
                        "  ]\n";

                    json = json.TrimEnd();
                    if (json.EndsWith("}"))
                        json = json.Substring(0, json.Length - 1) + block + "}";

                    WriteUtf8NoBom(path, json);
                    AssetDatabase.Refresh();
                    Debug.Log("[VoyagerSDK] Google scoped registry added.");
                    return true;
                }
                else
                {
                    // If scopedRegistries exists, ensure one entry called Google with the right scopes
                    var updated = EnsureOrReplaceRegistry(json,
                        registryName: "Google",
                        url: "https://packages.unity.com",
                        scopes: new[] { "com.google", "com.google.firebase", "com.google.play", "com.google.external-dependency-manager" },
                        replaceOnlyScopes: true);

                    if (!ReferenceEquals(updated, json))
                    {
                        WriteUtf8NoBom(path, updated);
                        AssetDatabase.Refresh();
                        Debug.Log("[VoyagerSDK] Google scoped registry ensured/updated.");
                    }
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[VoyagerSDK] Failed to ensure Google scoped registry:\n" + e);
                return false;
            }
        }

        private bool EnsureOpenUpmScopedRegistry()
        {
            try
            {
                var path = Path.Combine(Directory.GetCurrentDirectory(), "Packages/manifest.json");
                if (!File.Exists(path))
                {
                    Debug.LogError("[VoyagerSDK] manifest.json not found.");
                    return false;
                }

                var json = ReadTextNoBom(path);
                json = json.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

                // Ensure that OpenUPM is present and **only** claims com.revenuecat (avoid scope overlaps)
                if (!json.Contains("\"scopedRegistries\""))
                {
                    var blockNew =
                        ",\n  \"scopedRegistries\": [\n" +
                        "    {\n" +
                        "      \"name\": \"package.openupm.com\",\n" +
                        "      \"url\": \"https://package.openupm.com\",\n" +
                        "      \"scopes\": [ \"com.revenuecat\" ]\n" +
                        "    }\n" +
                        "  ]\n";

                    json = json.TrimEnd();
                    if (json.EndsWith("}"))
                        json = json.Substring(0, json.Length - 1) + blockNew + "}";

                    WriteUtf8NoBom(path, json);
                    AssetDatabase.Refresh();
                    Debug.Log("[VoyagerSDK] OpenUPM scoped registry added.");
                    return true;
                }
                else
                {
                    var updated = EnsureOrReplaceRegistry(json,
                        registryName: "package.openupm.com",
                        url: "https://package.openupm.com",
                        scopes: new[] { "com.revenuecat" },
                        replaceOnlyScopes: true);

                    if (!ReferenceEquals(updated, json))
                    {
                        WriteUtf8NoBom(path, updated);
                        AssetDatabase.Refresh();
                        Debug.Log("[VoyagerSDK] OpenUPM scoped registry scopes set to [com.revenuecat].");
                    }
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[VoyagerSDK] Failed to ensure OpenUPM scoped registry:\n" + e);
                return false;
            }
        }

        // Replace or insert a scoped registry entry by name; when replaceOnlyScopes=true, only the scopes array is replaced/created.
        private string EnsureOrReplaceRegistry(string manifestJson, string registryName, string url, IEnumerable<string> scopes, bool replaceOnlyScopes)
        {
            // naive but effective string manipulation (keeps us editor-only, no extra JSON libs)
            var nameToken = "\"name\": \"" + registryName + "\"";
            var idxName = manifestJson.IndexOf(nameToken, StringComparison.Ordinal);
            if (idxName >= 0)
            {
                // Found an entry with this name — replace scopes (and optionally url)
                var scopesIdx = manifestJson.IndexOf("\"scopes\"", idxName, StringComparison.Ordinal);
                if (scopesIdx < 0) return manifestJson; // malformed; bail

                var lb = manifestJson.IndexOf('[', scopesIdx);
                var rb = manifestJson.IndexOf(']', lb + 1);
                if (lb < 0 || rb < 0) return manifestJson;

                var rebuiltScopes = " " + string.Join(", ", scopes.Select(s => $"\"{s}\"")) + " ";
                var replaced = manifestJson.Substring(0, lb + 1) + rebuiltScopes + manifestJson.Substring(rb);

                if (!replaceOnlyScopes)
                {
                    // best-effort url replacement
                    var urlKey = "\"url\"";
                    var urlIdx = replaced.IndexOf(urlKey, idxName, StringComparison.Ordinal);
                    if (urlIdx >= 0)
                    {
                        var q1 = replaced.IndexOf('"', urlIdx + urlKey.Length);
                        var q2 = replaced.IndexOf('"', q1 + 1);
                        if (q1 > 0 && q2 > q1)
                        {
                            replaced = replaced.Substring(0, q1 + 1) + url + replaced.Substring(q2);
                        }
                    }
                }
                return replaced;
            }
            else
            {
                // Insert a new entry at the start of scopedRegistries array
                var srKey = "\"scopedRegistries\"";
                var srIdx = manifestJson.IndexOf(srKey, StringComparison.Ordinal);
                if (srIdx < 0) return manifestJson; // unexpected; someone else created array — we handled the no-array path earlier

                var arrStart = manifestJson.IndexOf('[', srIdx);
                if (arrStart < 0) return manifestJson;

                var entry =
                    "\n    {\n" +
                    $"      \"name\": \"{registryName}\",\n" +
                    $"      \"url\": \"{url}\",\n" +
                    $"      \"scopes\": [ {string.Join(", ", scopes.Select(s => $"\"{s}\""))} ]\n" +
                    "    },";

                return manifestJson.Insert(arrStart + 1, entry);
            }
        }

        private string TryReadPackageVersion()
        {
            try
            {
                var pkgInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.voyager.sdk.core");
                if (pkgInfo != null && !string.IsNullOrEmpty(pkgInfo.version))
                    return pkgInfo.version;
            }
            catch { }

            try
            {
                var pathA = "Packages/com.voyager.sdk.core/package.json";
                var pathB = Path.GetFullPath(Path.Combine(Application.dataPath, "../Packages/com.voyager.sdk.core/package.json"));
                var path = File.Exists(pathA) ? pathA : pathB;
                if (File.Exists(path))
                {
                    var json = ReadTextNoBom(path);
                    var key = "\"version\"";
                    var i = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                    if (i >= 0)
                    {
                        var j = json.IndexOf('"', i + key.Length);
                        var k = json.IndexOf('"', j + 1);
                        return json.Substring(j + 1, k - j - 1);
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
#endif
