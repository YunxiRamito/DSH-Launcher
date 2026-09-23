# SignPath code signing

Launcher tag builds are signed with
[SignPath Foundation](https://signpath.org/), the free code signing service for
open source projects.

The certificate and publisher identity belong to SignPath Foundation. The
private key is managed by SignPath and is not available to this repository.

## One-time SignPath setup

1. Apply at <https://signpath.org/apply>.
2. Create a SignPath project for this repository.
3. Link the project to the predefined **GitHub.com** trusted build system.
4. Create an artifact configuration named `launcher-release` using
   [`.signpath/launcher-artifact-configuration.xml`](.signpath/launcher-artifact-configuration.xml).
5. Create a release signing policy, for example `release-signing`.
6. Create an API token for a user that can submit requests to this project.

Add these repository variables under **Settings > Secrets and variables >
Actions > Variables**:

| Variable | Value |
| --- | --- |
| `SIGNPATH_ORGANIZATION_ID` | SignPath organization ID |
| `SIGNPATH_PROJECT_SLUG` | Project slug created in SignPath |
| `SIGNPATH_SIGNING_POLICY_SLUG` | `release-signing` or the chosen slug |
| `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | `launcher-release` |

Add this repository secret under **Settings > Secrets and variables > Actions >
Secrets**:

| Secret | Value |
| --- | --- |
| `SIGNPATH_API_TOKEN` | API token created in SignPath |

## Release behavior

Tag builds now fail before publishing when the SignPath configuration is
missing. The workflow:

1. Builds the launcher ZIP.
2. Uploads it as a GitHub Actions artifact.
3. Uses SignPath to Authenticode-sign the launcher bootstrap, main executable,
   and main managed assembly inside the ZIP.
4. Verifies every expected signature has `Status = Valid`.
5. Publishes the signed ZIP to npm and GitHub Releases.

Manual `workflow_dispatch` runs still produce an unsigned build artifact for CI
diagnostics, but tag releases require signing.
