#!/usr/bin/env python3
"""Asserts the `publish` job's event/ref condition and prerequisites are actually
gating, by parsing and generically evaluating the job's own `if:` string rather than
reimplementing its logic.

Publishing packages must happen only on a push to `fpsim`, after `build` and
`acceptance` have succeeded (QuickFIXn's own package feed, ci.yml `publish:` job).
`assert_gate_coverage.py` explicitly EXEMPTS the publish job from its universal
merge-blocking check (`GATE_EXEMPT: publish` in fixportal-ci.yml), so nothing else in
CI verifies this job's own condition. A test that re-derives the "must be a push to
fpsim" rule separately from the workflow file can stay green even after the real
condition is weakened or removed -- this script instead extracts and evaluates the
LITERAL `if:` string committed in the workflow, so a weakened condition changes what
this script evaluates, not just what a hand-written assertion expects.

Generic boolean evaluator: only understands `A && B && ...` conjunctions of
`context.path == 'literal'` atoms (the shape this repo's conditions use). It does not
know the atoms' meaning -- github.event_name, github.ref, or anything else -- so it
cannot be fooled by editing which context field or literal the condition checks,
only by removing the check.

Pure Python, invoked directly (see assert_gate_coverage.py's docstring for why: CRLF
checkouts break bash's `pipefail`, not Python).
"""
import re
import sys
from pathlib import Path

ATOM = re.compile(r"^\s*([\w.]+)\s*==\s*'([^']*)'\s*$")


def extract_job_block(workflow_text: str, job_id: str) -> str:
    """Returns the raw text of one top-level job block (by 2-space-indented job id)."""
    pattern = re.compile(
        rf"^  {re.escape(job_id)}:\n((?:^(?:    .*)?\n?)*)", re.MULTILINE
    )
    match = pattern.search(workflow_text)
    if match is None:
        raise AssertionError(f"job '{job_id}' not found in workflow")
    return match.group(0)


def extract_if_condition(job_block: str) -> str:
    match = re.search(r"^\s*if:\s*(.+)$", job_block, re.MULTILINE)
    if match is None:
        raise AssertionError("job has no 'if:' condition -- publish is unconditional")
    return match.group(1).strip()


def extract_needs(job_block: str) -> list[str]:
    match = re.search(r"^\s*needs:\s*\[([^\]]*)\]", job_block, re.MULTILINE)
    if match is None:
        raise AssertionError("job has no 'needs:' list -- publish has no prerequisites")
    return [n.strip() for n in match.group(1).split(",") if n.strip()]


def evaluate(condition: str, context: dict) -> bool:
    """Evaluates a `&&`-joined conjunction of `context.path == 'literal'` atoms
    against the given context values. Raises if the condition contains anything
    this generic evaluator does not understand, rather than silently mis-evaluating
    an unrecognised (and possibly weakened/expanded) condition as passing.
    """
    for clause in condition.split("&&"):
        m = ATOM.match(clause)
        if m is None:
            raise AssertionError(
                f"condition clause not understood by the generic evaluator: {clause!r}. "
                "Update the evaluator (not a hardcoded expectation) if the condition's "
                "shape has legitimately changed."
            )
        path, literal = m.group(1), m.group(2)
        if context.get(path) != literal:
            return False
    return True


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: assert_publish_gate.py <path-to-workflow.yml>", file=sys.stderr)
        return 2

    workflow_text = Path(sys.argv[1]).read_text(encoding="utf-8")
    job_block = extract_job_block(workflow_text, "publish")
    condition = extract_if_condition(job_block)
    needs = extract_needs(job_block)

    for prerequisite in ("build", "acceptance"):
        if prerequisite not in needs:
            raise AssertionError(
                f"publish job no longer depends on '{prerequisite}' (needs: {needs})"
            )

    scenarios = [
        ({"github.event_name": "pull_request", "github.ref": "refs/heads/fpsim"}, False, "pull request targeting fpsim"),
        ({"github.event_name": "push", "github.ref": "refs/heads/some-other-branch"}, False, "push to a non-fpsim branch"),
        ({"github.event_name": "push", "github.ref": "refs/tags/v1.2.3"}, False, "tag push"),
        ({"github.event_name": "workflow_dispatch", "github.ref": "refs/heads/fpsim"}, False, "manual dispatch on fpsim"),
        ({"github.event_name": "push", "github.ref": "refs/heads/fpsim"}, True, "push to fpsim"),
    ]

    failures = []
    for context, expected, description in scenarios:
        actual = evaluate(condition, context)
        if actual != expected:
            failures.append(
                f"  {description}: expected publish-enabled={expected}, got {actual} "
                f"(condition={condition!r}, context={context!r})"
            )

    if failures:
        print("Publish gate condition failed scenario checks:", file=sys.stderr)
        for failure in failures:
            print(failure, file=sys.stderr)
        return 1

    print(f"Publish gate OK: condition={condition!r}, needs={needs!r}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
