param(
    [ValidateSet("win-x64", "linux-x64")]
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
$Project = Join-Path $PSScriptRoot "..\src\XemuTestRunner\XemuTestRunner.csproj"
$Output = Join-Path $PSScriptRoot "..\publish\$Rid"

dotnet publish $Project -c Release -r $Rid --self-contained true -p:PublishSingleFile=true -o $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Rid -eq "win-x64") {
    $GamepadSource = Join-Path $PSScriptRoot "..\native\gamepad"
    $GamepadBuild = Join-Path $PSScriptRoot "..\.build\gamepad-win-x64"
    cmake -S $GamepadSource -B $GamepadBuild -A x64
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    cmake --build $GamepadBuild --config Release --parallel 4
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    cmake --install $GamepadBuild --config Release --prefix $Output
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
