using System;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace TheDevAtlas.EditorTools
{
    /// <summary>
    /// A console utility that extracts and copies all currently visible console entries to the system clipboard.
    /// Injects a custom "Copy Stack" trigger button into the Unity Console toolbar.
    /// </summary>
    [InitializeOnLoad]
    public static class ConsoleCopyStack
    {
        private static EditorWindow _consoleWindow;
        private static double _lastCheckTime;

        // Cached Reflection Data
        private static Type _logEntriesType;
        private static Type _logEntryType;
        private static MethodInfo _getCountMethod;
        private static MethodInfo _getEntryMethod;
        private static MethodInfo _startGettingMethod;
        private static MethodInfo _endGettingMethod;
        private static FieldInfo _messageField;
        private static object _logEntryInstance;
        private static bool _reflectionInitialized;

        static ConsoleCopyStack()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        /// <summary>
        /// Initializes reflection to access Unity's internal LogEntries API safely.
        /// </summary>
        private static void InitializeReflection()
        {
            if (_reflectionInitialized) return;

            try
            {
                _logEntriesType = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                _logEntryType = Type.GetType("UnityEditor.LogEntry,UnityEditor");

                if (_logEntriesType != null && _logEntryType != null)
                {
                    const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public;
                    
                    _getCountMethod = _logEntriesType.GetMethod("GetCount", staticFlags);
                    _getEntryMethod = _logEntriesType.GetMethod("GetEntryInternal", staticFlags);
                    _startGettingMethod = _logEntriesType.GetMethod("StartGettingEntries", staticFlags);
                    _endGettingMethod = _logEntriesType.GetMethod("EndGettingEntries", staticFlags);

                    _messageField = _logEntryType.GetField("message", BindingFlags.Instance | BindingFlags.Public);
                    
                    if (_messageField != null)
                    {
                        _logEntryInstance = Activator.CreateInstance(_logEntryType);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ConsoleCopyStack] Reflection initialization failed: {ex.Message}");
            }
            finally
            {
                _reflectionInitialized = true;
            }
        }

        private static void OnEditorUpdate()
        {
            if (_consoleWindow != null) return;

            // Throttle the heavy Resource search to once per second
            if (EditorApplication.timeSinceStartup - _lastCheckTime < 1.0) return;
            _lastCheckTime = EditorApplication.timeSinceStartup;

            Type consoleWindowType = Type.GetType("UnityEditor.ConsoleWindow,UnityEditor");
            if (consoleWindowType == null) return;

            var windows = Resources.FindObjectsOfTypeAll(consoleWindowType);
            if (windows.Length > 0)
            {
                _consoleWindow = windows[0] as EditorWindow;
                if (_consoleWindow != null)
                {
                    AttachCopyButton(_consoleWindow);
                }
            }
        }

        /// <summary>
        /// Injects the custom button into the Console window's UI Toolkit toolbar.
        /// </summary>
        private static void AttachCopyButton(EditorWindow console)
        {
            var root = console.rootVisualElement;

            if (root.Q<Button>("btn-copy-stack") != null) return;

            var copyBtn = new ToolbarButton(CopyVisibleLogs)
            {
                name = "btn-copy-stack",
                focusable = false 
            };

            var btnText = new Label("Copy Stack");
            btnText.style.position = Position.Relative;
            btnText.style.top = 1; 
            copyBtn.Add(btnText);

            copyBtn.style.height = 20; 
            copyBtn.style.marginTop = 0;
            copyBtn.style.marginBottom = 0;
            copyBtn.style.marginRight = 0;
            copyBtn.style.marginLeft = -1; 

            var toolbar = root.Q(className: "unity-toolbar");
            if (toolbar != null)
            {
                // Safely insert based on actual child count
                int insertIndex = Mathf.Min(5, toolbar.childCount);
                toolbar.Insert(insertIndex, copyBtn);
            }
            else
            {
                copyBtn.style.position = Position.Absolute;
                copyBtn.style.left = 244; 
                copyBtn.style.top = 0;
                root.Add(copyBtn);
            }
        }

        /// <summary>
        /// Extracts visible logs using internal APIs and writes them to the clipboard.
        /// </summary>
        private static void CopyVisibleLogs()
        {
            InitializeReflection();

            if (_getCountMethod == null || _getEntryMethod == null || _logEntryInstance == null)
            {
                Debug.LogError("[ConsoleCopyStack] Unity version incompatible or reflection failed.");
                return;
            }

            // Pre-allocate buffer to prevent memory spikes on large log stacks
            var sb = new StringBuilder(1024 * 16); 
            int copiedEntriesCount = 0;

            try
            {
                _startGettingMethod?.Invoke(null, null);
                int count = (int)_getCountMethod.Invoke(null, null);

                object[] entryArgs = { 0, _logEntryInstance };

                for (int i = 0; i < count; i++)
                {
                    entryArgs[0] = i;
                    _getEntryMethod.Invoke(null, entryArgs);
                    
                    if (_messageField.GetValue(_logEntryInstance) is string message && !string.IsNullOrEmpty(message))
                    {
                        // Filter out this tool's own feedback
                        if (message.Contains("[ConsoleCopyStack]")) continue;

                        sb.AppendLine(message);
                        sb.AppendLine("-----------------------------------------");
                        copiedEntriesCount++;
                    }
                }
            }
            finally
            {
                // Crucial: Ensure Unity's internal log fetching is always closed, even if an error occurs
                _endGettingMethod?.Invoke(null, null);
            }

            if (sb.Length > 0)
            {
                GUIUtility.systemCopyBuffer = sb.ToString();
                Debug.Log($"[ConsoleCopyStack] Successfully copied {copiedEntriesCount} visible entries to clipboard.");
            }
            else
            {
                Debug.Log("[ConsoleCopyStack] No visible entries to copy.");
            }
        }
    }
}