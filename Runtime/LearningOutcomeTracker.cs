// Copyright (c) 2026 Ocean View Games Ltd.
// Licensed under the MIT Licence. See LICENSE file in the project root.
// https://oceanviewgames.co.uk

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using UnityEngine;

namespace OceanViewGames.EdTech
{
    /// <summary>
    /// Represents a curriculum-aligned learning objective that can be tracked
    /// against student performance. Objective IDs should follow curriculum codes
    /// (e.g. "KS2.EN.R.3.1" for Key Stage 2 English Reading).
    /// </summary>
    [Serializable]
    public class LearningObjective
    {
        /// <summary>Unique identifier matching a curriculum code (e.g. "KS2.EN.R.3.1").</summary>
        [Tooltip("Curriculum code for this objective, e.g. KS2.EN.R.3.1")]
        public string objectiveId;

        /// <summary>Human-readable description of the learning objective.</summary>
        [Tooltip("Brief description of what the student should learn")]
        public string description;

        /// <summary>Subject area (e.g. English, Mathematics, Science).</summary>
        [Tooltip("Subject area this objective belongs to")]
        public string subject;

        /// <summary>
        /// The accuracy threshold (0 to 1) a student must reach to be considered
        /// as having mastered this objective. The Secure and Developing bands are
        /// positioned proportionally beneath this value, so lowering the threshold
        /// lowers the whole ladder rather than collapsing it.
        /// </summary>
        [Tooltip("Accuracy threshold (greater than 0, up to 1) required to achieve Mastered status")]
        [Range(0.01f, 1f)]
        public float masteryThreshold = 0.85f;
    }

    /// <summary>
    /// Mastery levels representing a student's progress towards a learning objective.
    /// Based on common UK assessment terminology.
    /// </summary>
    public enum MasteryLevel
    {
        NotStarted,
        Emerging,
        Developing,
        Secure,
        Mastered
    }

    /// <summary>
    /// Records a single student attempt at an activity linked to a learning objective.
    /// </summary>
    [Serializable]
    public class AttemptRecord
    {
        /// <summary>The objective this attempt relates to.</summary>
        public string objectiveId;

        /// <summary>Whether the student answered correctly.</summary>
        public bool correct;

        /// <summary>Time taken to respond, in seconds.</summary>
        public float responseTimeSeconds;

        /// <summary>Identifier for the activity or question that generated this attempt.</summary>
        public string activityId;

        /// <summary>UTC timestamp when the attempt was recorded, in ISO 8601 round-trip format.</summary>
        public string timestamp;

        /// <summary>
        /// Monotonically increasing counter assigned by the tracker when the attempt
        /// was recorded. Use this rather than <see cref="timestamp"/> for ordering:
        /// the system clock has coarser resolution than a single frame, so several
        /// attempts can share a timestamp.
        /// </summary>
        public long sequence;
    }

    /// <summary>
    /// Summary of a student's mastery for a single objective, suitable for
    /// JSON serialisation and export to an LMS via REST API.
    /// </summary>
    [Serializable]
    public class ObjectiveMastery
    {
        public string objectiveId;
        public string description;
        public string subject;

        /// <summary>
        /// The mastery level as an enum. Note that <see cref="JsonUtility"/> serialises
        /// this as an integer; consumers expecting a human-readable value (such as an
        /// xAPI statement) should read <see cref="masteryLevelName"/> instead.
        /// </summary>
        public MasteryLevel masteryLevel;

        /// <summary>The mastery level as a string, for LMS and xAPI consumers.</summary>
        public string masteryLevelName;

        public int totalAttempts;
        public int correctAttempts;
        public float accuracy;

        /// <summary>The threshold this objective was assessed against, for interpretation downstream.</summary>
        public float masteryThreshold;

        public float averageResponseTimeSeconds;
    }

    /// <summary>
    /// A structured report containing mastery data for all tracked objectives.
    /// Designed for JSON serialisation and REST API export to Learning Management Systems.
    /// </summary>
    [Serializable]
    public class MasteryReport
    {
        public string studentId;
        public string sessionId;
        public string generatedAtUtc;
        public List<ObjectiveMastery> objectives = new();
    }

