# Contributing to Code Archeologist

The project uses Apache-2.0 licensing and the Developer Certificate of Origin (DCO). Sign off your contribution with git commit -s; see https://developercertificate.org.

Discuss major architectural changes before implementing them. Changes require focused branches and pull requests, with the owner approving merges. Do not create GitHub Actions workflows or introduce paid CI without approval.

Development prerequisites: .NET 10 SDK, Node.js 22.12+ and npm. Run scripts/check.ps1 on Windows or scripts/check.sh on macOS/Linux. The frontend dependency lockfile is committed, and verification uses `npm ci` for repeatable installs. Validate lockfile changes and report local execution results rather than assuming success. The check scripts include local API negative-security tests; stop the running development server before running checks because the tests bind the same fixed loopback port. Report exact tests actually run, expected security effects, and open risks. Do not report unknown tests as passing.

Do not submit tests that execute untrusted imported repositories or examples that leak credentials. Private security reports follow SECURITY.md.
