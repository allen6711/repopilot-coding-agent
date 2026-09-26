#!/usr/bin/env python3
"""Fails when a governance control was left unverified by a skipped test.

A skip is not automatically a problem; an unexplained one is. The constitution
names eleven areas that MUST have automated tests, and the tests covering them
are gated on a Docker daemon or on the pre-baked sandbox image — both of which
CI has, so those gates must never fire here. What CI does not have is a
model-provider credential, and the two tests that need one measure latency
rather than a control, so those skips are reported and allowed.

Reads the reason rather than counting outcomes: "a test was skipped" cannot
distinguish the two cases, and the earlier version of this check failed the
build for the allowed one.
"""
import re
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# The only reason a skip is tolerated. Matched on the phrase the attribute
# writes, so a new gate with a new reason fails closed rather than being
# silently added to the allow-list.
ALLOWED = re.compile(r"model-provider credential|live stack", re.I)


def skips(path):
    root = ET.parse(path).getroot()

    for result in root.iterfind(".//t:UnitTestResult", NS):
        if result.get("outcome") != "NotExecuted":
            continue

        message = result.findtext(".//t:Message", default="", namespaces=NS).strip()
        yield result.get("testName", "<unnamed>"), message


def executed(path):
    root = ET.parse(path).getroot()

    return sum(
        1
        for result in root.iterfind(".//t:UnitTestResult", NS)
        if result.get("outcome") != "NotExecuted"
    )


def main(paths):
    failed = False

    for path in paths:
        try:
            ran = executed(path)
        except (OSError, ET.ParseError) as error:
            print(f"::error::Could not read {path}: {error}")
            failed = True
            continue

        # An empty run passes every assertion it never made. The e2e suite in
        # particular skips wholesale when the sandbox image is missing, which
        # would otherwise read as a green build with the constitution's mandated
        # end-to-end coverage never executed.
        if ran == 0:
            print(f"::error::{path} executed no tests.")
            failed = True

        for name, reason in skips(path):
            if ALLOWED.search(reason):
                print(f"::notice::skipped (allowed): {name} — {reason}")
                continue

            print(f"::error::{name} was skipped in CI: {reason or 'no reason given'}")
            failed = True

        print(f"{path}: {ran} executed")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
