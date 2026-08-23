#!/usr/bin/env python3
"""Assert workflow hygiene structurally: no privileged target-context trigger
(pull_request_target or workflow_run), no blanket write token, third-party
actions pinned to an immutable ref — including refs hidden inside local
composite actions.

This replaces three line-anchored `grep` assertions. They were bypassable by
ORDINARY block-style YAML, not by any evasion technique: a value may sit on the
line after its key, so

    permissions:
      write-all

resolves to exactly what `permissions: write-all` resolves to, while matching
neither the key-anchored `permissions:.*write-all` pattern nor anything else the
guard looked for. The same held for a `uses:` split across two lines, which never
reached the pin check at all. Demonstrated 2026-08-22: both greps returned no
match on a file PyYAML resolves to `permissions: 'write-all'` and
`uses: 'third/party@v1'`.

Parsing also removes the self-match hazard the greps carried. They needed a
`^[^#]*` prefix so the guard's own comments describing the rules did not trip it
(that bit on the fixportal-venue pilot). A parser reads values, so prose about a
rule cannot be mistaken for the rule.

Exit codes: 0 clean, 1 a hard violation, 2 the checker could not run.
"""

import sys
from pathlib import Path

try:
    import yaml
except ImportError:
    # Same policy as assert_gate_coverage.py: fail legibly rather than installing
    # PyYAML at CI time. This gates merges, so an arbitrary-at-install-time
    # dependency must not enter the gating path.
    #
    # sys.exit(2), not sys.exit(<str>): the string form prints to stderr and exits 1,
    # which is the code this module documents for a real violation. Both fail the step,
    # but a consumer branching on 2 ("infra problem, retry") versus 1 ("violation, do
    # not retry") would misclassify a missing interpreter dependency as a bad workflow.
    print(
        "PyYAML is not available to this runner. Install it in the image rather "
        "than at gate time, or restore '.github/workflows/**' to the review "
        "policy's high tier so a reviewer sees these diffs.",
        file=sys.stderr,
    )
    sys.exit(2)

WORKFLOWS = Path(".github/workflows")
SHA_LEN = 40
DIGEST_LEN = 64


def load(path):
    with path.open(encoding="utf-8") as handle:
        return yaml.safe_load(handle)


def triggers(document):
    """The event names in `on:`, whatever spelling was used.

    YAML 1.1 reads a bare `on` as the boolean True, which is why the key is
    looked up both ways -- `document["on"]` alone silently finds nothing in every
    workflow that writes the key unquoted, i.e. all of them.
    """
    section = document.get("on", document.get(True))
    if isinstance(section, str):
        return [section]
    if isinstance(section, list):
        return [event for event in section if isinstance(event, str)]
    if isinstance(section, dict):
        return [event for event in section if isinstance(event, str)]
    return []


def permission_blocks(document):
    """Every `permissions:` value in the file: workflow level, then each job.

    KNOWN GAP, stated so this is not mistaken for full coverage: only the literal
    `write-all` scalar is flagged (see main()). A mapping that grants every scope
    `write` individually carries the same privilege and passes. Not closed here because
    the test cannot be written soundly -- "every scope is write" needs the complete set
    of scopes GitHub defines, which changes as GitHub adds them, and an omitted scope is
    a *narrower* grant, not a broader one. A heuristic on scope count would fail
    workflows that legitimately need three or four write scopes.

    This is the same scope as the grep it replaces, so it is not a regression; the
    bypass this file closes is the block-style spelling of `write-all`, not the
    enumerated equivalent.
    """
    blocks = []
    if "permissions" in document:
        blocks.append(("workflow", document["permissions"]))
    jobs = document.get("jobs")
    if isinstance(jobs, dict):
        for name, job in jobs.items():
            if isinstance(job, dict) and "permissions" in job:
                blocks.append((f"job '{name}'", job["permissions"]))
    return blocks


def action_refs(document):
    """Every `uses:` value in the file, with the job it came from."""
    refs = []
    jobs = document.get("jobs")
    if not isinstance(jobs, dict):
        return refs
    for name, job in jobs.items():
        if not isinstance(job, dict):
            continue
        # A reusable-workflow call carries `uses:` on the job itself.
        if isinstance(job.get("uses"), str):
            refs.append((name, job["uses"]))
        steps = job.get("steps")
        if isinstance(steps, list):
            for step in steps:
                if isinstance(step, dict) and isinstance(step.get("uses"), str):
                    refs.append((name, step["uses"]))
    return refs


def is_pinned(ref):
    """True when the ref names an immutable revision.

    Both forms the estate actually uses are accepted. The previous `sed`-based
    scanner kept surrounding quotes in the extracted value, so a correctly pinned
    `uses: "actions/checkout@<40-hex>"` failed the hex test and was reported as an
    unpinned third-party action; and a digest-pinned `docker://` ref could never
    pass it at all. Parsed values carry no quotes, and the digest form is now
    recognised explicitly.

    `./`-prefixed (local) refs are no longer waved through here: main() opens the
    referenced action file and checks its inner `uses:` instead (see
    local_composite_inner_refs). This branch remains only as the answer for any
    future caller that has already done that expansion.
    """
    if ref.startswith("./"):
        return True  # A local action is this repository's own reviewed code.
    if "@" not in ref:
        return False
    revision = ref.rsplit("@", 1)[1]
    if ref.startswith("docker://"):
        algorithm, _, digest = revision.partition(":")
        return algorithm == "sha256" and len(digest) == DIGEST_LEN and all(
            char in "0123456789abcdef" for char in digest
        )
    return len(revision) == SHA_LEN and all(char in "0123456789abcdef" for char in revision)


