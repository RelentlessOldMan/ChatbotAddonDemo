# ADR-006: The app does the work; the model orchestrates and explains

**Status:** Accepted

## Context
The AI features create test plans (XML), start bench runs, and analyze results. A language model *could* write the XML itself and compute statistics from raw CSV rows. Should it?

## Decision
No. The model only chooses which tools to call and with what arguments, then explains the results in words. Our code does everything that has to be exactly right:

| Task | Done by | How |
|---|---|---|
| Writing the plan file | App | `create_test_plan` / `create_test_plan_from_spec` take structured arguments. The app builds the XML, validates it against `TestPlan.xsd` and the bench rules, and saves it. |
| Copying limits from a datasheet | App | `create_test_plan_from_spec` reads the spec file and copies the limits; the model never retypes numbers. Parameters the bench can't measure are reported back, not silently dropped. |
| Pass/fail, statistics, drift, worst case | App | `get_run_summary` returns a verdict and findings as sentences, plus a statistics table. |
| "Which units failed at 125 °C?" | Model picks the filter, app answers | `query_measurements(temperatureC: 125, failedOnly: true)` |
| Deciding what to do next, explaining it to the user | Model | |

## Why
- **Correct numbers.** Models are unreliable at arithmetic over hundreds of rows and at copying many decimal numbers exactly. Reports go to customers and drive engineering decisions.
- **Validation in one place.** A plan made by the assistant, by an agent, by the console tool or by hand passes the same checks.
- **Cost.** Raw measurement data never goes through the model. A summary is a few hundred tokens; the raw CSV of a big run would be tens of thousands.
- **Testability.** Everything that matters is ordinary code with unit tests. The AI layer can be tested with a scripted fake model.

## Consequences
- Every new analysis the users want has to be built as a capability. That's deliberate: it puts it in front of people, agents and tests alike.
- Agents with file access can still open `results.csv` themselves for custom analysis. The files are ordinary CSV and PNG.
