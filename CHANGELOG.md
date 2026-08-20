# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.1] - 2026-08-20

### Changed

- **Minimum supported editor raised to Unity 6000.0.73f1** (from 2021.3). The package
  was never verified on 2021.3; the suite is run against 6000.0.73f1 and 6000.3.22f1.
  Nothing in the package obviously requires Unity 6, so earlier editors may well work,
  but an untested version is not a supported one. Consumers on an editor older than
  6000.0.73f1 will now see a compatibility warning from Package Manager.

### Added

- **Classroom Simulation** sample, importable from the Package Manager. A simulated
  student answers across three curriculum objectives while an on-screen readout shows
  accuracy, mastery level, and the difficulty the controller settles on. Requires no
  scene setup or assets beyond a single script.
- Continuous integration workflow covering package metadata, assembly definition
  validity, declared sample paths, and `.meta` file consistency. It needs no Unity
  licence, so it runs on forks and on external pull requests.
- Documented command for running the Play Mode suite headlessly against a consuming
  project, so the published test results can be reproduced without the Editor.
- `Tools~/verify.ps1`, which assembles that consuming project, copies the samples into
  it so they are compiled, runs the suite, and reports pass/fail. Compiling the samples
  is the only check that catches one drifting out of sync with the runtime API, since
  `Samples~` is invisible to Unity in normal use.
- `AdaptiveDifficultyController` now warns once if both `Evaluate` and
  `EvaluateForObjective` adjust difficulty on the same instance. The two modes track
  consumed attempts separately, so mixing them moves difficulty at twice the configured
  step; that was previously documented but undetectable at runtime. Behaviour is
  otherwise unchanged — the adjustment still applies, so no existing build lands on a
  different difficulty.

## [1.0.0] - 2026-08-20

First public release. Earlier versions were internal and are not documented here.

### Added

- `LearningOutcomeTracker`: records student attempts against curriculum-coded
  learning objectives, calculates mastery levels (NotStarted, Emerging,
  Developing, Secure, Mastered) against a configurable per-objective threshold,
  and generates a `MasteryReport` serialisable via `GenerateReportJson`.
- `StartSession` for handing a tracker over between students on shared
  classroom hardware, issuing a fresh session ID and clearing prior progress.
- Bounded raw attempt history (1,000 records per objective by default,
  configurable in the Inspector) with lifetime aggregates retained across the
  full session after older records roll out.
- `AdaptiveDifficultyController`: adjusts a continuous difficulty value and a
  discrete `DifficultyTier` from sliding-window accuracy, raising
  `OnDifficultyChanged` and `OnTierChanged` UnityEvents.
- Learning Outcome Viewer Editor window (**Ocean View Games > Learning Outcome
  Viewer**) for inspecting objectives, monitoring mastery during play mode, and
  exporting reports as JSON.
- Assembly definitions separating runtime, editor, and test code. The test
  assembly is constrained to `UNITY_INCLUDE_TESTS` and is excluded from
  consumer projects.
- 89 Play Mode test cases covering mastery banding, session handling, bounded
  history, report generation, difficulty adjustment, component lifecycle, event
  wiring, defensive snapshots, and input guards.
- Architecture documentation covering data flow, LMS integration, school
  deployment constraints, and data protection considerations.
- `CONTRIBUTING.md` and `SECURITY.md`, including a private vulnerability
  reporting route and guidance on handling learner data.

[Unreleased]: https://github.com/Ocean-View-Games/educational-gamification-systems-unity/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/Ocean-View-Games/educational-gamification-systems-unity/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/Ocean-View-Games/educational-gamification-systems-unity/releases/tag/v1.0.0
