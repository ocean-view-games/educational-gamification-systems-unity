// Copyright (c) 2026 Ocean View Games Ltd.
// Licensed under the MIT Licence. See LICENSE file in the project root.
// https://oceanviewgames.co.uk

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace OceanViewGames.EdTech
{
    /// <summary>
    /// Discrete difficulty levels exposed to gameplay systems.
    /// </summary>
    public enum DifficultyTier
    {
        Easy,
        Medium,
        Hard,
        Challenge
    }

    /// <summary>
    /// Fired when the continuous difficulty value changes. Passes the new value (0 to 1).
    /// </summary>
    /// <remarks>
    /// Unity cannot serialise an open generic type, so <c>UnityEvent&lt;float&gt;</c> must be
    /// subclassed concretely for the event to appear in the Inspector.
    /// </remarks>
    [Serializable]
    public class DifficultyChangedEvent : UnityEvent<float> { }

    /// <summary>
    /// Fired when the discrete difficulty tier changes. Passes the new tier.
    /// </summary>
    /// <remarks>
    /// Unity cannot serialise an open generic type, so <c>UnityEvent&lt;DifficultyTier&gt;</c>
    /// must be subclassed concretely for the event to appear in the Inspector.
    /// </remarks>
    [Serializable]
    public class DifficultyTierChangedEvent : UnityEvent<DifficultyTier> { }

    /// <summary>
    /// Adjusts game difficulty dynamically based on student performance data from
    /// a <see cref="LearningOutcomeTracker"/>. The controller monitors recent accuracy
    /// over a sliding window of attempts and raises or lowers difficulty accordingly.
    /// </summary>
    /// <remarks>
    /// Algorithm: if recent accuracy exceeds the increase threshold, difficulty rises;
    /// if it falls below the decrease threshold, difficulty drops; otherwise it holds
    /// steady. This produces a smooth scaffolding curve that keeps students in their
    /// zone of proximal development.
    ///
    /// Developed by Ocean View Games (https://oceanviewgames.co.uk) for curriculum-aligned
    /// educational games. See the architecture documentation for integration details.
    /// </remarks>
    [AddComponentMenu("Ocean View Games/EdTech/Adaptive Difficulty Controller")]
    public class AdaptiveDifficultyController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField]
        [Tooltip("The LearningOutcomeTracker to read performance data from")]
        private LearningOutcomeTracker _tracker;

        [Header("Difficulty Settings")]
        [SerializeField]
        [Tooltip("Difficulty the controller starts at, and the value ResetDifficulty() returns to (0-1)")]
        [Range(0f, 1f)]
        private float _initialDifficulty = 0.25f;

        [SerializeField]
        [Tooltip("Number of recent attempts to consider when evaluating performance")]
        // Bounded by the tracker's global recent-attempt window: a larger value here would be
        // silently truncated by Evaluate, so the two are pinned to one constant.
        [Range(3, LearningOutcomeTracker.RecentAttemptCapacity)]
        private int _windowSize = 10;

        [SerializeField]
        [Tooltip("Accuracy threshold above which difficulty increases (0-1)")]
        [Range(0f, 1f)]
        private float _increaseThreshold = 0.8f;

        [SerializeField]
        [Tooltip("Accuracy threshold below which difficulty decreases (0-1). Must not exceed the increase threshold.")]
        [Range(0f, 1f)]
        private float _decreaseThreshold = 0.5f;

        [SerializeField]
        [Tooltip("How much difficulty changes per adjustment step (0-1)")]
        [Range(0.01f, 0.25f)]
        private float _adjustmentStep = 0.1f;

        [Header("Events")]
        [SerializeField]
        [Tooltip("Fired when the difficulty value changes. Passes the new difficulty (0-1).")]
        private DifficultyChangedEvent _onDifficultyChanged = new();

        [SerializeField]
        [Tooltip("Fired when the difficulty tier changes. Passes the new tier.")]
        private DifficultyTierChangedEvent _onTierChanged = new();

        // Current difficulty as a continuous value between 0 (easiest) and 1 (hardest).
        private float _currentDifficulty = 0.25f;

        // The last tier that was broadcast, used to detect tier transitions. Kept in
        // sync with _currentDifficulty from Awake onwards so the first genuine change
        // does not report a transition that never happened.
        private DifficultyTier _lastTier = DifficultyTier.Medium;

        // Evaluation cursors prevent a timer or duplicate callback from applying the
        // same evidence repeatedly. Global and per-objective evaluation are independent
        // modes, so each keeps its own cursor.
        private long _lastGlobalEvaluationSequence;
        private readonly Dictionary<string, long> _lastObjectiveEvaluationSequences = new();
        private string _evaluationSessionId;

        // The tracker's progress generation as of the last time this controller reconciled
        // with it. Compared alongside the session ID so a ResetAllProgress that happened
        // while this component was disabled — whose ProgressReset event it never received —
        // is still detected on the next reconcile.
        private long _evaluationProgressGeneration;
        private LearningOutcomeTracker _subscribedTracker;

        // Set once Start has run. Until then, every component in the scene may not yet
        // have received OnEnable, so event-raising work is deferred to Start.
        private bool _started;

        // Latches the "no tracker" warning. Evaluate is documented as callable on a regular
        // interval, so warning on every call floods the player log from Update.
        private bool _missingTrackerLogged;

        // Records which evaluation mode has actually consumed attempts. Mixing the two
        // applies the same attempts twice, and the symptom — difficulty moving at double
        // the configured step — is easy to misread as a badly tuned adjustment step. These
        // are deliberately never reset: the invariant is one mode per controller instance
        // for its whole lifetime, so a new session does not make mixing acceptable.
        private bool _globalEvaluationUsed;
        private bool _objectiveEvaluationUsed;
        private bool _mixedEvaluationLogged;

        /// <summary>
        /// Current difficulty as a continuous value between 0 (easiest) and 1 (hardest).
        /// </summary>
        public float CurrentDifficulty => _currentDifficulty;

        /// <summary>
        /// Current difficulty expressed as a discrete tier.
        /// </summary>
        public DifficultyTier CurrentTier => MapToTier(_currentDifficulty);

        /// <summary>
        /// Fired when the difficulty value changes. Passes the new difficulty (0 to 1).
        /// </summary>
        public DifficultyChangedEvent OnDifficultyChanged => _onDifficultyChanged;

        /// <summary>
        /// Fired when the difficulty tier changes. Passes the new tier.
        /// </summary>
        public DifficultyTierChangedEvent OnTierChanged => _onTierChanged;

        private void Awake()
        {
            // The event fields carry initialisers, but a component deserialised from a
            // scene authored before they existed can still arrive with them null.
            _onDifficultyChanged ??= new DifficultyChangedEvent();
            _onTierChanged ??= new DifficultyTierChangedEvent();

            WarnIfThresholdsInverted();

            _currentDifficulty = Mathf.Clamp01(_initialDifficulty);
            _lastTier = MapToTier(_currentDifficulty);
            SyncEvaluationBaseline();
        }

        private void OnEnable()
        {
            SubscribeToTracker();

            // RefreshEvaluationState can fire OnDifficultyChanged/OnTierChanged
            // synchronously, and Unity does not define the order in which components
            // receive OnEnable, so on the first enable a listener that subscribes in its
            // own OnEnable could miss the reset. Start runs after every OnEnable in the
            // scene, so defer to it; later re-enables are past that ambiguity already.
            if (_started && _tracker != null)
                RefreshEvaluationState();
        }

        private void Start()
        {
            _started = true;
            if (_tracker != null)
                RefreshEvaluationState();
        }

        private void OnDisable()
        {
            UnsubscribeFromTracker();
        }

        /// <summary>
        /// The decrease threshold actually used when adjusting difficulty, never above
        /// the increase threshold.
        /// </summary>
        /// <remarks>
        /// A decrease threshold above the increase threshold makes the two branches of
        /// <see cref="AdjustDifficulty"/> overlap, so accuracy in the overlap would raise
        /// difficulty for a student who is struggling. The window is collapsed here at the
        /// point of use rather than by rewriting the serialised field: <c>OnValidate</c>
        /// fires continuously while a slider is dragged, so a destructive clamp let a
        /// transient inversion mid-drag permanently ratchet the authored value with no way
        /// to recover it. Reading it through this property also covers player builds,
        /// where <c>OnValidate</c> never runs at all.
        /// </remarks>
        private float EffectiveDecreaseThreshold => Mathf.Min(_decreaseThreshold, _increaseThreshold);

        /// <summary>
        /// Warns the first time both evaluation modes have consumed attempts on this
        /// controller, since from that point on shared attempts are applied twice.
        /// </summary>
        /// <remarks>
        /// This warns rather than throwing or ignoring the second mode. Refusing the call
        /// would change the difficulty a shipped game arrives at, and silently dropping it
        /// would hide the mistake entirely; neither is a good trade for a component that
        /// runs in front of children mid-lesson. The warning is latched because the fault
        /// is in the calling code's shape, not in any one call, so repeating it once per
        /// evaluation would flood the player log without adding information.
        /// </remarks>
        private void WarnIfEvaluationModesMixed()
        {
            if (_mixedEvaluationLogged || !_globalEvaluationUsed || !_objectiveEvaluationUsed)
                return;

            _mixedEvaluationLogged = true;

            Debug.LogWarning(
                "[AdaptiveDifficultyController] Both Evaluate() and EvaluateForObjective() have " +
                "adjusted difficulty on this controller. They track consumed attempts separately, " +
                "so attempts counted by both move difficulty at twice the configured step. Pick one " +
                "mode per controller instance, and use a controller per objective if objectives need " +
                "independent difficulty.", this);
        }

        /// <summary>
        /// Logs once at startup if the authored thresholds are inverted, since the
        /// collapsed window silently ignores the configured decrease threshold.
        /// </summary>
        private void WarnIfThresholdsInverted()
        {
            if (_decreaseThreshold <= _increaseThreshold)
                return;

            Debug.LogWarning(
                $"[AdaptiveDifficultyController] Decrease threshold ({_decreaseThreshold:0.00}) cannot exceed " +
                $"the increase threshold ({_increaseThreshold:0.00}); treating it as " +
                $"{_increaseThreshold:0.00}.", this);
        }

        /// <summary>
        /// Evaluates recent student performance across all tracked objectives and
        /// adjusts difficulty accordingly. Call this after recording new attempts,
        /// or on a regular interval (e.g. end of each round).
        /// </summary>
        /// <remarks>
        /// This is one of two mutually exclusive evaluation modes. It tracks which
        /// attempts it has already consumed independently of
        /// <see cref="EvaluateForObjective"/>, so calling both on the same controller
        /// applies the same attempts twice and moves difficulty at double the configured
        /// step. Pick one mode per controller instance. If both modes do adjust difficulty
        /// on one controller, a warning is logged once identifying the mistake.
        /// </remarks>
        public void Evaluate()
        {
            if (!HasTracker())
                return;

            RefreshEvaluationState();

            long latestSequence = _tracker.LatestAttemptSequence;
            if (latestSequence <= _lastGlobalEvaluationSequence)
                return;

            float recentAccuracy = CalculateRecentAccuracy();

            // No attempts yet; keep current difficulty.
            if (recentAccuracy < 0f)
                return;

            _lastGlobalEvaluationSequence = latestSequence;
            _globalEvaluationUsed = true;
            WarnIfEvaluationModesMixed();
            AdjustDifficulty(recentAccuracy);
        }

        /// <summary>
        /// Evaluates performance using attempts from a single objective and adjusts this
        /// controller's shared difficulty value. Use one controller instance per objective
        /// if independent difficulty states are required.
        /// </summary>
        /// <remarks>
        /// This is one of two mutually exclusive evaluation modes. It tracks which
        /// attempts it has already consumed independently of <see cref="Evaluate"/>, so
        /// calling both on the same controller applies the same attempts twice and moves
        /// difficulty at double the configured step. Pick one mode per controller instance.
        /// If both modes do adjust difficulty on one controller, a warning is logged once
        /// identifying the mistake.
        /// </remarks>
        /// <param name="objectiveId">The curriculum code of the objective to evaluate.</param>
        public void EvaluateForObjective(string objectiveId)
        {
            if (!HasTracker())
                return;

            RefreshEvaluationState();

            var attempts = _tracker.GetAttemptsForEvaluation(objectiveId);
            if (attempts.Count == 0)
                return;

            long latestSequence = attempts[attempts.Count - 1].sequence;
            if (_lastObjectiveEvaluationSequences.TryGetValue(objectiveId, out var lastSequence) &&
                latestSequence <= lastSequence)
                return;

            int start = Mathf.Max(0, attempts.Count - _windowSize);
            int correct = 0;

            for (int i = start; i < attempts.Count; i++)
                if (attempts[i].correct) correct++;

            _lastObjectiveEvaluationSequences[objectiveId] = latestSequence;
            _objectiveEvaluationUsed = true;
            WarnIfEvaluationModesMixed();
            AdjustDifficulty((float)correct / (attempts.Count - start));
        }

        /// <summary>
        /// Resets difficulty to the configured initial value and fires change events
        /// if the value or tier actually changes.
        /// </summary>
        public void ResetDifficulty()
        {
            ResetDifficulty(_initialDifficulty);
        }

        /// <summary>
        /// Resets difficulty to the specified value and fires change events if the
        /// value or tier actually changes.
        /// </summary>
        /// <param name="difficulty">The new difficulty value, clamped between 0 and 1.</param>
        public void ResetDifficulty(float difficulty)
        {
            if (float.IsNaN(difficulty) || float.IsInfinity(difficulty))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(difficulty), difficulty, "Difficulty must be a finite value.");
            }

            float nextDifficulty = Mathf.Clamp01(difficulty);
            if (Mathf.Approximately(_currentDifficulty, nextDifficulty))
                return;

            _currentDifficulty = nextDifficulty;
            _onDifficultyChanged?.Invoke(_currentDifficulty);

            var newTier = MapToTier(_currentDifficulty);
            if (newTier != _lastTier)
            {
                _lastTier = newTier;
                _onTierChanged?.Invoke(newTier);
            }
        }

        /// <summary>
        /// Sets the tracker reference at runtime. Useful when the tracker is
        /// instantiated dynamically.
        /// </summary>
        /// <param name="tracker">The LearningOutcomeTracker to read from.</param>
        public void SetTracker(LearningOutcomeTracker tracker)
        {
            if (_tracker == tracker)
                return;

            UnsubscribeFromTracker();
            _tracker = tracker;
            SyncEvaluationBaseline();
            _lastGlobalEvaluationSequence = 0;
            _lastObjectiveEvaluationSequences.Clear();

            if (_tracker != null)
            {
                if (isActiveAndEnabled)
                    SubscribeToTracker();
                ResetDifficulty();
            }
        }

        /// <summary>
        /// Reports whether a tracker is assigned, warning once per gap rather than once per
        /// call. <see cref="Evaluate"/> is documented as callable on a regular interval, so an
        /// unlatched warning writes a line per frame into the player log when the reference is
        /// missing. The latch clears when a tracker is assigned, so a later gap warns again.
        /// </summary>
        private bool HasTracker()
        {
            if (_tracker != null)
            {
                _missingTrackerLogged = false;
                return true;
            }

            if (!_missingTrackerLogged)
            {
                _missingTrackerLogged = true;
                Debug.LogWarning("[AdaptiveDifficultyController] No LearningOutcomeTracker assigned.", this);
            }

            return false;
        }

        /// <summary>
        /// Calculates accuracy over the most recent attempts across all objectives.
        /// Returns -1 if no attempts exist.
        /// </summary>
        private float CalculateRecentAccuracy()
        {
            var recentAttempts = _tracker.RecentAttemptsForEvaluation;
            if (recentAttempts.Count == 0)
                return -1f;

            int window = Mathf.Min(_windowSize, recentAttempts.Count);
            int start = recentAttempts.Count - window;
            int correct = 0;

            for (int i = start; i < recentAttempts.Count; i++)
                if (recentAttempts[i].correct) correct++;

            return (float)correct / window;
        }

        /// <summary>
        /// Reconciles this controller with the tracker's current state, discarding difficulty
        /// earned from evidence the tracker no longer holds.
        /// </summary>
        /// <remarks>
        /// Both a new session and a within-session <see cref="LearningOutcomeTracker.ResetAllProgress"/>
        /// invalidate the difficulty. Each normally arrives as an event, but events only reach
        /// an enabled component, so this also compares the tracker's progress generation: that
        /// catches a clear that happened while the controller was disabled, which the session ID
        /// alone cannot see because <c>ResetAllProgress</c> leaves it unchanged.
        /// </remarks>
        private void RefreshEvaluationState()
        {
            string sessionId = _tracker.SessionId;
            long progressGeneration = _tracker.ProgressGeneration;

            if (string.Equals(_evaluationSessionId, sessionId, StringComparison.Ordinal) &&
                progressGeneration == _evaluationProgressGeneration)
                return;

            bool replacingKnownState = _evaluationSessionId != null;
            _evaluationSessionId = sessionId;
            _evaluationProgressGeneration = progressGeneration;
            _lastGlobalEvaluationSequence = 0;
            _lastObjectiveEvaluationSequences.Clear();

            if (replacingKnownState)
                ResetDifficulty();
        }

        /// <summary>
        /// Adopts the tracker's current session and progress generation as this controller's
        /// baseline, so an already-reconciled state is not mistaken for a missed change.
        /// </summary>
        private void SyncEvaluationBaseline()
        {
            _evaluationSessionId = _tracker != null ? _tracker.SessionId : null;
            _evaluationProgressGeneration = _tracker != null ? _tracker.ProgressGeneration : 0;
        }

        private void SubscribeToTracker()
        {
            if (_tracker == null || _subscribedTracker == _tracker)
                return;

            UnsubscribeFromTracker();
            _tracker.SessionStarted += HandleSessionStarted;
            _tracker.ProgressReset += HandleProgressReset;
            _subscribedTracker = _tracker;
        }

        private void UnsubscribeFromTracker()
        {
            if (_subscribedTracker == null)
                return;

            _subscribedTracker.SessionStarted -= HandleSessionStarted;
            _subscribedTracker.ProgressReset -= HandleProgressReset;
            _subscribedTracker = null;
        }

        private void HandleSessionStarted(string sessionId)
        {
            _evaluationSessionId = sessionId;
            _evaluationProgressGeneration = _tracker != null ? _tracker.ProgressGeneration : 0;
            _lastGlobalEvaluationSequence = 0;
            _lastObjectiveEvaluationSequences.Clear();
            ResetDifficulty();
        }

        /// <summary>
        /// Handles the tracker clearing attempts within the current session (for example a
        /// "restart activity" control), so the controller does not keep difficulty earned by
        /// evidence that no longer exists. This resets immediately; the progress-generation
        /// check in <see cref="RefreshEvaluationState"/> is the fallback for a clear that
        /// happened while this component was disabled and this handler never ran.
        /// </summary>
        private void HandleProgressReset()
        {
            _evaluationProgressGeneration = _tracker != null ? _tracker.ProgressGeneration : 0;
            _lastGlobalEvaluationSequence = 0;
            _lastObjectiveEvaluationSequences.Clear();
            ResetDifficulty();
        }

        private void AdjustDifficulty(float accuracy)
        {
            float previousDifficulty = _currentDifficulty;

            if (accuracy > _increaseThreshold)
                _currentDifficulty = Mathf.Min(1f, _currentDifficulty + _adjustmentStep);
            else if (accuracy < EffectiveDecreaseThreshold)
                _currentDifficulty = Mathf.Max(0f, _currentDifficulty - _adjustmentStep);

            if (Mathf.Approximately(previousDifficulty, _currentDifficulty))
                return;

            _onDifficultyChanged?.Invoke(_currentDifficulty);

            var newTier = MapToTier(_currentDifficulty);
            if (newTier != _lastTier)
            {
                _lastTier = newTier;
                _onTierChanged?.Invoke(newTier);
            }
        }

        /// <summary>
        /// Maps a continuous difficulty value (0 to 1) to a discrete tier.
        /// </summary>
        private static DifficultyTier MapToTier(float difficulty)
        {
            return difficulty switch
            {
                < 0.25f => DifficultyTier.Easy,
                < 0.5f => DifficultyTier.Medium,
                < 0.75f => DifficultyTier.Hard,
                _ => DifficultyTier.Challenge
            };
        }
    }
}
