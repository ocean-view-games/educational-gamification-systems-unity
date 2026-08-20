// Copyright (c) 2026 Ocean View Games Ltd.
// Licensed under the MIT Licence. See LICENSE file in the project root.
// https://oceanviewgames.co.uk

using System.Collections;
using System.Text;
using UnityEngine;

namespace OceanViewGames.EdTech.Samples
{
    /// <summary>
    /// A self-contained demonstration of <see cref="LearningOutcomeTracker"/> and
    /// <see cref="AdaptiveDifficultyController"/> working together. Add this component to
    /// an empty GameObject and press Play: a simulated student answers questions across
    /// three curriculum objectives while an on-screen readout shows accuracy, mastery
    /// level, and the difficulty the controller settles on.
    /// </summary>
    /// <remarks>
    /// The simulated student has a fixed latent ability, and harder questions are answered
    /// correctly less often. So as the controller raises difficulty the observed accuracy
    /// falls, and the two settle into the band between the controller's decrease and
    /// increase thresholds. That convergence is the behaviour the controller exists to
    /// produce, and watching it happen is the point of this sample.
    /// </remarks>
    [AddComponentMenu("Ocean View Games/EdTech/Classroom Simulation Sample")]
    [RequireComponent(typeof(LearningOutcomeTracker))]
    [RequireComponent(typeof(AdaptiveDifficultyController))]
    public class ClassroomSimulationSample : MonoBehaviour
    {
        [Header("Simulated Student")]
        [Tooltip("Pseudonymous token standing in for a real student identifier. Never a child's name.")]
        [SerializeField] private string _studentToken = "student-4021";

        [Tooltip("How capable the simulated student is, from 0 (struggling) to 1 (fluent).")]
        [Range(0f, 1f)]
        [SerializeField] private float _studentAbility = 0.7f;

        [Header("Pacing")]
        [Tooltip("Seconds between simulated attempts.")]
        [Range(0.05f, 2f)]
        [SerializeField] private float _secondsPerAttempt = 0.35f;

        [Tooltip("How many attempts to simulate before generating the final report.")]
        [Range(10, 500)]
        [SerializeField] private int _attemptsToSimulate = 120;

        [Tooltip("Seed for the simulated student's answers, so a run is reproducible.")]
        [SerializeField] private int _randomSeed = 20260820;

        private static readonly LearningObjective[] Objectives =
        {
            new LearningObjective
            {
                objectiveId = "KS2.EN.R.3.1",
                description = "Retrieve and record information from non-fiction texts",
                subject = "English",
                masteryThreshold = 0.85f
            },
            new LearningObjective
            {
                objectiveId = "KS2.MA.N.4.2",
                description = "Recall multiplication facts up to 12 x 12",
                subject = "Mathematics",
                masteryThreshold = 0.9f
            },
            new LearningObjective
            {
                objectiveId = "KS2.SC.B.5.1",
                description = "Describe the life process of reproduction in plants",
                subject = "Science",
                masteryThreshold = 0.75f
            }
        };

        private LearningOutcomeTracker _tracker;
        private AdaptiveDifficultyController _difficulty;
        private System.Random _random;

        private int _attemptsMade;
        private string _lastEvent = "waiting for first attempt";
        private bool _reportGenerated;

        private GUIStyle _headingStyle;
        private GUIStyle _rowStyle;

        private void Awake()
        {
            _tracker = GetComponent<LearningOutcomeTracker>();
            _difficulty = GetComponent<AdaptiveDifficultyController>();
            _random = new System.Random(_randomSeed);

            foreach (var objective in Objectives)
                _tracker.RegisterObjective(objective);
        }

        private void Start()
        {
            // Subscribe before the first Evaluate call, or the adjustment it makes goes
            // unobserved. SetTracker resets difficulty and can fire synchronously, so the
            // listeners go on first.
            _difficulty.OnDifficultyChanged.AddListener(OnDifficultyChanged);
            _difficulty.OnTierChanged.AddListener(OnTierChanged);
            _difficulty.SetTracker(_tracker);

            // Issues a fresh session ID and clears any previous progress, so one tracker
            // can serve a succession of students on shared classroom hardware.
            _tracker.StartSession(_studentToken);

            StartCoroutine(SimulateLesson());
        }

