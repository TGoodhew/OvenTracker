# Releasing

Releases are built by GitHub Actions ([release.yml](../.github/workflows/release.yml)). Each package is
built and tried on its own system, and they're published together, with a `SHA256SUMS` file, as one GitHub
Release:

| Package | For |
| --- | --- |
| `OvenTracker-<version>-win-x64.zip` | Windows, with the .NET 11 runtime installed |
| `OvenTracker-<version>-win-x64-self-contained.zip` | Windows, nothing else needed |
| `OvenTracker-<version>-linux-x64.tar.gz` | Linux on a PC, with the .NET 11 runtime installed |
| `OvenTracker-<version>-linux-x64-self-contained.tar.gz` | Linux on a PC, nothing else needed |
| `OvenTracker-<version>-linux-arm64.tar.gz` | Linux on 64-bit ARM (e.g. a Raspberry Pi 4 or 5), with the .NET 11 runtime |
| `OvenTracker-<version>-linux-arm64-self-contained.tar.gz` | Linux on 64-bit ARM, nothing else needed |
| `oventracker-<version>-py3-none-any.whl`, `oventracker-<version>.tar.gz` | The Python package (the analysis), for `pip install`. Also published to PyPI once that's set up (below). |

The Windows programs (`OvenTracker.exe`, `OvenTracker.dll`) are signed with Azure Artifact Signing. A
release is never made with unsigned Windows programs: if signing isn't set up, the Windows job fails and
nothing is published.

## Making a release

1. Set the version in `src/OvenTracker/OvenTracker.csproj` (`<Version>`) and in `python/pyproject.toml`
   (`version`). Move `CHANGELOG.md`'s Unreleased entries under it, and merge to `main`. The workflow stamps
   the tag's version into both anyway, so the release can't ship two versions.
2. Tag the commit and push the tag:

   ```sh
   git tag v1.1.0
   git push origin v1.1.0
   ```

   Or run the **Release** workflow from the Actions tab and enter the version. It tags the commit itself.
3. If the `release` environment requires a reviewer, approve the Windows job when it asks.

The workflow stamps the tag's version into the programs (`OvenTracker --version` reports it) and names the
packages after it.

## Publishing the Python package to PyPI (once)

The Python package is always built, tested and attached to the GitHub Release. Publishing it to PyPI as well
uses PyPI's **trusted publishing**: PyPI trusts this repository's release workflow, so no token is stored
anywhere.

1. On pypi.org, under *Your projects → Publishing*, add a pending trusted publisher:
   - project `oventracker`;
   - owner `TGoodhew`, repository `OvenTracker`;
   - workflow `release.yml`, environment `pypi`.
2. In the repository's settings, create the environment `pypi`. A required reviewer is a good idea.
3. Set the repository variable `PYPI_PUBLISH` to `true`.

Until then, the PyPI job is skipped and the release is otherwise complete. If the name `oventracker` is taken
on PyPI, change `name` in `python/pyproject.toml` and the pending publisher to match.

## Setting up signing (once)

Signing uses GitHub's OIDC token, so the repository stores no secret. Azure trusts this repository's
`release` environment, and only to sign with the one certificate profile.

1. **Entra app.** Register an app (e.g. "OvenTracker release signing"). Give it a federated credential:
   - Issuer: `https://token.actions.githubusercontent.com`
   - Subject: `repo:TGoodhew/OvenTracker:environment:release`
   - Audience: `api://AzureADTokenExchange`
2. **Role.** Give the app the role **Artifact Signing Certificate Profile Signer**, scoped to the
   certificate profile only (not the account, resource group or subscription).
3. **GitHub environment.** In the repository's Settings → Environments, create `release`. Add yourself as
   a required reviewer, and limit it to tags matching `v*`. Give it these **variables** (not secrets; none of
   them is a credential):

   | Variable | Value |
   | --- | --- |
   | `AZURE_CLIENT_ID` | the Entra app's application (client) ID |
   | `AZURE_TENANT_ID` | the directory (tenant) ID |
   | `ARTIFACT_SIGNING_ENDPOINT` | the signing account's regional endpoint, `https://<region>.codesigning.azure.net` |
   | `ARTIFACT_SIGNING_ACCOUNT` | the signing account's name |
   | `ARTIFACT_SIGNING_PROFILE` | the certificate profile's name |

The Windows job signs with [packaging/Sign-ArtifactSigning.ps1](../packaging/Sign-ArtifactSigning.ps1):
signtool, Microsoft's `Microsoft.ArtifactSigning.Client` dlib from nuget.org, and only the Azure CLI's
sign-in (the identity `azure/login` set up).

## Building a package yourself

`build.ps1` builds the same packages on your own machine (PowerShell 7, on Windows or Linux):

```powershell
./build.ps1 -SignScript C:\path\to\Sign-Files.ps1          # Windows, signed with your own signing setup
./build.ps1 -Runtime linux-x64,linux-arm64 -SelfContained  # Linux packages (build them on Linux)
```

Build Linux packages on Linux: tar on Windows can't mark the program executable.