    /// <summary>
    /// Tracks student performance against curriculum-aligned learning objectives.
    /// Records attempts, calculates mastery levels, and generates structured reports
    /// suitable for export to a Learning Management System (LMS).
    /// </summary>
    /// <remarks>
    /// This component holds attempt data in memory only. It does not persist across
    /// scene loads, editor domain reloads, or application restarts, and it does not
    /// transmit anything anywhere. Generating the report and delivering it to an LMS
    /// are separate responsibilities: call <see cref="GenerateReportJson"/> and POST
    /// the result from your own networking layer.
    ///
    /// Developed by Ocean View Games (https://oceanviewgames.co.uk) for use in
    /// educational games targeting UK Key Stage curricula. See the architecture
    /// documentation for integration details.
    /// </remarks>
    [AddComponentMenu("Ocean View Games/EdTech/Learning Outcome Tracker")]
    public class LearningOutcomeTracker : MonoBehaviour
    {
        /// <summary>Fallback threshold for invalid authored data and legacy state.</summary>
        private const float DefaultMasteryThreshold = 0.85f;

        /// <summary>
        /// Size of the global recent-attempt window. Keeping exactly the controller's maximum
        /// window avoids sorting or scanning the full session history.
        /// <see cref="AdaptiveDifficultyController"/> constrains its own window to this value
        /// through the <c>Range</c> attribute on its window size field, so the two cannot drift:
        /// widening one without the other would silently truncate the global window.
        /// </summary>
        internal const int RecentAttemptCapacity = 50;

        /// <summary>Upper bound on the retained raw history per objective.</summary>
        private const int MaxAttemptHistoryLimit = 10000;

        private sealed class ObjectiveStatistics
        {
            public int totalAttempts;
            public int correctAttempts;
            public double totalResponseTimeSeconds;

            public void Reset()
            {
                totalAttempts = 0;
                correctAttempts = 0;
                totalResponseTimeSeconds = 0d;
            }
        }

        // The Secure and Developing bands sit proportionally beneath the objective's
        // mastery threshold. At the default threshold of 0.85 these ratios reproduce
        // the conventional cut-offs of 0.65 and 0.35 exactly, while a lower threshold
        // scales the whole ladder down instead of making the middle bands unreachable.
        private const float SecureBandRatio = 0.65f / 0.85f;
        private const float DevelopingBandRatio = 0.35f / 0.85f;

        private static readonly ReadOnlyCollection<AttemptRecord> EmptyAttempts =
            new(Array.Empty<AttemptRecord>());

        // Deliberately not serialised. An Inspector-authored value would be baked into
        // the scene or prefab and shipped in the build, which SECURITY.md treats as a
        // disclosure of a child's identifier; it would also collide with the one-shot
        // assignment enforced by SetStudentId. Assign at runtime only.
        private string _studentId = "";

        [SerializeField]
        [Tooltip("Learning objectives to track in this session")]
        private List<LearningObjective> _objectives = new();

        [SerializeField]
        [Tooltip("Maximum recent attempt records retained per objective. Lifetime summary statistics are preserved.")]
        [Range(RecentAttemptCapacity, MaxAttemptHistoryLimit)]
        private int _attemptHistoryLimit = 1000;

        // Index from objective ID to its entry in _objectives, so mastery lookups do not
        // scan the list. Holds the same instances the list does rather than copies, so an
        // objective's threshold is still read live, exactly as GenerateReport reads it.
        private readonly Dictionary<string, LearningObjective> _objectivesById = new();

        // Bounded recent attempt records, grouped by objective ID for inspection and
        // per-objective adaptive windows. Lifetime aggregates are stored separately.
        private readonly Dictionary<string, List<AttemptRecord>> _attempts = new();
        private readonly Dictionary<string, ObjectiveStatistics> _statistics = new();
        private readonly List<AttemptRecord> _recentAttempts = new(RecentAttemptCapacity);

