# Tek dogrulama kapisi. Eksik altyapida SESSIZCE GECMEZ, hata verir.
# Kullanim: powershell -ExecutionPolicy Bypass -File scripts/verify.ps1
#           ... -SetupRuntime   runtime/.venv'i BU cihazin Python'u ile (yeniden) kurar
[CmdletBinding()]
param(
    [switch]$SkipUi,
    [switch]$SkipRuntime,
    [switch]$SetupRuntime
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
        # Onceki adimdan sizan cikis kodu yeni adimi dusurmesin: saf PowerShell govdeleri
        # $LASTEXITCODE'a dokunmaz, dolayisiyla eski deger kalirsa alakasiz adimlar da
        # BASARISIZ raporlanir (2026-09-21: bir test hatasi dort hata gibi gorundu).
        $global:LASTEXITCODE = 0
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

# Python yorumlayicisi CIHAZA baglidir: .venv icindeki shim, onu kuran makinenin
# (ve Windows kullanicisinin) python.exe'sini mutlak yolla cagirir. Ayni calisma
# dizinini baska bir kullanici actiginda shim DURUYOR ama calismiyor -- bu yuzden
# "dosya var mi" yetmez, yorumlayici fiilen kosturularak yoklanir.
function Test-PythonRuns {
    param([string]$Exe)
    if (-not $Exe -or -not (Test-Path $Exe)) { return $false }
    try { & $Exe -c "pass" 2>&1 | Out-Null } catch { return $false }
    return ($LASTEXITCODE -eq 0)
}

function Get-VenvPython {
    param([string]$Root)
    foreach ($rel in @("runtime/.venv/Scripts/python.exe", "runtime/.venv/bin/python")) {
        $p = Join-Path $Root $rel
        if (Test-Path $p) { return $p }
    }
    return $null
}

# Bu cihazdaki yorumlayici: once Windows launcher (py -3), sonra PATH.
# sys.executable sorulur ki "py" shim'i degil gercek exe yolu donsun.
function Get-SystemPython {
    foreach ($cand in @(@("py", "-3"), @("python"), @("python3"))) {
        $exe = $cand[0]
        if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) { continue }
        $pyArgs = @()
        if ($cand.Count -gt 1) { $pyArgs += $cand[1] }
        $pyArgs += @("-c", "import sys; print(sys.executable)")
        $out = $null
        try { $out = & $exe @pyArgs 2>$null | Select-Object -Last 1 } catch { continue }
        # Microsoft Store kisayolu sifirdan farkli doner ya da bos yol basar.
        if ($LASTEXITCODE -eq 0 -and $out -and (Test-Path $out)) { return $out }
    }
    return $null
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
    $venvDir = Join-Path $root "runtime/.venv"
    $venvPy = Get-VenvPython $root
    $venvOk = Test-PythonRuns $venvPy

    if ($SetupRuntime -and -not $venvOk) {
        Step "runtime sanal ortami (bu cihaz)" {
            $sysPy = Get-SystemPython
            if (-not $sysPy) { throw "Bu cihazda Python bulunamadi. Python 3.12+ kurun (py launcher ya da PATH)." }
            Write-Host "   bu cihazin Python'u: $sysPy"
            if (Test-Path $venvDir) {
                Write-Host "   var olan runtime/.venv bu cihazda calismiyor; siliniyor"
                Remove-Item $venvDir -Recurse -Force
            }
            & $sysPy -m venv $venvDir
            if ($LASTEXITCODE -ne 0) { throw "venv kurulamadi" }
            $newPy = Get-VenvPython $root
            if (-not $newPy) { throw "venv kuruldu ama yorumlayici bulunamadi" }
            & $newPy -m pip install --quiet --upgrade pip
            & $newPy -m pip install --quiet -e (Join-Path $root "runtime[dev]")
            if ($LASTEXITCODE -ne 0) { throw "runtime bagimliliklari kurulamadi" }
        }
        $venvPy = Get-VenvPython $root
        $venvOk = Test-PythonRuns $venvPy
    }

    if ($venvOk) {
        Step "python testleri" { Push-Location "runtime"; & $venvPy -m pytest -q; Pop-Location }
    }
    else {
        Step "python testleri" {
            $sysPy = Get-SystemPython
            $hint = if ($sysPy) { "Bu cihazdaki Python: $sysPy." } else { "Bu cihazda Python bulunamadi; Python 3.12+ kurun." }
            $fix = "Onarim: powershell -ExecutionPolicy Bypass -File scripts/verify.ps1 -SetupRuntime"
            if ($venvPy) {
                throw "runtime/.venv var ama bu cihazda calismiyor (baska bir makine ya da Windows kullanicisi kurmus; shim mutlak yol cagiriyor). $hint $fix"
            }
            throw "runtime/.venv yok. $hint $fix"
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
