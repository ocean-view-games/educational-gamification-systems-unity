// Copyright (c) 2026 Ocean View Games Ltd.
// Licensed under the MIT Licence. See LICENSE file in the project root.
// https://oceanviewgames.co.uk

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace OceanViewGames.EdTech.Tests
{
    /// <summary>
    /// Behavioural tests for <see cref="LearningOutcomeTracker"/>.
    /// </summary>
    /// <remarks>
    /// These run as Play Mode tests. Adding a component to an active GameObject in
    /// Play Mode invokes Awake synchronously, which the tracker relies on to issue
    /// its session ID and de-duplicate authored objectives.
    /// </remarks>
    [TestFixture]
    public class LearningOutcomeTrackerTests
    {
        private const string ObjectiveId = "KS2.EN.R.3.1";

        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created)
                if (go != null)
                    UnityEngine.Object.Destroy(go);

            _created.Clear();
        }

        private LearningOutcomeTracker NewTracker(float masteryThreshold = 0.85f, string objectiveId = ObjectiveId)
        {
            var tracker = NewBareTracker();
            tracker.RegisterObjective(new LearningObjective
            {
                objectiveId = objectiveId,
                description = "Test objective",
                subject = "English",
                masteryThreshold = masteryThreshold
            });
            return tracker;
        }

        private LearningOutcomeTracker NewBareTracker()
        {
            var go = new GameObject("Tracker");
            _created.Add(go);
            return go.AddComponent<LearningOutcomeTracker>();
        }

        private static void Record(LearningOutcomeTracker tracker, string objectiveId, int correct, int incorrect)
        {
            for (int i = 0; i < correct; i++) tracker.RecordAttempt(objectiveId, true, 1f);
            for (int i = 0; i < incorrect; i++) tracker.RecordAttempt(objectiveId, false, 1f);
        }

        // -- Mastery banding -------------------------------------------------

        [TestCase(10, 0, MasteryLevel.Mastered)]    // 1.00
        [TestCase(9, 1, MasteryLevel.Mastered)]     // 0.90
        [TestCase(8, 2, MasteryLevel.Secure)]       // 0.80
        [TestCase(7, 3, MasteryLevel.Secure)]       // 0.70
        [TestCase(6, 4, MasteryLevel.Developing)]   // 0.60
        [TestCase(4, 6, MasteryLevel.Developing)]   // 0.40
        [TestCase(3, 7, MasteryLevel.Emerging)]     // 0.30
        [TestCase(0, 10, MasteryLevel.Emerging)]    // 0.00
        public void GetMasteryLevel_AtDefaultThreshold_MatchesDocumentedBands(
            int correct, int incorrect, MasteryLevel expected)
        {
            var tracker = NewTracker();
            Record(tracker, ObjectiveId, correct, incorrect);

            Assert.AreEqual(expected, tracker.GetMasteryLevel(ObjectiveId));
        }

        [Test]
        public void GetMasteryLevel_WithNoAttempts_IsNotStarted()
        {
            var tracker = NewTracker();

            Assert.AreEqual(MasteryLevel.NotStarted, tracker.GetMasteryLevel(ObjectiveId));
        }

        /// <summary>
        /// Regression test: with fixed 0.65 / 0.35 cut-offs, any threshold below 0.65
        /// made Secure unreachable, because accuracy at 0.65 already cleared the
        /// mastery threshold. The bands are now proportional to the threshold.
        /// </summary>
        [Test]
        public void GetMasteryLevel_WithLowThreshold_KeepsEveryBandReachable()
        {
            var seen = new HashSet<MasteryLevel>();

            for (int correct = 0; correct <= 20; correct++)
            {
                var tracker = NewTracker(masteryThreshold: 0.5f);
                Record(tracker, ObjectiveId, correct, 20 - correct);
                seen.Add(tracker.GetMasteryLevel(ObjectiveId));
            }

            Assert.That(seen, Does.Contain(MasteryLevel.Mastered));
            Assert.That(seen, Does.Contain(MasteryLevel.Secure));
            Assert.That(seen, Does.Contain(MasteryLevel.Developing));
            Assert.That(seen, Does.Contain(MasteryLevel.Emerging));
        }

        [Test]
        public void GetMasteryLevel_WithLowThreshold_MastersAtThatThreshold()
        {
            var tracker = NewTracker(masteryThreshold: 0.5f);
            Record(tracker, ObjectiveId, 6, 4); // 0.60, comfortably above 0.5

            Assert.AreEqual(MasteryLevel.Mastered, tracker.GetMasteryLevel(ObjectiveId));
        }

        [Test]
        public void GetMasteryLevel_ForUnknownObjective_IsNotStarted()
        {
            var tracker = NewTracker();

            Assert.AreEqual(MasteryLevel.NotStarted, tracker.GetMasteryLevel("does.not.exist"));
            Assert.AreEqual(MasteryLevel.NotStarted, tracker.GetMasteryLevel(null));
        }

        // -- Accuracy --------------------------------------------------------

        [Test]
        public void GetAccuracy_ReflectsRecordedAttempts()
        {
            var tracker = NewTracker();
            Record(tracker, ObjectiveId, 3, 1);

            Assert.That(tracker.GetAccuracy(ObjectiveId), Is.EqualTo(0.75f).Within(0.0001f));
        }

        [Test]
        public void GetAccuracy_ForUnknownObjective_IsZero()
        {
            var tracker = NewTracker();

            Assert.That(tracker.GetAccuracy("does.not.exist"), Is.EqualTo(0f).Within(0.0001f));
            Assert.That(tracker.GetAccuracy(null), Is.EqualTo(0f).Within(0.0001f));
        }

        // -- Sessions --------------------------------------------------------

        /// <summary>
        /// Regression test: the session ID used to be generated inside GenerateReport,
        /// so every periodic sync within one sitting reported a different session.
        /// </summary>
        [Test]
        public void GenerateReport_RepeatedWithinASession_ReusesTheSameSessionId()
        {
            var tracker = NewTracker();
            Record(tracker, ObjectiveId, 3, 1);

            string first = tracker.GenerateReport().sessionId;
            string second = tracker.GenerateReport().sessionId;

            Assert.That(first, Is.Not.Null.And.Not.Empty);
            Assert.AreEqual(first, second);
            Assert.AreEqual(first, tracker.SessionId);
        }

        [Test]
        public void StartSession_IssuesNewSessionIdAndClearsAttemptsButKeepsObjectives()
        {
            var tracker = NewTracker();
            Record(tracker, ObjectiveId, 5, 5);
            string previousSession = tracker.SessionId;

            string newSession = tracker.StartSession("student-2");

            Assert.AreNotEqual(previousSession, newSession);
            Assert.AreEqual(newSession, tracker.SessionId);
            Assert.AreEqual("student-2", tracker.StudentId);
            Assert.AreEqual(0, tracker.GetAttempts(ObjectiveId).Count);
            Assert.AreEqual(1, tracker.Objectives.Count);
        }

        /// <summary>
        /// Regression test: the student ID used to carry [SerializeField]. An Inspector
        /// value was baked into the scene or prefab and shipped in the build — a child's
        /// identifier disclosed in the player, which SECURITY.md puts in scope — and it
        /// also tripped the one-shot guard in SetStudentId, so the documented runtime
        /// call threw. It must be assignable only at runtime.
        /// </summary>
        [Test]
        public void StudentId_IsNotSerialised()
        {
            var field = typeof(LearningOutcomeTracker)
                .GetField("_studentId", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field, "Expected a _studentId backing field.");
            Assert.IsEmpty(field.GetCustomAttributes(typeof(SerializeField), false),
                "_studentId must not be serialised into scenes, prefabs, or builds.");
        }

        /// <summary>
        /// A freshly added tracker starts anonymous, so the documented runtime assignment
        /// succeeds rather than colliding with an authored placeholder.
        /// </summary>
        [Test]
        public void SetStudentId_OnAFreshTracker_Succeeds()
        {
            var tracker = NewTracker();

            Assert.IsEmpty(tracker.StudentId);
            Assert.DoesNotThrow(() => tracker.SetStudentId("student-4021"));
        }

        [Test]
        public void SetStudentId_IsReflectedOnTheReport()
        {
            var tracker = NewTracker();

            tracker.SetStudentId("student-4021");

            Assert.AreEqual("student-4021", tracker.StudentId);
            Assert.AreEqual("student-4021", tracker.GenerateReport().studentId);
        }

        [Test]
        public void SetStudentId_WithNull_BecomesEmpty()
        {
            var tracker = NewTracker();

            tracker.SetStudentId(null);

            Assert.AreEqual(string.Empty, tracker.StudentId);
        }

        [Test]
        public void SetStudentId_CannotRelabelExistingAttempts()
        {
            var tracker = NewTracker();
            tracker.SetStudentId("student-1");
            Record(tracker, ObjectiveId, 1, 0);

            Assert.Throws<InvalidOperationException>(() => tracker.SetStudentId("student-2"));
            Assert.AreEqual("student-1", tracker.StudentId);
            Assert.AreEqual(1, tracker.GetAttempts(ObjectiveId).Count);

            tracker.ResetAllProgress();
            Assert.Throws<InvalidOperationException>(() => tracker.SetStudentId("student-2"),
                "clearing progress must not reuse the same session for another student");
        }

        [Test]
        public void SetStudentId_WithTheSameId_DoesNotRejectAnActiveSession()
        {
            var tracker = NewTracker();
            tracker.SetStudentId("student-1");
            Record(tracker, ObjectiveId, 1, 0);

            Assert.DoesNotThrow(() => tracker.SetStudentId("student-1"));
        }

        [Test]
        public void SetStudentId_CannotReplaceAnAssignedIdBeforeAttempts()
        {
            var tracker = NewTracker();
            tracker.SetStudentId("student-1");

            Assert.Throws<InvalidOperationException>(() => tracker.SetStudentId("student-2"));
            Assert.AreEqual("student-1", tracker.StudentId);
        }

        [Test]
        public void SetStudentId_CanIdentifyAnAnonymousActiveSession()
        {
            var tracker = NewTracker();
            Record(tracker, ObjectiveId, 1, 0);

            Assert.DoesNotThrow(() => tracker.SetStudentId("student-1"));
            Assert.AreEqual("student-1", tracker.GenerateReport().studentId);
            Assert.AreEqual(1, tracker.GenerateReport().objectives[0].totalAttempts);
        }

        [Test]
        public void ResetAllProgress_ClearsAttemptsButKeepsTheSession()
        {
            var tracker = NewTracker();
            Record(tracker, ObjectiveId, 4, 2);
            string session = tracker.SessionId;

            tracker.ResetAllProgress();

            Assert.AreEqual(0, tracker.GetAttempts(ObjectiveId).Count);
            Assert.AreEqual(0, tracker.GetAttemptCount(ObjectiveId));
            Assert.AreEqual(session, tracker.SessionId);
            Assert.AreEqual(1, tracker.Objectives.Count);
        }

        [Test]
        public void ResetAllProgress_RaisesProgressReset()
        {
            var tracker = NewTracker();
            Record(tracker, ObjectiveId, 4, 2);
            int raised = 0;
            tracker.ProgressReset += () => raised++;

            tracker.ResetAllProgress();

            Assert.AreEqual(1, raised);
        }

        /// <summary>
        /// StartSession clears progress too, but raises SessionStarted for it. Raising both
        /// would make every consumer reset twice for one logical event.
        /// </summary>
        [Test]
        public void StartSession_RaisesSessionStartedOnly()
        {
            var tracker = NewTracker();
            Record(tracker, ObjectiveId, 4, 2);
            int progressReset = 0;
            int sessionStarted = 0;
            tracker.ProgressReset += () => progressReset++;
            tracker.SessionStarted += _ => sessionStarted++;

            tracker.StartSession("student-2");

            Assert.AreEqual(1, sessionStarted);
            Assert.AreEqual(0, progressReset);
        }

        // -- Reporting -------------------------------------------------------

        [Test]
        public void GenerateReport_PopulatesEveryObjectiveField()
        {
            var tracker = NewTracker();
            tracker.SetStudentId("student-1");
            Record(tracker, ObjectiveId, 9, 1);

            var entry = tracker.GenerateReport().objectives[0];

            Assert.AreEqual(ObjectiveId, entry.objectiveId);
            Assert.AreEqual("English", entry.subject);
            Assert.AreEqual(MasteryLevel.Mastered, entry.masteryLevel);
            Assert.AreEqual(10, entry.totalAttempts);
            Assert.AreEqual(9, entry.correctAttempts);
            Assert.That(entry.accuracy, Is.EqualTo(0.9f).Within(0.0001f));
            Assert.That(entry.masteryThreshold, Is.EqualTo(0.85f).Within(0.0001f));
            Assert.That(entry.averageResponseTimeSeconds, Is.EqualTo(1f).Within(0.0001f));
        }

        /// <summary>
        /// JsonUtility writes enums as integers, so the report carries a string form
        /// for xAPI and other LMS consumers.
        /// </summary>
        [Test]
        public void GenerateReport_ExposesMasteryLevelAsAReadableString()
        {
            var tracker = NewTracker();
            Record(tracker, ObjectiveId, 9, 1);

            var entry = tracker.GenerateReport().objectives[0];

            Assert.AreEqual("Mastered", entry.masteryLevelName);
            Assert.AreEqual(entry.masteryLevel.ToString(), entry.masteryLevelName);
        }

        [Test]
        public void GenerateReport_WithNoAttempts_ReportsNotStartedAndZeroes()
        {
            var tracker = NewTracker();

            var entry = tracker.GenerateReport().objectives[0];

            Assert.AreEqual(MasteryLevel.NotStarted, entry.masteryLevel);
            Assert.AreEqual("NotStarted", entry.masteryLevelName);
            Assert.AreEqual(0, entry.totalAttempts);
            Assert.That(entry.accuracy, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(entry.averageResponseTimeSeconds, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void GenerateReportJson_ContainsTheReportFields()
        {
            var tracker = NewTracker();
            tracker.SetStudentId("student-1");
            Record(tracker, ObjectiveId, 9, 1);

            string json = tracker.GenerateReportJson();

            Assert.That(json, Does.Contain("student-1"));
            Assert.That(json, Does.Contain(ObjectiveId));
            Assert.That(json, Does.Contain("Mastered"));
            Assert.That(json, Does.Contain(tracker.SessionId));
        }

        // -- Objective registration -----------------------------------------

        [Test]
        public void RegisterObjective_WithExistingId_ReplacesInPlace()
        {
            var tracker = NewBareTracker();
            tracker.RegisterObjective(new LearningObjective { objectiveId = "X", subject = "First" });
            tracker.RegisterObjective(new LearningObjective { objectiveId = "Y", subject = "Second" });
            tracker.RegisterObjective(new LearningObjective { objectiveId = "X", subject = "Updated" });

            Assert.AreEqual(2, tracker.Objectives.Count);
            Assert.AreEqual("X", tracker.Objectives[0].objectiveId, "order should be preserved");
            Assert.AreEqual("Updated", tracker.Objectives[0].subject);
        }

        [Test]
        public void RegisterObjective_WithNull_Throws()
        {
            var tracker = NewBareTracker();

            Assert.Throws<ArgumentNullException>(() => tracker.RegisterObjective(null));
        }

        [Test]
        public void RegisterObjective_WithEmptyId_Throws()
        {
            var tracker = NewBareTracker();

            Assert.Throws<ArgumentException>(
                () => tracker.RegisterObjective(new LearningObjective { objectiveId = "" }));
            Assert.Throws<ArgumentException>(
                () => tracker.RegisterObjective(new LearningObjective { objectiveId = "   " }));
        }

        [TestCase(-0.1f)]
        [TestCase(0f)]
        [TestCase(1.1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void RegisterObjective_WithInvalidThreshold_Throws(float threshold)
        {
            var tracker = NewBareTracker();

            Assert.Throws<ArgumentOutOfRangeException>(() => tracker.RegisterObjective(
                new LearningObjective { objectiveId = "X", masteryThreshold = threshold }));
        }

        [Test]
        public void RegisterObjective_StoresADefensiveCopy()
        {
            var tracker = NewBareTracker();
            var objective = new LearningObjective { objectiveId = "X", subject = "English" };

            tracker.RegisterObjective(objective);
            objective.objectiveId = "MUTATED";
            objective.subject = "Changed";

            Assert.AreEqual("X", tracker.Objectives[0].objectiveId);
            Assert.AreEqual("English", tracker.Objectives[0].subject);
        }

        /// <summary>
        /// Objectives authored in the Inspector bypass RegisterObjective, so duplicates
        /// there used to produce duplicate rows in the exported report.
        /// </summary>
        [Test]
        public void Awake_DropsDuplicateAndBlankAuthoredObjectives()
        {
            var go = new GameObject("Tracker");
            go.SetActive(false); // hold Awake until the authored list is in place
            _created.Add(go);

            var tracker = go.AddComponent<LearningOutcomeTracker>();
            typeof(LearningOutcomeTracker)
                .GetField("_objectives", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(tracker, new List<LearningObjective>
                {
                    new LearningObjective { objectiveId = "DUP", masteryThreshold = 0.85f },
                    new LearningObjective { objectiveId = "DUP", masteryThreshold = 0.85f },
                    new LearningObjective { objectiveId = "", masteryThreshold = 0.85f },
                    new LearningObjective { objectiveId = "OK", masteryThreshold = 0.85f }
                });

            go.SetActive(true);

            Assert.AreEqual(2, tracker.Objectives.Count);
            Assert.AreEqual(2, tracker.GenerateReport().objectives.Count);
        }

        /// <summary>
        /// Regression test: authored objectives were only indexed in Awake, so a tracker on a
        /// GameObject that starts inactive never indexed them at all and reported a correctly
        /// authored objective as unregistered.
        /// </summary>
        [Test]
        public void AuthoredObjectives_AreUsableBeforeAwakeHasRun()
        {
            var go = new GameObject("Tracker");
            go.SetActive(false); // Awake never runs while the GameObject stays inactive.
            _created.Add(go);

            var tracker = go.AddComponent<LearningOutcomeTracker>();
            typeof(LearningOutcomeTracker)
                .GetField("_objectives", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(tracker, new List<LearningObjective>
                {
                    new LearningObjective { objectiveId = ObjectiveId, masteryThreshold = 0.85f }
                });

            Assert.DoesNotThrow(() => Record(tracker, ObjectiveId, 9, 1));

            Assert.AreEqual(10, tracker.GetAttemptCount(ObjectiveId));
            Assert.That(tracker.GetAccuracy(ObjectiveId), Is.EqualTo(0.9f).Within(0.0001f));
            Assert.AreEqual(MasteryLevel.Mastered, tracker.GetMasteryLevel(ObjectiveId));
        }

        /// <summary>
        /// The setup deferred out of Awake must not run twice: a later Awake re-indexing the
        /// objectives would discard attempts already recorded against them.
        /// </summary>
        [Test]
        public void Awake_AfterEarlyUse_PreservesRecordedAttempts()
        {
            var go = new GameObject("Tracker");
            go.SetActive(false);
            _created.Add(go);

            var tracker = go.AddComponent<LearningOutcomeTracker>();
            typeof(LearningOutcomeTracker)
                .GetField("_objectives", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(tracker, new List<LearningObjective>
                {
                    new LearningObjective { objectiveId = ObjectiveId, masteryThreshold = 0.85f }
                });

            Record(tracker, ObjectiveId, 3, 0);
            string sessionId = tracker.SessionId;

            go.SetActive(true); // Awake runs now.

            Assert.AreEqual(3, tracker.GetAttemptCount(ObjectiveId));
            Assert.AreEqual(sessionId, tracker.SessionId, "Awake must not reissue the session ID.");
        }

        // -- Attempts --------------------------------------------------------

        [Test]
        public void RecordAttempt_StampsSequenceAndTimestamp()
        {
            var tracker = NewTracker();
            Record(tracker, ObjectiveId, 3, 0);

            var attempts = tracker.GetAttempts(ObjectiveId);

            Assert.AreEqual(3, attempts.Count);
            Assert.Less(attempts[0].sequence, attempts[1].sequence);
            Assert.Less(attempts[1].sequence, attempts[2].sequence);
            Assert.That(attempts[0].timestamp, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void RecordAttempt_WithEmptyObjectiveId_Throws()
        {
            var tracker = NewTracker();

            Assert.Throws<ArgumentException>(() => tracker.RecordAttempt("", true, 1f));
            Assert.Throws<ArgumentException>(() => tracker.RecordAttempt(null, true, 1f));
            Assert.Throws<ArgumentException>(() => tracker.RecordAttempt("   ", true, 1f));
        }

        [Test]
        public void RecordAttempt_ForUnregisteredObjective_Throws()
        {
            var tracker = NewTracker();

            Assert.Throws<KeyNotFoundException>(
                () => tracker.RecordAttempt("does.not.exist", true, 1f));
        }

        [TestCase(-0.1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void RecordAttempt_WithInvalidResponseTime_Throws(float responseTime)
        {
            var tracker = NewTracker();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => tracker.RecordAttempt(ObjectiveId, true, responseTime));
        }

        [Test]
        public void GetAttempts_ForUnknownObjective_IsEmptyAndDoesNotThrow()
        {
            var tracker = NewTracker();

            Assert.AreEqual(0, tracker.GetAttempts("does.not.exist").Count);
            Assert.AreEqual(0, tracker.GetAttempts(null).Count);
        }

        [Test]
        public void GetAttempts_ReturnsADefensiveSnapshot()
        {
            var tracker = NewTracker();
            Record(tracker, ObjectiveId, 1, 0);
            var snapshot = tracker.GetAttempts(ObjectiveId);

            snapshot[0].correct = false;
            snapshot[0].objectiveId = "MUTATED";
            Record(tracker, ObjectiveId, 1, 0);

            Assert.AreEqual(1, snapshot.Count, "a snapshot should not change after later writes");
            Assert.AreEqual(2, tracker.GetAttempts(ObjectiveId).Count);
            Assert.AreEqual(ObjectiveId, tracker.GetAttempts(ObjectiveId)[0].objectiveId);
            Assert.IsTrue(tracker.GetAttempts(ObjectiveId)[0].correct);
        }

        [Test]
        public void Objectives_ReturnsADefensiveSnapshot()
        {
            var tracker = NewTracker();
            var snapshot = tracker.Objectives;

            snapshot[0].objectiveId = "MUTATED";
            snapshot[0].masteryThreshold = 0f;

            Assert.AreEqual(ObjectiveId, tracker.Objectives[0].objectiveId);
            Assert.That(tracker.Objectives[0].masteryThreshold, Is.EqualTo(0.85f).Within(0.0001f));
        }

        [Test]
        public void AttemptHistory_IsBoundedButLifetimeStatisticsArePreserved()
        {
            var tracker = NewTracker();
            int total = tracker.AttemptHistoryLimit + 5;

            for (int i = 0; i < total; i++)
                tracker.RecordAttempt(ObjectiveId, i % 2 == 0, 2f);

            var retained = tracker.GetAttempts(ObjectiveId);
            var report = tracker.GenerateReport().objectives[0];

            Assert.AreEqual(tracker.AttemptHistoryLimit, retained.Count);
            Assert.AreEqual(6, retained[0].sequence);
            Assert.AreEqual(total, tracker.GetAttemptCount(ObjectiveId));
            Assert.AreEqual(total, report.totalAttempts);
            Assert.AreEqual((total + 1) / 2, report.correctAttempts);
            Assert.That(report.averageResponseTimeSeconds, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(report.accuracy,
                Is.EqualTo((float)report.correctAttempts / total).Within(0.0001f));
        }
    }
}