        private string _sessionId;
        private long _sequenceCounter;

        // Incremented every time recorded attempts are cleared, by either StartSession or
        // ResetAllProgress. Consumers that were disabled when the clear happened miss the
        // corresponding event, so they compare this value instead of relying on the notification.
        private long _progressGeneration;

        // Guards the one-time setup normally performed by Awake. See EnsureInitialised.
        private bool _initialised;

        /// <summary>
        /// A read-only snapshot of the currently configured learning objectives.
        /// Mutating an objective in the returned collection does not change tracker state.
        /// </summary>
        public IReadOnlyList<LearningObjective> Objectives
        {
            get
            {
                EnsureInitialised();
                return CreateObjectiveSnapshot();
            }
        }

        /// <summary>The current student identifier.</summary>
        public string StudentId => _studentId;

        /// <summary>
        /// Fired after <see cref="StartSession"/> has cleared progress and issued the
        /// new session ID. Consumers can reset learner-specific runtime state immediately.
        /// </summary>
        public event Action<string> SessionStarted;

        /// <summary>
        /// Fired after <see cref="ResetAllProgress"/> has cleared recorded attempts within
        /// the current session. The session ID and student ID are unchanged, but every
        /// consumer holding state derived from the cleared attempts — an adaptive
        /// difficulty level, a cached accuracy — must discard it.
        /// </summary>
        public event Action ProgressReset;

        /// <summary>
        /// Identifier for the current session. Stable for the lifetime of the session:
        /// every report generated before the next <see cref="StartSession"/> carries the
        /// same value, so an LMS can correlate periodic syncs as one sitting.
        /// </summary>
        public string SessionId => _sessionId ??= Guid.NewGuid().ToString();

        /// <summary>
        /// Sequence assigned to the most recently recorded attempt in this session.
        /// Used by other runtime components to avoid processing the same evidence twice.
        /// </summary>
        internal long LatestAttemptSequence => _sequenceCounter;

        /// <summary>
        /// Counts how many times recorded attempts have been cleared, by either
        /// <see cref="StartSession"/> or <see cref="ResetAllProgress"/>. Consumers that hold
        /// state derived from those attempts compare this against the value they last saw, so
        /// a clear that happened while they were disabled — and whose event they therefore
        /// never received — is still detected when they resume.
        /// </summary>
        internal long ProgressGeneration => _progressGeneration;

        /// <summary>Internal global recent-attempt window, stored in recording order.</summary>
        internal IReadOnlyList<AttemptRecord> RecentAttemptsForEvaluation
        {
            get
            {
                EnsureInitialised();
                return _recentAttempts;
            }
        }

        /// <summary>Maximum number of recent raw attempt records retained per objective.</summary>
        public int AttemptHistoryLimit
        {
            get
            {
                EnsureInitialised();
                return _attemptHistoryLimit;
            }
        }

        private void Awake()
        {
            EnsureInitialised();
        }

        /// <summary>
        /// Performs the one-time setup that would otherwise sit in <see cref="Awake"/>:
        /// issuing the session ID, clamping the history limit, and indexing the objectives
        /// authored in the Inspector.
        /// </summary>
        /// <remarks>
        /// Awake alone is not enough. Unity does not define the order in which components
        /// receive it, so a caller invoking <see cref="RecordAttempt"/> from its own Awake
        /// could arrive before the objectives were indexed and be told a correctly authored
        /// objective is not registered; a tracker on a GameObject that starts inactive never
        /// receives Awake at all. Every public entry point that reads the indexes calls this
        /// first, and it is idempotent, so the later Awake is a no-op.
        /// </remarks>
        private void EnsureInitialised()
        {
            if (_initialised)
                return;

            // Set before the work below so the EnsureAttemptList calls inside
            // SanitiseAuthoredObjectives cannot recurse back into this method.
            _initialised = true;

            _sessionId ??= Guid.NewGuid().ToString();
            _attemptHistoryLimit = Mathf.Clamp(_attemptHistoryLimit, RecentAttemptCapacity, MaxAttemptHistoryLimit);
            SanitiseAuthoredObjectives();
        }