        private void OnDestroy()
        {
            if (_difficulty == null)
                return;

            _difficulty.OnDifficultyChanged.RemoveListener(OnDifficultyChanged);
            _difficulty.OnTierChanged.RemoveListener(OnTierChanged);
        }

        private IEnumerator SimulateLesson()
        {
            var wait = new WaitForSeconds(_secondsPerAttempt);

            while (_attemptsMade < _attemptsToSimulate)
            {
                var objective = Objectives[_attemptsMade % Objectives.Length];

                // Harder questions are answered correctly less often, centred so a student
                // whose ability matches the current difficulty sits near 50 per cent.
                float successChance = Mathf.Clamp01(_studentAbility - _difficulty.CurrentDifficulty + 0.5f);
                bool correct = _random.NextDouble() < successChance;

                // Harder questions also take longer to answer.
                float responseTime = 2f + (_difficulty.CurrentDifficulty * 6f) + (float)_random.NextDouble();

                _tracker.RecordAttempt(
                    objective.objectiveId,
                    correct,
                    responseTime,
                    activityId: objective.subject.ToLowerInvariant() + "-quiz-01");

                // Evaluate and EvaluateForObjective are mutually exclusive modes. This
                // sample uses the whole-session view.
                _difficulty.Evaluate();

                _attemptsMade++;
                yield return wait;
            }

            string reportJson = _tracker.GenerateReportJson();
            _reportGenerated = true;
            _lastEvent = "lesson complete after " + _attemptsMade + " attempts";
            Debug.Log("[ClassroomSimulationSample] Mastery report:\n" + reportJson, this);
        }

        private void OnDifficultyChanged(float difficulty)
        {
            _lastEvent = $"difficulty -> {difficulty:0.00} (attempt {_attemptsMade + 1})";
        }

        private void OnTierChanged(DifficultyTier tier)
        {
            Debug.Log($"[ClassroomSimulationSample] Tier changed to {tier}.", this);
        }

        private void OnGUI()
        {
            EnsureStyles();

            var area = new Rect(16f, 16f, 600f, 230f);
            GUI.Box(area, GUIContent.none);
            GUILayout.BeginArea(new Rect(area.x + 14f, area.y + 12f, area.width - 28f, area.height - 24f));

            GUILayout.Label("Classroom simulation — " + _tracker.StudentId, _headingStyle);
            GUILayout.Label("Session " + _tracker.SessionId, _rowStyle);
            GUILayout.Space(6f);

            GUILayout.Label(
                $"Attempts {_attemptsMade} / {_attemptsToSimulate}     " +
                $"Difficulty {_difficulty.CurrentDifficulty:0.00} ({_difficulty.CurrentTier})",
                _rowStyle);
            GUILayout.Label("Last event: " + _lastEvent, _rowStyle);
            GUILayout.Space(10f);

            foreach (var objective in Objectives)
            {
                string id = objective.objectiveId;
                var row = new StringBuilder();
                row.Append(id.PadRight(15));
                row.Append($"{_tracker.GetAccuracy(id):P0}".PadLeft(4));
                row.Append("   ");
                row.Append(_tracker.GetMasteryLevel(id).ToString().PadRight(12));
                row.Append(_tracker.GetAttemptCount(id) + " attempts");
                GUILayout.Label(row.ToString(), _rowStyle);
            }

            if (_reportGenerated)
            {
                GUILayout.Space(8f);
                GUILayout.Label("Full JSON report written to the Console.", _rowStyle);
            }

            GUILayout.EndArea();
        }

        private void EnsureStyles()
        {
            if (_headingStyle != null)
                return;

            _headingStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _rowStyle = new GUIStyle(GUI.skin.label);
        }
    }
}
