# Security Policy

## Reporting a Vulnerability

Please report security vulnerabilities **privately** — do not open a public
issue, pull request, or discussion for a suspected vulnerability.

Preferred channel: GitHub's private vulnerability reporting. Open the
repository's **Security** tab and click **Report a vulnerability**. (If the tab
is not visible, the maintainer enables it under Settings → Code security and
analysis → Private vulnerability reporting.)

If you cannot use GitHub Security Advisories, email **chris@yjcsolutions.co.uk**
with details and, if possible, a minimal reproduction.

We aim to acknowledge a report within 5 working days and will agree a
disclosure timeline with you. This is a small open-source project maintained on
a best-effort basis — please allow reasonable time for a fix before any public
disclosure.

## Upstream vulnerabilities

This repository is a fork of [QuickFIX/n](https://github.com/connamara/quickfixn).
A vulnerability in unmodified upstream code affects every QuickFIX/n user, not
only this fork — report it to the upstream project as well, so a fix reaches
them. Report it here too if you believe a FixPortal change makes it reachable
in a way upstream is not.

## Supported Versions

Only the current head of `fpsim` is supported. `master` tracks upstream and
carries no FixPortal changes; it is not separately maintained here. There are
no long-term-support branches.
