using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ClaudeBridge
{
    /// <summary>
    /// Lets an external tool drive this editor by dropping a command file into
    /// <c>Temp/ClaudeBridge</c> and reading the result written back beside it.
    /// </summary>
    /// <remarks>
    /// Why a file and not a socket: no ports, no dependencies, no third-party editor code, and a
    /// transcript of what was asked and what happened stays on disk to inspect afterwards.
    ///
    /// Why <c>Temp/</c> and not <c>Assets/</c>: anything written under Assets triggers an asset
    /// import, so polling there would put the editor in a reimport loop.
    ///
    /// Why this lives in its own assembly with no references: when compilation fails, Unity keeps
    /// running the last assemblies that built. If the bridge shared an assembly with the code being
    /// edited, it would go down at exactly the moment it is needed to report that breakage.
    /// </remarks>
    [InitializeOnLoad]
    public static class ClaudeBridge
    {
        #region Constants

        private const string BridgeDirectory = "Temp/ClaudeBridge";
        private const string CommandFile = BridgeDirectory + "/command.txt";
        private const string ResultFile = BridgeDirectory + "/result.txt";
        private const double PollSeconds = 0.5;

        #endregion

        #region Fields

        private static double s_NextPoll;
        private static readonly List<string> s_CapturedLogs = new List<string>();

        #endregion

        #region Lifecycle

        static ClaudeBridge()
        {
            EditorApplication.update += Poll;
        }

        #endregion

        #region Polling

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < s_NextPoll)
            {
                return;
            }

            s_NextPoll = EditorApplication.timeSinceStartup + PollSeconds;

            // Running a command mid-compile would execute against assemblies about to be replaced,
            // and the result would describe a build that no longer exists a second later.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            if (!File.Exists(CommandFile))
            {
                return;
            }

            string command;
            try
            {
                command = File.ReadAllText(CommandFile).Trim();
                File.Delete(CommandFile);
            }
            catch (IOException)
            {
                // Half-written file; try again on the next tick.
                return;
            }

            Execute(command);
        }

        #endregion

        #region Command Execution

        private static void Execute(string command)
        {
            s_CapturedLogs.Clear();
            Application.logMessageReceived += CaptureLog;

            string status;
            string detail;

            try
            {
                detail = Dispatch(command, out status);
            }
            catch (Exception exception)
            {
                status = "ERROR";
                detail = exception.GetType().Name + ": " + exception.Message;
            }
            finally
            {
                Application.logMessageReceived -= CaptureLog;
            }

            WriteResult(command, status, detail);
        }

        /// <summary>
        /// Runs one command and returns a human-readable description of what happened.
        /// </summary>
        private static string Dispatch(string command, out string status)
        {
            status = "OK";

            if (command.Equals("ping", StringComparison.OrdinalIgnoreCase))
            {
                return "alive; Unity " + Application.unityVersion;
            }

            if (command.Equals("info", StringComparison.OrdinalIgnoreCase))
            {
                return DescribeState();
            }

            if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                // Unity 6 has no Edit/Play menu item; play mode is a toolbar button, so it can only
                // be left through the API.
                if (!EditorApplication.isPlaying)
                {
                    return "already stopped";
                }

                EditorApplication.isPlaying = false;
                return "leaving play mode";
            }

            if (command.StartsWith("serialization", StringComparison.OrdinalIgnoreCase))
            {
                // ProjectSettings/EditorSettings.asset can say ForceText while the editor is running
                // with something else in memory, so report what the editor actually believes.
                string argument = command.Substring("serialization".Length).Trim();
                if (argument.Equals("text", StringComparison.OrdinalIgnoreCase))
                {
                    EditorSettings.serializationMode = SerializationMode.ForceText;
                    AssetDatabase.SaveAssets();
                }

                return "serializationMode = " + EditorSettings.serializationMode;
            }

            if (command.Equals("reserialize", StringComparison.OrdinalIgnoreCase))
            {
                // A scene already on disk in binary stays binary through an ordinary save. Forcing a
                // reserialize rewrites it in whatever the current serialization mode is.
                string path = SceneManager.GetActiveScene().path;
                if (string.IsNullOrEmpty(path))
                {
                    status = "ERROR";
                    return "active scene has never been saved, so it has no path to reserialize";
                }

                AssetDatabase.ForceReserializeAssets(new[] { path });
                AssetDatabase.Refresh();
                return "reserialized " + path + " as " + EditorSettings.serializationMode;
            }

            if (command.StartsWith("open ", StringComparison.OrdinalIgnoreCase))
            {
                string path = command.Substring(5).Trim();
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    status = "REFUSED";
                    return "in play mode";
                }

                Scene opened = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                status = opened.IsValid() ? "OK" : "ERROR";
                return opened.IsValid() ? "opened " + opened.path : "could not open " + path;
            }

            if (command.Equals("guards", StringComparison.OrdinalIgnoreCase))
            {
                // Asks the loaded scene what it contains. Grepping the scene file cannot answer this
                // when Unity has written it in binary, where GUIDs are not stored as searchable text.
                var brains = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
                int guards = 0, armed = 0, killable = 0, aiming = 0;
                foreach (MonoBehaviour behaviour in brains)
                {
                    if (behaviour == null || behaviour.GetType().Name != "GuardBrain") continue;
                    guards++;
                    GameObject go = behaviour.gameObject;
                    if (go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) armed++;
                    if (go.GetComponent<Collider>() != null) killable++;
                    foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                        if (t.name == "Muzzle") { aiming++; break; }
                }

                return $"guards: {guards}, with a body: {armed}, with a collider: {killable}, with a muzzle: {aiming}";
            }

            if (command.Equals("save", StringComparison.OrdinalIgnoreCase))
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    status = "REFUSED";
                    return "in play mode; saving during play throws and can leave a malformed scene on disk";
                }

                bool saved = EditorSceneManager.SaveOpenScenes();
                status = saved ? "OK" : "ERROR";
                return saved
                    ? "saved " + SceneManager.GetActiveScene().path
                    : "SaveOpenScenes returned false";
            }

            if (command.StartsWith("menu ", StringComparison.OrdinalIgnoreCase))
            {
                string path = command.Substring(5).Trim();
                bool executed = EditorApplication.ExecuteMenuItem(path);
                status = executed ? "OK" : "ERROR";
                return executed
                    ? "executed menu item: " + path
                    : "no such menu item, or it refused to run: " + path;
            }

            status = "ERROR";
            return "unknown command. Supported: ping | info | stop | save | serialization [text] | menu <Menu/Path>";
        }

        /// <summary>
        /// Summarises the state most often needed after a command: which scene is open, whether it
        /// has unsaved changes, and whether the editor is mid-play or mid-compile.
        /// </summary>
        private static string DescribeState()
        {
            Scene scene = SceneManager.GetActiveScene();
            return string.Join("\n", new[]
            {
                "scene: " + (string.IsNullOrEmpty(scene.path) ? "<unsaved scene>" : scene.path),
                "dirty: " + scene.isDirty,
                "playing: " + EditorApplication.isPlaying,
                "compiling: " + EditorApplication.isCompiling,
                "rootObjects: " + scene.rootCount
            });
        }

        #endregion

        #region Result Reporting

        private static void CaptureLog(string message, string stackTrace, LogType type)
        {
            // Message only: stack traces would bury the one line that matters.
            s_CapturedLogs.Add(type + ": " + message);
        }

        private static void WriteResult(string command, string status, string detail)
        {
            Directory.CreateDirectory(BridgeDirectory);

            var lines = new List<string>
            {
                "command: " + command,
                "status: " + status,
                "time: " + DateTime.Now.ToString("HH:mm:ss"),
                "detail: " + detail
            };

            if (s_CapturedLogs.Count > 0)
            {
                lines.Add("--- console ---");
                lines.AddRange(s_CapturedLogs);
            }

            File.WriteAllText(ResultFile, string.Join("\n", lines) + "\n");
        }

        #endregion

        #region Menu

        /// <summary>
        /// Confirms the bridge is loaded and says where it is listening.
        /// </summary>
        [MenuItem("Tools/Claude/Bridge Status")]
        private static void BridgeStatus()
        {
            EditorUtility.DisplayDialog(
                "Claude Bridge",
                "Listening.\n\nCommand file: " + CommandFile +
                "\nResult file: " + ResultFile +
                "\n\nPolls every " + PollSeconds + "s while the editor is ticking.",
                "OK");
        }

        #endregion
    }
}
