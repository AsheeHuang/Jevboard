# Microsoft Bopomofo local feasibility experiment

This directory is a disposable diagnostic prototype, not a Jevboard product implementation.
It uses two blank, local test fields (WinForms native EDIT and WPF TextBox), a read-only
external observer, artificial test strings, and local evidence files. It does not call Jev
or any remote service, install software, change IME settings, or commit/push Git changes.

The host and observer use the existing .NET Framework compiler. See their launch scripts
and the final `REPORT.md` for exact commands and observed outcomes.

## Evidence rules

* A host-side event is not evidence that an external process can read the same data.
* Visible text, active composition, current conversion target, original reading/tone,
  and candidate list are separate measurements.
* An IMM32 error from another process does not prove that Microsoft Bopomofo has no data.
* A candidate list at one caret position is not an entire sentence candidate lattice.
* Candidate selection by a physical key is distinct from a TSF/IMM32 selection API.
* A fixed test replacement proves the tested write-back route, not Chinese model quality.
* All snapshots must be scoped to the diagnostic fields. Never substitute a chat field.

## Planned physical-key cases

With Microsoft's standard Bopomofo keyboard layout active:

1. `s`, `u`: ㄋㄧ before a tone is entered.
2. `3`: ㄋㄧˇ, converted but not explicitly committed by Enter.
3. `Down`: open candidate UI; inspect/capture current-position candidates.
4. A visible candidate's numeric key: select that known candidate externally.
5. Repeat in a second framework and inspect a longer synthetic composition at more than
   one caret position.
6. Test selected-text read/preview/confirm/replacement in a fixed synthetic fixture,
   verifying prefix, suffix, and absence of Enter-based submission.

No positive result is assumed by this plan. `REPORT.md` records actual successes,
failures, and untested cases.