def local_composite_inner_refs(ref):
    """The `uses:` refs inside a LOCAL composite action's steps.

    Adversarial review 2026-08-23 (Low): is_pinned() previously returned True for
    every ./-prefixed ref without opening the file, so a local wrapper invoking
    `third/party@v1` executed while the guard reported everything pinned. The
    referenced action.yml/action.yaml is now parsed and its composite steps are
    checked like any workflow step. Missing or unparseable action files fail
    closed -- a workflow referencing one is broken or hostile either way. Only
    one level is expanded; a composite action nesting another local action is
    out of scope (no such action exists in this repo).
    """
    base = Path(ref)
    action_file = None
    for candidate in (base / "action.yml", base / "action.yaml"):
        if candidate.is_file():
            action_file = candidate
            break
    if action_file is None:
        raise FileNotFoundError(f"no action.yml/action.yaml under {ref}")

    document = load(action_file)
    runs = document.get("runs") if isinstance(document, dict) else None
    if not isinstance(runs, dict) or runs.get("using") != "composite":
        return []  # JavaScript and docker actions carry no further `uses:`.
    refs = []
    steps = runs.get("steps")
    if isinstance(steps, list):
        for step in steps:
            if isinstance(step, dict) and isinstance(step.get("uses"), str):
                refs.append(step["uses"])
    return refs


def main():
    if not WORKFLOWS.is_dir():
        print(f"::error::{WORKFLOWS} does not exist; the guard cannot assert anything.")
        return 2

    failed = False
    unpinned_first_party = 0

    for path in sorted(list(WORKFLOWS.glob("*.yml")) + list(WORKFLOWS.glob("*.yaml"))):
        try:
            document = load(path)
        except yaml.YAMLError as error:
            print(f"::error file={path}::Not parseable as YAML: {error}")
            failed = True
            continue
        if not isinstance(document, dict):
            print(f"::error file={path}::Workflow does not parse to a mapping.")
            failed = True
            continue

        for event in triggers(document):
            # These triggers run with a write token and repository secrets in the
            # base repo's context: pull_request_target while able to check out
            # attacker-controlled head code, workflow_run while able to consume
            # artifacts from an untrusted upstream run (the second standard
            # pwn-request vector; added after adversarial review 2026-08-23 found
            # the guard refused the first but not the second). Both have
            # legitimate uses; none should land without being argued for, so they
            # are refused here rather than reviewed by glob.
            if event in ("pull_request_target", "workflow_run"):
                print(
                    f"::error file={path}::This workflow uses the '{event}' trigger, which grants "
                    "a write token and repository secrets in the base repo's context to a run "
                    "reachable from untrusted code or artifacts. Use the plain pull_request "
                    "trigger, or remove this assertion deliberately with a written rationale."
                )
                failed = True

        for scope, value in permission_blocks(document):
            if isinstance(value, str) and value.strip() == "write-all":
                print(
                    f"::error file={path}::A write-all token at {scope} scope discards least "
                    "privilege. Declare the specific permissions each job needs."
                )
                failed = True

        for job, ref in action_refs(document):
            if ref.startswith("./"):
                # A local action is this repository's own reviewed code, but its
                # composite steps may invoke third-party actions; expand one level.
                try:
                    inner_refs = local_composite_inner_refs(ref)
                except (FileNotFoundError, yaml.YAMLError) as error:
                    print(
                        f"::error file={path}::Local action '{ref}' (job '{job}') could not be "
                        f"inspected: {error}. The guard fails closed on an uninspectable action."
                    )
                    failed = True
                    continue
                for inner_ref in inner_refs:
                    if inner_ref.startswith("actions/") or is_pinned(inner_ref):
                        continue
                    print(
                        f"::error file={path}::Local composite action '{ref}' (job '{job}') invokes "
                        f"third-party action '{inner_ref}' without an immutable pin. A mutable tag "
                        "can change after review, and the wrapper hid it from the top-level check."
                    )
                    failed = True
                continue
            if is_pinned(ref):
                continue
            # actions/* is GitHub's own namespace, and a mutable tag there means
            # trusting GitHub -- which every workflow already does by running on
            # their runners. A third-party mutable tag means trusting that owner
            # forever, with no re-review when they move it. Only the second is
            # gated. Flip this to a failure once a pinning sweep lands.
            if ref.startswith("actions/"):
                print(
                    f"::notice file={path}::'{ref}' (job '{job}') is not SHA-pinned. First-party "
                    "(GitHub) action, so not failed -- pin it when convenient."
                )
                unpinned_first_party += 1
            else:
                print(
                    f"::error file={path}::Third-party action '{ref}' (job '{job}') is not pinned to "
                    "an immutable revision. A mutable tag can change after review."
                )
                failed = True

    if failed:
        return 1

    print(
        "Workflow hygiene: no target-context trigger, no write-all token, every third-party "
        f"action pinned ({unpinned_first_party} first-party ref(s) unpinned, not gated)."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
