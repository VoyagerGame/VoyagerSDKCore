#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace VoyagerSDK.Editor
{
    public class VoyagerIntegrationWizard : EditorWindow
    {
        // Kurulabilir “önerilen” paketlerin tanımı
        // Buraya senin eklemek istediklerini koy: id ya da git url
        private class Pkg
        {
            public string Label;
            public string IdOrUrl;
            public bool Selected;
            public string Note;
            public Action PostInstall; // örn: define symbol ekle vs.
        }

        private Vector2 _scroll;
        private List<Pkg> _pkgs;
        private AddRequest _currentAdd;
        private Queue<Pkg> _queue;
        private string _status = "Hazır";
        private bool _busy;

        [MenuItem("VoyagerSDK/Integration Wizard")]
        public static void ShowWindow()
        {
            var w = GetWindow<VoyagerIntegrationWizard>("VoyagerSDK Wizard");
            w.minSize = new Vector2(520, 420);
            w.Init();
        }

        private void Init()
        {
            _pkgs = new List<Pkg>
            {
                new Pkg {
                    Label = "GameAnalytics (UPM/Git)",
                    IdOrUrl = "https://github.com/GameAnalytics/GA-SDK-UNITY.git#v7.10.0",
                    Selected = true,
                    Note = "Resmi GA Unity SDK (git)."
                },
                new Pkg {
                    Label = "Firebase Core (Registry üzerinden)",
                    IdOrUrl = "com.google.firebase.app",
                    Selected = false,
                    Note = "Google UPM registry gerektirir (aşağıdaki 'Scoped Registries' butonunu kullan)."
                },
                new Pkg {
                    Label = "Unity IAP",
                    IdOrUrl = "com.unity.purchasing",
                    Selected = false,
                    Note = "Resmi Unity IAP."
                },
                new Pkg {
                    Label = "Unity Mediation Ads",
                    IdOrUrl = "com.unity.services.mediation",
                    Selected = false,
                    Note = "Unity Ads/Mediation."
                }
                // Buraya Facebook SDK gibi diğerleri (varsa git UPM sürümü) eklenebilir.
            };
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("VoyagerSDK Integration Wizard", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Kurmak istediğin paketleri seç. 'Install Selected' ile tek tek sırayla kurulur.", MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var p in _pkgs)
            {
                EditorGUILayout.BeginVertical("box");
                p.Selected = EditorGUILayout.ToggleLeft(p.Label, p.Selected);
                EditorGUILayout.LabelField("ID/URL:", p.IdOrUrl);
                if (!string.IsNullOrEmpty(p.Note))
                    EditorGUILayout.LabelField("Not:", p.Note, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(_busy))
            {
                if (GUILayout.Button("Add Scoped Registries (Firebase/Google)"))
                {
                    AddGoogleScopedRegistry();
                }
                if (GUILayout.Button("Install Selected"))
                {
                    StartInstall();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Durum: " + _status);
        }

        private void StartInstall()
        {
            var list = new List<Pkg>();
            foreach (var p in _pkgs) if (p.Selected) list.Add(p);
            if (list.Count == 0)
            {
                _status = "Hiç paket seçilmedi.";
                return;
            }

            _queue = new Queue<Pkg>(list);
            _busy = true;
            _status = "Kurulum kuyruğa alındı...";
            InstallNext();
        }

        private void InstallNext()
        {
            if (_queue == null || _queue.Count == 0)
            {
                _busy = false;
                _status = "Tamamlandı.";
                AssetDatabase.Refresh();
                return;
            }

            var p = _queue.Dequeue();
            _status = "Kuruluyor: " + p.Label;

            try
            {
                _currentAdd = Client.Add(p.IdOrUrl);
                EditorApplication.update += Progress;
            }
            catch (Exception e)
            {
                Debug.LogError($"Paket eklenemedi: {p.Label}\n{e}");
                InstallNext();
            }
        }

        private void Progress()
        {
            if (_currentAdd == null) return;

            if (_currentAdd.IsCompleted)
            {
                EditorApplication.update -= Progress;

                if (_currentAdd.Status == StatusCode.Success)
                {
                    Debug.Log($"[VoyagerSDK] Kuruldu: {_currentAdd.Result.packageId}");
                }
                else if (_currentAdd.Status >= StatusCode.Failure)
                {
                    Debug.LogError($"[VoyagerSDK] HATA: {_currentAdd.Error?.message}");
                }

                _currentAdd = null;
                InstallNext();
            }
        }

        private void AddGoogleScopedRegistry()
        {
            // Packages/manifest.json’a scopedRegistries ekler
            // Basit bir util: mevcut manifest’i yükle -> json değiştir -> kaydet
            var manifestPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Packages/manifest.json");
            if (!System.IO.File.Exists(manifestPath))
            {
                _status = "manifest.json bulunamadı.";
                return;
            }

            var json = System.IO.File.ReadAllText(manifestPath);
            var marker = "\"scopedRegistries\"";
            if (json.Contains(marker))
            {
                _status = "Scoped registries zaten var (kontrol et).";
                return;
            }

            // Çok basit ekleme (dayanıklı bir JSON parser ile yapman daha iyi)
            var insert = @",
  ""scopedRegistries"": [
    {
      ""name"": ""Google"",
      ""url"": ""https://packages.unity.com"",
      ""scopes"": [ ""com.google.firebase"", ""com.google"",""com.google.external-dependency-manager"" ]
    }
  ]";

            json = json.TrimEnd('}', ' ', '\n', '\r', '\t') + insert + "\n}";
            System.IO.File.WriteAllText(manifestPath, json);
            AssetDatabase.Refresh();
            _status = "Google scoped registry eklendi.";
        }
    }
}
#endif
