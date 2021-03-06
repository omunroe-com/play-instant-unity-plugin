// Copyright 2018 Google LLC
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if UNITY_2018_1_OR_NEWER
using UnityEditor.Build.Reporting;
#endif

namespace GooglePlayInstant.Editor
{
    /// <summary>
    /// Builder for Play Instant APKs.
    /// </summary>
    public static class PlayInstantBuilder
    {
        private const string BuildErrorTitle = "Build Error";

        /// <summary>
        /// Returns an array of enabled scenes from the "Scenes In Build" section of Unity's Build Settings window.
        /// </summary>
        public static string[] GetEditorBuildEnabledScenes()
        {
            return EditorBuildSettings.scenes
                .Where(scene => scene.enabled && !string.IsNullOrEmpty(scene.path))
                .Select(scene => scene.path)
                .ToArray();
        }

        /// <summary>
        /// Returns a BuildPlayerOptions struct based on the specified options that is suitable for building an app.
        /// </summary>
        public static BuildPlayerOptions CreateBuildPlayerOptions(string locationPathName, BuildOptions options)
        {
            var scenesInBuild = PlayInstantBuildConfiguration.ScenesInBuild;
            if (scenesInBuild.Length == 0)
            {
                scenesInBuild = GetEditorBuildEnabledScenes();
            }

            return new BuildPlayerOptions
            {
                assetBundleManifestPath = PlayInstantBuildConfiguration.AssetBundleManifestPath,
                locationPathName = locationPathName,
                options = options,
                scenes = scenesInBuild,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android
            };
        }

        /// <summary>
        /// Builds a Play Instant APK based on the specified options and signs it (if necessary) via
        /// <a href="https://source.android.com/security/apksigning/v2">APK Signature Scheme V2</a>.
        /// Displays warning/error dialogs if there are issues during the build.
        /// </summary>
        /// <returns>True if the build succeeded, false if it failed or was cancelled.</returns>
        public static bool BuildAndSign(BuildPlayerOptions buildPlayerOptions)
        {
            if (!Build(buildPlayerOptions))
            {
                return false;
            }

#if UNITY_2018_1_OR_NEWER
            // On Unity 2018.1+ we require Gradle builds. Unity 2018+ Gradle builds always yield a properly signed APK.
            return true;
#else
            // ApkSigner is fast so we call it synchronously rather than wait for the post build AppDomain reset.
            if (!ApkSigner.IsAvailable())
            {
                DisplayBuildError("Unable to locate apksigner. Check that a recent version of Android SDK " +
                         "Build-Tools is installed and check the Console log for more details on the error.");
                return false;
            }

            Debug.Log("Checking for APK Signature Scheme V2...");
            var apkPath = buildPlayerOptions.locationPathName;
            if (ApkSigner.Verify(apkPath))
            {
                return true;
            }

            Debug.Log("APK must be re-signed for APK Signature Scheme V2...");
            var signingResult = ApkSigner.SignApk(apkPath);
            if (signingResult == null)
            {
                Debug.Log("Re-signed with APK Signature Scheme V2.");
                return true;
            }

            DisplayBuildError(string.Format("Failed to re-sign the APK using apksigner:\n\n{0}", signingResult));
            return false;
#endif
        }

        /// <summary>
        /// Builds a Play Instant APK or AAB based on the specified options.
        /// Displays warning/error dialogs if there are issues during the build.
        /// </summary>
        /// <returns>True if the build succeeded, false if it failed or was cancelled.</returns>
        public static bool Build(BuildPlayerOptions buildPlayerOptions)
        {
            if (!PlayInstantBuildConfiguration.IsInstantBuildType())
            {
                Debug.LogError("Build halted since selected build type is \"Installed\"");
                var message = string.Format(
                    "The currently selected Android build type is \"Installed\".\n\n" +
                    "Click \"{0}\" to open the \"{1}\" window where the build type can be changed to \"Instant\".",
                    WindowUtils.OkButtonText, BuildSettingsWindow.WindowTitle);
                if (DisplayBuildErrorDialog(message))
                {
                    BuildSettingsWindow.ShowWindow();
                }

                return false;
            }

            var failedPolicies = new List<string>(PlayInstantSettingPolicy.GetRequiredPolicies()
                .Where(policy => !policy.IsCorrectState())
                .Select(policy => policy.Name));
            if (failedPolicies.Count > 0)
            {
                Debug.LogErrorFormat("Build halted due to incompatible settings: {0}",
                    string.Join(", ", failedPolicies.ToArray()));
                var message = string.Format(
                    "{0}\n\nClick \"{1}\" to open the settings window and make required changes.",
                    string.Join("\n\n", failedPolicies.ToArray()), WindowUtils.OkButtonText);
                if (DisplayBuildErrorDialog(message))
                {
                    PlayerSettingsWindow.ShowWindow();
                }

                return false;
            }

            var buildReport = BuildPipeline.BuildPlayer(buildPlayerOptions);
#if UNITY_2018_1_OR_NEWER
            switch (buildReport.summary.result)
            {
                case BuildResult.Cancelled:
                    Debug.Log("Build cancelled");
                    return false;
                case BuildResult.Succeeded:
                    // BuildPlayer can fail and still return BuildResult.Succeeded so detect by checking totalErrors.
                    if (buildReport.summary.totalErrors > 0)
                    {
                        // No need to display a message since Unity will already have done this.
                        return false;
                    }

                    // Actual success.
                    return true;
                case BuildResult.Failed:
                    DisplayBuildError(string.Format("Build failed with {0} error(s)", buildReport.summary.totalErrors));
                    return false;
                default:
                    DisplayBuildError("Build failed with unknown error");
                    return false;
            }
#else
            if (string.IsNullOrEmpty(buildReport))
            {
                return true;
            }

            // Check for intended build cancellation.
            if (buildReport == "Building Player was cancelled")
            {
                Debug.Log(buildReport);
            }
            else
            {
                DisplayBuildError(buildReport);
            }

            return false;
#endif
        }

        /// <summary>
        /// Displays the specified message indicating that a build error occurred. Displays an "OK" button that can
        /// be used to indicate that the user wants to perform a followup action, e.g. fixing a build setting.
        /// </summary>
        /// <returns>True if the user clicks "OK", otherwise false.</returns>
        public static bool DisplayBuildErrorDialog(string message)
        {
            if (WindowUtils.IsHeadlessMode())
            {
                // During a headless build it isn't possible to prompt to fix the issue, so always return false.
                Debug.LogErrorFormat("Build error in headless mode: {0}", message);
                return false;
            }
            return EditorUtility.DisplayDialog(
                BuildErrorTitle, message, WindowUtils.OkButtonText, WindowUtils.CancelButtonText);
        }

        /// <summary>
        /// Displays the specified message indicating that a build error occurred.
        /// </summary>
        public static void DisplayBuildError(string message)
        {
            Debug.LogErrorFormat("Build error: {0}", message);

            if (!WindowUtils.IsHeadlessMode())
            {
                EditorUtility.DisplayDialog(BuildErrorTitle, message, WindowUtils.OkButtonText);
            }
        }
    }
}