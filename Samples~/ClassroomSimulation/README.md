# Classroom Simulation

A runnable demonstration of `LearningOutcomeTracker` and `AdaptiveDifficultyController`
working together, with no scene setup and no assets beyond a single script.

## Running it

1. Import this sample from **Window > Package Manager > Educational Gamification Systems > Samples**.
2. Create an empty GameObject in any scene.
3. Add the **Classroom Simulation Sample** component (**Add Component > Ocean View Games > EdTech**).
   The tracker and difficulty controller are added automatically alongside it.
4. Press Play.

An on-screen readout shows attempts accumulating, accuracy and mastery level per
objective, and the current difficulty and tier. When the lesson finishes, the full JSON
mastery report is written to the Console — the same payload you would post to an LMS.

## What you are watching

The simulated student has a fixed latent ability (`Student Ability` on the component).
Harder questions are answered correctly less often, so as the controller raises difficulty
the student's observed accuracy falls, until the two settle in the band between the
controller's decrease and increase thresholds. That convergence — keeping the learner in
their zone of proximal development rather than bored or overwhelmed — is what the
controller exists to produce.

The consequence is worth stating plainly, because it surprises people the first time:
accuracy converges to roughly the same place whatever the student's ability. Ability shows
up in the *difficulty* the student sustains, not in how often they answer correctly. Over
150 attempts:

| Student Ability | Settles at difficulty | Tier |
| --- | --- | --- |
| 0.95 | ~0.70 | Hard |
| 0.25 | ~0.00 | Easy |

Both students end up answering around six questions in ten, and both sit in the Developing
mastery band. Mastery levels here reflect performance *under adaptive pressure*; if you
want a mastery report that measures a student against a fixed standard, assess them at a
pinned difficulty rather than an adapting one.

Things worth trying:

- Set **Student Ability** to `0.95` and watch difficulty climb into the Hard tier and hold.
- Set it to `0.25` and watch difficulty fall away to keep the student in the game.
- Change **Random Seed** to get a different run; keep it fixed to reproduce one exactly.
- Open **Ocean View Games > Learning Outcome Viewer** while playing to see the same data
  through the package's own editor tooling.

## Notes for real use

The sample passes a pseudonymous token (`student-4021`) to `StartSession`, never a name.
That is not a stylistic choice — see the data-protection notes in the root
[README](../../README.md) before deploying anything that records real children's attempts.

The sample calls `Evaluate()` once per attempt. `Evaluate()` and `EvaluateForObjective()`
are mutually exclusive modes on a single controller; calling both applies the same
attempts twice. Pick one per controller instance.
