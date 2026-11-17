# Loads the page served by serve.mjs in each browser given, headless with a fresh profile, once cold and once from the
# cache, three times over, and waits for each pair of reports to reach results.jsonl in the directory serve.mjs runs in.
#
#   ./measure.ps1 -Results <serve.mjs's directory>/results.jsonl -Browsers chrome.exe,msedge.exe [-Library none]
param(
    [Parameter(Mandatory)] [string] $Results,
    [Parameter(Mandatory)] [string[]] $Browsers,
    [string] $Library = "checked",
    [int] $Rounds = 3,
    [string] $Url = "http://localhost:8123/"
)

foreach ($browser in $Browsers) {
    foreach ($round in 1..$Rounds) {
        $profile = Join-Path ([IO.Path]::GetTempPath()) "nanyx-browser-$([Guid]::NewGuid().ToString('N'))"
        $before = if (Test-Path $Results) { (Get-Content $Results).Count } else { 0 }
        $process = Start-Process $browser -PassThru -ArgumentList @(
            "--headless=new", "--user-data-dir=$profile", "--no-first-run", "--disable-extensions",
            "$($Url)?run=cold&library=$Library")
        $deadline = (Get-Date).AddMinutes(2)
        while ((Get-Date) -lt $deadline -and ((Test-Path $Results) ? (Get-Content $Results).Count : 0) -lt $before + 2) {
            Start-Sleep -Milliseconds 500
        }
        # The headless browser and the processes it started, and nothing else.
        taskkill /PID $process.Id /T /F | Out-Null
        Start-Sleep -Seconds 1
        Remove-Item -Recurse -Force $profile -ErrorAction SilentlyContinue
    }
}