        private void OnValidate()
        {
            _attemptHistoryLimit = Mathf.Clamp(_attemptHistoryLimit, RecentAttemptCapacity, MaxAttemptHistoryLimit);
        }

        /// <summary>
        /// Sets the student identifier at runtime. Call this once the student has been
        /// identified (sign-in, class roster selection, or launch parameter) and before
        /// generating any report.
        /// </summary>
        /// <remarks>
        /// Pass a pseudonymous token, never a child's name. Reports pair this identifier
        /// with aggregated correctness and response-time statistics, which in a UK school
        /// setting is personal data about a child. Once an identifier has been assigned,
        /// use <see cref="StartSession"/> rather than changing it in place. An anonymous
        /// session may be identified after attempts have begun.
        /// </remarks>
        /// <param name="studentId">The student identifier. Null is treated as empty.</param>
        public void SetStudentId(string studentId)
        {
            string nextStudentId = studentId ?? string.Empty;
            if (!string.IsNullOrEmpty(_studentId) &&
                !string.Equals(_studentId, nextStudentId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The student ID has already been assigned for this session. " +
                    "Call StartSession to begin tracking another student.");
            }

            _studentId = nextStudentId;
        }

        /// <summary>
        /// Begins a new session for the given student: assigns the student ID, clears all
        /// recorded attempts, and issues a fresh session ID. Registered objectives are kept,
        /// so a single tracker can serve a succession of students on shared classroom hardware.
        /// </summary>
        /// <remarks>
        /// Pass a pseudonymous token, never a child's name. See <see cref="SetStudentId"/>.
        /// </remarks>
        /// <param name="studentId">The student identifier for the new session.</param>
        /// <returns>The newly generated session ID.</returns>
        public string StartSession(string studentId)
        {
            EnsureInitialised();
            ClearProgress();
            _sequenceCounter = 0;
            _studentId = string.Empty;
            SetStudentId(studentId);
            _sessionId = Guid.NewGuid().ToString();
            SessionStarted?.Invoke(_sessionId);
            return _sessionId;
        }

        /// <summary>
        /// Registers a learning objective for tracking. If an objective with the
        /// same ID already exists it is replaced in place, preserving list order.
        /// </summary>
        /// <param name="objective">The learning objective to register.</param>
        public void RegisterObjective(LearningObjective objective)
        {
            EnsureInitialised();

            if (objective == null)
                throw new ArgumentNullException(nameof(objective));

            if (string.IsNullOrWhiteSpace(objective.objectiveId))
                throw new ArgumentException("Objective ID must not be empty or whitespace.", nameof(objective));

            if (!IsFinite(objective.masteryThreshold) ||
                objective.masteryThreshold <= 0f || objective.masteryThreshold > 1f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(objective), objective.masteryThreshold,
                    "Mastery threshold must be a finite value greater than 0 and no greater than 1.");
            }

            var storedObjective = CloneObjective(objective);

            int existingIndex = _objectives.FindIndex(o => o != null && o.objectiveId == objective.objectiveId);
            if (existingIndex >= 0)
                _objectives[existingIndex] = storedObjective;
            else
                _objectives.Add(storedObjective);

            _objectivesById[objective.objectiveId] = storedObjective;
            EnsureAttemptList(objective.objectiveId);
        }

        /// <summary>
        /// Records a student attempt against a specific learning objective.
        /// </summary>
        /// <param name="objectiveId">The curriculum code of the objective.</param>
        /// <param name="correct">Whether the student answered correctly.</param>
        /// <param name="responseTimeSeconds">Time taken to respond, in seconds.</param>
        /// <param name="activityId">Identifier for the activity or question.</param>
        public void RecordAttempt(string objectiveId, bool correct, float responseTimeSeconds, string activityId = "")
        {
            EnsureInitialised();

            if (string.IsNullOrWhiteSpace(objectiveId))
                throw new ArgumentException("Objective ID must not be empty or whitespace.", nameof(objectiveId));

            if (!_attempts.TryGetValue(objectiveId, out var attempts))
            {
                throw new KeyNotFoundException(
                    $"Objective '{objectiveId}' is not registered. Register it before recording attempts.");
            }

            if (!IsFinite(responseTimeSeconds) || responseTimeSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(responseTimeSeconds), responseTimeSeconds,
                    "Response time must be a finite, non-negative value.");
            }

