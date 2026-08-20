// Copyright (c) 2026 Ocean View Games Ltd.
// Licensed under the MIT Licence. See LICENSE file in the project root.
// https://oceanviewgames.co.uk

using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace OceanViewGames.EdTech.Tests
{
    /// <summary>
    /// Behavioural tests for <see cref="AdaptiveDifficultyController"/>.
    /// </summary>
    [TestFixture]
    public class AdaptiveDifficultyControllerTests
    {
        private const string ObjectiveId = "KS2.EN.R.3.1";
        private const float InitialDifficulty = 0.25f;

        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created)
                if (go != null)
                    UnityEngine.Object.Destroy(go);

            _created.Clear();
        }

        private LearningOutcomeTracker NewTracker(params string[] objectiveIds)
        {
            var go = new GameObject("Tracker");
            _created.Add(go);
            var tracker = go.AddComponent<LearningOutcomeTracker>();

            foreach (var id in objectiveIds)
                tracker.RegisterObjective(new LearningObjective { objectiveId = id, masteryThreshold = 0.85f });

            return tracker;
        }

        private AdaptiveDifficultyController NewController(LearningOutcomeTracker tracker)
        {
            var go = new GameObject("Controller");
            _created.Add(go);
            var controller = go.AddComponent<AdaptiveDifficultyController>();
            controller.SetTracker(tracker);
            return controller;
        }

        private static void Record(LearningOutcomeTracker tracker, string objectiveId, int correct, int incorrect)
        {
            for (int i = 0; i < correct; i++) tracker.RecordAttempt(objectiveId, true, 1f);
            for (int i = 0; i < incorrect; i++) tracker.RecordAttempt(objectiveId, false, 1f);
        }

        // -- Event wiring ----------------------------------------------------

        /// <summary>
        /// Regression test: these were declared as bare <c>UnityEvent&lt;T&gt;</c> fields,
        /// which Unity cannot serialise. They never appeared in the Inspector and were
        /// null at runtime, so AddListener threw and no listener ever fired.
        /// </summary>
        [Test]
        public void Events_AreNeverNull()
        {
            var controller = NewController(NewTracker(ObjectiveId));

            Assert.IsNotNull(controller.OnDifficultyChanged);
            Assert.IsNotNull(controller.OnTierChanged);
        }

        [Test]
        public void Events_AcceptListeners()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            float received = -1f;
            controller.OnDifficultyChanged.AddListener(d => received = d);

            Record(tracker, ObjectiveId, 10, 0);
            controller.Evaluate();

            Assert.That(received, Is.EqualTo(controller.CurrentDifficulty).Within(0.0001f));
        }

        // -- Initial state ---------------------------------------------------

        /// <summary>
        /// Regression test: the tier cache was initialised to Easy while the starting
        /// difficulty of 0.25 maps to Medium, so the first in-band difficulty change
        /// reported a tier transition that had not occurred.
        /// </summary>
        [Test]
        public void InitialTier_MatchesInitialDifficulty()
        {
            var controller = NewController(NewTracker(ObjectiveId));

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
            Assert.AreEqual(DifficultyTier.Medium, controller.CurrentTier);
        }

        [Test]
        public void Evaluate_WithinTheSameTier_DoesNotRaiseATierChange()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            var tierEvents = new List<DifficultyTier>();
            controller.OnTierChanged.AddListener(t => tierEvents.Add(t));

            Record(tracker, ObjectiveId, 10, 0);
            controller.Evaluate(); // 0.25 -> 0.35, still Medium

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(0.35f).Within(0.0001f));
            Assert.AreEqual(DifficultyTier.Medium, controller.CurrentTier);
            Assert.IsEmpty(tierEvents);
        }

        // -- Adjustment ------------------------------------------------------

        [Test]
        public void Evaluate_WithStrongPerformance_RaisesDifficulty()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            var diffEvents = new List<float>();
            controller.OnDifficultyChanged.AddListener(d => diffEvents.Add(d));

            Record(tracker, ObjectiveId, 10, 0);
            controller.Evaluate();

            Assert.Greater(controller.CurrentDifficulty, InitialDifficulty);
            Assert.AreEqual(1, diffEvents.Count);
        }

        [Test]
        public void Evaluate_WithWeakPerformance_LowersDifficulty()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);

            Record(tracker, ObjectiveId, 0, 10);
            controller.Evaluate();

            Assert.Less(controller.CurrentDifficulty, InitialDifficulty);
        }

        [Test]
        public void Evaluate_WithMiddlingPerformance_HoldsSteadyAndRaisesNoEvent()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            var diffEvents = new List<float>();
            controller.OnDifficultyChanged.AddListener(d => diffEvents.Add(d));

            Record(tracker, ObjectiveId, 6, 4); // 0.60: between the 0.5 and 0.8 thresholds
            controller.Evaluate();

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
            Assert.IsEmpty(diffEvents);
        }

        [Test]
        public void Evaluate_WithNoAttempts_HoldsSteady()
        {
            var controller = NewController(NewTracker(ObjectiveId));

            controller.Evaluate();

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
        }

        [Test]
        public void Evaluate_CrossingABandBoundary_RaisesATierChange()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            var tierEvents = new List<DifficultyTier>();
            controller.OnTierChanged.AddListener(t => tierEvents.Add(t));

            for (int i = 0; i < 4; i++)
            {
                Record(tracker, ObjectiveId, 1, 0);
                controller.Evaluate(); // 0.25 -> 0.65 across four new attempts
            }

            Assert.AreEqual(DifficultyTier.Hard, controller.CurrentTier);
            Assert.AreEqual(1, tierEvents.Count);
            Assert.AreEqual(DifficultyTier.Hard, tierEvents[0]);
        }

        [Test]
        public void Evaluate_RepeatedlyWithStrongPerformance_ClampsAtOne()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);

            for (int i = 0; i < 40; i++)
            {
                Record(tracker, ObjectiveId, 1, 0);
                controller.Evaluate();
            }

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(1f).Within(0.0001f));
            Assert.AreEqual(DifficultyTier.Challenge, controller.CurrentTier);
        }

        [Test]
        public void Evaluate_RepeatedlyWithWeakPerformance_ClampsAtZero()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);

            for (int i = 0; i < 40; i++)
            {
                Record(tracker, ObjectiveId, 0, 1);
                controller.Evaluate();
            }

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(0f).Within(0.0001f));
            Assert.AreEqual(DifficultyTier.Easy, controller.CurrentTier);
        }

        [Test]
        public void Evaluate_WithoutANewAttempt_DoesNotAdjustAgain()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            Record(tracker, ObjectiveId, 10, 0);

            controller.Evaluate();
            float afterFirstEvaluation = controller.CurrentDifficulty;
            controller.Evaluate();

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(afterFirstEvaluation).Within(0.0001f));
        }

        [Test]
        public void Evaluate_AfterSessionChange_ResetsDifficultyAndProcessesTheNewSessionsSequence()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            for (int i = 0; i < 10; i++)
            {
                Record(tracker, ObjectiveId, 1, 0);
                controller.Evaluate();
            }
            Assert.That(controller.CurrentDifficulty, Is.EqualTo(1f).Within(0.0001f));

            tracker.StartSession("student-2");

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));

            controller.Evaluate(); // no attempts: the reset remains stable

            Record(tracker, ObjectiveId, 0, 1); // sequence restarts at one
            controller.Evaluate();

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(0.15f).Within(0.0001f));
        }

        /// <summary>
        /// Regression test: ResetAllProgress keeps the session ID, so the session-change
        /// check does not fire. Without an explicit notification the controller kept the
        /// difficulty earned by attempts that no longer exist, and a "restart activity"
        /// control handed the next learner a session still clamped at maximum difficulty.
        /// </summary>
        [Test]
        public void ResetAllProgress_ResetsDifficultyWithinTheSameSession()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            for (int i = 0; i < 10; i++)
            {
                Record(tracker, ObjectiveId, 1, 0);
                controller.Evaluate();
            }
            Assert.That(controller.CurrentDifficulty, Is.EqualTo(1f).Within(0.0001f));
            string sessionId = tracker.SessionId;

            tracker.ResetAllProgress();

            Assert.AreEqual(sessionId, tracker.SessionId, "ResetAllProgress must keep the session.");
            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
        }

        /// <summary>
        /// The evaluation cursor is rewound with the difficulty, so attempts recorded
        /// after the reset are evaluated rather than skipped as already consumed.
        /// </summary>
        [Test]
        public void ResetAllProgress_EvaluatesAttemptsRecordedAfterTheReset()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            Record(tracker, ObjectiveId, 10, 0);
            controller.Evaluate();
            tracker.ResetAllProgress();

            Record(tracker, ObjectiveId, 0, 10);
            controller.Evaluate();

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(0.15f).Within(0.0001f));
        }

        /// <summary>
        /// Regression test: the sliding window used to sort by the ISO timestamp string.
        /// The system clock is coarser than a frame, so attempts recorded together tie,
        /// and the tie-break fell back to objective grouping rather than true recency.
        /// </summary>
        [Test]
        public void Evaluate_WindowsByRecency_NotByObjectiveGrouping()
        {
            var tracker = NewTracker("A", "B");
            Record(tracker, "A", 10, 0); // older, all correct
            Record(tracker, "B", 0, 10); // newer, all incorrect
            var controller = NewController(tracker);

            controller.Evaluate();

            Assert.Less(controller.CurrentDifficulty, InitialDifficulty,
                "the ten most recent attempts were all incorrect, so difficulty should drop");
        }

        [Test]
        public void EvaluateForObjective_UsesOnlyThatObjective()
        {
            var tracker = NewTracker("A", "B");
            Record(tracker, "A", 10, 0);
            Record(tracker, "B", 0, 10);
            var controller = NewController(tracker);

            controller.EvaluateForObjective("A");

            Assert.Greater(controller.CurrentDifficulty, InitialDifficulty);
        }

        [Test]
        public void EvaluateForObjective_WithNoAttempts_HoldsSteady()
        {
            var tracker = NewTracker("A");
            var controller = NewController(tracker);

            controller.EvaluateForObjective("A");
            controller.EvaluateForObjective("does.not.exist");

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
        }

        [Test]
        public void EvaluateForObjective_WithoutANewAttempt_DoesNotAdjustAgain()
        {
            var tracker = NewTracker("A");
            var controller = NewController(tracker);
            Record(tracker, "A", 10, 0);

            controller.EvaluateForObjective("A");
            float afterFirstEvaluation = controller.CurrentDifficulty;
            controller.EvaluateForObjective("A");

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(afterFirstEvaluation).Within(0.0001f));
        }

        // -- Evaluation mode guard -------------------------------------------

        [Test]
        public void MixingEvaluationModes_Warns()
        {
            var tracker = NewTracker("A");
            var controller = NewController(tracker);
            Record(tracker, "A", 10, 0);

            controller.Evaluate();
            controller.EvaluateForObjective("A");

            LogAssert.Expect(LogType.Warning, new Regex("Both Evaluate\\(\\) and EvaluateForObjective\\(\\)"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void MixingEvaluationModes_WarnsOnlyOnce()
        {
            var tracker = NewTracker("A");
            var controller = NewController(tracker);
            Record(tracker, "A", 10, 0);

            controller.Evaluate();
            controller.EvaluateForObjective("A");

            // Fresh attempts, so both modes have new evidence to consume and would warn
            // again were the warning not latched.
            Record(tracker, "A", 10, 0);
            controller.Evaluate();
            controller.EvaluateForObjective("A");

            LogAssert.Expect(LogType.Warning, new Regex("Both Evaluate\\(\\) and EvaluateForObjective\\(\\)"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void MixingEvaluationModes_StillAdjustsDifficulty()
        {
            var tracker = NewTracker("A");
            var controller = NewController(tracker);
            Record(tracker, "A", 10, 0);

            controller.Evaluate();
            float afterGlobalEvaluation = controller.CurrentDifficulty;
            controller.EvaluateForObjective("A");

            // The guard reports the mistake without changing what the game does, so a
            // shipped build does not land on a different difficulty than it did before.
            Assert.Greater(controller.CurrentDifficulty, afterGlobalEvaluation,
                "the second mode should still apply its adjustment");

            LogAssert.Expect(LogType.Warning, new Regex("Both Evaluate\\(\\) and EvaluateForObjective\\(\\)"));
        }

        [Test]
        public void SingleEvaluationMode_DoesNotWarn()
        {
            var tracker = NewTracker("A");
            var controller = NewController(tracker);

            for (int i = 0; i < 3; i++)
            {
                Record(tracker, "A", 10, 0);
                controller.Evaluate();
            }

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SingleObjectiveEvaluationMode_AcrossObjectives_DoesNotWarn()
        {
            var tracker = NewTracker("A", "B");
            var controller = NewController(tracker);
            Record(tracker, "A", 10, 0);
            Record(tracker, "B", 10, 0);

            // Several objectives driven through the same mode is the documented usage,
            // not mixing, so it must stay silent.
            controller.EvaluateForObjective("A");
            controller.EvaluateForObjective("B");

            LogAssert.NoUnexpectedReceived();
        }

        // -- Reset -----------------------------------------------------------

        [Test]
        public void ResetDifficulty_ReturnsToTheInitialValue()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            Record(tracker, ObjectiveId, 10, 0);
            for (int i = 0; i < 3; i++) controller.Evaluate();

            controller.ResetDifficulty();

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
            Assert.AreEqual(DifficultyTier.Medium, controller.CurrentTier);
        }

        [Test]
        public void ResetDifficulty_ClampsOutOfRangeValues()
        {
            var controller = NewController(NewTracker(ObjectiveId));

            controller.ResetDifficulty(5f);
            Assert.That(controller.CurrentDifficulty, Is.EqualTo(1f).Within(0.0001f));

            controller.ResetDifficulty(-5f);
            Assert.That(controller.CurrentDifficulty, Is.EqualTo(0f).Within(0.0001f));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void ResetDifficulty_RejectsNonFiniteValues(float difficulty)
        {
            var controller = NewController(NewTracker(ObjectiveId));

            Assert.Throws<System.ArgumentOutOfRangeException>(() => controller.ResetDifficulty(difficulty));
            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
        }

        [Test]
        public void ResetDifficulty_WhenAlreadyAtTheTarget_RaisesNoEvent()
        {
            var controller = NewController(NewTracker(ObjectiveId));
            var changes = new List<float>();
            controller.OnDifficultyChanged.AddListener(d => changes.Add(d));

            controller.ResetDifficulty();

            Assert.IsEmpty(changes);
        }

        [Test]
        public void SetTracker_WithADifferentTracker_ResetsDifficulty()
        {
            var firstTracker = NewTracker("A");
            var secondTracker = NewTracker("B");
            var controller = NewController(firstTracker);
            Record(firstTracker, "A", 1, 0);
            controller.Evaluate();
            Assert.Greater(controller.CurrentDifficulty, InitialDifficulty);

            controller.SetTracker(secondTracker);

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
        }

        // -- Configuration guards --------------------------------------------

        /// <summary>
        /// Regression test: the clamp used to rewrite the serialised decrease threshold.
        /// OnValidate fires continuously while a slider is dragged, so dragging the
        /// increase threshold down and back up permanently ratcheted the authored decrease
        /// threshold down with it, with no way to recover the configured value. Clamping
        /// now happens at the point of use, leaving the authored value intact.
        /// </summary>
        [Test]
        public void InvertedThresholds_LeaveTheAuthoredDecreaseThresholdIntact()
        {
            var controller = NewController(NewTracker(ObjectiveId));
            var type = typeof(AdaptiveDifficultyController);
            var decrease = type.GetField("_decreaseThreshold", BindingFlags.Instance | BindingFlags.NonPublic);
            var increase = type.GetField("_increaseThreshold", BindingFlags.Instance | BindingFlags.NonPublic);

            decrease.SetValue(controller, 0.9f);
            increase.SetValue(controller, 0.5f); // transient inversion, as mid-drag
            type.GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, null);
            LogAssert.Expect(LogType.Warning, new Regex("Decrease threshold"));

            // Restoring the increase threshold restores the configured behaviour.
            increase.SetValue(controller, 0.95f);

            Assert.That((float)decrease.GetValue(controller), Is.EqualTo(0.9f).Within(0.0001f));
        }

        /// <summary>
        /// The effective decrease threshold is collapsed whenever the thresholds are
        /// inverted, including in a player build where OnValidate never runs at all.
        /// </summary>
        [Test]
        public void InvertedThresholds_CollapseTheEffectiveWindow()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            var type = typeof(AdaptiveDifficultyController);
            type.GetField("_decreaseThreshold", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, 0.9f);
            type.GetField("_increaseThreshold", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, 0.5f);
            type.GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, null);
            LogAssert.Expect(LogType.Warning, new Regex("Decrease threshold"));

            // 0.6 sits inside the inverted overlap: below the authored decrease threshold
            // of 0.9 but above the increase threshold, so it must raise difficulty.
            Record(tracker, ObjectiveId, 6, 4);
            controller.Evaluate();

            Assert.Greater(controller.CurrentDifficulty, InitialDifficulty);
        }

        /// <summary>
        /// The one accuracy at which clamping changes the outcome. Because the increase
        /// branch is tested first, every other point in the overlap band already resolved
        /// to "increase"; only accuracy exactly on the increase threshold differs, where
        /// unclamped thresholds would lower difficulty for a student who is on target.
        /// </summary>
        [Test]
        public void Evaluate_AtTheIncreaseThreshold_HoldsRatherThanLoweringDifficulty()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);
            var type = typeof(AdaptiveDifficultyController);
            type.GetField("_decreaseThreshold", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, 0.9f);
            type.GetField("_increaseThreshold", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, 0.5f);
            type.GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(controller, null);
            LogAssert.Expect(LogType.Warning, new Regex("Decrease threshold"));

            Record(tracker, ObjectiveId, 5, 5); // accuracy exactly 0.5
            controller.Evaluate();

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
        }

        [Test]
        public void Evaluate_WithoutATracker_DoesNotThrowOrChangeDifficulty()
        {
            var go = new GameObject("Controller");
            _created.Add(go);
            var controller = go.AddComponent<AdaptiveDifficultyController>();

            Assert.DoesNotThrow(() => controller.Evaluate());
            Assert.DoesNotThrow(() => controller.EvaluateForObjective(ObjectiveId));
            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
        }

        /// <summary>
        /// The missing-tracker warning is latched. Evaluate is documented as callable on a
        /// regular interval, so an unlatched warning wrote one line per frame into the player
        /// log. Warnings do not fail a test on their own, so this asserts the count directly.
        /// </summary>
        [Test]
        public void Evaluate_WithoutATracker_WarnsOnceRatherThanOnEveryCall()
        {
            var go = new GameObject("Controller");
            _created.Add(go);
            var controller = go.AddComponent<AdaptiveDifficultyController>();

            for (int i = 0; i < 5; i++)
            {
                controller.Evaluate();
                controller.EvaluateForObjective(ObjectiveId);
            }

            LogAssert.Expect(LogType.Warning, new Regex("No LearningOutcomeTracker assigned"));
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>
        /// Assigning the tracker once a tracker is available again clears the latch, so a
        /// genuinely new gap is still reported rather than being swallowed by the first one.
        /// </summary>
        [Test]
        public void Evaluate_AfterTheTrackerIsRestoredAndLost_WarnsAgain()
        {
            var go = new GameObject("Controller");
            _created.Add(go);
            var controller = go.AddComponent<AdaptiveDifficultyController>();

            controller.Evaluate();
            controller.SetTracker(NewTracker(ObjectiveId));
            controller.Evaluate();
            controller.SetTracker(null);
            controller.Evaluate();

            LogAssert.Expect(LogType.Warning, new Regex("No LearningOutcomeTracker assigned"));
            LogAssert.Expect(LogType.Warning, new Regex("No LearningOutcomeTracker assigned"));
            LogAssert.NoUnexpectedReceived();
        }

        // -- Scene lifecycle -------------------------------------------------

        /// <summary>
        /// Exercises the real Awake/OnEnable/Start ordering that an Inspector-wired controller
        /// goes through. The rest of the fixture calls SetTracker synchronously after
        /// AddComponent and never yields, so Start never runs and the deferral logic that
        /// OnEnable relies on is otherwise never executed.
        /// </summary>
        [UnityTest]
        public IEnumerator InspectorWiredController_RunsStartAndThenEvaluatesNormally()
        {
            var tracker = NewTracker(ObjectiveId);

            var go = new GameObject("Controller");
            go.SetActive(false);
            _created.Add(go);
            var controller = go.AddComponent<AdaptiveDifficultyController>();

            // Stand in for the Inspector reference, so Awake sees the tracker as a scene
            // component would rather than through the SetTracker runtime path.
            typeof(AdaptiveDifficultyController)
                .GetField("_tracker", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(controller, tracker);

            go.SetActive(true);
            yield return null; // Awake, OnEnable and Start have all run by now.

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f),
                "Start must not disturb difficulty when the session is unchanged.");

            Record(tracker, ObjectiveId, 9, 1);
            controller.Evaluate();

            Assert.Greater(controller.CurrentDifficulty, InitialDifficulty);
        }

        /// <summary>
        /// A controller disabled across a StartSession call misses the SessionStarted event,
        /// because it unsubscribes in OnDisable. Re-enabling must notice the session changed
        /// underneath it and drop the difficulty earned by the previous student.
        /// </summary>
        [UnityTest]
        public IEnumerator ReEnabling_AfterASessionChangeMissedWhileDisabled_ResetsDifficulty()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);

            yield return null; // Let Start run, so the re-enable path is not deferred.

            Record(tracker, ObjectiveId, 9, 1);
            controller.Evaluate();
            Assert.Greater(controller.CurrentDifficulty, InitialDifficulty,
                "Precondition: the previous student raised the difficulty.");

            controller.gameObject.SetActive(false);
            tracker.StartSession("student-next");
            controller.gameObject.SetActive(true);

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
        }

        /// <summary>
        /// Regression test: a controller disabled across ResetAllProgress misses the
        /// ProgressReset event, and the session ID is unchanged, so re-enabling used to leave
        /// it holding a difficulty earned by attempts the tracker has since discarded.
        /// </summary>
        [UnityTest]
        public IEnumerator ReEnabling_AfterAProgressResetMissedWhileDisabled_ResetsDifficulty()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);

            yield return null; // Let Start run, so the re-enable path is not deferred.

            Record(tracker, ObjectiveId, 9, 1);
            controller.Evaluate();
            Assert.Greater(controller.CurrentDifficulty, InitialDifficulty,
                "Precondition: the difficulty was earned by attempts about to be discarded.");

            controller.gameObject.SetActive(false);
            tracker.ResetAllProgress();
            controller.gameObject.SetActive(true);

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(InitialDifficulty).Within(0.0001f));
        }

        /// <summary>
        /// The re-enable reconcile must only fire on a genuine change: an uneventful
        /// disable/enable cycle has to leave hard-won difficulty exactly where it was.
        /// </summary>
        [UnityTest]
        public IEnumerator ReEnabling_WithNothingChanged_LeavesDifficultyUntouched()
        {
            var tracker = NewTracker(ObjectiveId);
            var controller = NewController(tracker);

            yield return null;

            Record(tracker, ObjectiveId, 9, 1);
            controller.Evaluate();
            float earnedDifficulty = controller.CurrentDifficulty;
            Assert.Greater(earnedDifficulty, InitialDifficulty, "Precondition: difficulty rose.");

            controller.gameObject.SetActive(false);
            controller.gameObject.SetActive(true);

            Assert.That(controller.CurrentDifficulty, Is.EqualTo(earnedDifficulty).Within(0.0001f));
        }
    }
}
