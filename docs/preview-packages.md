# Fork preview packages

The **Preview packages** workflow publishes prerelease packages to
`https://nuget.pkg.github.com/OleRoss/index.json`. It runs for PRs from branches
in this fork targeting `master`, and can also be run manually. It does not
publish to nuget.org. PRs from other repositories do not publish packages.

The workflow builds these packages with the same version:

- `LiveChartsCore`
- `LiveChartsCore.SkiaSharpView`
- `LiveChartsCore.SkiaSharpView.Avalonia`

PR versions use `<base>-pr.<PR number>.<run number>.<attempt>`; manual runs use
`<base>-preview.<run number>.<attempt>`. The base comes from `Directory.Build.props`.
Find the exact version in the workflow run summary. These are experimental
builds, not upstream releases. Other UI frameworks are not included.

## Install

GitHub Packages requires authentication to download NuGet packages. Configure
the feed locally with a GitHub personal access token (classic) with
`read:packages` access. Keep the token out of checked-in configuration.

```powershell
dotnet nuget add source https://nuget.pkg.github.com/OleRoss/index.json --name OleRoss --username YOUR_GITHUB_USERNAME --password YOUR_READ_PACKAGES_TOKEN
dotnet add package LiveChartsCore.SkiaSharpView.Avalonia --version VERSION_FROM_RUN_SUMMARY
```

If your project references Core or SkiaSharp directly, update those references
to the same preview version. Keep nuget.org enabled for third-party dependencies.

Alternatively, download the `preview-packages` artifact from the workflow run,
extract it, and use that directory as a local NuGet source. The artifact includes
`.nupkg` packages and `.snupkg` symbols.

## Publishing permissions

The PR build has read-only repository access. A separate job downloads its
packages and publishes them with the workflow's `GITHUB_TOKEN`; it does not
check out or execute PR source. No additional publishing secret is required.
