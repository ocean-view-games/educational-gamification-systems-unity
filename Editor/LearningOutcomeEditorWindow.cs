// Copyright (c) 2026 Ocean View Games Ltd.
// Licensed under the MIT Licence. See LICENSE file in the project root.
// https://oceanviewgames.co.uk

#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OceanViewGames.EdTech.EditorTools
{
    /// <summary>
    /// An Editor window that displays all registered learning objectives and their
    /// mastery progress. Provides a button to export the current mastery report
    /// as JSON. Only functional during play mode, as it reads live data from a
    /// <see cref="LearningOutcomeTracker"/> in the scene.
    /// </summary>
    public class LearningOutcomeEditorWindow : EditorWindow
    {
        private LearningOutcomeTracker _tracker;
        private Vector2 _scrollPosition;

        // Colour coding for mastery levels in the editor.
        private static readonly Color ColourNotStarted = Color.grey;
        private static readonly Color ColourEmerging = new(0.9f, 0.4f, 0.4f);
        private static readonly Color ColourDeveloping = new(0.9f, 0.7f, 0.3f);
        private static readonly Color ColourSecure = new(0.4f, 0.7f, 0.9f);
        private static readonly Color ColourMastered = new(0.3f, 0.85f, 0.4f);

        [MenuItem("Ocean View Games/Learning Outcome Viewer")]
        public static void ShowWindow()
        {
            var window = GetWindow<LearningOutcomeEditorWindow>("Learning Outcomes");
            window.minSize = new Vector2(400, 300);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Learning Outcome Viewer", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play Mode to view live learning outcome data.",
                    MessageType.Info);
                return;
            }

            // Find tracker in scene if not yet assigned.
            if (_tracker == null)
                _tracker = FindTracker();

            if (_tracker == null)
            {
                EditorGUILayout.HelpBox(
                    "No LearningOutcomeTracker found in the scene. " +
                    "Add one to a GameObject to begin tracking.",
                    MessageType.Warning);
                return;
            }

            // Objectives returns a defensive snapshot, cloning every objective on each
            // access. OnInspectorUpdate repaints at roughly 10 Hz during play mode, so take
            // one snapshot per frame rather than one per use site.
            var objectives = _tracker.Objectives;

            EditorGUILayout.LabelField("Student ID",
                string.IsNullOrEmpty(_tracker.StudentId) ? "(not set)" : _tracker.StudentId);
            EditorGUILayout.LabelField("Session ID", _tracker.SessionId);
            EditorGUILayout.LabelField("Objectives", objectives.Count.ToString());
            EditorGUILayout.Space(4);

            DrawObjectiveList(objectives);

            EditorGUILayout.Space(8);
            DrawExportButton();
        }

        private static LearningOutcomeTracker FindTracker()
        {
#if UNITY_2023_1_OR_NEWER
            return FindFirstObjectByType<LearningOutcomeTracker>();
#else
            return FindObjectOfType<LearningOutcomeTracker>();
#endif
        }

        private void DrawObjectiveList(IReadOnlyList<LearningObjective> objectives)
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            foreach (var objective in objectives)
            {
                var mastery = _tracker.GetMasteryLevel(objective.objectiveId);
                float accuracy = _tracker.GetAccuracy(objective.objectiveId);
                int attemptCount = _tracker.GetAttemptCount(objective.objectiveId);

                EditorGUILayout.BeginVertical("box");

                // Header row with objective ID and mastery badge.
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(objective.objectiveId, EditorStyles.boldLabel);
                DrawMasteryBadge(mastery);
                EditorGUILayout.EndHorizontal();

                // Description and subject.
                if (!string.IsNullOrEmpty(objective.description))
                    EditorGUILayout.LabelField(objective.description, EditorStyles.wordWrappedLabel);

                EditorGUILayout.LabelField("Subject", objective.subject);
                EditorGUILayout.LabelField("Attempts", attemptCount.ToString());
                EditorGUILayout.LabelField("Accuracy", $"{accuracy:P0}");

                // Progress bar showing accuracy against mastery threshold.
                var rect = EditorGUILayout.GetControlRect(false, 18);
                EditorGUI.ProgressBar(rect, accuracy, $"{accuracy:P0} / {objective.masteryThreshold:P0} threshold");

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawMasteryBadge(MasteryLevel level)
        {
            var previousColour = GUI.backgroundColor;
            GUI.backgroundColor = level switch
            {
                MasteryLevel.NotStarted => ColourNotStarted,
                MasteryLevel.Emerging => ColourEmerging,
                MasteryLevel.Developing => ColourDeveloping,
                MasteryLevel.Secure => ColourSecure,
                MasteryLevel.Mastered => ColourMastered,
                _ => ColourNotStarted
            };

            GUILayout.Label(level.ToString(), "Button", GUILayout.Width(90));
            GUI.backgroundColor = previousColour;
        }

        private void DrawExportButton()
        {
            if (!GUILayout.Button("Export Mastery Report as JSON"))
                return;

            string path = EditorUtility.SaveFilePanel(
                "Export Mastery Report",
                Application.dataPath,
                "mastery_report",
                "json");

            if (string.IsNullOrEmpty(path))
                return;

            // Generate only once a destination is confirmed, so a cancelled dialog
            // does not do the work for nothing.
            try
            {
                File.WriteAllText(path, _tracker.GenerateReportJson());
            }
            catch (IOException exception)
            {
                ReportExportFailure(path, exception);
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                // A read-only volume or a file locked by another process. Letting this
                // escape OnGUI surfaces as an opaque IMGUI exception and leaves the layout
                // stack inconsistent for the frame, so handle it here.
                ReportExportFailure(path, exception);
                return;
            }

            Debug.Log($"[LearningOutcomeEditorWindow] Mastery report exported to: {path}");
            EditorUtility.RevealInFinder(path);
        }

        private static void ReportExportFailure(string path, Exception exception)
        {
            Debug.LogError(
                $"[LearningOutcomeEditorWindow] Failed to export the mastery report to '{path}': " +
                exception.Message);

            EditorUtility.DisplayDialog(
                "Export Failed",
                $"The mastery report could not be written to:\n\n{path}\n\n{exception.Message}",
                "OK");
        }

        private void OnInspectorUpdate()
        {
            // Repaint periodically during play mode to show updated data.
            if (Application.isPlaying)
                Repaint();
        }
    }
}

#endif