            var record = new AttemptRecord
            {
                objectiveId = objectiveId,
                correct = correct,
                responseTimeSeconds = responseTimeSeconds,
                activityId = activityId,
                timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                sequence = ++_sequenceCounter
            };

            attempts.Add(record);
            while (attempts.Count > _attemptHistoryLimit)
                attempts.RemoveAt(0);

            var statistics = _statistics[objectiveId];
            statistics.totalAttempts++;
            if (correct)
                statistics.correctAttempts++;
            statistics.totalResponseTimeSeconds += responseTimeSeconds;

            _recentAttempts.Add(record);
            if (_recentAttempts.Count > RecentAttemptCapacity)
                _recentAttempts.RemoveAt(0);
        }

        /// <summary>
        /// Returns a snapshot of the retained recent attempts for a given objective, in
        /// recording order. Mutating a returned record does not alter tracker state.
        /// Lifetime counts and mastery calculations remain accurate when older records
        /// roll out of this bounded history.
        /// </summary>
        /// <param name="objectiveId">The curriculum code of the objective.</param>
        /// <returns>A read-only snapshot of the attempt records, or an empty list if none exist.</returns>
        public IReadOnlyList<AttemptRecord> GetAttempts(string objectiveId)
        {
            EnsureInitialised();

            if (objectiveId == null || !_attempts.TryGetValue(objectiveId, out var attempts) || attempts.Count == 0)
                return EmptyAttempts;

            var snapshot = new AttemptRecord[attempts.Count];
            for (int i = 0; i < attempts.Count; i++)
                snapshot[i] = CloneAttempt(attempts[i]);

            return Array.AsReadOnly(snapshot);
        }

        /// <summary>Internal non-allocating attempt view used by the adaptive controller.</summary>
        internal IReadOnlyList<AttemptRecord> GetAttemptsForEvaluation(string objectiveId)
        {
            EnsureInitialised();

            return objectiveId != null && _attempts.TryGetValue(objectiveId, out var attempts)
                ? attempts
                : EmptyAttempts;
        }

        /// <summary>
        /// Returns the lifetime number of attempts recorded for an objective in the
        /// current session, including records older than <see cref="AttemptHistoryLimit"/>.
        /// </summary>
        public int GetAttemptCount(string objectiveId)
        {
            EnsureInitialised();

            return objectiveId != null && _statistics.TryGetValue(objectiveId, out var statistics)
                ? statistics.totalAttempts
                : 0;
        }

        /// <summary>
        /// Calculates the current mastery level for a specific objective based on
        /// all recorded attempts. Given the objective's mastery threshold T:
        /// <list type="bullet">
        ///   <item>NotStarted: no attempts recorded</item>
        ///   <item>Emerging: accuracy below 0.41 x T</item>
        ///   <item>Developing: accuracy between 0.41 x T and 0.76 x T</item>
        ///   <item>Secure: accuracy between 0.76 x T and T</item>
        ///   <item>Mastered: accuracy at or above T</item>
        /// </list>
        /// At the default threshold of 0.85 these bands fall at 0.35 and 0.65.
        /// </summary>
        /// <param name="objectiveId">The curriculum code of the objective.</param>
        /// <returns>The calculated mastery level.</returns>
        public MasteryLevel GetMasteryLevel(string objectiveId)
        {
            EnsureInitialised();

            if (objectiveId == null || !_statistics.TryGetValue(objectiveId, out var statistics) ||
                statistics.totalAttempts == 0)
                return MasteryLevel.NotStarted;

            return ClassifyMastery(CalculateAccuracy(statistics), GetMasteryThreshold(objectiveId));
        }

        /// <summary>
        /// Calculates the accuracy (0 to 1) for a given objective based on all attempts.
        /// </summary>
        /// <param name="objectiveId">The curriculum code of the objective.</param>
        /// <returns>Accuracy as a float between 0 and 1, or 0 if no attempts exist.</returns>
        public float GetAccuracy(string objectiveId)
        {
            EnsureInitialised();

            if (objectiveId == null || !_statistics.TryGetValue(objectiveId, out var statistics) ||
                statistics.totalAttempts == 0)
                return 0f;

            return CalculateAccuracy(statistics);
        }

        /// <summary>
        /// Generates a structured mastery report covering all registered objectives.
        /// The report is suitable for JSON serialisation and export to an LMS.
        /// Repeated calls within a session share the same <see cref="SessionId"/>.
        /// </summary>
        /// <returns>A <see cref="MasteryReport"/> containing mastery data for each objective.</returns>
        public MasteryReport GenerateReport()
        {
            EnsureInitialised();

            var report = new MasteryReport
            {
                studentId = _studentId,
                sessionId = SessionId,
                generatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };

            foreach (var objective in _objectives)
            {
                if (objective == null || string.IsNullOrWhiteSpace(objective.objectiveId))
                    continue;

                _statistics.TryGetValue(objective.objectiveId, out var statistics);
                int totalAttempts = statistics?.totalAttempts ?? 0;
                int correctAttempts = statistics?.correctAttempts ?? 0;
                double totalResponseTime = statistics?.totalResponseTimeSeconds ?? 0d;

                float accuracy = totalAttempts > 0 ? (float)correctAttempts / totalAttempts : 0f;
                var level = totalAttempts > 0
                    ? ClassifyMastery(accuracy, objective.masteryThreshold)
                    : MasteryLevel.NotStarted;

                report.objectives.Add(new ObjectiveMastery
                {
                    objectiveId = objective.objectiveId,
                    description = objective.description,
                    subject = objective.subject,
                    masteryLevel = level,
                    masteryLevelName = level.ToString(),
                    totalAttempts = totalAttempts,
                    correctAttempts = correctAttempts,
                    accuracy = accuracy,
                    masteryThreshold = objective.masteryThreshold,
                    averageResponseTimeSeconds = totalAttempts > 0
                        ? (float)(totalResponseTime / totalAttempts)
                        : 0f
                });
            }

            return report;
        }

        /// <summary>
        /// Serialises the current mastery report to a JSON string.
        /// </summary>
        /// <returns>A JSON representation of the mastery report.</returns>
        public string GenerateReportJson()
        {
            return JsonUtility.ToJson(GenerateReport(), true);
        }

        /// <summary>
        /// Clears all recorded attempts for every objective. Objectives themselves
        /// remain registered, and the session ID is unchanged. To start a genuinely
        /// new session, use <see cref="StartSession"/>.
        /// </summary>
        public void ResetAllProgress()
        {
            EnsureInitialised();
            ClearProgress();
            ProgressReset?.Invoke();
        }

        /// <summary>
        /// Clears recorded attempts and statistics without raising <see cref="ProgressReset"/>.
        /// <see cref="StartSession"/> uses this so consumers receive a single
        /// <see cref="SessionStarted"/> notification rather than two overlapping resets.
        /// </summary>
        private void ClearProgress()
        {
            // Advanced on every clear, including the one StartSession performs, so a consumer
            // that missed the accompanying event can still detect that its evidence is gone.
            _progressGeneration++;

            foreach (var attempts in _attempts.Values)
                attempts.Clear();

            foreach (var statistics in _statistics.Values)
                statistics.Reset();

            _recentAttempts.Clear();
        }

        /// <summary>
        /// Drops objectives authored in the Inspector that have no ID or that duplicate
        /// an earlier entry. Without this, duplicates would produce duplicate rows in the
        /// exported report; <see cref="RegisterObjective"/> already guards the runtime path.
        /// </summary>
        private void SanitiseAuthoredObjectives()
        {
            _objectivesById.Clear();
            var cleaned = new List<LearningObjective>(_objectives.Count);

            foreach (var objective in _objectives)
            {
                if (objective == null || string.IsNullOrWhiteSpace(objective.objectiveId))
                {
                    Debug.LogWarning(
                        "[LearningOutcomeTracker] Ignored an objective with a missing objective ID.", this);
                    continue;
                }

                if (_objectivesById.ContainsKey(objective.objectiveId))
                {
                    Debug.LogWarning(
                        $"[LearningOutcomeTracker] Ignored duplicate objective '{objective.objectiveId}'. " +
                        "Objective IDs must be unique.", this);
                    continue;
                }

                if (!IsFinite(objective.masteryThreshold) || objective.masteryThreshold <= 0f)
                {
                    Debug.LogWarning(
                        $"[LearningOutcomeTracker] Objective '{objective.objectiveId}' had an invalid " +
                        $"mastery threshold; using the default {DefaultMasteryThreshold:0.00}.", this);
                    objective.masteryThreshold = DefaultMasteryThreshold;
                }
                else if (objective.masteryThreshold > 1f)
                {
                    Debug.LogWarning(
                        $"[LearningOutcomeTracker] Objective '{objective.objectiveId}' had an out-of-range " +
                        "mastery threshold; clamping it to 0-1.", this);
                    objective.masteryThreshold = Mathf.Clamp01(objective.masteryThreshold);
                }

                cleaned.Add(objective);
                _objectivesById[objective.objectiveId] = objective;
                EnsureAttemptList(objective.objectiveId);
            }

            if (cleaned.Count == _objectives.Count)
                return;

            _objectives = cleaned;
        }

        private List<AttemptRecord> EnsureAttemptList(string objectiveId)
        {
            if (!_attempts.TryGetValue(objectiveId, out var attempts))
            {
                attempts = new List<AttemptRecord>();
                _attempts[objectiveId] = attempts;
            }

            if (!_statistics.ContainsKey(objectiveId))
                _statistics[objectiveId] = new ObjectiveStatistics();

            return attempts;
        }

        private ReadOnlyCollection<LearningObjective> CreateObjectiveSnapshot()
        {
            var snapshot = new LearningObjective[_objectives.Count];
            for (int i = 0; i < _objectives.Count; i++)
                snapshot[i] = CloneObjective(_objectives[i]);

            return Array.AsReadOnly(snapshot);
        }

        private static LearningObjective CloneObjective(LearningObjective objective)
        {
            if (objective == null)
                return null;

            return new LearningObjective
            {
                objectiveId = objective.objectiveId,
                description = objective.description,
                subject = objective.subject,
                masteryThreshold = objective.masteryThreshold
            };
        }

        private static AttemptRecord CloneAttempt(AttemptRecord attempt)
        {
            return new AttemptRecord
            {
                objectiveId = attempt.objectiveId,
                correct = attempt.correct,
                responseTimeSeconds = attempt.responseTimeSeconds,
                activityId = attempt.activityId,
                timestamp = attempt.timestamp,
                sequence = attempt.sequence
            };
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private float GetMasteryThreshold(string objectiveId)
        {
            return objectiveId != null &&
                   _objectivesById.TryGetValue(objectiveId, out var objective) && objective != null
                ? objective.masteryThreshold
                : DefaultMasteryThreshold;
        }

        private static MasteryLevel ClassifyMastery(float accuracy, float threshold)
        {
            if (accuracy >= threshold) return MasteryLevel.Mastered;
            if (accuracy >= threshold * SecureBandRatio) return MasteryLevel.Secure;
            if (accuracy >= threshold * DevelopingBandRatio) return MasteryLevel.Developing;
            return MasteryLevel.Emerging;
        }

        private static float CalculateAccuracy(ObjectiveStatistics statistics)
        {
            return statistics.totalAttempts == 0
                ? 0f
                : (float)statistics.correctAttempts / statistics.totalAttempts;
        }
    }
}
