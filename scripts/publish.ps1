param(
    [ValidateSet("win-x64", "linux-x64")]
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
$Project = Join-Path $PSScriptRoot "..\src\XemuTestRunner\XemuTestRunner.csproj"
$Output = Join-Path $PSScriptRoot "..\publish\$Rid"

dotnet publish $Project -c Release -r $Rid --self-contained true -p:PublishSingleFile=true -o $Output
