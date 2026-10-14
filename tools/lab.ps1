# Deploys the built plugin into the lab copy of Risk of Rain 2 and starts it running a harness script.
param([string]$Script = "$PSScriptRoot\lab-test.txt", [string]$Lab = "E:\RoR2Lab")
Get-Process "Risk of Rain 2" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Lab*" } | ForEach-Object { Stop-Process -Id $_.Id }
Start-Sleep -Seconds 2
Copy-Item "$PSScriptRoot\..\bin\Release\MarioRoR2.dll" "$Lab\BepInEx\plugins\MarioRoR2\" -Force
Remove-Item "$Lab\BepInEx\mario-shots\*" -ErrorAction SilentlyContinue
Copy-Item $Script "$Lab\BepInEx\mario_cmd.txt" -Force
$p = Start-Process -FilePath "$Lab\Risk of Rain 2.exe" -WorkingDirectory $Lab -ArgumentList "-screen-fullscreen","0","-screen-width","1600","-screen-height","900" -PassThru
"started $($p.Id)"
