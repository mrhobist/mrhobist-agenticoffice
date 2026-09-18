# Tek dogrulama kapisi. Eksik altyapida SESSIZCE GECMEZ, hata verir.
# Kullanim: powershell -ExecutionPolicy Bypass -File scripts/verify.ps1
[CmdletBinding()]
param(
    [switch]$SkipUi,
    [switch]$SkipRuntime
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
$failed = @()

function Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ""
    Write-Host "== $Name" -ForegroundColor Cyan
    try {
        & $Body
        if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
            throw "cikis kodu $LASTEXITCODE"
        }
        Write-Host "   OK" -ForegroundColor Green
    }
    catch {
        Write-Host "   BASARISIZ: $_" -ForegroundColor Red
        $script:failed += $Name
    }
}

Step "dotnet restore" { dotnet restore MrHobist.AITeam.slnx --nologo }
Step "dotnet build"   { dotnet build MrHobist.AITeam.slnx --nologo --no-restore -warnaserror }
Step "dotnet test"    { dotnet test MrHobist.AITeam.slnx --nologo --no-build --verbosity quiet }

Step "mimari: bagimlilik yonu" {
    # Domain hicbir projeye referans vermez; Application yalniz Domain'i gorur.
    $domain = Get-Content "src/MrHobist.AITeam.Domain/MrHobist.AITeam.Domain.csproj" -Raw
    if ($domain -match 'ProjectReference') { throw "Domain bir projeye referans veriyor" }
    $app = Get-Content "src/MrHobist.AITeam.Application/MrHobist.AITeam.Application.csproj" -Raw
    if ($app -match 'Infrastructure') { throw "Application, Infrastructure'a referans veriyor" }
}

Step "mimari: yasakli ad alanlari" {
    # Domain ve Application altyapi paketi gormez.
    $hits = Get-ChildItem "src/MrHobist.AITeam.Domain","src/MrHobist.AITeam.Application" -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
        Select-String -Pattern 'using\s+(Microsoft\.AspNetCore|Microsoft\.EntityFrameworkCore|Serilog)' -ErrorAction SilentlyContinue
    if ($hits) { throw "Domain/Application icinde altyapi ad alani: $($hits -join '; ')" }
}

Step "kural: Python sinirinda is mantigi yok" {
    # YALNIZ bizim yazdigimiz kod taranir. Onceden butun runtime/ taraniyordu
    # ve .venv icindeki ucuncu parti kod (pydantic'in round_trip=, pip'in
    # round_count=) kurali yanlis tetikliyordu.
    $roots = @("runtime/app", "runtime/tests") | Where-Object { Test-Path $_ }
    if ($roots) {
        $bad = Get-ChildItem $roots -Recurse -Filter *.py -ErrorAction SilentlyContinue |
            Select-String -Pattern '\b(round|phase|stage|workflow|orchestr)\w*\s*=' -ErrorAction SilentlyContinue
        if ($bad) { throw "runtime/ icinde is mantigi izi: $($bad -join '; ')" }
    }
}

if (-not $SkipRuntime -and (Test-Path "runtime/pyproject.toml")) {
    # Sistem Python'inda pytest yok; runtime kendi sanal ortamini kullanir.
    $venvPy = Join-Path $root "runtime/.venv/Scripts/python.exe"
    if (-not (Test-Path $venvPy)) { $venvPy = Join-Path $root "runtime/.venv/bin/python" }
    if (Test-Path $venvPy) {
        Step "python testleri" { Push-Location "runtime"; & $venvPy -m pytest -q; Pop-Location }
    }
    else {
        Step "python testleri" {
            throw "runtime/.venv yok. Kurulum: python -m venv runtime/.venv; runtime/.venv/Scripts/pip install -e 'runtime[dev]'"
        }
    }
}

if (-not $SkipUi -and (Test-Path "ui/package.json")) {
    Step "ui typecheck" { npm --prefix ui run typecheck }
}

Pop-Location
Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "BASARISIZ ADIMLAR: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "Tum dogrulamalar gecti." -ForegroundColor Green
exit 0
