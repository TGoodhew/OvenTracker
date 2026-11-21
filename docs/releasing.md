# Releasing

Releases are built by GitHub Actions ([release.yml](../.github/workflows/release.yml)). Each package is built
on its own system and installed and run on a fresh runner. Then they're published together as one GitHub
Release, with a `SHA256SUMS` file and a build-provenance attestation for every file:

| Package | For |
| --- | --- |
| `OvenTracker-<version>-win-x64-setup.exe` | **Windows: the installer.** Installs for the current user (no administrator rights), adds a Start menu entry and, optionally, `OvenTracker` to the PATH. If the .NET 11 runtime is missing, it offers to download and install it (that step asks for permission). |
| `oventracker_<version>_amd64.deb`, `oventracker_<version>_arm64.deb` | **Ubuntu, Debian, Raspberry Pi OS: the .NET app.** Self-contained, so it needs no .NET packages. Installs to `/opt/oventracker`, runs as `oventracker`. |
| `oventracker-py_<version>_all.deb` | **Ubuntu, Debian: the Python version.** Uses Ubuntu's own `python3-rich` and `python3-serial`. Runs as `oventracker-py`. |
| `OvenTracker-<version>-win-x64.zip`, `-win-x64-self-contained.zip` | Windows without installing: with the .NET 11 runtime, or with everything included |
| `OvenTracker-<version>-linux-x64[-self-contained].tar.gz`, `-linux-arm64[-self-contained].tar.gz` | Other Linux distributions |
| `oventracker-<version>-py3-none-any.whl`, `oventracker-<version>.tar.gz` | The Python version, for `pipx install`. Also published to PyPI once that's set up (below). |

The Python version is offered for Linux; on Windows, use the .NET app.

The Windows programs (`OvenTracker.exe`, `OvenTracker.dll`), the installer and its uninstaller are signed
with Azure Artifact Signing. A release is never made with unsigned Windows programs: if signing isn't set up,
the Windows job fails and nothing is published. Linux packages can't carry an Authenticode signature. The
checksums and attestations are their check instead:

```sh
gh attestation verify oventracker_1.0.0_amd64.deb --repo TGoodhew/OvenTracker
```

## Making a release

1. **Set the version and the notes.**
   - Set the version in `src/OvenTracker/OvenTracker.csproj` (`<Version>`) and `python/pyproject.toml` (`version`).
   - Move `CHANGELOG.md`'s Unreleased entries under a `## <version> (<date>)` heading. That section becomes the release notes.
   - Merge to `main`.

   The workflow stamps the release's version into both apps anyway, so a release can't ship two versions.
2. **Do a dry run first,** if the `release` environment allows `main` (see the signing setup below). On the
   Actions tab, run the **Release** workflow on `main` with the version, and leave **Dry run** ticked. It
   builds, signs and tries everything, and leaves the packages as the run's artifacts to look at, but tags
   and publishes nothing. A tagged release also installs and runs every package before it publishes, so
   nothing is published if any of them fails.
3. **Publish.** Tag the commit and push the tag:

   ```sh
   git tag v1.1.0
   git push origin v1.1.0
   ```

   Or run the workflow again with **Dry run** unticked: it tags the commit itself.
4. **Approve the Windows job** when the `release` environment asks. It waits for your approval before it signs anything.

The workflow stamps the version into the programs (`OvenTracker --version` reports it) and names the packages after it.

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

This is done for this repository. These steps are here for reference, or to set it up again.

Signing uses GitHub's OIDC token, so the repository stores no secret. Azure trusts this repository's
`release` environment, and only to sign with the one certificate profile.

1. **Entra app.** Register an app (e.g. "OvenTracker release signing"). Give it a federated credential:
   - Issuer: `https://token.actions.githubusercontent.com`
   - Subject: `repo:TGoodhew/OvenTracker:environment:release`
   - Audience: `api://AzureADTokenExchange`
2. **Role.** Give the app the role **Artifact Signing Certificate Profile Signer**, scoped to the
   certificate profile only (not the account, resource group or subscription).
3. **GitHub environment.** In the repository's Settings → Environments, create `release`.
   - **Reviewer:** add yourself as a required reviewer.
   - **Where it can run:** limit it to tags matching `v*`. A dry run, or a release started from the Actions tab, runs on `main`. To allow those, add the `main` branch too; the approval is still needed. Without it, only a pushed tag can sign.
   - **Variables:** give it these. They're variables, not secrets, since none of them is a credential.

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

The same scripts build the packages on your own machine.

**Windows:** PowerShell 7, and [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```powershell
./build.ps1 -SignScript C:\path\to\Sign-Files.ps1                 # test, publish, sign, zip
./packaging/build-installer.ps1 -SignScript C:\path\to\Sign-Files.ps1  # the signed installer
```

The installer takes the framework-dependent build in `artifacts/publish/win-x64`, the one `./build.ps1` makes
without `-SelfContained`. Without a signing script (`-SignScript`, or `OVENTRACKER_SIGN_SCRIPT`), both work,
unsigned, with a warning.

**Linux:** PowerShell 7 and `dpkg-deb`.

```sh
pwsh ./build.ps1 -Runtime linux-x64 -SelfContained -SkipTests
bash packaging/build-deb.sh dotnet 1.0.0 linux-x64 artifacts/publish/linux-x64
python3 -m build python --outdir dist-python   # pip install build
bash packaging/build-deb.sh python 1.0.0 dist-python/oventracker-1.0.0-py3-none-any.whl
```

Build Linux packages on Linux: tar on Windows can't mark the program executable.

**The icon** is drawn by [packaging/icon/make_icon.py](../packaging/icon/make_icon.py), which writes the SVG,
the PNGs and the `.ico` from one description of its shapes. The results are committed. Run it again (with
Pillow installed) after changing it.
